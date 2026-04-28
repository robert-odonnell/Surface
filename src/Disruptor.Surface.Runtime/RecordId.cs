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

    /// <summary>Well-known root slot for a table — <c>{table}:root</c>.</summary>
    public static RecordId Root(string table) => new(table, "root");

    /// <summary>Well-known named slot on a table, e.g. <c>{table}:next</c> for staged roots during COW.</summary>
    public static RecordId Slot(string table, string name) => new(table, name);

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
