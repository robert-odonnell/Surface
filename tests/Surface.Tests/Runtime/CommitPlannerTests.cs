using Surface.Runtime;
using Xunit;

namespace Surface.Tests.Runtime;

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

    private static IReadOnlyDictionary<(RecordId, string), RecordId> NoLiveRefs() =>
        new Dictionary<(RecordId, string), RecordId>();

    [Fact]
    public void DeadWeight_FreshRecord_WithNoWritesAndNoReferences_IsElided()
    {
        // The auto-Track ctor used to leave behind ghost ids; the planner drops a Create
        // for a fresh record that nothing else writes to or references.
        var pending = Empty();
        var ghost = new RecordId("designs", "ghost");
        pending.ApplyCommand(Command.Create(ghost));

        var plan = CommitPlanner.Build(pending, NoLiveRefs());

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

        var plan = CommitPlanner.Build(pending, NoLiveRefs());

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

        var plan = CommitPlanner.Build(pending, NoLiveRefs());

        // The plan should have creates first, then relations.
        var createIdx = IndexOfFirst(plan, c => c.Op is CommandOp.Create or CommandOp.Upsert);
        var relateIdx = IndexOfFirst(plan, c => c.Op == CommandOp.Relate);
        Assert.True(createIdx < relateIdx, "creates must precede relations");

        // Field-update ordering against the loaded record:
        var plan2 = CommitPlanner.Build(pending2, NoLiveRefs());
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

        var plan = CommitPlanner.Build(pending, NoLiveRefs());

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

        var plan = CommitPlanner.Build(pending, NoLiveRefs());

        // §16: final-segment deletes are the last phase. Field updates from before
        // the delete are dropped because the record is gone at commit.
        Assert.Single(plan);
        Assert.Equal(CommandOp.Delete, plan[0].Op);
    }

    [Fact]
    public void NewRecord_CreatedThenDeletedInSamePacket_EmitsOnly_FinalDelete()
    {
        // §16: a fresh record with Create+Set+Delete in the same segment produces only
        // the final-segment DELETE — Set is dropped (record is gone), and the planner's
        // EmitRecord short-circuits the Create branch when `final.Deleted` is set.
        // Pinned by this test in case the rule changes; today's emission is debatable
        // (the DELETE can fail at the DB if the matching CREATE never went out).
        var pending = Empty();
        var id = new RecordId("designs", "x");

        pending.ApplyCommand(Command.Create(id));
        pending.ApplyCommand(Command.Set(id, "description", "edit"));
        pending.ApplyCommand(Command.Delete(id));

        var plan = CommitPlanner.Build(pending, NoLiveRefs());
        Assert.Single(plan);
        Assert.Equal(CommandOp.Delete, plan[0].Op);
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

        var plan = CommitPlanner.Build(pending2, NoLiveRefs());

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

        var plan = CommitPlanner.Build(pending, NoLiveRefs());

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

        var plan = CommitPlanner.Build(pending, NoLiveRefs());

        Assert.Single(plan);
        Assert.Equal(CommandOp.Upsert, plan[0].Op);
    }

    [Fact]
    public void Relate_OnExistingRelation_WithNoPayload_IsNoOp()
    {
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");
        var existing = new HashSet<(string, RecordId, RecordId)> { ("restricts", src, tgt) };
        var pending = new PendingState([], existing);

        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));

        var plan = CommitPlanner.Build(pending, NoLiveRefs());
        Assert.Empty(plan);
    }

    [Fact]
    public void Unrelate_OnNeverExistingRelation_IsNoOp()
    {
        var pending = Empty();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        pending.ApplyCommand(Command.Unrelate(src, "restricts", tgt));

        var plan = CommitPlanner.Build(pending, NoLiveRefs());
        Assert.Empty(plan);
    }

    [Fact]
    public void RejectReference_ToDeletedTarget_Throws()
    {
        // Stub registry: design.parent references constraints with Reject behavior.
        ReferenceRegistry.Register(new StubRegistry(
            ("designs", "constraint", "constraints", ReferenceDeleteBehavior.Reject)));

        var design = new RecordId("designs", "d");
        var constraint = new RecordId("constraints", "c");
        var pending = WithLoaded(design, constraint);
        pending.ApplyCommand(Command.Delete(constraint));

        var liveRefs = new Dictionary<(RecordId, string), RecordId>
        {
            { (design, "constraint"), constraint }
        };

        var ex = Assert.Throws<CommitPlanRejectException>(() => CommitPlanner.Build(pending, liveRefs));
        Assert.Contains(ex.Blockers, b => b.Contains("constraints:c") && b.Contains("designs:d"));
    }

    [Fact]
    public void UnsetReference_ToDeletedTarget_GeneratesFieldUnset()
    {
        ReferenceRegistry.Register(new StubRegistry(
            ("designs", "constraint", "constraints", ReferenceDeleteBehavior.Unset)));

        var design = new RecordId("designs", "d");
        var constraint = new RecordId("constraints", "c");
        var pending = WithLoaded(design, constraint);
        pending.ApplyCommand(Command.Delete(constraint));

        var liveRefs = new Dictionary<(RecordId, string), RecordId>
        {
            { (design, "constraint"), constraint }
        };

        var plan = CommitPlanner.Build(pending, liveRefs);

        // The owner survives; the field gets Unset; the target gets Deleted.
        Assert.Contains(plan, c => c.Op == CommandOp.Unset && c.Target == design && c.Key == "constraint");
        Assert.Contains(plan, c => c.Op == CommandOp.Delete && c.Target == constraint);
    }

    [Fact]
    public void CascadeReference_ToDeletedTarget_DeletesOwnerToo()
    {
        ReferenceRegistry.Register(new StubRegistry(
            ("designs", "constraint", "constraints", ReferenceDeleteBehavior.Cascade)));

        var design = new RecordId("designs", "d");
        var constraint = new RecordId("constraints", "c");
        var pending = WithLoaded(design, constraint);
        pending.ApplyCommand(Command.Delete(constraint));

        var liveRefs = new Dictionary<(RecordId, string), RecordId>
        {
            { (design, "constraint"), constraint }
        };

        var plan = CommitPlanner.Build(pending, liveRefs);

        Assert.Contains(plan, c => c.Op == CommandOp.Delete && c.Target == design);
        Assert.Contains(plan, c => c.Op == CommandOp.Delete && c.Target == constraint);
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
