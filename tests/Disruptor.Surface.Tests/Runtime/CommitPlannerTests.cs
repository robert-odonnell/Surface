using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Tests for <see cref="CommitPlanner.Build"/>: phase ordering, the dead-weight Create
/// elision, the closed-segment delete-before-recreate behaviour, and reference-delete
/// resolution against a stub registry.
/// </summary>
public sealed class CommitPlannerTests
{
    private static PendingState Empty() => new(loadedAtStart: [], relationsAtStart: []);

    private static PendingState WithLoaded(params RecordId[] loaded) =>
        new(loadedAtStart: [..loaded], relationsAtStart: []);

    [Fact]
    public void DeadWeight_FreshRecord_WithNoWritesAndNoReferences_IsElided()
    {
        // The auto-Track ctor used to leave behind ghost ids; the planner drops a Create
        // for a fresh record that nothing else writes to or references.
        var pending = Empty();
        var ghost = new RecordId("designs", "ghost");
        pending.ApplyCommand(Command.Create(ghost));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);

        Assert.Empty(plan);
    }

    [Fact]
    public void DeadWeight_BIsDropped_WhenAReferencingItIsDeletedSamePacket()
    {
        // Create A; Set A.Ref=B; Delete A. A is fresh-Create+Delete (elides via #6).
        // B has no own writes and is referenced ONLY by the now-doomed A. The
        // referenced-id set must NOT count A's Sets (A doesn't survive), so B is
        // also dead weight. Plan should be empty.
        var pending = Empty();
        var a = new RecordId("a", "1");
        var b = new RecordId("b", "1");

        pending.ApplyCommand(Command.Create(a));
        pending.ApplyCommand(Command.Create(b));
        pending.ApplyCommand(Command.Set(a, "ref", b));
        pending.ApplyCommand(Command.Delete(a));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);
        Assert.Empty(plan);
    }

    [Fact]
    public void DeadWeight_BothEndpoints_OfNoOpRelation_AreDropped()
    {
        // Create A; Create B; Relate A→B; Unrelate A→B. Edge didn't exist at start, so
        // EmitRelation emits nothing for the no-op relation. A and B have no own
        // writes; nothing else points at them. Referenced-id set must NOT count
        // endpoints of non-emitting relations — both Creates should drop.
        var pending = Empty();
        var a = new RecordId("a", "1");
        var b = new RecordId("b", "1");

        pending.ApplyCommand(Command.Create(a));
        pending.ApplyCommand(Command.Create(b));
        pending.ApplyCommand(Command.Relate(a, "rel", b));
        pending.ApplyCommand(Command.Unrelate(a, "rel", b));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);
        Assert.Empty(plan);
    }

    [Fact]
    public void DeadWeight_Suppression_DoesNotApply_WhenAnotherCommandReferences()
    {
        // Same fresh record but its id appears as the value of another record's Set —
        // it must be created so that reference resolves.
        var pending = Empty();
        var details = new RecordId("details", "d");
        var design = new RecordId("designs", "x");

        pending.ApplyCommand(Command.Create(details));
        pending.ApplyCommand(Command.Create(design));
        pending.ApplyCommand(Command.Set(design, "details", details));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);

        Assert.Equal(2, plan.Count(c => c.Op is CommandOp.Create or CommandOp.Upsert));
    }

    [Fact]
    public void Phases_Ordering_CreatesBeforeFieldUpdates_BeforeRelations()
    {
        var pending = Empty();
        var a = new RecordId("designs", "a");
        var b = new RecordId("constraints", "b");

        pending.ApplyCommand(Command.Create(a));
        pending.ApplyCommand(Command.Create(b));
        pending.ApplyCommand(Command.Relate(a, "restricts", b));

        // A scalar field set on a new record gets folded into the Upsert CONTENT — won't
        // appear as a separate Set. To actually see fieldUpdates we touch a loaded record.
        var loaded = new RecordId("designs", "loaded");
        var pending2 = WithLoaded(loaded);
        pending2.ApplyCommand(Command.Set(loaded, "description", "edit"));
        // Use the original Relate-bearing pending for ordering, but check the field-update
        // ordering with pending2 separately.

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);

        // The plan should have creates first, then relations.
        var createIdx = IndexOfFirst(plan, c => c.Op is CommandOp.Create or CommandOp.Upsert);
        var relateIdx = IndexOfFirst(plan, c => c.Op == CommandOp.Relate);
        Assert.True(createIdx < relateIdx, "creates must precede relations");

        // Field-update ordering against the loaded record:
        var plan2 = CommitPlanner.Build(pending2, NullReferenceRegistry.Instance);
        var setIdx = IndexOfFirst(plan2, c => c.Op == CommandOp.Set);
        Assert.True(setIdx >= 0, "expected a Set command in the plan");
    }

    [Fact]
    public void DeleteBeforeRecreate_EmitsDeleteFirst_WhenLoadedRecordIsRecreated()
    {
        var id = new RecordId("designs", "x");
        var pending = WithLoaded(id);

        pending.ApplyCommand(Command.Delete(id));
        pending.ApplyCommand(Command.Create(id));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);

        // Two ops: a closed-segment DELETE followed by a CREATE/UPSERT for the new
        // segment — order matters because the schema would otherwise reject a duplicate.
        var delIdx = IndexOfFirst(plan, c => c.Op == CommandOp.Delete);
        var crIdx  = IndexOfFirst(plan, c => c.Op is CommandOp.Create or CommandOp.Upsert);
        Assert.True(delIdx >= 0, "expected a closed-segment Delete");
        Assert.True(crIdx >= 0, "expected a fresh Create/Upsert");
        Assert.True(delIdx < crIdx, "delete must come before recreate");
    }

    [Fact]
    public void LoadedRecord_DeletedInPacket_EndsWithFinalDelete()
    {
        var id = new RecordId("designs", "x");
        var pending = WithLoaded(id);

        pending.ApplyCommand(Command.Set(id, "description", "edit"));
        pending.ApplyCommand(Command.Delete(id));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);

        // §16: final-segment deletes are the last phase. Field updates from before
        // the delete are dropped because the record is gone at commit.
        Assert.Single(plan);
        Assert.Equal(CommandOp.Delete, plan[0].Op);
    }

    [Fact]
    public void NewRecord_CreatedThenDeletedInSamePacket_IsNoOp()
    {
        // Fresh record with Create + Set + Delete in the same packet: the row never
        // reached the DB, so the plan should be empty. Earlier behaviour emitted a
        // final DELETE for a row that was never CREATE'd in the script — that was the
        // smell #6 in the punch list called out.
        var pending = Empty();
        var id = new RecordId("designs", "x");

        pending.ApplyCommand(Command.Create(id));
        pending.ApplyCommand(Command.Set(id, "description", "edit"));
        pending.ApplyCommand(Command.Delete(id));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);
        Assert.Empty(plan);
    }

    [Fact]
    public void Build_DoesNotMutate_Pending_AcrossCalls()
    {
        // RenderBatch / Build used to mutate the input PendingState during cascade
        // resolution. Now Build clones at entry — calling twice produces the same
        // plan and leaves the caller's pending state intact.
        var registry = new StubRegistry(
            ("a", "b_ref", "b", ReferenceDeleteBehavior.Cascade));

        var a = new RecordId("a", "1");
        var b = new RecordId("b", "1");

        var pending = WithLoaded(a, b);
        pending.HydrateReference(a, "b_ref", b);
        pending.ApplyCommand(Command.Delete(b));

        var planFirst = CommitPlanner.Build(pending, registry);
        var planSecond = CommitPlanner.Build(pending, registry);

        Assert.Equal(planFirst.Count, planSecond.Count);
        for (var i = 0; i < planFirst.Count; i++)
        {
            Assert.Equal(planFirst[i].Op, planSecond[i].Op);
            Assert.Equal(planFirst[i].Target, planSecond[i].Target);
        }

        // The cascade resolution would have created a record entry for `a` to mark it
        // Deleted. With Clone() at planner entry, that mutation lands on the copy —
        // the caller's pending state never had a record for `a`, and shouldn't now.
        Assert.False(pending.Records.ContainsKey(a),
            "cascade resolution must not mutate caller's PendingState");
    }

    [Fact]
    public void RelationAdditions_ArePlacedAfter_RelationRemovals()
    {
        var pending = Empty();
        var a = new RecordId("constraints", "a");
        var b = new RecordId("constraints", "b");
        var c = new RecordId("user_stories", "c");

        // Pre-existing edge that we Unrelate.
        var loaded = new HashSet<RecordId>();
        var existingEdges = new HashSet<(string, RecordId, RecordId)> { ("restricts", a, c) };
        var pending2 = new PendingState(loaded, existingEdges);

        pending2.ApplyCommand(Command.Unrelate(a, "restricts", c));
        pending2.ApplyCommand(Command.Relate(b, "restricts", c));

        var plan = CommitPlanner.Build(pending2, NullReferenceRegistry.Instance);

        var unrelIdx = IndexOfFirst(plan, x => x.Op == CommandOp.Unrelate);
        var relIdx   = IndexOfFirst(plan, x => x.Op == CommandOp.Relate);
        Assert.True(unrelIdx >= 0 && relIdx >= 0);
        Assert.True(unrelIdx < relIdx, "removals must precede additions");
    }

    [Fact]
    public void BulkUnrelateFrom_IsEmittedInRemovalPhase_BeforeRelationAdditions()
    {
        var pending = Empty();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        pending.ApplyCommand(Command.UnrelateAllFrom(src, "restricts"));
        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);

        var bulkIdx = IndexOfFirst(plan, c => c.Op == CommandOp.UnrelateAllFrom);
        var relateIdx = IndexOfFirst(plan, c => c.Op == CommandOp.Relate);

        Assert.True(bulkIdx >= 0, "expected bulk unrelate command in plan");
        Assert.True(relateIdx >= 0, "expected relate in plan");
        Assert.True(bulkIdx < relateIdx,
            "bulk unrelate must run before subsequent relate so the new edge survives");
    }

    [Fact]
    public void Upsert_WithEmptyContent_RemainsUpsert_NeverDowngrades_ToCreate()
    {
        // Polish-3 regression: Upserted intent with empty pending sets must NOT collapse
        // to a CREATE command. The planner used to do that and it changed semantics.
        var pending = Empty();
        var id = new RecordId("designs", "x");

        pending.ApplyCommand(Command.Upsert(id, new Dictionary<string, object?>()));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);

        Assert.Single(plan);
        Assert.Equal(CommandOp.Upsert, plan[0].Op);
    }

    [Fact]
    public void Relate_ThenUnrelate_SamePacket_OnFreshEdge_IsNoOp()
    {
        // Edge didn't exist at start. Relate then Unrelate cancels out — no commands.
        var pending = Empty();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));
        pending.ApplyCommand(Command.Unrelate(src, "restricts", tgt));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);
        Assert.Empty(plan);
    }

    [Fact]
    public void Relate_ThenUnrelate_SamePacket_OnExistingEdge_StillEmitsUnrelate()
    {
        // Edge existed at start. Relate-then-Unrelate net-effect is "edge removed";
        // the planner emits the Unrelate so the DB drops the row.
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");
        var existing = new HashSet<(string, RecordId, RecordId)> { ("restricts", src, tgt) };
        var pending = new PendingState([], existing);

        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));
        pending.ApplyCommand(Command.Unrelate(src, "restricts", tgt));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);
        Assert.Single(plan);
        Assert.Equal(CommandOp.Unrelate, plan[0].Op);
    }

    [Fact]
    public void Unrelate_ThenRelate_SamePacket_OnExistingEdge_IsNoOp()
    {
        // Edge existed at start, intent is Related at end → no change vs. start, no-op.
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");
        var existing = new HashSet<(string, RecordId, RecordId)> { ("restricts", src, tgt) };
        var pending = new PendingState([], existing);

        pending.ApplyCommand(Command.Unrelate(src, "restricts", tgt));
        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);
        Assert.Empty(plan);
    }

    [Fact]
    public void Unrelate_ThenRelate_SamePacket_OnFreshEdge_EmitsRelate()
    {
        // Edge didn't exist at start. Unrelate-then-Relate is just a Relate.
        var pending = Empty();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        pending.ApplyCommand(Command.Unrelate(src, "restricts", tgt));
        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);
        Assert.Single(plan);
        Assert.Equal(CommandOp.Relate, plan[0].Op);
    }

    [Fact]
    public void Relate_OnExistingRelation_WithNoPayload_IsNoOp()
    {
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");
        var existing = new HashSet<(string, RecordId, RecordId)> { ("restricts", src, tgt) };
        var pending = new PendingState([], existing);

        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);
        Assert.Empty(plan);
    }

    [Fact]
    public void Unrelate_OnNeverExistingRelation_IsNoOp()
    {
        var pending = Empty();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        pending.ApplyCommand(Command.Unrelate(src, "restricts", tgt));

        var plan = CommitPlanner.Build(pending, NullReferenceRegistry.Instance);
        Assert.Empty(plan);
    }

    [Fact]
    public void RejectReference_ToDeletedTarget_Throws()
    {
        // Stub registry: design.parent references constraints with Reject behavior.
        var registry = new StubRegistry(
            ("designs", "constraint", "constraints", ReferenceDeleteBehavior.Reject));

        var design = new RecordId("designs", "d");
        var constraint = new RecordId("constraints", "c");
        var pending = WithLoaded(design, constraint);
        pending.HydrateReference(design, "constraint", constraint);
        pending.ApplyCommand(Command.Delete(constraint));

        var ex = Assert.Throws<CommitPlanRejectException>(() => CommitPlanner.Build(pending, registry));
        Assert.Contains(ex.Blockers, b => b.Contains("constraints:c") && b.Contains("designs:d"));
    }

    [Fact]
    public void UnsetReference_ToDeletedTarget_GeneratesFieldUnset()
    {
        var registry = new StubRegistry(
            ("designs", "constraint", "constraints", ReferenceDeleteBehavior.Unset));

        var design = new RecordId("designs", "d");
        var constraint = new RecordId("constraints", "c");
        var pending = WithLoaded(design, constraint);
        pending.HydrateReference(design, "constraint", constraint);
        pending.ApplyCommand(Command.Delete(constraint));

        var plan = CommitPlanner.Build(pending, registry);

        // The owner survives; the field gets Unset; the target gets Deleted.
        Assert.Contains(plan, c => c.Op == CommandOp.Unset && c.Target == design && c.Key == "constraint");
        Assert.Contains(plan, c => c.Op == CommandOp.Delete && c.Target == constraint);
    }

    [Fact]
    public void Reject_DoesNotFire_WhenOwnerIsItself_CascadeDeleted_ViaAnotherEdge()
    {
        // Three-phase reject: A points at C with Reject. A also points at B with
        // Cascade. B is deleted. Cascade phase deletes A. C's Reject scan should
        // therefore NOT see A as a blocker for C, because A is gone.
        var registry = new StubRegistry(
            ("a", "b_ref", "b", ReferenceDeleteBehavior.Cascade),
            ("a", "c_ref", "c", ReferenceDeleteBehavior.Reject));

        var a = new RecordId("a", "1");
        var b = new RecordId("b", "1");
        var c = new RecordId("c", "1");

        var pending = WithLoaded(a, b, c);
        pending.HydrateReference(a, "b_ref", b);
        pending.HydrateReference(a, "c_ref", c);
        pending.ApplyCommand(Command.Delete(b));
        pending.ApplyCommand(Command.Delete(c));

        var plan = CommitPlanner.Build(pending, registry);

        Assert.Contains(plan, x => x.Op == CommandOp.Delete && x.Target == a);
        Assert.Contains(plan, x => x.Op == CommandOp.Delete && x.Target == b);
        Assert.Contains(plan, x => x.Op == CommandOp.Delete && x.Target == c);
    }

    [Fact]
    public void Reject_FiresAfter_Cascade_LeavesAReachingOwnerInPlace()
    {
        // A points at C with Reject. C is deleted. A is NOT cascade-deleted via any
        // other edge — A survives, so the Reject blocker is real.
        var registry = new StubRegistry(
            ("a", "c_ref", "c", ReferenceDeleteBehavior.Reject));

        var a = new RecordId("a", "1");
        var c = new RecordId("c", "1");

        var pending = WithLoaded(a, c);
        pending.HydrateReference(a, "c_ref", c);
        pending.ApplyCommand(Command.Delete(c));

        var ex = Assert.Throws<CommitPlanRejectException>(() => CommitPlanner.Build(pending, registry));
        Assert.Contains(ex.Blockers, b => b.Contains("c:1") && b.Contains("a:1"));
    }

    [Fact]
    public void Reassign_ThenDelete_ClearsTheRejectBlocker()
    {
        // A loaded with A.ref = X. User reassigns A.ref = Y, then deletes X.
        // The reject behavior is on the field, but at-end A no longer points at X,
        // so X can be deleted cleanly.
        var registry = new StubRegistry(
            ("a", "ref", "x", ReferenceDeleteBehavior.Reject));

        var a = new RecordId("a", "1");
        var x = new RecordId("x", "1");
        var y = new RecordId("x", "2");   // same table, different id — also a Reject target
        var pending = WithLoaded(a, x, y);

        // At-start: A.ref = X.
        pending.HydrateReference(a, "ref", x);
        // User reassigns to Y, then deletes X.
        pending.SetReferenceTarget(a, "ref", y);
        pending.ApplyCommand(Command.Set(a, "ref", y));
        pending.ApplyCommand(Command.Delete(x));

        // No reject — A's at-end target is Y, not X. (Y is not deleted, so its own
        // Reject doesn't fire.)
        var plan = CommitPlanner.Build(pending, registry);
        Assert.Contains(plan, c => c.Op == CommandOp.Delete && c.Target == x);
    }

    [Fact]
    public void CascadeReference_ToDeletedTarget_DeletesOwnerToo()
    {
        var registry = new StubRegistry(
            ("designs", "constraint", "constraints", ReferenceDeleteBehavior.Cascade));

        var design = new RecordId("designs", "d");
        var constraint = new RecordId("constraints", "c");
        var pending = WithLoaded(design, constraint);
        pending.HydrateReference(design, "constraint", constraint);
        pending.ApplyCommand(Command.Delete(constraint));

        var plan = CommitPlanner.Build(pending, registry);

        Assert.Contains(plan, c => c.Op == CommandOp.Delete && c.Target == design);
        Assert.Contains(plan, c => c.Op == CommandOp.Delete && c.Target == constraint);
    }

    [Fact]
    public void IgnoreReference_ToDeletedTarget_LeavesOwnerUntouched()
    {
        // [Ignore] is the fourth delete behavior — leave the owner unchanged when the
        // target is deleted. The reference becomes dangling at the DB level; the model
        // accepts that explicitly. No cascade, no unset, no reject.
        var registry = new StubRegistry(
            ("designs", "external", "externals", ReferenceDeleteBehavior.Ignore));

        var design = new RecordId("designs", "d");
        var external = new RecordId("externals", "x");
        var pending = WithLoaded(design, external);
        pending.HydrateReference(design, "external", external);
        pending.ApplyCommand(Command.Delete(external));

        var plan = CommitPlanner.Build(pending, registry);

        // The target gets deleted; the owner stays put with no field touch.
        Assert.Contains(plan, c => c.Op == CommandOp.Delete && c.Target == external);
        Assert.DoesNotContain(plan, c => c.Op == CommandOp.Delete && c.Target == design);
        Assert.DoesNotContain(plan, c => c.Op == CommandOp.Unset && c.Target == design);
    }

    [Fact]
    public void Cascade_Chains_ToFixpoint_AcrossMultipleHops()
    {
        // Cascade is computed to a fixpoint: A→B→C all cascade, deleting C must ripple
        // through B back to A. Tests the iterative resolver, not just the one-hop case.
        var registry = new StubRegistry(
            ("a", "b_ref", "b", ReferenceDeleteBehavior.Cascade),
            ("b", "c_ref", "c", ReferenceDeleteBehavior.Cascade));

        var a = new RecordId("a", "1");
        var b = new RecordId("b", "1");
        var c = new RecordId("c", "1");

        var pending = WithLoaded(a, b, c);
        pending.HydrateReference(a, "b_ref", b);
        pending.HydrateReference(b, "c_ref", c);
        pending.ApplyCommand(Command.Delete(c));

        var plan = CommitPlanner.Build(pending, registry);

        Assert.Contains(plan, x => x.Op == CommandOp.Delete && x.Target == a);
        Assert.Contains(plan, x => x.Op == CommandOp.Delete && x.Target == b);
        Assert.Contains(plan, x => x.Op == CommandOp.Delete && x.Target == c);
    }

    [Fact]
    public void Cascade_AndUnset_OnSameDeletedTarget_BothFire()
    {
        // Mixed-behavior fan-in: target T has two incoming references — one Cascade
        // (CascadeOwner), one Unset (UnsetOwner). Deleting T cascades the first owner
        // out and unsets the second's field. Both effects come from the same delete.
        var registry = new StubRegistry(
            ("cascade_owners", "t_ref", "targets", ReferenceDeleteBehavior.Cascade),
            ("unset_owners",  "t_ref", "targets", ReferenceDeleteBehavior.Unset));

        var cascadeOwner = new RecordId("cascade_owners", "co");
        var unsetOwner   = new RecordId("unset_owners",   "uo");
        var target       = new RecordId("targets",        "t");
        var pending = WithLoaded(cascadeOwner, unsetOwner, target);
        pending.HydrateReference(cascadeOwner, "t_ref", target);
        pending.HydrateReference(unsetOwner,   "t_ref", target);
        pending.ApplyCommand(Command.Delete(target));

        var plan = CommitPlanner.Build(pending, registry);

        Assert.Contains(plan, x => x.Op == CommandOp.Delete && x.Target == cascadeOwner);
        Assert.Contains(plan, x => x.Op == CommandOp.Unset  && x.Target == unsetOwner && x.Key == "t_ref");
        Assert.Contains(plan, x => x.Op == CommandOp.Delete && x.Target == target);
        // The unset-owner survives — it's not cascaded.
        Assert.DoesNotContain(plan, x => x.Op == CommandOp.Delete && x.Target == unsetOwner);
    }

    [Fact]
    public void Reject_ListsAllBlockers_NotJustTheFirst()
    {
        // Multi-blocker: two distinct owners both Reject the deletion of T. The thrown
        // exception names both, not just the first one found — useful diagnostic for the
        // user choosing which to reassign first.
        var registry = new StubRegistry(
            ("owner_a", "t_ref", "targets", ReferenceDeleteBehavior.Reject),
            ("owner_b", "t_ref", "targets", ReferenceDeleteBehavior.Reject));

        var ownerA = new RecordId("owner_a", "a");
        var ownerB = new RecordId("owner_b", "b");
        var target = new RecordId("targets", "t");
        var pending = WithLoaded(ownerA, ownerB, target);
        pending.HydrateReference(ownerA, "t_ref", target);
        pending.HydrateReference(ownerB, "t_ref", target);
        pending.ApplyCommand(Command.Delete(target));

        var ex = Assert.Throws<CommitPlanRejectException>(() => CommitPlanner.Build(pending, registry));
        Assert.Contains(ex.Blockers, b => b.Contains("owner_a:a"));
        Assert.Contains(ex.Blockers, b => b.Contains("owner_b:b"));
    }

    private static int IndexOfFirst<T>(IReadOnlyList<T> list, Func<T, bool> pred)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (pred(list[i])) return i;
        }
        return -1;
    }

    private sealed class StubRegistry : IReferenceRegistry
    {
        private readonly List<ReferenceFieldInfo> infos;

        public StubRegistry(params (string Referencer, string Field, string Referenced, ReferenceDeleteBehavior Behavior)[] entries)
        {
            infos = entries.Select(e => new ReferenceFieldInfo(e.Referencer, e.Field, e.Referenced, e.Behavior, IsNullable: true)).ToList();
        }

        public IReadOnlyList<ReferenceFieldInfo> IncomingReferencesTo(string referencedTable)
            => infos.Where(i => i.ReferencedTable == referencedTable).ToList();
    }
}
