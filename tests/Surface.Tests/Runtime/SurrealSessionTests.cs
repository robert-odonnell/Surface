using System.Text.Json;
using Surface.Runtime;
using Xunit;

namespace Surface.Tests.Runtime;

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

        Assert.Equal(new[] { "Bind", "Initialize", "Flush" }, entity.Calls.ToArray());
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
        session.HydrateTrack(entity);
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

        session.HydrateTrack(entity);

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
    public void UnrelateAllFrom_TypedKind_RemovesEveryEdgeFromSource()
    {
        var session = new SurrealSession();
        var src = new RecordId("constraints", "c");
        var t1  = new RecordId("user_stories", "u1");
        var t2  = new RecordId("user_stories", "u2");

        session.Relate<StubKind>(src, t1);
        session.Relate<StubKind>(src, t2);
        session.UnrelateAllFrom<StubKind>(src);

        // Two Relate + two Unrelate (cleanup walks the in-memory edge index).
        var relates   = session.Log.Entries.Count(e => e.Op == CommandOp.Relate);
        var unrelates = session.Log.Entries.Count(e => e.Op == CommandOp.Unrelate);
        Assert.Equal(2, relates);
        Assert.Equal(2, unrelates);
    }

    /// <summary>Test-only entity that records the order of session-side hook calls.</summary>
    private sealed class StubEntity : IEntity
    {
        public StubEntity(RecordId id) { Id = id; }

        public RecordId Id { get; }
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
        public void Hydrate(JsonElement json, SurrealSession session) => Calls.Add("Hydrate");
        public void OnDeleting()                       => Calls.Add("OnDeleting");
    }

    /// <summary>Test-only relation kind so the typed Relate&lt;TKind&gt; surface can be exercised without the generator.</summary>
    private sealed class StubKind : IRelationKind
    {
        public static string EdgeName => "stub_edge";
    }
}
