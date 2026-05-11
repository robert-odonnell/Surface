namespace Disruptor.Surface.Runtime;

/// <summary>
/// Append-only diagnostic log of model commands recorded by sync write methods on
/// <see cref="SurrealSession"/>. Useful for tests asserting "what intent did the
/// session capture?" and for application-side telemetry; the log is not consumed by
/// the per-entity Save dispatch path (which reads the entity's current state directly
/// instead of replaying recorded commands), nor by the typed-CBOR Relate/Unrelate/Delete
/// dispatch paths (which talk to the SDK directly).
/// </summary>
public sealed class CommandLog
{
    private readonly List<Command> entries = [];

    public IReadOnlyList<Command> Entries => entries;
    public int Count => entries.Count;

    public void Append(Command c) => entries.Add(c);
    public void Clear() => entries.Clear();
}

/// <summary>The kind of session-level intent recorded in <see cref="CommandLog"/>.</summary>
public enum CommandOp
{
    /// <summary>A fresh entity was registered with <c>session.Track</c>.</summary>
    Create,
    /// <summary>A record was deleted via <c>SurrealSession.DeleteAsync</c>.</summary>
    Delete,
    /// <summary>A typed edge was created via <c>SurrealSession.RelateAsync</c> (or sync <c>Relate</c>).</summary>
    Relate,
    /// <summary>A typed edge was removed via <c>SurrealSession.UnrelateAsync</c> (or sync <c>Unrelate</c>).</summary>
    Unrelate,
}

/// <summary>
/// Atomic SurrealDB intent — diagnostic record only. The typed-CBOR dispatch paths talk
/// to the SDK directly; this captures what intent the session observed for tests and
/// telemetry.
/// </summary>
/// <param name="Op">The kind of operation.</param>
/// <param name="Target">For Create/Delete: the record. For Relate/Unrelate: the <c>in</c> endpoint.</param>
/// <param name="Key">For Relate/Unrelate: the edge-table name. Null otherwise.</param>
/// <param name="Value">For Relate/Unrelate: the <c>out</c> endpoint <see cref="RecordId"/> (nullable for bulk Unrelate).</param>
/// <param name="EdgeContent">For Relate only: optional payload dict carried by the edge.</param>
/// <param name="Edge">For Relate only: the full edge id, carrying the strategy (Idempotent / Slug / Random).</param>
public readonly record struct Command(
    CommandOp Op,
    RecordId Target,
    string? Key = null,
    object? Value = null,
    IReadOnlyDictionary<string, object?>? EdgeContent = null,
    RecordId Edge = default)
{
    public static Command Create(RecordId target) => new(CommandOp.Create, target);

    public static Command Delete(RecordId target) => new(CommandOp.Delete, target);

    public static Command Relate(RecordId source, RecordId edge, RecordId target, IReadOnlyDictionary<string, object?>? content = null) =>
        new(CommandOp.Relate, source, Key: edge.Table, Value: target, EdgeContent: Freeze(content), Edge: edge);

    /// <summary>
    /// Edge removal. At least one of <paramref name="source"/> / <paramref name="target"/>
    /// must be non-null. Both non-null targets a single edge; one-side-null is the bulk
    /// form (every matching edge of <paramref name="edgeTable"/>).
    /// </summary>
    public static Command Unrelate(RecordId? source, string edgeTable, RecordId? target)
    {
        if (source is null && target is null)
        {
            throw new ArgumentException(
                "Unrelate requires at least one of source or target to be non-null.");
        }
        // `default(RecordId)` (Table = null, Value = null) is the in-band "no source"
        // sentinel — Command.Target is non-nullable so we can't store null directly.
        return new(CommandOp.Unrelate, source ?? default, edgeTable, Value: target);
    }

    private static Dictionary<string, object?>? Freeze(IEnumerable<KeyValuePair<string, object?>>? source) =>
        source is null ? null : new Dictionary<string, object?>(source, StringComparer.Ordinal);
}
