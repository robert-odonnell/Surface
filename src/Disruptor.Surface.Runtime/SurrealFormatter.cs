using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Boring-as-sin SurrealQL formatting helpers. Every code path that builds SurrealQL
/// text — generator-emitted loaders, the commit emitter, the HTTP client's
/// <c>LET $...</c> rendering — funnels through here so that record values, identifiers,
/// and string literals are quoted consistently and dangerous content fails fast instead
/// of producing malformed SQL or worse.
/// <para>
/// Generator-emitted identifiers (table names, field names) come from snake-cased C#
/// member names — they're trusted but still validated against a strict regex so a
/// misbehaving emitter can't smuggle in unexpected characters. Record id VALUES are
/// always Ulid stringifications today, but every value flowing through here is still
/// routed through Surreal's <c>⟨value⟩</c> escape form when it contains anything outside
/// <c>[A-Za-z0-9_]</c> — defence in depth against future id types or stale call sites.
/// </para>
/// </summary>
public static partial class SurrealFormatter
{
    private static readonly Regex IdentifierPattern = IdentifierPatternRegex();
    private static readonly Regex BareValuePattern = BareValuePatternRegex();

    /// <summary>
    /// Render a binding value as a SurrealQL literal. Strings/dates/enums route through
    /// <see cref="SurrealFormatter.StringLiteral"/> for proper escaping; record ids use
    /// <see cref="SurrealFormatter.RecordId"/> for the bare-or-bracketed form;
    /// enumerables become array literals; everything else falls through to JSON for
    /// generic objects (rare, mostly diagnostics).
    /// </summary>
    public static string RenderSurrealLiteral(this object? value) => value switch
    {
        null => "NONE",
        bool b => b ? "true" : "false",
        sbyte or byte or short or ushort or int or uint or long or ulong => value.ToString()!,
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        string s => s.StringLiteral(),
        RecordId rid => rid.RecordId(),
        IEntity v => v.Id.RecordId(),
        IRecordId r => Runtime.RecordId.From(r).RecordId(),
        Guid g => g.ToString().StringLiteral(),
        Ulid u => u.ToString().StringLiteral(),
        // DateTime / DateTimeOffset render as SurrealQL datetime literals (`d"…"`), not
        // plain strings. SchemaEmitter maps these CLR types to `TYPE datetime`; a string
        // assignment to a schemafull datetime field is rejected by SurrealDB. The
        // d-prefix tells the parser to coerce the inner ISO 8601 to datetime so writes
        // and predicate operands both land correctly. Round-trip "O" gives sub-second
        // precision; SurrealDB accepts both `Z` (UTC) and offset suffix forms.
        DateTime dt => "d" + dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture).StringLiteral(),
        DateTimeOffset dto => "d" + dto.ToString("O", CultureInfo.InvariantCulture).StringLiteral(),
        Enum e => e.ToString().StringLiteral(),
        IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Select(RenderSurrealLiteral)) + "]",
        _ => JsonSerializer.Serialize(value, SurrealJson.SerializerOptions),
    };

    /// <summary>
    /// Validates a generator-emitted identifier (table name, field name, edge name) and
    /// returns it. Throws if the identifier doesn't match the strict regex — the emitter
    /// is the only legitimate caller and a failure means an upstream bug, not user input.
    /// </summary>
    public static string Identifier(this string name)
    {
        if (string.IsNullOrEmpty(name) || !IdentifierPattern.IsMatch(name))
        {
            throw new SurrealFormatException($"Invalid SurrealQL identifier: '{name}'. Identifiers must match {IdentifierPattern}.");
        }
        return name;
    }

    /// <summary>
    /// Formats a <see cref="RecordId"/> as a SurrealQL record literal. Bare
    /// <c>table:value</c> when the value is alphanumeric/underscore; otherwise
    /// <c>table:⟨value⟩</c> using Surreal's angle-bracket escape. Throws if the value
    /// contains the closing-bracket character itself (no two-level escape exists).
    /// </summary>
    public static string RecordId(this RecordId id)
    {
        var table = id.Table.Identifier();
        var value = id.Value;

        if (BareValuePattern.IsMatch(value))
        {
            return $"{table}:{value}";
        }

        if (value.Contains('⟩'))   // RIGHT MATHEMATICAL ANGLE BRACKET
        {
            throw new SurrealFormatException($"Record id value '{value}' contains the SurrealQL escape close-bracket '⟩' and cannot be safely formatted.");
        }
        return $"{table}:⟨{value}⟩";
    }

    /// <summary>
    /// Formats an arbitrary string as a SurrealQL double-quoted string literal with
    /// standard backslash escapes. Used by the <c>LET $...</c> renderer for scalar
    /// values where Surreal expects a string literal.
    /// </summary>
    public static string StringLiteral(this string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled)] private static partial Regex IdentifierPatternRegex();
    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.Compiled)] private static partial Regex BareValuePatternRegex();
}

public sealed class SurrealFormatException(string message) : Exception(message);
