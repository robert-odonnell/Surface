using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Disruptor.Surface.Runtime;

[JsonConverter(typeof(RecordIdJsonConverter))]
public readonly record struct RecordId(string Table, string Value) : IRecordId, IComparable<RecordId>
{
    public string Table { get; } = Table;
    public string Value { get; } = Value;

    /// <summary>Fresh, ulid-backed record id — used for newly minted Entities.</summary>
    public static RecordId New(string table, string? value = null)
        => new(table, value ?? Ulid.NewUlid().ToString());

    /// <summary>
    /// Deterministic, content-addressed record id. The value is derived from
    /// <paramref name="text"/> via <see cref="RecordIdFormat.HashText"/> — same input
    /// always yields the same id, so this is the natural pick when the record is
    /// keyed by something the caller knows up front (a code symbol's full name, a
    /// canonical message, …) rather than minted at create time. Optional
    /// <paramref name="prefix"/> is a single ASCII lowercase letter for visual
    /// categorisation.
    /// </summary>
    public static RecordId FromText(string table, string text, char? prefix = null)
        => new(table, RecordIdFormat.HashText(text, prefix));

    /// <summary>
    /// Deferred edge-id strategy: the <c>Value</c> is left empty as a sentinel and the
    /// commit emitter computes it at write time as <c>HashText("{source}|{Table}|{target}")</c>
    /// — same triple → same hash, so re-running the same Relate lands on the same edge
    /// row. Pair with <see cref="SurrealSession.Relate(IRecordId, IRecordId, RecordId)"/>
    /// to express idempotent edges without changing the entity-id surface.
    /// <para>
    /// Only meaningful as the <c>edge</c> argument to a relate command — using an
    /// idempotent id elsewhere will produce empty-value strings in rendered SurrealQL.
    /// </para>
    /// </summary>
    public static RecordId Idempotent(string table) => new(table, "");

    /// <summary>
    /// True iff this id is the deferred-idempotent sentinel: <see cref="Table"/> is set
    /// and <see cref="Value"/> is empty. The empty value is unambiguous — every other
    /// id form (Ulid, slug, hash) is at least one character long and is rejected by
    /// <see cref="RecordIdFormat.Validate"/> if empty.
    /// </summary>
    public bool IsIdempotent => Value.Length == 0 && !string.IsNullOrEmpty(Table);

    /// <summary>
    /// Resolve a deferred-idempotent edge id against its <paramref name="source"/> and
    /// <paramref name="target"/> endpoints. No-op for already-resolved ids; for the
    /// idempotent sentinel, returns a concrete <see cref="RecordId"/> whose value is
    /// <see cref="RecordIdFormat.HashText"/> of <c>{source}|{Table}|{target}</c>. Same
    /// triple always lands on the same row.
    /// </summary>
    public RecordId Resolve(RecordId source, RecordId target)
        => IsIdempotent
            ? new RecordId(Table, RecordIdFormat.HashText($"{source}|{Table}|{target}"))
            : this;

    /// <summary>
    /// Collapse any <see cref="IRecordId"/> (typed per-table or canonical) to the canonical
    /// <see cref="RecordId"/> form SurrealSession internals key off.
    /// </summary>
    public static RecordId From(IRecordId id) =>
        id is RecordId r ? r : new RecordId(id.Table, id.ToLiteral());

    public static RecordId Parse(string? source)
        => !TryParse(source, out var result)
            ? throw new InvalidOperationException("RecordId.Parse requires input in the format 'table:value'.")
            : result.Value;

    public static bool TryParse(string? source, [NotNullWhen(true)] out RecordId? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }
        var parts = source.Split([':'], 2);
        if (parts.Length != 2)
        {
            return false;
        }
        result = new RecordId(parts[0], parts[1]);
        return true;
    }

    public override string ToString() => $"{Table}:{Value}";

    public int CompareTo(RecordId other)
    {
        var byTable = StringComparer.Ordinal.Compare(Table, other.Table);
        return byTable != 0
            ? byTable
            : StringComparer.Ordinal.Compare(Value, other.Value);
    }

    public bool IsForTable(string table) =>
        StringComparer.Ordinal.Equals(Table, table);

    public string ToLiteral() => Value;
}

public sealed class RecordIdJsonConverter : JsonConverter<RecordId>
{
    public override RecordId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => RecordId.Parse(reader.GetString());

    public override void Write(Utf8JsonWriter writer, RecordId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
