using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Coverage for <see cref="SurrealArray{T}"/>'s mutation-aware contract: every mutation
/// invokes the writer callback with the live <see cref="List{T}"/> reference.
/// Generator-emitted code passes a no-op writer under the pure-setter model — Save
/// reads the list at dispatch time — but the wrapper itself still honours whatever
/// writer the constructor receives.
/// </summary>
public sealed class SurrealArrayTests
{
    [Fact]
    public void Mutation_Invokes_TheWriterCallback_WithLiveListReference()
    {
        // The writer receives the SAME List<T> reference on every call — that lets a
        // caller hold a single live reference whose subsequent mutations are visible
        // without re-querying the wrapper.
        var captures = new List<List<int>>();
        _ = new SurrealArray<int>(initial: null, items => captures.Add(items))
        {
            1,
            2
        };

        Assert.Equal(2, captures.Count);
        Assert.Same(captures[0], captures[1]);
        Assert.Equal(new[] { 1, 2 }, captures[1]);
    }

    [Fact]
    public void Initial_Items_Are_Loaded_Without_FiringWriter()
    {
        var fired = false;
        var array = new SurrealArray<int>(initial: [1, 2, 3], _ => fired = true);

        Assert.Equal(3, array.Count);
        Assert.False(fired);
    }

    [Fact]
    public void Move_Reorders_AndFires_Writer()
    {
        var fireCount = 0;
        var array = new SurrealArray<string>(
            initial: ["a", "b", "c"],
            _ => fireCount++);

        array.Move(2, 0);

        Assert.Equal(new[] { "c", "a", "b" }, array.ToArray());
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void Move_NoOp_When_FromEqualsTo()
    {
        var fireCount = 0;
        var array = new SurrealArray<string>(
            initial: ["a", "b"],
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
