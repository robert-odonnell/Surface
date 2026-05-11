using Disruptor.Surface.Annotations;
using Disruptor.Surface.Runtime;
using Disruptor.Surreal.Values;
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
        // Edge mutations now require a SurrealTransaction so the closed-check is
        // exercised by the dispatch path (variant SaveAsync / UnrelateAsync) — no
        // sync overload to test from this angle anymore.
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

    // Sync Relate / typed-kind Relate behaviour tests (TypedRelate_*, Relate_With*) were
    // removed in preview.45 along with the sync surface itself. Edge dispatch is now
    // exercised end-to-end through variant SaveAsync — see EmissionShapeTests.Variant_*
    // for the SQL-shape assertions on the emitted dispatch path.

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
    // entity removal now (without cascade — re-anchor lands in preview.35). Sync Unrelate
    // tests removed in preview.45 alongside the sync surface; UnrelateAsync still does
    // the both-null guard but exercising it requires a fake SurrealTransaction.

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

    // GetNewOutgoingEdges_* tests added in preview.44 went away with the snapshot-diff
    // chain itself. The buffer they exercised (state.Edges as write store, PendingEdge
    // as the dispatch payload) no longer exists — variant SaveAsync ships each edge
    // straight through the user's transaction, so the post-load diff has nothing to compute.

    [Fact]
    public void Track_CrossSession_Throws_AndDoesNotPolluteIdentityMap()
    {
        // Regression: pre-preview.48 Track did `state.Entities[id] = entity` BEFORE
        // calling entity.Bind(this). If Bind threw because the entity was already bound
        // to another session, the receiving session's identity map kept the entry — a
        // dangling reference to an instance whose Session pointed at someone else.
        // Post-fix: Bind runs first, so the cross-session throw leaves state.Entities
        // empty.
        var sessionA = new SurrealSession();
        var sessionB = new SurrealSession();
        var entity = new StubEntity(new RecordId("designs", "x"));
        sessionA.Track(entity);

        Assert.Throws<InvalidOperationException>(() => sessionB.Track(entity));

        // sessionB's identity map must be untouched.
        Assert.False(sessionB.IsTracked(entity.Id));
        Assert.Null(sessionB.Get<StubEntity>(entity.Id));

        // The entity is still bound to sessionA.
        Assert.Same(sessionA, entity.Session);
    }

    // RelateAsyncReplace_* tests removed in preview.51 phase 6a — the helper they
    // exercised (Session.RelateAsyncReplace<TKind>) was the dispatch core for the
    // typed RelateAsync extensions emitted alongside ForwardRelation<TPayload>; the
    // payload story has moved to per-variant relation classes whose generated
    // SaveAsync emits the same INSERT RELATION INTO ... ON DUPLICATE KEY UPDATE
    // shape (covered by EmissionShapeTests.Variant_SaveAsync_*).

    [Fact]
    public async Task SaveAsync_CrossSession_Throws_BeforeAnyWireDispatch()
    {
        // Regression: pre-preview.48 EnsureBoundForSave only bound when entity.Session
        // was null — it didn't reject `entity.Session != this`. So SaveAsync would
        // proceed: the generated body reads .Children / relations through the entity's
        // *original* session while dispatching writes through this session's tx. Silent
        // cross-contamination. Post-fix: EnsureBoundForSave throws cleanly before any
        // wire op.
        var sessionA = new SurrealSession();
        var sessionB = new SurrealSession();
        var entity = sessionA.Track(new StubEntity(new RecordId("designs", "x")));

        // FakeSurreal.Throwing surfaces an IOException if anything reaches the connection.
        // We expect InvalidOperationException from the cross-session guard, not IOException.
        var db = FakeSurreal.Throwing(new IOException("should never reach the wire"));
        await using var tx = await db.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sessionB.SaveAsync(entity, tx));

        // sessionB closes per the fail-closed contract; sessionA stays usable.
        Assert.True(sessionB.IsClosed);
        Assert.False(sessionA.IsClosed);
    }

    [Fact]
    public async Task DeleteAsync_CascadeChain_RemovesAllAndFiresOnDeletingForEach()
    {
        // a.b_ref → b (Cascade), b.c_ref → c (Cascade). Delete c → b cascades, a cascades.
        // All three vanish from the snapshot; OnDeleting fires on each. Single tx.DeleteAsync
        // dispatch — substrate cascades the rest under REFERENCE ON DELETE CASCADE.
        var registry = new StubReferenceRegistry();
        registry.Add("b", referencer: "a", field: "b_ref", behavior: ReferenceDeleteBehavior.Cascade);
        registry.Add("c", referencer: "b", field: "c_ref", behavior: ReferenceDeleteBehavior.Cascade);
        var session = new SurrealSession(registry);

        var c = new RefStubEntity(new RecordId("c", "c1"));
        var b = new RefStubEntity(new RecordId("b", "b1"), ("c_ref", c.Id));
        var a = new RefStubEntity(new RecordId("a", "a1"), ("b_ref", b.Id));
        ((IHydrationSink)session).Track(c);
        ((IHydrationSink)session).Track(b);
        ((IHydrationSink)session).Track(a);

        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();
        await session.DeleteAsync(c, tx);

        Assert.True(c.OnDeletingCalled);
        Assert.True(b.OnDeletingCalled);
        Assert.True(a.OnDeletingCalled);
        Assert.False(session.IsClosed);
    }

    [Fact]
    public async Task DeleteAsync_Unset_NullsReferenceInSnapshot()
    {
        // a.b_ref → b (Unset). Delete b → a survives, a.b_ref is null in the snapshot
        // mirroring the substrate's REFERENCE ON DELETE UNSET. Reading a.b_ref via
        // EnumerateReferences must return (b_ref, null).
        var registry = new StubReferenceRegistry();
        registry.Add("b", referencer: "a", field: "b_ref", behavior: ReferenceDeleteBehavior.Unset);
        var session = new SurrealSession(registry);

        var b = new RefStubEntity(new RecordId("b", "b1"));
        var a = new RefStubEntity(new RecordId("a", "a1"), ("b_ref", b.Id));
        ((IHydrationSink)session).Track(b);
        ((IHydrationSink)session).Track(a);

        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();
        await session.DeleteAsync(b, tx);

        Assert.True(b.OnDeletingCalled);
        Assert.False(a.OnDeletingCalled);
        var nowOn = ((IEntity)a).EnumerateReferences().Single();
        Assert.Equal("b_ref", nowOn.FieldName);
        Assert.Null(nowOn.Target);
    }

    [Fact]
    public async Task DeleteAsync_RejectBlocker_ThrowsCascadeRejectException_BeforeDispatch()
    {
        // a.b_ref → b (Reject). Delete b → CascadeRejectException with a as the blocker.
        // Wire never sees the DELETE: FakeSurreal.Throwing would surface as IOException
        // if anything reached the connection.
        var registry = new StubReferenceRegistry();
        registry.Add("b", referencer: "a", field: "b_ref", behavior: ReferenceDeleteBehavior.Reject);
        var session = new SurrealSession(registry);

        var b = new RefStubEntity(new RecordId("b", "b1"));
        var a = new RefStubEntity(new RecordId("a", "a1"), ("b_ref", b.Id));
        ((IHydrationSink)session).Track(b);
        ((IHydrationSink)session).Track(a);

        var db = FakeSurreal.Throwing(new IOException("should never reach the wire"));
        await using var tx = await db.BeginTransactionAsync();

        var ex = await Assert.ThrowsAsync<CascadeRejectException>(() => session.DeleteAsync(b, tx));
        var blocker = ex.Blockers.Single();
        Assert.Equal(a.Id, blocker.Referencer);
        Assert.Equal("b_ref", blocker.FieldName);
        Assert.Equal(b.Id, blocker.BlockedTarget);

        // Per the one-catcher-many-throwers contract, even a pre-flight throw closes
        // the session — the catch in DeleteAsync sets closed = true.
        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task DeleteAsync_MultiPassResolve_RejecterCascadesAwayBeforeBlocking()
    {
        // The classic three-phase split: a.c_ref → c (Reject) AND a.d_ref → d (Cascade);
        // d.c_ref → c (Cascade). Delete c. Phase 1 BFS:
        //   c → d cascades (via d.c_ref) → enqueue d.
        //   d → a cascades (via a.d_ref) → enqueue a.
        //   a's enumeration also yields the c_ref Reject pointing at c — collected as
        //     a *provisional* rejecter.
        // Phase 2 filters rejecters whose owner is in the cascade set: a is, so the
        //   provisional Reject is dropped. No steady-state blockers — no throw.
        // Single-pass collection would have thrown a false Reject before cascading a.
        var registry = new StubReferenceRegistry();
        registry.Add("c", referencer: "a", field: "c_ref", behavior: ReferenceDeleteBehavior.Reject);
        registry.Add("d", referencer: "a", field: "d_ref", behavior: ReferenceDeleteBehavior.Cascade);
        registry.Add("c", referencer: "d", field: "c_ref", behavior: ReferenceDeleteBehavior.Cascade);
        var session = new SurrealSession(registry);

        var c = new RefStubEntity(new RecordId("c", "c1"));
        var d = new RefStubEntity(new RecordId("d", "d1"), ("c_ref", c.Id));
        var a = new RefStubEntity(new RecordId("a", "a1"), ("c_ref", c.Id), ("d_ref", d.Id));
        ((IHydrationSink)session).Track(c);
        ((IHydrationSink)session).Track(d);
        ((IHydrationSink)session).Track(a);

        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();
        await session.DeleteAsync(c, tx);

        Assert.True(c.OnDeletingCalled);
        Assert.True(d.OnDeletingCalled);
        Assert.True(a.OnDeletingCalled);
        Assert.False(session.IsClosed);
    }

    // ──────────────────────────── Phase 4: variant query terminals ─────────

    [Fact]
    public async Task QueryVariantsOutgoingAsync_DispatchesSelectFromEdgeWhereInEqualsSource()
    {
        // Option A wire shape: SELECT * FROM {edge} WHERE in = $_src; with the source id
        // bound as a typed SurrealRecordIdValue. Edge name resolves via the variant's
        // [StubKind] attribute → StubKind marker class → static EdgeName "stub_edge".
        var session = new SurrealSession();
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();
        var src = new RecordId("constraints", "c");

        await session.QueryVariantsOutgoingAsync<StubVariant>(src, tx);

        var query = conn.Sent.Single(s => s.Method == "query");
        var (sql, bindings) = ExtractQueryParts(query.Params);
        Assert.Equal("SELECT * FROM stub_edge WHERE in = $_src;", sql);
        var srcBinding = Assert.IsType<SurrealRecordIdValue>(bindings["_src"]);
        Assert.Equal("constraints", srcBinding.SurrealRecordId.Table.Name);
        Assert.Equal("c", ((SurrealStringRecordIdKey)srcBinding.SurrealRecordId.Key).Value);
    }

    [Fact]
    public async Task QueryVariantsIncomingAsync_DispatchesSelectFromEdgeWhereOutEqualsTarget()
    {
        var session = new SurrealSession();
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();
        var tgt = new RecordId("epics", "e");

        await session.QueryVariantsIncomingAsync<StubVariant>(tgt, tx);

        var query = conn.Sent.Single(s => s.Method == "query");
        var (sql, bindings) = ExtractQueryParts(query.Params);
        Assert.Equal("SELECT * FROM stub_edge WHERE out = $_tgt;", sql);
        var tgtBinding = Assert.IsType<SurrealRecordIdValue>(bindings["_tgt"]);
        Assert.Equal("epics", tgtBinding.SurrealRecordId.Table.Name);
        Assert.Equal("e", ((SurrealStringRecordIdKey)tgtBinding.SurrealRecordId.Key).Value);
    }

    [Fact]
    public async Task QueryVariantsOutgoingAsync_HydratesEachRow_AsTVariant_AndPopulatesEdgeIndex()
    {
        // The full read path: substrate returns two edge rows; the helper hydrates each
        // as a typed StubVariant (id/in/out backing fields populated) AND adds the edge
        // tuple to state.Edges so subsequent sync reads off entity-side relation
        // collections see the new edges. Only the helper does the sink.Edge call —
        // variant Hydrate emits sink.Track(this) only (no edge mirroring).
        var srcEntity = new StubEntity(new RecordId("constraints", "c"));
        var src = srcEntity.Id;
        var tgt1 = new RecordId("epics", "e1");
        var tgt2 = new RecordId("epics", "e2");
        var edgeId1 = new RecordId("stub_edge", "01h00000000000000000000000");
        var edgeId2 = new RecordId("stub_edge", "01h00000000000000000000001");

        var rows = new SurrealListValue(
        [
            BuildEdgeRow(edgeId1, src, tgt1),
            BuildEdgeRow(edgeId2, src, tgt2),
        ]);

        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method switch
        {
            "begin" => new SurrealUuidValue(Guid.NewGuid()),
            "query" => WrapAsQueryResponse(rows),
            _ => SurrealValue.None,
        };

        var session = new SurrealSession();
        ((IHydrationSink)session).Track(srcEntity);
        await using var tx = await db.BeginTransactionAsync();

        var variants = await session.QueryVariantsOutgoingAsync<StubVariant>(src, tx);

        Assert.Equal(2, variants.Count);
        Assert.Equal(edgeId1, variants[0].Id);
        Assert.Equal(src, variants[0].InId);
        Assert.Equal(tgt1, variants[0].OutId);
        Assert.Equal(edgeId2, variants[1].Id);
        Assert.Equal(tgt2, variants[1].OutId);

        // Variants are tracked in the session (sink.Track from the variant Hydrate body).
        Assert.Same(variants[0], session.Get<StubVariant>(edgeId1));
        Assert.Same(variants[1], session.Get<StubVariant>(edgeId2));

        // The edge index was populated by HydrateOneVariant's sink.Edge call — sync
        // reads off QueryRelatedIds<StubKind> on the source entity see both targets,
        // which is the load-shape contract that gates entity-side relation collections.
        var relatedIds = session.QueryRelatedIds<StubKind>(srcEntity);
        Assert.Equal(2, relatedIds.Count);
        Assert.Contains(relatedIds, id => RecordId.From(id) == tgt1);
        Assert.Contains(relatedIds, id => RecordId.From(id) == tgt2);
    }

    [Fact]
    public async Task QueryVariantsOutgoingAsync_OnClosedSession_Throws()
    {
        var session = new SurrealSession();
        session.Abandon();
        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.QueryVariantsOutgoingAsync<StubVariant>(new RecordId("constraints", "c"), tx));
    }

    [Fact]
    public async Task QueryVariantsAsync_RawSql_PassesThrough_AndHydratesAsTVariant()
    {
        // Option E escape hatch: caller-supplied SQL + bindings flow straight to the
        // wire; the helper's only contribution is hydrating each returned row as a
        // typed StubVariant and updating the in-session edge index.
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("epics", "e");
        var edgeId = new RecordId("stub_edge", "01h00000000000000000000099");

        var rows = new SurrealListValue([BuildEdgeRow(edgeId, src, tgt)]);

        var (db, conn) = FakeSurreal.NullWithRecording();
        conn.Responder = (method, _, _) => method switch
        {
            "begin" => new SurrealUuidValue(Guid.NewGuid()),
            "query" => WrapAsQueryResponse(rows),
            _ => SurrealValue.None,
        };

        var session = new SurrealSession();
        await using var tx = await db.BeginTransactionAsync();

        var customSql = "SELECT * FROM stub_edge WHERE in = $_src AND out = $_tgt;";
        var customBindings = new SurrealObject
        {
            ["_src"] = new SurrealRecordIdValue(src.ToSdk()),
            ["_tgt"] = new SurrealRecordIdValue(tgt.ToSdk()),
        };

        var variants = await session.QueryVariantsAsync<StubVariant>(customSql, customBindings, tx);

        Assert.Single(variants);
        Assert.Equal(edgeId, variants[0].Id);

        var query = conn.Sent.Single(s => s.Method == "query");
        var (sql, bindings) = ExtractQueryParts(query.Params);
        Assert.Equal(customSql, sql);
        Assert.True(bindings.ContainsKey("_src"));
        Assert.True(bindings.ContainsKey("_tgt"));
    }

    [Fact]
    public async Task QueryOutgoingAsync_KindAndTarget_DispatchesSelectValueOutTraversal()
    {
        // Option B wire shape: SELECT VALUE out.* FROM {edge} WHERE in = $_src AND
        // out.tb = $_outTable. The "out.tb = ..." filter narrows the typed traversal
        // to one target table (necessary when an edge can land on multiple kinds).
        var session = new SurrealSession();
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();
        var src = new RecordId("constraints", "c");

        await session.QueryOutgoingAsync<StubKind, StubTarget>(src, tx);

        var query = conn.Sent.Single(s => s.Method == "query");
        var (sql, _) = ExtractQueryParts(query.Params);
        Assert.Equal("SELECT VALUE out.* FROM stub_edge WHERE in = $_src AND out.tb = $_outTable;", sql);
    }

    [Fact]
    public async Task QueryOutgoingAsync_KindAndTarget_FiltersOutTbToSpecifiedTable()
    {
        // The $_outTable binding is the StubTarget's table name (read off a probe
        // instance's Id.Table). Cached per TTarget type.
        var session = new SurrealSession();
        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();
        var src = new RecordId("constraints", "c");

        await session.QueryOutgoingAsync<StubKind, StubTarget>(src, tx);

        var query = conn.Sent.Single(s => s.Method == "query");
        var (_, bindings) = ExtractQueryParts(query.Params);
        var tableBinding = Assert.IsType<StringSurrealValue>(bindings["_outTable"]);
        Assert.Equal("targets", tableBinding.Value);
    }

    [Fact]
    public async Task QueryVariantsOutgoingAsync_PropagatesQueryFailure_AndClosesSession()
    {
        // Fail-closed contract — any wire failure marks the session done. Mirrors
        // SaveAsync / DeleteAsync / UnrelateAsync semantics.
        var session = new SurrealSession();
        var db = FakeSurreal.Throwing(new IOException("boom"));
        await using var tx = await db.BeginTransactionAsync();

        var ex = await Assert.ThrowsAsync<IOException>(
            () => session.QueryVariantsOutgoingAsync<StubVariant>(new RecordId("constraints", "c"), tx));
        Assert.Equal("boom", ex.Message);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task QueryVariantsOutgoingAsync_IEntityOverload_DelegatesToIRecordIdCore()
    {
        // The IEntity-flavoured overloads collapse to the IRecordId core via .Id;
        // wire shape and bindings should be identical.
        var session = new SurrealSession();
        var entity = new StubEntity(new RecordId("constraints", "c"));
        ((IHydrationSink)session).Track(entity);

        var (db, conn) = FakeSurreal.NullWithRecording();
        await using var tx = await db.BeginTransactionAsync();

        await session.QueryVariantsOutgoingAsync<StubVariant>(entity, tx);

        var query = conn.Sent.Single(s => s.Method == "query");
        var (sql, bindings) = ExtractQueryParts(query.Params);
        Assert.Equal("SELECT * FROM stub_edge WHERE in = $_src;", sql);
        var srcBinding = Assert.IsType<SurrealRecordIdValue>(bindings["_src"]);
        Assert.Equal("constraints", srcBinding.SurrealRecordId.Table.Name);
        Assert.Equal("c", ((SurrealStringRecordIdKey)srcBinding.SurrealRecordId.Key).Value);
    }

    // ──────────────────────────── Phase 5: variant Save / Delete edge-index ─

    [Fact]
    public async Task SaveAsync_VariantInstance_AddsEdgeToReadIndex()
    {
        // The phase-5 gap: variant SaveAsync dispatches INSERT RELATION INTO ... but
        // doesn't itself touch state.Edges — the variant's emitted Hydrate intentionally
        // calls sink.Track(this) only (no sink.Edge). Without the IRelationVariant
        // branch in MarkSaved, the edge would only show up after a session reload.
        // Asserts the sync read off QueryRelatedIds<StubKind> sees the new edge
        // immediately.
        var session = new SurrealSession();
        var srcOwner = new StubEntity(new RecordId("constraints", "c"));
        ((IHydrationSink)session).Track(srcOwner);

        var tgtId = new RecordId("epics", "e");
        var variant = new StubVariant();
        variant.ConfigureForSave(
            id: new RecordId("stub_edge", "01h00000000000000000000000"),
            @in: srcOwner.Id,
            @out: tgtId);

        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();
        await session.SaveAsync(variant, tx);

        var related = session.QueryRelatedIds<StubKind>(srcOwner);
        Assert.Single(related);
        Assert.Equal(tgtId, RecordId.From(related.Single()));
    }

    [Fact]
    public async Task SaveAsync_VariantInstance_PutsVariantInIdentityMap()
    {
        // MarkSaved registers the variant in state.Entities the same way it does for
        // table entities — Get<TVariant>(id) must return the same instance immediately
        // after Save (no reload required). Belt-and-braces alongside the edge-index test.
        var session = new SurrealSession();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("epics", "e");
        var variant = new StubVariant();
        var variantId = new RecordId("stub_edge", "01h00000000000000000000001");
        variant.ConfigureForSave(variantId, src, tgt);

        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();
        await session.SaveAsync(variant, tx);

        Assert.Same(variant, session.Get<StubVariant>(variantId));
    }

    [Fact]
    public async Task SaveAsync_TwoSequentialCalls_PromoteSavedEntityToTrackedAcrossSaveContexts()
    {
        // Each top-level Session.SaveAsync(...) builds a fresh SaveContext with its own
        // savedThisPass set. Without promoting MarkSaved'd entities into the session's
        // loadedAtStart set, a second top-level SaveAsync — whose entity body's forward-dep
        // walk reaches an entity that was already saved in the first call — would see
        // IsTracked=false and re-dispatch CREATE, blowing up at the substrate with
        // "record already exists". This test pins the post-MarkSaved promotion contract:
        // the second SaveContext sees the previously-saved entity as IsTracked.
        var session = new SurrealSession();
        var aId = new RecordId("designs", "a");
        var a = new StubSaveableEntity(aId);
        var b = new StubTrackingObserver(new RecordId("designs", "b"), observedDep: aId);

        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();

        await session.SaveAsync(a, tx);   // first SaveContext — A.MarkSaved promotes A into loadedAtStart
        await session.SaveAsync(b, tx);   // second SaveContext — B's IsTracked(aId) check happens here

        Assert.Single(b.CapturedIsTracked);
        Assert.True(b.CapturedIsTracked[0],
            "Second SaveContext must report IsTracked=true for an entity saved in a prior pass; "
            + "without the loadedAtStart promotion the variant-save forward-dep walk re-CREATEs already-persisted entities.");
    }

    [Fact]
    public async Task SaveAsync_NonVariantEntity_DoesNotMutateEdgeIndex()
    {
        // Sanity check the IRelationVariant gate: ordinary entities going through
        // MarkSaved must not call RecordVariantEdge. Uses StubSaveableEntity (a non-
        // variant whose SaveAsync delegates to ctx.MarkSaved) so the branch is the only
        // thing under test — no wire dispatch, no other state mutation.
        var session = new SurrealSession();
        var entity = new StubSaveableEntity(new RecordId("designs", "d"));

        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();
        await session.SaveAsync(entity, tx);

        // Edge index untouched (entity isn't a variant; EnumerateReferences returns
        // the default empty enumeration).
        Assert.Empty(session.QueryRelatedIds(entity, "stub_edge"));
        // ...but the entity itself is in the identity map as expected.
        Assert.Same(entity, session.Get<StubSaveableEntity>(entity.Id));
    }

    [Fact]
    public async Task DeleteAsync_VariantInstance_RemovesEdgeFromReadIndex()
    {
        // The Gap-2 fix: deleting a variant must drop its (in, edge, out) tuple from
        // state.Edges. Pre-flight: Track the variant and seed the edge via SaveAsync
        // so the read index is populated; DeleteAsync should clear both the entity
        // entry AND the specific edge tuple. The endpoint walk in CleanupLocalState
        // doesn't fire for the variant id itself (it isn't an endpoint), so without
        // the variant-specific drop the edge would survive in the snapshot.
        var session = new SurrealSession();
        var srcOwner = new StubEntity(new RecordId("constraints", "c"));
        ((IHydrationSink)session).Track(srcOwner);

        var tgtId = new RecordId("epics", "e");
        var variantId = new RecordId("stub_edge", "01h00000000000000000000002");
        var variant = new StubVariant();
        variant.ConfigureForSave(variantId, srcOwner.Id, tgtId);

        var db = FakeSurreal.Null();
        await using var tx = await db.BeginTransactionAsync();
        await session.SaveAsync(variant, tx);
        Assert.Single(session.QueryRelatedIds<StubKind>(srcOwner)); // sanity: the seed worked

        await session.DeleteAsync(variant, tx);

        Assert.Empty(session.QueryRelatedIds<StubKind>(srcOwner));
        Assert.Null(session.Get<StubVariant>(variantId));
    }

    // ──────────────────────────── Phase 4 helpers ────────────────────────────

    private static (string Sql, SurrealObject Bindings) ExtractQueryParts(SurrealValue? @params)
    {
        // QueryCommand serialises params as SurrealListValue([sql_string, $vars_object]).
        var list = Assert.IsType<SurrealListValue>(@params);
        var sql = Assert.IsType<StringSurrealValue>(list.List[0]).Value;
        var bindings = Assert.IsType<SurrealObjectValue>(list.List[1]).Object;
        return (sql, bindings);
    }

    private static SurrealObjectValue BuildEdgeRow(RecordId id, RecordId @in, RecordId @out)
        => new(new SurrealObject
        {
            ["id"] = new SurrealRecordIdValue(id.ToSdk()),
            ["in"] = new SurrealRecordIdValue(@in.ToSdk()),
            ["out"] = new SurrealRecordIdValue(@out.ToSdk()),
        });

    /// <summary>
    /// Wraps <paramref name="rows"/> as a single-statement <c>query</c> response so
    /// <see cref="global::Disruptor.Surreal.SurrealQueryResponse.FromValue"/> decodes it
    /// to one OK statement whose <c>Result</c> is the row list. Mirrors the wire shape
    /// the SDK's <c>FakeConnectionTests</c> use for the same purpose.
    /// </summary>
    private static SurrealValue WrapAsQueryResponse(SurrealValue rows)
        => new SurrealListValue(
        [
            new SurrealObjectValue(new SurrealObject
            {
                ["status"] = "OK",
                ["time"] = "1ms",
                ["result"] = rows,
            }),
        ]);

    /// <summary>Test-only IReferenceRegistry. Lets each test declare its own (referencedTable → list of referencer fields) mapping without needing a full generated registry.</summary>
    private sealed class StubReferenceRegistry : IReferenceRegistry
    {
        private readonly Dictionary<string, List<ReferenceFieldInfo>> _byReferenced = [];

        public void Add(string referencedTable, string referencer, string field, ReferenceDeleteBehavior behavior, bool isNullable = true)
        {
            if (!_byReferenced.TryGetValue(referencedTable, out var list))
            {
                list = [];
                _byReferenced[referencedTable] = list;
            }
            list.Add(new ReferenceFieldInfo(referencer, field, referencedTable, behavior, isNullable));
        }

        public IReadOnlyList<ReferenceFieldInfo> IncomingReferencesTo(string referencedTable)
            => _byReferenced.TryGetValue(referencedTable, out var refs) ? refs : [];
    }

    /// <summary>Test-only entity with configurable [Reference] backing fields. Implements EnumerateReferences and SetReferenceTo so the cascade resolve can walk it.</summary>
    private sealed class RefStubEntity : IEntity
    {
        private readonly Dictionary<string, RecordId?> _refs;

        public RefStubEntity(RecordId id, params (string Field, RecordId? Target)[] refs)
        {
            Id = id;
            _refs = refs.ToDictionary(r => r.Field, r => r.Target);
        }

        public RecordId Id { get; }
        public SurrealSession? Session { get; private set; }
        public bool OnDeletingCalled { get; private set; }

        public void Bind(SurrealSession session) => Session = session;
        public void Initialize(SurrealSession session) { }
        public void OnDeleting() => OnDeletingCalled = true;
        public void MarkAllSlicesLoaded(IHydrationSink sink) { }

        IEnumerable<(string FieldName, RecordId? Target)> IEntity.EnumerateReferences()
        {
            foreach (var kv in _refs)
            {
                yield return (kv.Key, kv.Value);
            }
        }

        void IEntity.SetReferenceTo(string fieldName, RecordId? value)
        {
            if (_refs.ContainsKey(fieldName))
            {
                _refs[fieldName] = value;
            }
        }
    }

    /// <summary>Test-only entity that records the order of session-side hook calls. Bind matches the generator's emitted shape: throws on a cross-session bind attempt so the cross-session-protection tests exercise the realistic path.</summary>
    private sealed class StubEntity(RecordId id) : IEntity
    {
        public RecordId Id { get; } = id;
        public SurrealSession? Session { get; private set; }
        public SurrealSession? BoundSession => Session;
        public List<string> Calls { get; } = [];

        public void Bind(SurrealSession session)
        {
            if (Session is not null && !ReferenceEquals(Session, session))
            {
                throw new InvalidOperationException("Entity is already bound to a different session.");
            }
            Session = session;
            Calls.Add("Bind");
        }

        public void Initialize(SurrealSession session) => Calls.Add("Initialize");
        public void OnDeleting()                       => Calls.Add("OnDeleting");
        public void MarkAllSlicesLoaded(IHydrationSink sink) => Calls.Add("MarkAllSlicesLoaded");
    }

}

/// <summary>
/// Test-only attribute mirroring what the generator emits per forward relation kind:
/// the attribute and the marker class are siblings in the same namespace, suffix-stripped
/// — <see cref="StubKindAttribute"/> pairs with the existing <c>StubKind</c> marker via
/// the reflection lookup in <c>SurrealSession.ResolveVariantEdgeName</c>. Top-level (not
/// nested) so <c>Type.Assembly.GetType(StubKind FQN)</c> resolves cleanly without the
/// nested-type <c>+</c> separator.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class StubKindAttribute : ForwardRelation { }

/// <summary>
/// Sibling marker for <see cref="StubKindAttribute"/>. The lookup target the
/// variant-edge-name reflection resolves to after stripping the <c>Attribute</c> suffix
/// off <see cref="StubKindAttribute"/>; also the generic argument for the
/// <c>UnrelateAsync</c> / <c>QueryRelatedIds</c> family in tests.
/// </summary>
public sealed class StubKind : IRelationKind
{
    public static string EdgeName => "stub_edge";
}

/// <summary>
/// Test-only relation variant — minimal IEntity matching the production variant
/// emitter shape: Hydrate parses <c>id</c> / <c>in</c> / <c>out</c> from the row,
/// stores them in backing fields, and calls <c>sink.Track(this)</c> (mirroring
/// <see cref="Disruptor.Surface.Generator.Emit.RelationVariantEmitter"/>'s emitted
/// body, which does NOT call <c>sink.Edge</c> — that's the helper's job).
/// Decorated with <see cref="StubKindAttribute"/> so the reflection-based edge-name
/// lookup resolves to <c>"stub_edge"</c>.
/// <para>
/// Implements <see cref="IRelationVariant"/> so <c>SurrealSession.MarkSaved</c> /
/// <c>CleanupLocalState</c> branch into the variant edge-index path; exposes
/// <see cref="IEntity.EnumerateReferences"/> the same shape the generator emits
/// (<c>("in", inId)</c> / <c>("out", outId)</c>) and a SaveAsync that calls
/// <c>ctx.MarkSaved(this)</c> without going through the wire so phase-5 tests can
/// exercise the post-dispatch session-state mutation in isolation.
/// </para>
/// </summary>
[StubKind]
public sealed class StubVariant : IEntity, IRelationVariant
{
    private RecordId _id;
    private RecordId? _inId;
    private RecordId? _outId;

    public RecordId Id => _id;
    public SurrealSession? Session { get; private set; }
    public RecordId? InId => _inId;
    public RecordId? OutId => _outId;

    public void Bind(SurrealSession session) => Session = session;
    public void Initialize(SurrealSession session) { }
    public void OnDeleting() { }
    public void MarkAllSlicesLoaded(IHydrationSink sink) { }

    /// <summary>
    /// Programmatic seed of id + endpoints — the parameterless ctor stays for the
    /// <c>where TVariant : new()</c> constraint on <c>QueryVariantsAsync*</c>; tests
    /// that need to dispatch a Save against a configured variant call this first to
    /// stand in for the loader-driven Hydrate path.
    /// </summary>
    public void ConfigureForSave(RecordId id, RecordId @in, RecordId @out)
    {
        _id = id;
        _inId = @in;
        _outId = @out;
    }

    void IEntity.Hydrate(SurrealValue row, IHydrationSink sink)
    {
        if (row is not SurrealObjectValue obj)
        {
            return;
        }

        if (HydrationValue.TryReadRecordId(obj, "id", out var id))
        {
            _id = id;
        }
        _inId = HydrationValue.TryReadReferenceId(obj, "in");
        _outId = HydrationValue.TryReadReferenceId(obj, "out");
        sink.Track(this);
    }

    /// <summary>Mirrors the generator-emitted <c>EnumerateReferences</c>: yields exactly two entries, <c>("in", inId)</c> and <c>("out", outId)</c>.</summary>
    IEnumerable<(string FieldName, RecordId? Target)> IEntity.EnumerateReferences()
    {
        yield return ("in", _inId);
        yield return ("out", _outId);
    }

    /// <summary>
    /// Stand-in for the generator-emitted variant SaveAsync body. Skips the wire
    /// dispatch (the actual <c>INSERT RELATION INTO ... ON DUPLICATE KEY UPDATE</c>
    /// is exercised separately at the emission-shape level) and goes straight to
    /// <see cref="ISaveContext.MarkSaved"/> so the session's edge-index branch is
    /// the single thing under test.
    /// </summary>
    Task IEntity.SaveAsync(ISaveContext ctx, CancellationToken ct)
    {
        ctx.MarkSaved(this);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Test-only non-variant entity that implements <see cref="IEntity.SaveAsync"/> as a
/// straight pass-through to <see cref="ISaveContext.MarkSaved"/>. Used by the
/// "MarkSaved branch doesn't fire for non-variant entities" sanity check — ordinary
/// <see cref="SurrealSessionTests.StubEntity"/> can't play this role because it
/// inherits the default <see cref="IEntity.SaveAsync"/> which throws.
/// </summary>
public sealed class StubSaveableEntity(RecordId id) : IEntity
{
    public RecordId Id { get; } = id;
    public SurrealSession? Session { get; private set; }

    public void Bind(SurrealSession session) => Session = session;
    public void Initialize(SurrealSession session) { }
    public void OnDeleting() { }
    public void MarkAllSlicesLoaded(IHydrationSink sink) { }

    Task IEntity.SaveAsync(ISaveContext ctx, CancellationToken ct)
    {
        ctx.MarkSaved(this);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Test-only entity whose SaveAsync captures <c>ctx.IsTracked(observedDep)</c> into a
/// public list before calling <c>ctx.MarkSaved(this)</c>. Lets a test assert what the
/// SaveContext reports about a specified id at SaveAsync time — the moment a real
/// entity body would decide CREATE vs UPDATE or skip a forward-dep walk.
/// </summary>
public sealed class StubTrackingObserver(RecordId id, RecordId observedDep) : IEntity
{
    public RecordId Id { get; } = id;
    public RecordId ObservedDep { get; } = observedDep;
    public SurrealSession? Session { get; private set; }
    public List<bool> CapturedIsTracked { get; } = [];

    public void Bind(SurrealSession session) => Session = session;
    public void Initialize(SurrealSession session) { }
    public void OnDeleting() { }
    public void MarkAllSlicesLoaded(IHydrationSink sink) { }

    Task IEntity.SaveAsync(ISaveContext ctx, CancellationToken ct)
    {
        CapturedIsTracked.Add(ctx.IsTracked(ObservedDep));
        ctx.MarkSaved(this);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Test-only target entity for option-B traversal hydration. Matches the production
/// entity emitter shape — <c>Id</c> defaults to a stable per-instance ULID so
/// <c>ResolveTargetTable</c>'s instantiation-probe yields a concrete table name
/// ("targets"). Real generated entities pre-bake their table via the <c>{Name}Id</c>
/// struct's <c>Table</c> property, but at the IEntity level we only need a non-empty
/// id at probe time.
/// </summary>
public sealed class StubTarget : IEntity
{
    private RecordId _id = new("targets", Ulid.NewUlid().ToString());

    public RecordId Id => _id;
    public SurrealSession? Session { get; private set; }
    public string Name { get; private set; } = "";

    public void Bind(SurrealSession session) => Session = session;
    public void Initialize(SurrealSession session) { }
    public void OnDeleting() { }
    public void MarkAllSlicesLoaded(IHydrationSink sink) { }

    void IEntity.Hydrate(SurrealValue row, IHydrationSink sink)
    {
        if (row is not SurrealObjectValue obj)
        {
            return;
        }

        if (HydrationValue.TryReadRecordId(obj, "id", out var id))
        {
            _id = id;
        }
        Name = HydrationValue.ReadString(obj, "name");
        // Mirrors generator-emitted shape: entity Hydrate calls sink.Track(this).
        // Option-B traversal routes through a NullHydrationSink so this becomes a
        // no-op; exercised explicitly by the "does not auto-track" test.
        sink.Track(this);
    }
}
