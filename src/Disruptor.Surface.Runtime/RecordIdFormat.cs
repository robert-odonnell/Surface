namespace Disruptor.Surface.Runtime;

/// <summary>
/// Single-source-of-truth validator for typed-id <c>Value</c> strings. Two and only two
/// forms are accepted:
/// <list type="bullet">
///   <item><b>Ulid</b> — exactly 26 characters of Crockford Base32 (<c>[A-Z0-9]</c>); what
///   <c>Ulid.ToString()</c> produces. The default mint path.</item>
///   <item><b>Lower-snake-case slug</b> — starts with <c>[a-z]</c>, followed by
///   <c>[a-z0-9_]*</c>, max 32 characters. The opt-in path for stable-named records
///   (singletons, config rows, well-known references). Short on purpose — if you're
///   reaching for a 30-character slug, you probably want a Ulid.</item>
/// </list>
/// Everything else throws <see cref="FormatException"/>. No quoted-string ids, no
/// uppercase identifiers, no special characters — Surreal record-id semantics treat ids
/// as records, not free-form strings, and we hold that line at the typed-id ctor.
/// </summary>
public static class RecordIdFormat
{
    /// <summary>Maximum length for the lower_snake_case slug form.</summary>
    public const int MaxSlugLength = 32;

    /// <summary>Validates <paramref name="value"/> and returns it. Throws if invalid.</summary>
    public static string Validate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new FormatException("Record id value cannot be null or empty.");
        }

        if (IsUlidForm(value) || IsSlugForm(value))
        {
            return value;
        }

        throw new FormatException(
            $"Record id value '{value}' must be either a 26-character Ulid (uppercase Crockford Base32) " +
            $"or a lower_snake_case slug starting with [a-z] containing [a-z0-9_] (max {MaxSlugLength} chars). " +
            "Quoted-string ids and other free-form values are not supported.");
    }

    /// <summary>True iff <paramref name="value"/> would pass <see cref="Validate"/>.</summary>
    public static bool IsValid(string? value)
        => !string.IsNullOrEmpty(value) && (IsUlidForm(value) || IsSlugForm(value));

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
