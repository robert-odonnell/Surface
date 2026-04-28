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
public static class SurrealFormatter
{
    private static readonly Regex IdentifierPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex BareValuePattern = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// Validates a generator-emitted identifier (table name, field name, edge name) and
    /// returns it. Throws if the identifier doesn't match the strict regex — the emitter
    /// is the only legitimate caller and a failure means an upstream bug, not user input.
    /// </summary>
    public static string Identifier(string name)
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
    public static string RecordId(RecordId id)
    {
        var table = Identifier(id.Table);
        var value = id.Value ?? string.Empty;

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
    public static string StringLiteral(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}

public sealed class SurrealFormatException(string message) : Exception(message);
