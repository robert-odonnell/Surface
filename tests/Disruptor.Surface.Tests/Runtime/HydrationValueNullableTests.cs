using Disruptor.Surface.Runtime;
using Disruptor.Surreal.Values;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Round-trip behaviour for nullable scalar hydration. Regression: pre-preview.48 the
/// emitted Hydrate body stripped Nullable&lt;T&gt; before calling ReadOrDefault, so a
/// persisted null hydrated as <c>0</c> / <c>""</c> / <c>MinValue</c> / <c>false</c> —
/// silently corrupting the null distinction. Post-fix: nullable scalars round-trip
/// null cleanly; non-nullable string keeps the empty-string fallback that matches the
/// schema's <c>DEFAULT ""</c>.
/// </summary>
public sealed class HydrationValueNullableTests
{
    [Fact]
    public void ReadOrDefault_NullableInt_FromNullValue_ReturnsNull()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["count"] = SurrealValue.Null });
        Assert.Null(HydrationValue.ReadOrDefault<int?>(obj, "count"));
    }

    [Fact]
    public void ReadOrDefault_NullableInt_FromMissingField_ReturnsNull()
    {
        var obj = new SurrealObjectValue(new SurrealObject());
        Assert.Null(HydrationValue.ReadOrDefault<int?>(obj, "count"));
    }

    [Fact]
    public void ReadOrDefault_NullableInt_FromValue_ReturnsValue()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["count"] = 42 });
        Assert.Equal(42, HydrationValue.ReadOrDefault<int?>(obj, "count"));
    }

    [Fact]
    public void ReadOrDefault_NullableDateTime_FromNullValue_ReturnsNull()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["at"] = SurrealValue.Null });
        Assert.Null(HydrationValue.ReadOrDefault<DateTime?>(obj, "at"));
    }

    [Fact]
    public void ReadOrDefault_NullableBool_FromNullValue_ReturnsNull()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["flag"] = SurrealValue.Null });
        Assert.Null(HydrationValue.ReadOrDefault<bool?>(obj, "flag"));
    }

    [Fact]
    public void ReadOrDefault_String_FromNullValue_ReturnsNull()
    {
        // The new path nullable string takes (post-preview.48): ReadOrDefault<string>
        // returns null on SurrealNullValue — distinguishing "DB has null" from "DB has empty".
        var obj = new SurrealObjectValue(new SurrealObject { ["title"] = SurrealValue.Null });
        Assert.Null(HydrationValue.ReadOrDefault<string>(obj, "title"));
    }

    [Fact]
    public void ReadOrDefault_String_FromEmpty_ReturnsEmpty()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["title"] = "" });
        Assert.Equal("", HydrationValue.ReadOrDefault<string>(obj, "title"));
    }

    [Fact]
    public void ReadString_NonNullableStringPath_StillReturnsEmptyOnNull()
    {
        // The non-nullable string emit path stays on ReadString — schema emits DEFAULT ""
        // for non-nullable strings, so missing/null in the response collapses to "" to
        // match. Regression-guard so a refactor doesn't accidentally route non-nullable
        // strings through ReadOrDefault.
        var obj = new SurrealObjectValue(new SurrealObject { ["title"] = SurrealValue.Null });
        Assert.Equal("", HydrationValue.ReadString(obj, "title"));
    }
}
