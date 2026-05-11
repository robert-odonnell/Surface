using System.Text.Json;
using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Tests <see cref="SurrealSession.Track{T}"/>'s Bind → Create → Initialize → Flush
/// lifecycle, idempotence, and the SetField cascade that auto-tracks IEntity values.
/// Uses a tiny in-test <see cref="StubEntity"/> instead of touching the generated
/// partials so the runtime is exercised in isolation.
/// </summary>
public sealed class SurrealSessionTests
{
    [Fact]
    public void Track_Runs_Bind_Then_Initialize_Then_MarkAllSlicesLoaded_InOrder()
    {
        var session = new SurrealSession();
        var entity = new StubEntity(new RecordId("designs", "x"));

        session.Track(entity);

        // Pure-setter model: no Flush phase (no pre-bind buffer to drain). Bind + Initialize
        // (mandatory-ref seeding) + MarkAllSlicesLoaded (fresh-entity owns its full state).
        Assert.Equal(new[] { "Bind", "Initialize", "MarkAllSlicesLoaded" }, entity.Calls.ToArray());
        Assert.Same(session, entity.BoundSession);
    }

    [Fact]
    public void Track_Records_Create_ForFreshEntity()
    {
        var session = new SurrealSession();
        var entity = new StubEntity(new RecordId("designs", "x"));

        session.Track(entity);

        Assert.Single(session.Log.Entries);
        Assert.Equal(CommandOp.Create, session.Log.Entries[0].Op);
    }

    [Fact]
    public void Abandon_ClosesTheSession_AndIsIdempotent()
    {
        var session = new SurrealSession();
        session.Abandon();
        Assert.True(session.IsClosed);
        // Second call must not throw — idempotent close.
        session.Abandon();
    }

    [Fact]
    public void ClosedSession_Reads_Throw()
    {
        var session = new SurrealSession();
        var entity = new StubEntity(new RecordId("t", "1"));
        ((IHydrationSink)session).Track(entity);

        session.Abandon();

        Assert.Throws<InvalidOperationException>(() => session.Get<StubEntity>(entity.Id));
        Assert.Throws<InvalidOperationException>(() => session.QueryChildren<StubEntity>(entity, "child"));
        Assert.Throws<InvalidOperationException>(() => session.QueryOutgoing<StubEntity>(entity, "edge"));
        Assert.Throws<InvalidOperationException>(() => session.QueryIncoming<StubEntity>(entity, "edge"));
    }

    [Fact]
    public void ClosedSession_Writes_Throw()
    {
        var session = new SurrealSession();
        session.Abandon();
        var id = new RecordId("t", "1");

        Assert.Throws<InvalidOperationException>(() => session.Track(new StubEntity(id)));
        Assert.Throws<InvalidOperationException>(() => session.Relate(id, id, RecordId.Idempotent("edge")));
        Assert.Throws<InvalidOperationException>(() => session.Unrelate(id, id, "edge"));
        Assert.Throws<InvalidOperationException>(() => session.Unrelate(id, null, "edge"));
        Assert.Throws<InvalidOperationException>(() => session.Unrelate(null, id, "edge"));
    }

    [Fact]
    public async Task DeleteAsync_OnDispatchFailure_ClosesSession_AndRethrows()
    {
        // Fail-closed at the single exception boundary: any exception during dispatch
        // marks the session closed and propagates. The txn handle is the app's to cancel.
        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        var db = FakeSurreal.Throwing(new IOException("boom"));

        await using var tx = await db.BeginTransactionAsync();
        var ex = await Assert.ThrowsAsync<IOException>(() => session.DeleteAsync(entity, tx));
        Assert.Equal("boom", ex.Message);

        Assert.True(session.IsClosed);
        Assert.Throws<InvalidOperationException>(() => session.Track(new StubEntity(new RecordId("designs", "y"))));
    }

    [Fact]
    public async Task SaveAsync_OnClosedSession_Throws()
    {
        var session = new SurrealSession();
        var entity = new StubEntity(new RecordId("t", "1"));
        session.Abandon();
        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SaveAsync(entity, tx));
    }

    [Fact]
    public void Track_DifferentInstance_SameId_Throws()
    {
        var session = new SurrealSession();
        var id = new RecordId("designs", "x");
        var first = new StubEntity(id);
        var second = new StubEntity(id);

        session.Track(first);

        // Identity-map poison: the rest of the session would refer to `first` while
        // user code holds `second`. Throw, don't silently bind the wrong instance.
        var ex = Assert.Throws<InvalidOperationException>(() => session.Track(second));
        Assert.Contains("designs:x", ex.Message);
    }

    [Fact]
    public void HydrateTrack_DifferentInstance_SameId_DedupsSilently()
    {
        // Behavior change from preview.16: include-heavy queries (multiple relation
        // traversals targeting the same id) construct duplicate instances per row in
        // their per-target hydrators. Loader-side Track absorbs the duplicate so the
        // hydration loop keeps going — the first instance wins identity. Public
        // Track<T> still throws on instance conflict (covered separately).
        var session = new SurrealSession();
        var id = new RecordId("details", "d");
        var first = new StubEntity(id);
        var second = new StubEntity(id);

        ((IHydrationSink)session).Track(first);
        var ex = Record.Exception(() => ((IHydrationSink)session).Track(second));

        Assert.Null(ex);
        Assert.Same(first, session.Get<StubEntity>(id));
    }

    [Fact]
    public void Track_IsIdempotent_OnRepeatCalls()
    {
        var session = new SurrealSession();
        var entity = new StubEntity(new RecordId("designs", "x"));

        session.Track(entity);
        session.Track(entity);
        session.Track(entity);

        Assert.Single(entity.Calls, c => c == "Bind");
        Assert.Single(session.Log.Entries);
    }

    [Fact]
    public void Track_DoesNotEmit_Create_ForHydratedEntity()
    {
        var session = new SurrealSession();
        var entity = new StubEntity(new RecordId("designs", "x"));

        // Hydrate path: HydrateTrack marks the id as loaded-at-start AND binds the entity.
        ((IHydrationSink)session).Track(entity);
        Assert.Equal(new[] { "Bind" }, entity.Calls.ToArray());

        // A subsequent explicit Track must not re-Initialize, must not record a Create —
        // the row already exists in Surreal.
        session.Track(entity);

        Assert.Equal(new[] { "Bind" }, entity.Calls.ToArray());
        Assert.Empty(session.Log.Entries);
    }

    [Fact]
    public void AdoptIfUnbound_PullsChildIntoSession()
    {
        // Cascade-track via Parent setter (the integration shape used by generated
        // entities) is tested here at the bare-Session level: AdoptIfUnbound called
        // from a parent with a session pulls the unbound child in via Track.
        var session = new SurrealSession();
        var owner = new StubEntity(new RecordId("designs", "d"));
        var child = new StubEntity(new RecordId("constraints", "c"));

        session.Track(owner);
        session.AdoptIfUnbound(child);

        Assert.Contains("Bind", child.Calls);
        Assert.Same(child, session.Get<StubEntity>(child.Id));
    }

    [Fact]
    public void AdoptIfUnbound_NoOp_WhenChildAlreadyBound()
    {
        // The other half of the cascade-track contract: don't move an already-bound
        // child to a different session.
        var session = new SurrealSession();
        var child = new StubEntity(new RecordId("constraints", "c"));
        session.Track(child);
        var bindCount = child.Calls.Count(c => c == "Bind");

        session.AdoptIfUnbound(child);

        Assert.Equal(bindCount, child.Calls.Count(c => c == "Bind"));
    }

    [Fact]
    public void Get_ReturnsTypedEntity_WhenTracked()
    {
        var session = new SurrealSession();
        var id = new RecordId("designs", "x");
        var entity = new StubEntity(id);

        ((IHydrationSink)session).Track(entity);

        var resolved = session.Get<StubEntity>(id);
        Assert.Same(entity, resolved);
    }

    [Fact]
    public void Get_ReturnsNull_WhenNotTracked()
    {
        var session = new SurrealSession();
        var resolved = session.Get<StubEntity>(new RecordId("designs", "missing"));
        Assert.Null(resolved);
    }

    [Fact]
    public void Relate_TypedKind_UsesTKindEdgeName()
    {
        var session = new SurrealSession();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        session.Relate<StubKind>(src, tgt);

        var relate = session.Log.Entries.Single(e => e.Op == CommandOp.Relate);
        Assert.Equal("stub_edge", relate.Key);
        Assert.Equal(src, relate.Target);
        Assert.Equal(tgt, (RecordId)relate.Value!);
    }

    [Fact]
    public void Relate_WithPayload_AttachesEdgeContent()
    {
        // Edges in SurrealDB can carry their own properties (confidence scores, run id,
        // resolution method, …). The payload-aware overload threads the dict through
        // Command.Relate's EdgeContent so SurrealCommandEmitter renders RELATE … CONTENT
        // { … } instead of bare RELATE.
        var session = new SurrealSession();
        var src = new RecordId("findings", "f");
        var tgt = new RecordId("observations", "o");
        var payload = new Dictionary<string, object?>
        {
            ["confidence"] = 0.92,
            ["method"] = "static-analysis",
        };

        session.Relate<StubKind>(src, tgt, payload);

        var relate = session.Log.Entries.Single(e => e.Op == CommandOp.Relate);
        Assert.Equal("stub_edge", relate.Key);
        Assert.NotNull(relate.EdgeContent);
        Assert.Equal(0.92, relate.EdgeContent!["confidence"]);
        Assert.Equal("static-analysis", relate.EdgeContent["method"]);
    }

    [Fact]
    public void TypedRelate_DefaultsToIdempotent_DeterministicAcrossSessions()
    {
        // The typed-kind shorthand `Relate<TKind>(src, tgt)` defaults to the Idempotent
        // edge strategy: same triple → same edge id, every run. Re-running the same
        // Relate after commit/reload lands on the same row instead of erroring against
        // the schema-level UNIQUE INDEX on (in, out).
        string Render()
        {
            var session = new SurrealSession();
            session.Relate<StubKind>(new RecordId("findings", "f"), new RecordId("issues", "i"));
            return SurrealCommandEmitter.Emit(session.Log.Entries);
        }

        var first = Render();
        var second = Render();
        Assert.Equal(first, second);
        Assert.StartsWith("RELATE findings:f->stub_edge:", first);
        Assert.EndsWith("->issues:i;\n", first);
        Assert.DoesNotContain("UNIQUE", first);
    }

    [Fact]
    public void TypedRelate_WithExplicitSlugEdge_RendersSlugVerbatim()
    {
        var session = new SurrealSession();
        session.Relate<StubKind>(
            new RecordId("findings", "f"),
            new RecordId("issues", "i"),
            edge: new RecordId("stub_edge", "main_link"));

        var sql = SurrealCommandEmitter.Emit(session.Log.Entries);
        Assert.Equal("RELATE findings:f->stub_edge:main_link->issues:i;\n", sql);
    }

    [Fact]
    public void TypedRelate_WithRandomUlidEdge_RendersClientMintedValue()
    {
        var session = new SurrealSession();
        var edge = RecordId.New("stub_edge");   // pre-minted Ulid
        session.Relate<StubKind>(
            new RecordId("findings", "f"),
            new RecordId("issues", "i"),
            edge);

        var sql = SurrealCommandEmitter.Emit(session.Log.Entries);
        Assert.Contains($"->stub_edge:{edge.Value}->", sql);
    }

    [Fact]
    public void Relate_WithoutPayload_LeavesEdgeContentNull()
    {
        // Regression: the legacy zero-payload overload must not start emitting an empty
        // CONTENT clause. EdgeContent stays null so SurrealCommandEmitter takes the
        // bare-RELATE branch.
        var session = new SurrealSession();
        var src = new RecordId("a", "1");
        var tgt = new RecordId("b", "2");

        session.Relate<StubKind>(src, tgt);

        var relate = session.Log.Entries.Single(e => e.Op == CommandOp.Relate);
        Assert.Null(relate.EdgeContent);
    }

    [Fact]
    public void QueryOutgoing_Returns_Targets_OnlyWhenOwnerIsSource()
    {
        var session = new SurrealSession();
        var src = new StubEntity(new RecordId("a", "1"));
        var tgt1 = new StubEntity(new RecordId("b", "1"));
        var tgt2 = new StubEntity(new RecordId("b", "2"));

        ((IHydrationSink)session).Track(src);
        ((IHydrationSink)session).Track(tgt1);
        ((IHydrationSink)session).Track(tgt2);

        ((IHydrationSink)session).Edge(src.Id, "stub_edge", tgt1.Id);
        ((IHydrationSink)session).Edge(src.Id, "stub_edge", tgt2.Id);
        // Reverse-direction edge that should NOT appear in src's outgoing query.
        ((IHydrationSink)session).Edge(tgt1.Id, "stub_edge", src.Id);

        var outgoing = session.QueryOutgoing<StubEntity>(src, "stub_edge");
        Assert.Equal(2, outgoing.Count);
        Assert.Contains(tgt1, outgoing);
        Assert.Contains(tgt2, outgoing);
    }

    [Fact]
    public void QueryIncoming_Returns_Sources_OnlyWhenOwnerIsTarget()
    {
        var session = new SurrealSession();
        var src1 = new StubEntity(new RecordId("a", "1"));
        var src2 = new StubEntity(new RecordId("a", "2"));
        var tgt = new StubEntity(new RecordId("b", "1"));

        ((IHydrationSink)session).Track(src1);
        ((IHydrationSink)session).Track(src2);
        ((IHydrationSink)session).Track(tgt);

        ((IHydrationSink)session).Edge(src1.Id, "stub_edge", tgt.Id);
        ((IHydrationSink)session).Edge(src2.Id, "stub_edge", tgt.Id);
        // Reverse-direction edge that should NOT appear in tgt's incoming query.
        ((IHydrationSink)session).Edge(tgt.Id, "stub_edge", src1.Id);

        var incoming = session.QueryIncoming<StubEntity>(tgt, "stub_edge");
        Assert.Equal(2, incoming.Count);
        Assert.Contains(src1, incoming);
        Assert.Contains(src2, incoming);
    }

    [Fact]
    public void TypedKind_QueryOutgoing_And_QueryIncoming_Differ_ForSameSelfReferentialEdge()
    {
        // The whole point of #3: same-table self-referential edges where forward and
        // inverse must produce different results. Without directional split, "the other
        // endpoint" returns BOTH neighbours regardless of which side the owner is on.
        var session = new SurrealSession();
        var a = new StubEntity(new RecordId("node", "a"));
        var b = new StubEntity(new RecordId("node", "b"));
        var c = new StubEntity(new RecordId("node", "c"));

        ((IHydrationSink)session).Track(a);
        ((IHydrationSink)session).Track(b);
        ((IHydrationSink)session).Track(c);

        ((IHydrationSink)session).Edge(a.Id, "stub_edge", b.Id);   // a → b
        ((IHydrationSink)session).Edge(c.Id, "stub_edge", a.Id);   // c → a

        var outgoing = session.QueryOutgoing<StubKind, StubEntity>(a);
        var incoming = session.QueryIncoming<StubKind, StubEntity>(a);

        Assert.Single(outgoing);
        Assert.Contains(b, outgoing);
        Assert.Single(incoming);
        Assert.Contains(c, incoming);
    }

    // Delete-related tests removed alongside the legacy sync Delete. DeleteAsync covers
    // entity removal now (without cascade — re-anchor lands in preview.35).

    [Fact]
    public void Unrelate_SourceOnly_EmitsSingleBulkCommand()
    {
        var session = new SurrealSession();
        var src = new RecordId("constraints", "c");
        var t1  = new RecordId("user_stories", "u1");
        var t2  = new RecordId("user_stories", "u2");

        session.Relate<StubKind>(src, t1);
        session.Relate<StubKind>(src, t2);
        session.Unrelate<StubKind>(src, target: null);

        // The bulk-clear is a single Unrelate command (target encoded as null). It
        // renders as `DELETE edge WHERE in = source` at commit time so persisted
        // edges (not just loaded ones) get cleared.
        var bulkOnly = session.Log.Entries.Single(e =>
            e.Op == CommandOp.Unrelate && e.Value is null);
        Assert.Equal("stub_edge", bulkOnly.Key);
    }

    [Fact]
    public void Unrelate_BothNull_Throws()
    {
        var session = new SurrealSession();
        Assert.Throws<ArgumentException>(() => session.Unrelate((IRecordId?)null, (IRecordId?)null, "edge"));
    }

    [Fact]
    public void HydrationSinkTrack_DuplicateInstance_DedupsSilently_AndReturns()
    {
        // Include-heavy queries can route the same row through the hydrator twice (e.g.
        // CodeSymbol B reached via both Calls and Uses traversals). The hydrator's
        // `new T() + Hydrate` pattern produces a fresh instance per row, but the
        // identity-map already has one. Loader-side Track must absorb the duplicate
        // silently — throwing aborts the hydration loop after the first conflict and
        // strands edge synthesis / nested traversal for the rest of the rows.
        //
        // Distinct from the public Track<T> path, which still throws on instance
        // conflict (user-side identity-map poison is loud-fail territory).
        var session = new SurrealSession();
        var sink = (IHydrationSink)session;
        var id = new RecordId("code_symbols", "shared");
        var first = new StubEntity(id);
        var second = new StubEntity(id);

        sink.Track(first);
        var ex = Record.Exception(() => sink.Track(second));

        Assert.Null(ex);
        // The first instance wins identity; the duplicate is dropped (no Bind, no
        // entries dict overwrite).
        Assert.Same(first, session.Get<StubEntity>(id));
        Assert.Same(session, first.BoundSession);
        Assert.Null(second.BoundSession);
    }

    /// <summary>Test-only entity that records the order of session-side hook calls.</summary>
    private sealed class StubEntity(RecordId id) : IEntity
    {
        public RecordId Id { get; } = id;
        public SurrealSession? Session { get; private set; }
        public SurrealSession? BoundSession => Session;
        public List<string> Calls { get; } = [];

        public void Bind(SurrealSession session)
        {
            Session = session;
            Calls.Add("Bind");
        }

        public void Initialize(SurrealSession session) => Calls.Add("Initialize");
        public void OnDeleting()                       => Calls.Add("OnDeleting");
        public void MarkAllSlicesLoaded(IHydrationSink sink) => Calls.Add("MarkAllSlicesLoaded");
    }

    /// <summary>Test-only relation kind so the typed Relate&lt;TKind&gt; surface can be exercised without the generator.</summary>
    private sealed class StubKind : IRelationKind
    {
        public static string EdgeName => "stub_edge";
    }

}
