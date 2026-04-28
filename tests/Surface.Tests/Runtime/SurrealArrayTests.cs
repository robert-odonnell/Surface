using Surface.Runtime;
using Xunit;

namespace Surface.Tests.Runtime;

/// <summary>
/// Regression coverage for the pre-bind data-loss bug: a <see cref="SurrealArray{T}"/>
/// mutated before its owner is tracked must replay through the owner's <see cref="IEntity.Flush"/>
/// — i.e. the writer callback the generator emits is what wires this up, NOT a direct
/// reach into <c>owner.Session</c>.
/// </summary>
public sealed class SurrealArrayTests
{
    [Fact]
    public void Mutation_Invokes_TheWriterCallback_WithLiveListReference()
    {
        // The writer receives the SAME List<T> reference on every call — that lets the
        // dirty batch hold a single live reference whose subsequent mutations are picked
        // up automatically at commit time without re-recording.
        var captures = new List<List<int>>();
        var array = new SurrealArray<int>(initial: null, items => captures.Add(items));

        array.Add(1);
        array.Add(2);

        Assert.Equal(2, captures.Count);
        Assert.Same(captures[0], captures[1]);
        Assert.Equal(new[] { 1, 2 }, captures[1]);
    }

    [Fact]
    public void Initial_Items_Are_Loaded_Without_FiringWriter()
    {
        var fired = false;
        var array = new SurrealArray<int>(initial: new[] { 1, 2, 3 }, _ => fired = true);

        Assert.Equal(3, array.Count);
        Assert.False(fired);
    }

    [Fact]
    public void PreBind_Mutations_Replay_Through_Buffer_AndArrive_AtSession()
    {
        // Simulates the entity-side wiring: pre-bind writes accumulate in a per-entity
        // buffer (here a Dictionary), and a single Flush call replays them. This mirrors
        // what the generator's __WriteField + IEntity.Flush emit.
        var pendingWrites = new Dictionary<string, object?>();
        SurrealSession? boundSession = null;

        // Writer captures into the buffer when unbound; routes to the session post-bind.
        Action<List<int>> writer = items =>
        {
            if (boundSession is null) pendingWrites["scenarios"] = items;
            else boundSession.SetField(new RecordId("ac", "x"), "scenarios", items);
        };

        var array = new SurrealArray<int>(initial: null, writer);

        // Pre-bind mutations — the bug used to drop these on the floor.
        array.Add(1);
        array.Add(2);

        Assert.Single(pendingWrites);
        Assert.Equal(new[] { 1, 2 }, (List<int>)pendingWrites["scenarios"]!);

        // Now bind and flush — the buffered writer payload lands in the session.
        var session = new SurrealSession();
        boundSession = session;
        foreach (var (field, value) in pendingWrites)
        {
            session.SetField(new RecordId("ac", "x"), field, value);
        }

        var setCmd = session.Log.Entries.Single(e => e.Op == CommandOp.Set);
        Assert.Equal("scenarios", setCmd.Key);
        Assert.IsAssignableFrom<List<int>>(setCmd.Value);
    }

    [Fact]
    public void Move_Reorders_AndFires_Writer()
    {
        var fireCount = 0;
        var array = new SurrealArray<string>(
            initial: new[] { "a", "b", "c" },
            items => fireCount++);

        array.Move(2, 0);

        Assert.Equal(new[] { "c", "a", "b" }, array.ToArray());
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void Move_NoOp_When_FromEqualsTo()
    {
        var fireCount = 0;
        var array = new SurrealArray<string>(
            initial: new[] { "a", "b" },
            _ => fireCount++);

        array.Move(0, 0);
        Assert.Equal(0, fireCount);
    }

    [Fact]
    public void Clear_NoOp_OnEmpty_DoesNotFire_Writer()
    {
        var fireCount = 0;
        var array = new SurrealArray<int>(initial: null, _ => fireCount++);

        array.Clear();
        Assert.Equal(0, fireCount);
    }
}
