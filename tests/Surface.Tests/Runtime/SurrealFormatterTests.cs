using Surface.Runtime;
using Xunit;

namespace Surface.Tests.Runtime;

public sealed class SurrealFormatterTests
{
    [Theory]
    [InlineData("designs", "designs")]
    [InlineData("user_stories", "user_stories")]
    [InlineData("_internal", "_internal")]
    [InlineData("Mixed_Case_Allowed", "Mixed_Case_Allowed")]
    public void Identifier_Accepts_SafeNames(string input, string expected)
        => Assert.Equal(expected, SurrealFormatter.Identifier(input));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("123leading_digit")]
    [InlineData("has-dash")]
    [InlineData("has space")]
    [InlineData("has;semicolon")]
    [InlineData("has\"quote")]
    public void Identifier_Throws_OnUnsafeNames(string input)
        => Assert.Throws<SurrealFormatException>(() => SurrealFormatter.Identifier(input));

    [Fact]
    public void RecordId_BareForm_ForAlphanumericValues()
    {
        var id = new RecordId("designs", "01HX7AF5");
        Assert.Equal("designs:01HX7AF5", SurrealFormatter.RecordId(id));
    }

    [Theory]
    [InlineData("has-dash")]
    [InlineData("has space")]
    [InlineData("has:colon")]
    [InlineData("has;semicolon")]
    [InlineData("\"double quoted\"")]
    public void RecordId_AngleBracketForm_ForUnsafeValues(string value)
    {
        var id = new RecordId("designs", value);
        var formatted = SurrealFormatter.RecordId(id);
        Assert.Equal($"designs:⟨{value}⟩", formatted);
    }

    [Fact]
    public void RecordId_Throws_WhenValueContains_AngleBracketCloser()
    {
        // No two-level escape; reject up front rather than emit malformed SQL.
        var id = new RecordId("t", "evil⟩drop_table");
        Assert.Throws<SurrealFormatException>(() => SurrealFormatter.RecordId(id));
    }

    [Fact]
    public void RecordId_Throws_OnInvalidTableName()
    {
        var id = new RecordId("123-bad", "ok");
        Assert.Throws<SurrealFormatException>(() => SurrealFormatter.RecordId(id));
    }

    [Fact]
    public void StringLiteral_Escapes_BackslashesAndQuotes()
    {
        Assert.Equal("\"hello \\\"world\\\" \\\\back\"",
            SurrealFormatter.StringLiteral("hello \"world\" \\back"));
    }

    [Fact]
    public void StringLiteral_Escapes_Whitespace()
    {
        Assert.Equal("\"line1\\nline2\\ttab\\rcr\"",
            SurrealFormatter.StringLiteral("line1\nline2\ttab\rcr"));
    }
}
