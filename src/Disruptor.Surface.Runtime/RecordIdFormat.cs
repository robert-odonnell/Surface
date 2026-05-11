using System.Security.Cryptography;
using System.Text;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Single-source-of-truth validator for typed-id <c>Value</c> strings. Three and only
/// three forms are accepted:
/// <list type="bullet">
///   <item><b>Ulid</b> — exactly 26 characters of Crockford Base32 (<c>[A-Z0-9]</c>); what
///   <c>Ulid.ToString()</c> produces. The default mint path.</item>
///   <item><b>Lower-snake-case slug</b> — starts with <c>[a-z]</c>, followed by
///   <c>[a-z0-9_]*</c>, max 32 characters. The opt-in path for stable-named records
///   (singletons, config rows, well-known references). Short on purpose — if you're
///   reaching for a 30-character slug, you probably want a Ulid.</item>
///   <item><b>Content hash</b> — the truncated SHA-256 of arbitrary input text, in
///   one of two shapes: bare <see cref="HashLength"/> lowercase hex chars
///   (<c>[0-9a-f]</c>) or an optional single-letter category prefix
///   (<c>{a-z}_{hex}</c>, total <see cref="PrefixedHashLength"/> chars). Mint with
///   <see cref="HashText"/>. The deterministic path — same input always produces the
///   same id — for code-index / content-addressed workloads where stable ids matter
///   more than time-ordering.</item>
/// </list>
/// Everything else throws <see cref="FormatException"/>. No quoted-string ids, no
/// uppercase identifiers, no special characters — Surreal record-id semantics treat ids
/// as records, not free-form strings, and we hold that line at the typed-id ctor.
/// </summary>
public static class RecordIdFormat
{
    /// <summary>Maximum length for the lower_snake_case slug form.</summary>
    public const int MaxSlugLength = 32;

    /// <summary>Length in characters of the bare hash form: 12 bytes of SHA-256 rendered as lowercase hex.</summary>
    public const int HashLength = 24;

    /// <summary>Length in characters of the prefixed hash form: a leading <c>[a-z]_</c> followed by <see cref="HashLength"/> hex chars.</summary>
    public const int PrefixedHashLength = HashLength + 2;

    /// <summary>Bytes of SHA-256 retained for the hash form. 96 bits → birthday collision of ~one in ten billion at one billion ids; fine for code-index workloads.</summary>
    private const int HashBytes = HashLength / 2;

    /// <summary>Validates <paramref name="value"/> and returns it. Throws if invalid.</summary>
    public static string Validate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new FormatException("Record id value cannot be null or empty.");
        }

        if (IsUlidForm(value) || IsSlugForm(value) || IsHashForm(value))
        {
            return value;
        }

        throw new FormatException(
            $"Record id value '{value}' must be either a 26-character Ulid (uppercase Crockford Base32), " +
            $"a lower_snake_case slug starting with [a-z] containing [a-z0-9_] (max {MaxSlugLength} chars), " +
            $"or a content hash — bare {HashLength}-char lowercase hex or {PrefixedHashLength}-char [a-z]_<hex> " +
            "(mint with RecordIdFormat.HashText). Quoted-string ids and other free-form values are not supported.");
    }

    /// <summary>True iff <paramref name="value"/> would pass <see cref="Validate"/>.</summary>
    public static bool IsValid(string? value)
        => !string.IsNullOrEmpty(value) && (IsUlidForm(value) || IsSlugForm(value) || IsHashForm(value));

    /// <summary>
    /// Mints a deterministic content-addressed id value: SHA-256 of <paramref name="text"/>
    /// (UTF-8 bytes) truncated to <see cref="HashBytes"/> bytes, rendered as lowercase hex.
    /// Same <paramref name="text"/> always yields the same value, on every machine, across
    /// every run — useful when the id is the natural key of the record (code-index entries
    /// keyed by symbol name, log entries keyed by canonical message, etc.).
    /// <para>
    /// When <paramref name="prefix"/> is supplied, it must be a single ASCII lowercase
    /// letter <c>[a-z]</c>; the result becomes <c>{prefix}_{hex}</c> for cheap visual
    /// categorisation of ids without affecting collision behaviour. Pass <c>null</c> for
    /// the bare hex form. Uniqueness is bounded by the birthday paradox — at 96 bits,
    /// collision-safe up to ~10^14 ids.
    /// </para>
    /// </summary>
    public static string HashText(string text, char? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (prefix is { } p && p is < 'a' or > 'z')
        {
            throw new ArgumentException(
                $"Prefix '{p}' must be a single ASCII lowercase letter [a-z], or null for the bare hex form.",
                nameof(prefix));
        }

        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), digest);
        var hex = Convert.ToHexStringLower(digest[..HashBytes]);
        return prefix is null ? hex : $"{prefix}_{hex}";
    }

    /// <summary>
    /// True iff <paramref name="v"/> is the bare <c>{hex}</c> or prefixed
    /// <c>{a-z}_{hex}</c> hash form. Hex chars are strictly lowercase to match
    /// <see cref="HashText"/>'s output and to keep the form unambiguously
    /// distinguishable from the all-uppercase Ulid form.
    /// </summary>
    private static bool IsHashForm(string v)
    {
        int hexStart;
        if (v.Length == HashLength)
        {
            hexStart = 0;
        }
        else if (v.Length == PrefixedHashLength)
        {
            if (v[0] < 'a' || v[0] > 'z')
            {
                return false;
            }

            if (v[1] != '_')
            {
                return false;
            }

            hexStart = 2;
        }
        else
        {
            return false;
        }

        for (var i = hexStart; i < v.Length; i++)
        {
            var c = v[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsUlidForm(string v)
    {
        if (v.Length != 26)
        {
            return false;
        }

        foreach (var c in v)
        {
            // Crockford Base32 strictly excludes I/L/O/U, but Ulid.ToString() may emit
            // them transitionally; allow the broad [A-Z0-9] surface — the cost of being
            // slightly permissive vs reading the full Crockford spec isn't worth it.
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z')))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSlugForm(string v)
    {
        if (v.Length > MaxSlugLength)
        {
            return false;
        }

        if (v[0] < 'a' || v[0] > 'z')
        {
            return false;
        }

        for (var i = 1; i < v.Length; i++)
        {
            var c = v[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return false;
            }
        }
        return true;
    }
}
