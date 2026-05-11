using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// SurrealFormatter only owns identifier validation now — values flow as typed-CBOR
/// SurrealValue bindings end-to-end, so RenderSurrealLiteral / StringLiteral /
/// RecordId formatter all retired with the typed-CBOR pivot. Identifier() stays
/// because schema DDL and identifier positions in queries (table / field / edge
/// names) are still text.
/// </summary>
public sealed class SurrealFormatterTests
{
    [Theory]
    [InlineData("designs", "designs")]
    [InlineData("user_stories", "user_stories")]
    [InlineData("_internal", "_internal")]
    [InlineData("Mixed_Case_Allowed", "Mixed_Case_Allowed")]
    public void Identifier_Accepts_SafeNames(string input, string expected)
        => Assert.Equal(expected, input.Identifier());

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("123leading_digit")]
    [InlineData("has-dash")]
    [InlineData("has space")]
    [InlineData("has;semicolon")]
    [InlineData("has\"quote")]
    public void Identifier_Throws_OnUnsafeNames(string input)
        => Assert.Throws<SurrealFormatException>(input.Identifier);
}
