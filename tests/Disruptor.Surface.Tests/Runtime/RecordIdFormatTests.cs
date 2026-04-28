using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// The id-value validator pins a hard rule: only Ulid stringifications (26 chars of
/// uppercase Crockford Base32) or short lower_snake_case slugs are accepted. Everything
/// else throws <see cref="System.FormatException"/>. This test covers the boundaries —
/// what makes it through and what gets rejected.
/// </summary>
public sealed class RecordIdFormatTests
{
    [Theory]
    [InlineData("01HXY7Z8K2N5VQDM9PE3WBFTGR")] // 26-char Ulid stringification
    [InlineData("00000000000000000000000000")] // boundary: all zeros, still 26 chars
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZ")] // boundary: all uppercase letters
    public void Validates_UlidForm(string value)
    {
        Assert.Equal(value, RecordIdFormat.Validate(value));
        Assert.True(RecordIdFormat.IsValid(value));
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("a")]                                  // single char minimum
    [InlineData("default_workspace")]
    [InlineData("config_v2")]
    [InlineData("a1")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]   // 32-char max
    public void Validates_ShortLowerSnakeCaseSlug(string value)
    {
        Assert.Equal(value, RecordIdFormat.Validate(value));
        Assert.True(RecordIdFormat.IsValid(value));
    }

    [Theory]
    [InlineData("")]                                    // empty
    [InlineData(" ")]                                   // whitespace
    [InlineData("MixedCase")]                           // uppercase + lowercase = neither form
    [InlineData("UPPER")]                               // uppercase but not 26 chars (not Ulid)
    [InlineData("primary-key")]                         // hyphens not allowed
    [InlineData("primary key")]                         // spaces not allowed
    [InlineData("1numeric")]                            // can't start with digit
    [InlineData("_underscore_first")]                   // can't start with underscore
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]   // 33 chars — exceeds slug cap
    [InlineData("01HXY7Z8K2N5VQDM9PE3WBFTG")]           // 25 chars — too short for Ulid
    [InlineData("01HXY7Z8K2N5VQDM9PE3WBFTGRX")]         // 27 chars — too long for Ulid
    [InlineData("\"quoted\"")]                          // quoted-string ids are rejected on principle
    public void Rejects_AnythingElse(string value)
    {
        Assert.Throws<FormatException>(() => RecordIdFormat.Validate(value));
        Assert.False(RecordIdFormat.IsValid(value));
    }

    [Fact]
    public void Rejects_Null()
    {
        Assert.Throws<FormatException>(() => RecordIdFormat.Validate(null!));
        Assert.False(RecordIdFormat.IsValid(null));
    }

    [Fact]
    public void MaxSlugLength_Is_32()
    {
        Assert.Equal(32, RecordIdFormat.MaxSlugLength);
    }
}
