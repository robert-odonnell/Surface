using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

public sealed class RecordIdTests
{
    [Fact]
    public void Parse_Splits_TableAndValue_OnFirstColon()
    {
        var id = RecordId.Parse("constraints:01HX");
        Assert.Equal("constraints", id.Table);
        Assert.Equal("01HX", id.Value);
    }

    [Fact]
    public void Parse_Keeps_AllSubsequentColons_InValue()
    {
        // SurrealDB allows colons in record-id values (e.g. compound keys, rare).
        // Parse splits on the FIRST colon; everything after is the value verbatim.
        var id = RecordId.Parse("compound:a:b:c");
        Assert.Equal("compound", id.Table);
        Assert.Equal("a:b:c", id.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("missing-colon")]
    public void TryParse_RejectsMalformed_AndReturnsFalse(string? source)
    {
        Assert.False(RecordId.TryParse(source, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void Parse_Throws_OnMalformedInput()
    {
        Assert.Throws<InvalidOperationException>(() => RecordId.Parse("no-colon"));
    }

    [Fact]
    public void ToString_RoundTrips_ThroughParse()
    {
        var original = new RecordId("designs", "01ABC");
        var roundTripped = RecordId.Parse(original.ToString());
        Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void From_PassThrough_ForCanonicalRecordId()
    {
        var canonical = new RecordId("epics", "x");
        // Concrete struct passed through IRecordId — From should not allocate a new one,
        // and the value should compare equal.
        IRecordId boxed = canonical;
        Assert.Equal(canonical, RecordId.From(boxed));
    }

    [Fact]
    public void New_GeneratesUlidValue_ForGivenTable()
    {
        var id = RecordId.New("constraints");
        Assert.Equal("constraints", id.Table);
        // Ulids are 26 chars Crockford base32 — sanity check the shape rather than the
        // literal value, since it's nondeterministic.
        Assert.Equal(26, id.Value.Length);
    }

    [Fact]
    public void IsForTable_IsCaseSensitive()
    {
        var id = new RecordId("designs", "v");
        Assert.True(id.IsForTable("designs"));
        Assert.False(id.IsForTable("Designs"));
    }
}
