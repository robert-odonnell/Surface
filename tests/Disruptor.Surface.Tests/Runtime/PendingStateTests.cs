using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

public sealed class PendingStateTests
{
    private static PendingState Empty() => new(loadedAtStart: [], relationsAtStart: []);

    private static PendingState WithLoaded(params RecordId[] loaded) =>
        new(loadedAtStart: [..loaded], relationsAtStart: []);

    [Fact]
    public void Set_Overwrites_PriorSet_OnSameField()
    {
        var pending = Empty();
        var id = new RecordId("designs", "x");

        pending.ApplyCommand(Command.Set(id, "description", "first"));
        pending.ApplyCommand(Command.Set(id, "description", "second"));

        var rec = pending.Records[id];
        Assert.Equal("second", rec.Current.Sets["description"]);
        Assert.Empty(rec.Current.Unsets);
    }

    [Fact]
    public void Unset_Cancels_PendingSet_OnSameField()
    {
        var pending = Empty();
        var id = new RecordId("designs", "x");

        pending.ApplyCommand(Command.Set(id, "description", "edited"));
        pending.ApplyCommand(Command.Unset(id, "description"));

        var rec = pending.Records[id];
        Assert.False(rec.Current.Sets.ContainsKey("description"));
        Assert.Contains("description", rec.Current.Unsets);
    }

    [Fact]
    public void Set_AfterUnset_Replaces_TheUnset()
    {
        var pending = Empty();
        var id = new RecordId("designs", "x");

        pending.ApplyCommand(Command.Unset(id, "description"));
        pending.ApplyCommand(Command.Set(id, "description", "back"));

        var rec = pending.Records[id];
        Assert.Equal("back", rec.Current.Sets["description"]);
        Assert.DoesNotContain("description", rec.Current.Unsets);
    }

    [Fact]
    public void Create_Then_Delete_StaysInOneSegment_WithBothBitsSet()
    {
        var pending = Empty();
        var id = new RecordId("designs", "x");

        pending.ApplyCommand(Command.Create(id));
        pending.ApplyCommand(Command.Delete(id));

        var rec = pending.Records[id];
        Assert.Single(rec.Segments);
        Assert.True(rec.Current.Created);
        Assert.True(rec.Current.Deleted);
        // Both bits are set in the single segment — `ExistsAtEnd` reads the Created bit
        // as dominant (so the planner sees "this record was minted in the packet").
        // The planner pairs this with a final DELETE; the matching CommitPlanner test
        // pins that behaviour.
        Assert.True(rec.ExistsAtEnd);
    }

    [Fact]
    public void Delete_Then_Create_Opens_NewSegment()
    {
        // Existed at start so the Delete is meaningful, then a fresh Create reincarnates
        // the record. Two segments — the first ending in Delete, the second a fresh Create.
        var id = new RecordId("designs", "x");
        var pending = WithLoaded(id);

        pending.ApplyCommand(Command.Delete(id));
        pending.ApplyCommand(Command.Create(id));

        var rec = pending.Records[id];
        Assert.Equal(2, rec.Segments.Count);
        Assert.True(rec.Segments[0].Deleted);
        Assert.True(rec.Segments[1].Created);
        Assert.True(rec.ExistsAtEnd);
    }

    [Fact]
    public void ExistsAtEnd_ForLoadedRecord_DependsOnLastSegment()
    {
        var id = new RecordId("designs", "x");
        var pending = WithLoaded(id);

        // Loaded, then deleted — gone at end.
        pending.ApplyCommand(Command.Delete(id));
        Assert.False(pending.Records[id].ExistsAtEnd);
    }

    [Fact]
    public void ExistsAtEnd_ForFreshRecord_RequiresCreateOrUpsert()
    {
        var pending = Empty();
        var id = new RecordId("designs", "x");

        // A bare Set on a record never created in this packet leaves the record state
        // ambiguous from the planner's POV — ExistsAtEnd is false.
        pending.ApplyCommand(Command.Set(id, "description", "x"));
        Assert.False(pending.Records[id].ExistsAtEnd);
    }

    [Fact]
    public void Relate_FollowedByUnrelate_LandsOnUnrelated()
    {
        var pending = Empty();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));
        pending.ApplyCommand(Command.Unrelate(src, "restricts", tgt));

        var rel = pending.Relations[("restricts", src, tgt)];
        Assert.Equal(RelationFinalState.Unrelated, rel.State);
        // Unrelate clears any payload that had been queued for the same edge.
        Assert.Empty(rel.PayloadSets);
    }

    [Fact]
    public void UnrelateAllFrom_DropsPriorRelateEntries_ForSameSourceAndKind()
    {
        var pending = Empty();
        var src = new RecordId("constraints", "c");
        var t1 = new RecordId("user_stories", "u1");
        var t2 = new RecordId("user_stories", "u2");

        pending.ApplyCommand(Command.Relate(src, "restricts", t1));
        pending.ApplyCommand(Command.Relate(src, "restricts", t2));
        pending.ApplyCommand(Command.UnrelateAllFrom(src, "restricts"));

        // Per-edge entries are gone — the bulk DELETE WHERE in=src will clear them at
        // the DB. The bulk intent is recorded once, deduped via HashSet semantics.
        Assert.Empty(pending.Relations);
        Assert.Single(pending.BulkUnrelateFrom);
        Assert.Contains(("restricts", src), pending.BulkUnrelateFrom);
    }

    [Fact]
    public void UnrelateAllFrom_ThenRelate_KeepsTheLaterRelate()
    {
        var pending = Empty();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        pending.ApplyCommand(Command.UnrelateAllFrom(src, "restricts"));
        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));

        // The bulk-clear is recorded; the subsequent Relate adds a per-edge entry the
        // planner emits AFTER the bulk DELETE — so the final state has just (src→tgt).
        Assert.Single(pending.Relations);
        Assert.Single(pending.BulkUnrelateFrom);
    }

    [Fact]
    public void UnrelateAllFrom_IsDeduped()
    {
        var pending = Empty();
        var src = new RecordId("constraints", "c");

        pending.ApplyCommand(Command.UnrelateAllFrom(src, "restricts"));
        pending.ApplyCommand(Command.UnrelateAllFrom(src, "restricts"));
        pending.ApplyCommand(Command.UnrelateAllFrom(src, "restricts"));

        Assert.Single(pending.BulkUnrelateFrom);
    }

    [Fact]
    public void Unrelate_FollowedByRelate_LandsOnRelated()
    {
        var pending = Empty();
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        pending.ApplyCommand(Command.Unrelate(src, "restricts", tgt));
        pending.ApplyCommand(Command.Relate(src, "restricts", tgt));

        var rel = pending.Relations[("restricts", src, tgt)];
        Assert.Equal(RelationFinalState.Related, rel.State);
    }
}
