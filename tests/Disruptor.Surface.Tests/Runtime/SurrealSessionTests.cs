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
    public void Track_Runs_Bind_Then_Initialize_Then_Flush_InOrder()
    {
        var session = new SurrealSession();
        var entity = new StubEntity(new RecordId("designs", "x"));

        session.Track(entity);

        // PR7: MarkAllSlicesLoaded runs after Flush — fresh-entity Track owns the entire
        // state, so every slice on the entity is marked loaded as part of the lifecycle.
        Assert.Equal(new[] { "Bind", "Initialize", "Flush", "MarkAllSlicesLoaded" }, entity.Calls.ToArray());
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
    public async Task CommitAsync_ClosesTheSession()
    {
        var session = new SurrealSession();
        var transport = new NullTransport();

        await session.CommitAsync(transport);

        Assert.True(session.IsClosed);
        Assert.Throws<InvalidOperationException>(() => session.Track(new StubEntity(new RecordId("t", "1"))));
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
    public async Task ClosedSession_Reads_Throw()
    {
        var session = new SurrealSession();
        var entity = new StubEntity(new RecordId("t", "1"));
        ((IHydrationSink)session).Track(entity);

        await session.CommitAsync(new NullTransport());

        Assert.Throws<InvalidOperationException>(() => session.Get<StubEntity>(entity.Id));
        Assert.Throws<InvalidOperationException>(() => session.GetReferenceOrDefault<StubEntity>(entity, "x"));
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
        Assert.Throws<InvalidOperationException>(() => session.SetField(id, "field", "v"));
        Assert.Throws<InvalidOperationException>(() => session.UnsetField(id, "field"));
        Assert.Throws<InvalidOperationException>(() => session.Delete(id));
        Assert.Throws<InvalidOperationException>(() => session.Relate(id, id, "edge"));
        Assert.Throws<InvalidOperationException>(() => session.Unrelate(id, id, "edge"));
        Assert.Throws<InvalidOperationException>(() => session.UnrelateAllFrom(id, "edge"));
        Assert.Throws<InvalidOperationException>(() => session.UnrelateAllTo(id, "edge"));
        Assert.Throws<InvalidOperationException>(() => session.RenderBatch());
    }

    [Fact]
    public async Task CommitAsync_OnTransportFailure_ClosesSession_AndRethrows()
    {
        // Fail-closed at the single exception boundary: any exception out of the
        // transport call marks the session closed and propagates to the domain.
        // The session never tries to reason about partial commits or recover state.
        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        session.SetField(entity.Id, "description", "edited"); // bare Create gets dead-weight-elided; force non-empty SQL
        var transport = new ThrowingTransport(new IOException("boom"));

        var ex = await Assert.ThrowsAsync<IOException>(() => session.CommitAsync(transport));
        Assert.Equal("boom", ex.Message);

        Assert.True(session.IsClosed);
        // Subsequent reads/writes throw because the session is closed.
        Assert.Throws<InvalidOperationException>(() => session.Track(new StubEntity(new RecordId("designs", "y"))));
    }

    [Fact]
    public async Task CommitAsync_OnClosedSession_Throws()
    {
        var session = new SurrealSession();
        await session.CommitAsync(new NullTransport());
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.CommitAsync(new NullTransport()));
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

        Assert.Single(entity.Calls.Where(c => c == "Bind"));
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

        // A subsequent explicit Track must not re-Initialize / re-Flush, must not record
        // a Create — the row already exists in Surreal.
        session.Track(entity);

        Assert.Equal(new[] { "Bind" }, entity.Calls.ToArray());
        Assert.Empty(session.Log.Entries);
    }

    [Fact]
    public void SetField_WithEntityValue_CascadesIntoTrack()
    {
        var session = new SurrealSession();
        var owner = new StubEntity(new RecordId("designs", "d"));
        var refTarget = new StubEntity(new RecordId("details", "t"));

        session.Track(owner);
        session.SetField(owner.Id, "details", refTarget, FieldKind.Reference);

        // The cascade should have tracked the Details entity (Bind+Create+Initialize+Flush).
        Assert.Contains("Bind", refTarget.Calls);
        Assert.Equal(2, session.Log.Entries.Count(e => e.Op == CommandOp.Create));
    }

    [Fact]
    public void SetField_WithEntityValue_CanonicalisesToId_BeforeRecording()
    {
        var session = new SurrealSession();
        var owner = new StubEntity(new RecordId("designs", "d"));
        var refTarget = new StubEntity(new RecordId("details", "t"));

        session.Track(owner);
        session.SetField(owner.Id, "details", refTarget, FieldKind.Reference);

        // The recorded Set value must be the target's RecordId, not the entity instance.
        var setCommand = session.Log.Entries.Single(e => e.Op == CommandOp.Set);
        Assert.IsType<RecordId>(setCommand.Value);
        Assert.Equal(refTarget.Id, (RecordId)setCommand.Value!);
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
    public void RelateOnce_RecordsAsRelateOnceCommand_AndStillTracksEdge()
    {
        // RelateOnce takes the same shape on the in-memory edge tracker (so reads of
        // session.QueryOutgoing etc. resolve identically) but records a different
        // CommandOp so the emitter renders UPSERT-with-deterministic-id at commit.
        var session = new SurrealSession();
        var src = new RecordId("findings", "f");
        var tgt = new RecordId("issues", "i");

        session.RelateOnce<StubKind>(src, tgt);

        var cmd = session.Log.Entries.Single(e =>
            e.Op == CommandOp.Relate || e.Op == CommandOp.RelateOnce);
        Assert.Equal(CommandOp.RelateOnce, cmd.Op);
        Assert.Equal("stub_edge", cmd.Key);
    }

    [Fact]
    public void RelateOnce_ProducesDeterministicEdgeId_AcrossInvocations()
    {
        // The whole point: same (src, kind, tgt) triple → same edge row id, every run.
        // Render twice in separate sessions; the rendered SQL must match exactly.
        string Render(string srcVal)
        {
            var session = new SurrealSession();
            session.RelateOnce(new RecordId("findings", srcVal), new RecordId("issues", "i"), "stub_edge");
            return SurrealCommandEmitter.Emit(session.Log.Entries);
        }

        var first = Render("f");
        var second = Render("f");
        Assert.Equal(first, second);
        // RELATE-with-explicit-edge-id is the form SurrealDB accepts for `TYPE RELATION
        // ENFORCED`; UPSERT-with-CONTENT (the previous shape) was rejected because the
        // resulting row never registered as a graph edge.
        Assert.StartsWith("RELATE findings:f->stub_edge:", first);
        Assert.EndsWith("->issues:i;\n", first);

        // Different src → different id (no aliasing).
        var different = Render("g");
        Assert.NotEqual(first, different);
    }

    [Fact]
    public void RelateOnce_WithPayload_AppendsContentClause()
    {
        // Caller-supplied payload becomes the CONTENT clause. `in` / `out` come from
        // the RELATE syntax itself, not the payload, so the user's payload is the only
        // thing inside CONTENT { … }.
        var session = new SurrealSession();
        var payload = new Dictionary<string, object?>
        {
            ["confidence"] = 0.92,
            ["method"] = "static-analysis",
        };

        session.RelateOnce<StubKind>(new RecordId("findings", "f"), new RecordId("issues", "i"), payload);
        var sql = SurrealCommandEmitter.Emit(session.Log.Entries);

        Assert.StartsWith("RELATE findings:f->stub_edge:", sql);
        Assert.Contains("->issues:i CONTENT {", sql);
        Assert.Contains("confidence: 0.92", sql);
        Assert.Contains("method: \"static-analysis\"", sql);
        Assert.DoesNotContain("UPSERT", sql);
        // No `in`/`out` in CONTENT — they're already encoded by the RELATE syntax.
        Assert.DoesNotContain("in: findings:f", sql);
        Assert.DoesNotContain("out: issues:i", sql);
    }

    [Fact]
    public void RelateOnce_NeverEmitsUpsertOnRelationTable_RegressionGuard()
    {
        // SurrealDB rejects `UPSERT edge_table:<hash> CONTENT { in, out, … }` on
        // `TYPE RELATION ENFORCED` tables — the row writes but never registers as a
        // graph edge, so `->edge->` traversal returns empty (preview.12 bug). This
        // test pins the only SQL shape SurrealDB accepts: RELATE with an explicit
        // edge id. Any drift back to UPSERT for RelateOnce would silently break every
        // consumer using ENFORCED relation schemas.
        var session = new SurrealSession();
        session.RelateOnce(new RecordId("code_symbols", "a"), new RecordId("code_symbols", "b"), "uses",
            new Dictionary<string, object?> { ["kind"] = "call" });

        var sql = SurrealCommandEmitter.Emit(session.Log.Entries);

        Assert.DoesNotContain("UPSERT", sql);
        Assert.StartsWith("RELATE code_symbols:a->uses:", sql);
        Assert.Contains("->code_symbols:b CONTENT { kind: \"call\" };", sql);

        // Determinism: same input → same edge id → same SQL string. Re-running the
        // commit on a tracked triple lands on the same edge row.
        var second = new SurrealSession();
        second.RelateOnce(new RecordId("code_symbols", "a"), new RecordId("code_symbols", "b"), "uses",
            new Dictionary<string, object?> { ["kind"] = "call" });
        Assert.Equal(sql, SurrealCommandEmitter.Emit(second.Log.Entries));
    }

    [Fact]
    public void RelateOnce_WithoutPayload_OmitsContentClause()
    {
        // No payload → no CONTENT clause. The RELATE itself encodes in/out, so an empty
        // CONTENT { } would be redundant noise on the wire.
        var session = new SurrealSession();
        session.RelateOnce(new RecordId("a", "1"), new RecordId("b", "2"), "stub_edge");
        var sql = SurrealCommandEmitter.Emit(session.Log.Entries);

        Assert.StartsWith("RELATE a:1->stub_edge:", sql);
        Assert.EndsWith("->b:2;\n", sql);
        Assert.DoesNotContain("CONTENT", sql);
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

    [Fact]
    public void Delete_Throws_WhenEntityIsNotTrackedInThisSession()
    {
        // Unbound: never seen by any session.
        var sessionA = new SurrealSession();
        var unbound = new StubEntity(new RecordId("designs", "u"));
        Assert.Throws<InvalidOperationException>(() => sessionA.Delete(unbound));

        // Foreign: tracked in B, passed into A.
        var sessionB = new SurrealSession();
        var foreign = new StubEntity(new RecordId("designs", "f"));
        ((IHydrationSink)sessionB).Track(foreign);
        Assert.Throws<InvalidOperationException>(() => sessionA.Delete(foreign));

        // Different-instance-same-id: identity poison.
        var id = new RecordId("designs", "d");
        var tracked = new StubEntity(id);
        var ghost   = new StubEntity(id);
        ((IHydrationSink)sessionA).Track(tracked);
        Assert.Throws<InvalidOperationException>(() => sessionA.Delete(ghost));

        // Double-delete: first Delete removed it from `entities`; second must throw.
        sessionA.Delete(tracked);
        Assert.Throws<InvalidOperationException>(() => sessionA.Delete(tracked));

        // None of the failing paths should have fired OnDeleting on the wrong entity.
        Assert.DoesNotContain("OnDeleting", unbound.Calls);
        Assert.DoesNotContain("OnDeleting", foreign.Calls);
        Assert.DoesNotContain("OnDeleting", ghost.Calls);
        // The legitimate first Delete on `tracked` did fire OnDeleting once.
        Assert.Single(tracked.Calls, c => c == "OnDeleting");
    }

    [Fact]
    public void Track_AfterDelete_OfLoadedId_GetsFreshLifecycle()
    {
        // Repro for the zombie-track bug: previously, Track(new T { Id = deletedLoadedId })
        // hit the loadedAtStart short-circuit and returned without Bind/Create/Initialize/Flush,
        // leaving an unbound zombie in `entities`. Fix removes that branch — the standard
        // Bind→Create→Initialize→Flush path runs, and PendingState's Delete→Create segment
        // logic emits both commands.
        var session = new SurrealSession();
        var id = new RecordId("designs", "x");

        var loaded = new StubEntity(id);
        ((IHydrationSink)session).Track(loaded);
        session.Delete(loaded);

        var fresh = new StubEntity(id);
        var tracked = session.Track(fresh);

        Assert.Same(fresh, tracked);
        Assert.Same(session, fresh.BoundSession);
        Assert.Equal(new[] { "Bind", "Initialize", "Flush", "MarkAllSlicesLoaded" }, fresh.Calls.ToArray());
        Assert.Contains(session.Log.Entries, e => e.Op == CommandOp.Delete);
        Assert.Contains(session.Log.Entries, e => e.Op == CommandOp.Create);
    }

    [Fact]
    public void Track_AfterDelete_DoesNotBleed_OutboundReferences()
    {
        // CleanupLocalState clears outbound references on Delete so the recreated entity
        // doesn't inherit the dead one's optional refs via GetReferenceOrDefault. Inbound
        // refs (where this id was the target) are preserved for the planner.
        var session = new SurrealSession();
        var id = new RecordId("designs", "x");
        var refTargetId = new RecordId("details", "t");

        var loaded = new StubEntity(id);
        var refTarget = new StubEntity(refTargetId);
        ((IHydrationSink)session).Track(loaded);
        ((IHydrationSink)session).Track(refTarget);
        ((IHydrationSink)session).Reference(loaded.Id, "optionalRef", refTargetId);

        Assert.Same(refTarget, session.GetReferenceOrDefault<StubEntity>(loaded, "optionalRef"));

        session.Delete(loaded);

        var fresh = new StubEntity(id);
        session.Track(fresh);

        // The recreated instance must not see the dead entity's optional ref.
        Assert.Null(session.GetReferenceOrDefault<StubEntity>(fresh, "optionalRef"));
    }

    [Fact]
    public void UnrelateAllFrom_TypedKind_EmitsSingle_BulkCommand()
    {
        var session = new SurrealSession();
        var src = new RecordId("constraints", "c");
        var t1  = new RecordId("user_stories", "u1");
        var t2  = new RecordId("user_stories", "u2");

        session.Relate<StubKind>(src, t1);
        session.Relate<StubKind>(src, t2);
        session.UnrelateAllFrom<StubKind>(src);

        // The bulk-clear is a single command — it renders as `DELETE edge WHERE in =
        // source` at commit time so persisted edges (not just loaded ones) get cleared.
        var bulks = session.Log.Entries.Count(e => e.Op == CommandOp.UnrelateAllFrom);
        Assert.Equal(1, bulks);
        // Per-edge Unrelate is not emitted — that path was for loaded-only enumeration.
        Assert.Equal(0, session.Log.Entries.Count(e => e.Op == CommandOp.Unrelate));
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
        public void Flush(SurrealSession session)      => Calls.Add("Flush");
        public void Hydrate(JsonElement json, IHydrationSink sink) => Calls.Add("Hydrate");
        public void HydratePartial(JsonElement json, IHydrationSink sink) => Calls.Add("HydratePartial");
        public void OnDeleting()                       => Calls.Add("OnDeleting");
        public void MarkAllSlicesLoaded(IHydrationSink sink) => Calls.Add("MarkAllSlicesLoaded");
    }

    /// <summary>Test-only relation kind so the typed Relate&lt;TKind&gt; surface can be exercised without the generator.</summary>
    private sealed class StubKind : IRelationKind
    {
        public static string EdgeName => "stub_edge";
    }

    /// <summary>No-op transport — accepts any call, returns an empty document. Used for closure tests where the SQL doesn't matter.</summary>
    private sealed class NullTransport : ISurrealTransport
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
            => Task.FromResult(JsonDocument.Parse("[]"));
    }

    /// <summary>Always-throws transport — drives the fail-closed-on-exception test.</summary>
    private sealed class ThrowingTransport(Exception ex) : ISurrealTransport
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
            => Task.FromException<JsonDocument>(ex);
    }
}
