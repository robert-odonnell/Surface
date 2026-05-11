using System.Text.RegularExpressions;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Identifier-validation helper for SurrealQL text. The library is end-to-end typed-CBOR
/// — values flow as <c>SurrealValue</c> bindings via <c>tx.QueryAsync(sql, bindings)</c>
/// and the SDK's typed CRUD methods, so SurrealQL strings only carry table / field /
/// edge identifiers (and DDL). This class is the chokepoint that validates those
/// identifiers against a strict regex so a misbehaving emitter can't smuggle in
/// unexpected characters.
/// </summary>
public static partial class SurrealFormatter
{
    private static readonly Regex IdentifierPattern = IdentifierPatternRegex();

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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled)] private static partial Regex IdentifierPatternRegex();
}

public sealed class SurrealFormatException(string message) : Exception(message);
