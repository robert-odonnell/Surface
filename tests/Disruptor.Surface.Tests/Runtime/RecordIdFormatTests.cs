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

    [Theory]
    [InlineData("0123456789abcdef01234567")] // bare 24-char hex starting with digit (would fail slug check)
    [InlineData("abcdef0123456789abcdef01")] // bare 24-char hex starting with letter
    [InlineData("a_0123456789abcdef01234567")] // prefixed: a_ + 24 hex
    [InlineData("z_ffffffffffffffffffffffff")] // prefixed: z_ + 24 hex (boundary)
    public void Validates_HashForm(string value)
    {
        Assert.Equal(value, RecordIdFormat.Validate(value));
        Assert.True(RecordIdFormat.IsValid(value));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456")]    // 23 chars — short
    [InlineData("0123456789abcdef012345678")]  // 25 chars — between bare and prefixed
    [InlineData("0123456789ABCDEF01234567")]   // uppercase hex — must be lowercase
    [InlineData("a-0123456789abcdef01234567")] // hyphen separator instead of _
    [InlineData("A_0123456789abcdef01234567")] // uppercase prefix
    // Note: `aa_<24-hex>` etc. happen to also satisfy the slug form (27 chars,
    // [a-z0-9_]), so they validate as slugs even though they're not legal hash
    // form. The two forms genuinely overlap there; that's by design.
    public void Rejects_MalformedHashForms(string value)
    {
        Assert.Throws<FormatException>(() => RecordIdFormat.Validate(value));
        Assert.False(RecordIdFormat.IsValid(value));
    }

    [Fact]
    public void HashText_IsDeterministic_AndProducesValidValue()
    {
        // Same input always produces the same value, every machine, every run.
        // The output must satisfy Validate so it can be passed straight to a typed
        // id ctor without further checks.
        var hash1 = RecordIdFormat.HashText("Disruptor.Surface.Runtime.SurrealSession");
        var hash2 = RecordIdFormat.HashText("Disruptor.Surface.Runtime.SurrealSession");

        Assert.Equal(hash1, hash2);
        Assert.Equal(RecordIdFormat.HashLength, hash1.Length);
        Assert.Equal(hash1, RecordIdFormat.Validate(hash1));
    }

    [Fact]
    public void HashText_DifferentInputs_ProduceDifferentValues()
    {
        var a = RecordIdFormat.HashText("symbol-A");
        var b = RecordIdFormat.HashText("symbol-B");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashText_WithPrefix_PrependsCategoryLetter()
    {
        var prefixed = RecordIdFormat.HashText("Foo", prefix: 'm');
        var bare = RecordIdFormat.HashText("Foo");

        Assert.Equal(RecordIdFormat.PrefixedHashLength, prefixed.Length);
        Assert.StartsWith("m_", prefixed);
        Assert.Equal(bare, prefixed[2..]);
        Assert.Equal(prefixed, RecordIdFormat.Validate(prefixed));
    }

    [Fact]
    public void HashText_RejectsNonLowercaseAsciiPrefix()
    {
        // Single-letter [a-z] only — anything else (uppercase, digit, multi-char) breaks
        // the prefixed-hash form's strict shape.
        Assert.Throws<ArgumentException>(() => RecordIdFormat.HashText("x", prefix: 'A'));
        Assert.Throws<ArgumentException>(() => RecordIdFormat.HashText("x", prefix: '1'));
        Assert.Throws<ArgumentException>(() => RecordIdFormat.HashText("x", prefix: '_'));
    }

    [Fact]
    public void HashText_RejectsNullText()
    {
        Assert.Throws<ArgumentNullException>(() => RecordIdFormat.HashText(null!));
    }

    [Fact]
    public void HashText_AcceptsEmptyText()
    {
        // Empty input is a valid hashable string — produces a stable id (the SHA-256 of
        // zero bytes truncated). Worth pinning rather than throwing in case a caller
        // reasonably wants the canonical "no-content" id.
        var hash = RecordIdFormat.HashText(string.Empty);

        Assert.Equal(RecordIdFormat.HashLength, hash.Length);
        Assert.Equal(hash, RecordIdFormat.Validate(hash));
    }
}
