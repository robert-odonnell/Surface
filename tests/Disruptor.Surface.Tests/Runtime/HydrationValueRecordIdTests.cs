using Disruptor.Surface.Runtime;
using Disruptor.Surreal.Values;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Projection materialisation routes every <c>row.Read&lt;T&gt;(prop)</c> through
/// <see cref="HydrationValue.ReadOrDefault{T}"/>. <see cref="PredicateFactoryEmitter"/>
/// emits the <c>[Id]</c> accessor as <c>PropertyExpr&lt;{Name}Id&gt;</c>, so projections
/// of an entity's id need <see cref="HydrationValue"/> to know how to land an
/// <see cref="IRecordId"/> implementation from any of the wire forms it might appear in.
/// These tests cover the canonical <see cref="RecordId"/> path and the generator-emitted
/// <c>{Name}Id</c> path (modelled here by a hand-rolled <see cref="TestTableId"/>).
/// </summary>
public sealed class HydrationValueRecordIdTests
{
    [Fact]
    public void ReadOrDefault_RecordId_FromSurrealRecordIdValue_RoundsTripCanonical()
    {
        var obj = new SurrealObjectValue(new SurrealObject
        {
            ["id"] = new SurrealRecordIdValue(new SurrealRecordId("symbols", "01HX7AF5")),
        });

        var rid = HydrationValue.ReadOrDefault<RecordId>(obj, "id");

        Assert.Equal("symbols", rid.Table);
        Assert.Equal("01HX7AF5", rid.Value);
    }

    [Fact]
    public void ReadOrDefault_RecordId_FromStringSurrealValue_ParsesLegacyForm()
    {
        // Legacy "table:value" string form — accepted by ReadRecordId for compatibility
        // with any path that still emits the flat text shape.
        var obj = new SurrealObjectValue(new SurrealObject { ["id"] = "symbols:01HX7AF5" });

        var rid = HydrationValue.ReadOrDefault<RecordId>(obj, "id");

        Assert.Equal("symbols", rid.Table);
        Assert.Equal("01HX7AF5", rid.Value);
    }

    [Fact]
    public void ReadOrDefault_RecordId_FromInlineObject_PullsIdField()
    {
        // SurrealObjectValue with an "id" field — the inline-record form from a `field.*`
        // projection. ReadRecordId already unwraps this; ConvertValue routes through it.
        var inner = new SurrealRecordIdValue(new SurrealRecordId("symbols", "01HX7AF5"));
        var inline = new SurrealObjectValue(new SurrealObject
        {
            ["id"] = inner,
            ["name"] = "Parser",
        });
        var obj = new SurrealObjectValue(new SurrealObject { ["ref"] = inline });

        var rid = HydrationValue.ReadOrDefault<RecordId>(obj, "ref");

        Assert.Equal("symbols", rid.Table);
        Assert.Equal("01HX7AF5", rid.Value);
    }

    [Fact]
    public void ReadOrDefault_NullableRecordId_FromNullValue_ReturnsNull()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["id"] = SurrealValue.Null });

        Assert.Null(HydrationValue.ReadOrDefault<RecordId?>(obj, "id"));
    }

    [Fact]
    public void ReadOrDefault_NullableRecordId_FromMissingField_ReturnsNull()
    {
        var obj = new SurrealObjectValue(new SurrealObject());

        Assert.Null(HydrationValue.ReadOrDefault<RecordId?>(obj, "id"));
    }

    [Fact]
    public void ReadOrDefault_TypedId_ConstructsViaStringCtor()
    {
        // Stand-in for a generator-emitted `{Name}Id` readonly record struct:
        // implements IRecordId and has a (string Value) primary ctor.
        var obj = new SurrealObjectValue(new SurrealObject
        {
            ["id"] = new SurrealRecordIdValue(new SurrealRecordId("test_table", "01HX7AF5XYZ0000000000000000")),
        });

        var typed = HydrationValue.ReadOrDefault<TestTableId>(obj, "id");

        Assert.Equal("01HX7AF5XYZ0000000000000000", typed.Value);
    }

    [Fact]
    public void ReadOrDefault_TypedId_FromLegacyStringForm_ConstructsViaStringCtor()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["id"] = "test_table:01HX7AF5XYZ0000000000000000" });

        var typed = HydrationValue.ReadOrDefault<TestTableId>(obj, "id");

        Assert.Equal("01HX7AF5XYZ0000000000000000", typed.Value);
    }

    [Fact]
    public void ReadOrDefault_NullableTypedId_FromNullValue_ReturnsNull()
    {
        var obj = new SurrealObjectValue(new SurrealObject { ["id"] = SurrealValue.Null });

        Assert.Null(HydrationValue.ReadOrDefault<TestTableId?>(obj, "id"));
    }

    /// <summary>
    /// Mirrors the shape of a generator-emitted <c>{Name}Id</c>: <see cref="IRecordId"/>
    /// implementation with a single-string primary constructor. No validation here —
    /// real <c>{Name}Id</c> ctors route through <c>RecordIdFormat.Validate</c> and reject
    /// non-Ulid / non-slug values; the projection path passes the raw record-id key
    /// through, so callers depending on the validation get the same throw.
    /// </summary>
    private readonly record struct TestTableId(string Value) : IRecordId
    {
        public string Table => "test_table";
        public string ToLiteral() => Value;
    }
}
