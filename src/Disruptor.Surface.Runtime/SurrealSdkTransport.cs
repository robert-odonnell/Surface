using System.Buffers;
using System.Text.Json;
using Disruptor.Surreal;
using Disruptor.Surreal.Values;
using SdkSurreal = Disruptor.Surreal.Surreal;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Transitional bridge: implements the legacy <see cref="ISurrealTransport"/> over the
/// <c>Disruptor.Surreal</c> SDK so the existing JsonElement-based hydration path keeps
/// working while the write side migrates to use <see cref="SdkSurreal"/> directly.
/// <para>
/// This class exists because hydration (HydrationJson, SurrealResultSet, every emitted
/// <c>IEntity.Hydrate</c>) still consumes <see cref="JsonElement"/>; the SDK responds in
/// its native <see cref="Value"/> tree. We project Value → wire-shape JSON
/// (<c>[{status, result, time}, …]</c>) so callers don't change. When hydration migrates
/// to consume <see cref="Value"/> directly (alongside Step 4 of the unpainting plan),
/// this whole class gets deleted.
/// </para>
/// </summary>
public sealed class SurrealSdkTransport(SdkSurreal db, bool ownsClient = false) : ISurrealTransport
{
    /// <summary>The underlying SDK client. Exposed so write-side code can open transactions directly.</summary>
    public SdkSurreal Db { get; } = db;

    /// <inheritdoc />
    public async Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        var response = await Db.QueryAsync(sql, bindings: null, ct).ConfigureAwait(false);
        return ProjectToJson(response);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (ownsClient) await Db.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Projects a <see cref="QueryResponse"/> into the SurrealDB-style HTTP-API envelope
    /// — <c>[{status, result, time}, …]</c> — that the runtime's hydration consumers
    /// expect. Errors in any statement throw; success path produces the array.
    /// </summary>
    internal static JsonDocument ProjectToJson(QueryResponse response)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < response.Count; i++)
            {
                var stmt = response.Statements[i];
                writer.WriteStartObject();
                if (stmt.IsError)
                {
                    // Match the Rust SDK's behaviour and the existing parser's expectations:
                    // surface statement errors as exceptions, not as ERR rows in the envelope.
                    throw new InvalidOperationException(
                        $"Statement {i} failed: {stmt.ErrorMessage ?? "unknown error"}");
                }
                writer.WriteString("status", "OK");
                writer.WritePropertyName("result");
                WriteValue(writer, stmt.Result ?? Value.None);
                if (stmt.ExecutionTime is { } ts)
                {
                    writer.WriteString("time", FormatDuration(ts));
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    /// <summary>
    /// Recursively project a <see cref="Value"/> into JSON. Targets the shape the
    /// runtime's <see cref="HydrationJson"/> helpers consume: RecordIds as flat
    /// <c>"table:value"</c> strings, datetimes as ISO-8601, decimals as JSON numbers
    /// (lossless for the values SurrealDB returns from typed schemas).
    /// </summary>
    private static void WriteValue(Utf8JsonWriter w, Value v)
    {
        switch (v)
        {
            case NoneValue:
            case NullValue:
                w.WriteNullValue();
                break;
            case BoolValue b:
                w.WriteBooleanValue(b.Value);
                break;
            case NumberValue n:
                switch (n.Number.Kind)
                {
                    case NumberKind.Int: w.WriteNumberValue(n.Number.AsInt()); break;
                    case NumberKind.Float: w.WriteNumberValue(n.Number.AsFloat()); break;
                    case NumberKind.Decimal: w.WriteNumberValue(n.Number.AsDecimal()); break;
                }
                break;
            case StringValue s:
                w.WriteStringValue(s.Value);
                break;
            case BytesValue bytes:
                w.WriteBase64StringValue(bytes.Value.Span);
                break;
            case DatetimeValue d:
                // ISO-8601 round-trip — matches SurrealDB's HTTP-API surface.
                w.WriteStringValue(d.Datetime.ToDateTimeOffset().ToString("o"));
                break;
            case DurationValue dur:
                w.WriteStringValue(dur.Duration.ToString());
                break;
            case UuidValue u:
                w.WriteStringValue(u.Value.ToString());
                break;
            case RecordIdValue r:
                // Flat "table:value" — the canonical hydration shape (see HydrationJson.ReadRecordId).
                // Surface only uses string keys; integer/uuid/composite keys aren't part of the
                // model surface we generate, so format only the kinds we need.
                w.WriteStringValue($"{r.RecordId.Table.Name}:{FormatKey(r.RecordId.Key)}");
                break;
            case TableValue t:
                w.WriteStringValue(t.Table.Name);
                break;
            case ArrayValue a:
                w.WriteStartArray();
                foreach (var item in a.Array) WriteValue(w, item);
                w.WriteEndArray();
                break;
            case ObjectValue o:
                w.WriteStartObject();
                foreach (var (k, val) in o.Object)
                {
                    w.WritePropertyName(k);
                    WriteValue(w, val);
                }
                w.WriteEndObject();
                break;
            default:
                // Set / Range / File / Geometry — not used by hydration today.
                throw new NotSupportedException(
                    $"Value kind {v.Kind} cannot be projected to JSON by SurrealSdkTransport. " +
                    "Add the case here when a schema needs it.");
        }
    }

    /// <summary>Format a <see cref="RecordIdKey"/> in its bare wire form for the "table:value" shape hydration expects.</summary>
    private static string FormatKey(RecordIdKey key) => key switch
    {
        StringRecordIdKey s => s.Value,
        IntegerRecordIdKey i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        UuidRecordIdKey u => u.Value.ToString("D"),
        _ => throw new NotSupportedException(
            $"RecordIdKey kind {key.Kind} is not used by Disruptor.Surface and isn't supported by SurrealSdkTransport's JSON projection."),
    };

    /// <summary>Format a TimeSpan back into SurrealDB's "1.234ms" / "12s" Debug-format.</summary>
    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds >= 1) return $"{ts.TotalSeconds:0.###}s";
        if (ts.TotalMilliseconds >= 1) return $"{ts.TotalMilliseconds:0.###}ms";
        return $"{ts.TotalMilliseconds * 1000:0.###}us";
    }
}
