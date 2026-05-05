using System.Globalization;
using System.Text.Json;
using Dahomey.Cbor;
using SurrealDb.Net.Models;
using SurrealDb.Net.Models.Response;

namespace Disruptor.Surface.Transport.Embedded;

/// <summary>
/// Projects a <see cref="SurrealDbResponse"/> from the SDK (which carries raw CBOR
/// bytes per statement) into the JSON-RPC envelope shape that
/// <c>Disruptor.Surface.Runtime.SurrealResultSet</c>,
/// <c>Disruptor.Surface.Runtime.HydrationJson</c>, and
/// <c>SurrealHttpClient.NormalizeStatementResults</c> already know how to consume.
/// <para>
/// Output shape per statement:
/// <code>{ "result": &lt;value&gt;, "status": "OK"|"ERR" }</code>
/// wrapped in a top-level array. Same envelope SurrealDB's <c>/rpc</c> endpoint
/// returns, so the rest of the runtime stays transport-agnostic.
/// </para>
/// <para>
/// The bridge owns its tagged-type rendering — Things become <c>"table:value"</c>
/// strings (matching the form <c>HydrationJson.ReadRecordId</c> expects), datetimes
/// become ISO 8601, decimals become string-quoted numerics. Anything else falls
/// through the default CBOR-to-CLR-primitive mapping (Dahomey.Cbor produces nested
/// <c>Dictionary&lt;object, object?&gt;</c> / <c>object?[]</c>).
/// </para>
/// </summary>
internal static class CborJsonProjection
{
    /// <summary>
    /// Build a <see cref="JsonDocument"/> matching the JSON-RPC response envelope
    /// from <paramref name="response"/>. Caller owns the document and disposes it.
    /// </summary>
    public static JsonDocument BuildEnvelope(SurrealDbResponse response, CborOptions cborOptions)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(cborOptions);

        using var stream = new MemoryStream(capacity: 256);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var result in response)
            {
                WriteStatement(writer, result, cborOptions);
            }
            writer.WriteEndArray();
        }
        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static void WriteStatement(Utf8JsonWriter writer, ISurrealDbResult result, CborOptions cborOptions)
    {
        writer.WriteStartObject();

        if (result is SurrealDbOkResult ok)
        {
            writer.WritePropertyName("result");
            // SDK carries the result as raw CBOR bytes. Deserialize to a generic CLR
            // tree (object/Dictionary/List/primitives + SurrealDB-typed instances for
            // tagged values), then walk that tree emitting JSON. Going via `object?`
            // keeps us out of the SDK's strongly-typed serializer paths — we don't
            // know the destination type at this layer.
            var value = DeserializeOkResult(ok, cborOptions);
            WriteValue(writer, value);
            writer.WriteString("status", "OK");
        }
        else if (result is ISurrealDbErrorResult err)
        {
            // Disruptor.Surface's NormalizeStatementResults reads `result` as raw text
            // for ERR statements. The SDK exposes the error message directly; mirror
            // SurrealDB's wire form by writing it as a JSON string under "result".
            writer.WriteString("result", DescribeError(err));
            writer.WriteString("status", "ERR");
        }
        else
        {
            // Unknown SDK result subtype — write status only so the runtime can still
            // walk the envelope without throwing on a missing property.
            writer.WriteString("status", "ERR");
        }

        writer.WriteEndObject();
    }

    private static object? DeserializeOkResult(SurrealDbOkResult ok, CborOptions cborOptions)
    {
        // Reflection access: SurrealDbOkResult holds `_binaryResult` (ReadOnlyMemory<byte>?)
        // and `_cborOptions` as private fields — the SDK's GetValue<T> goes through them.
        // We need raw CBOR bytes plus the options to drive a generic deserialize.
        var resultType = typeof(SurrealDbOkResult);
        var binaryField = resultType.GetField("_binaryResult", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (binaryField?.GetValue(ok) is not ReadOnlyMemory<byte> binary)
        {
            return null;
        }

        return CborSerializer.Deserialize<object?>(binary.Span, cborOptions);
    }

    /// <summary>
    /// Walk a generic CBOR-decoded tree and emit it as JSON. Recurses through
    /// dictionaries and lists; defers to <see cref="WriteScalar"/> for leaves.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;

            case RecordId recordId:
                writer.WriteStringValue(FormatRecordId(recordId));
                return;

            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (k, v) in map)
                {
                    writer.WritePropertyName(k);
                    WriteValue(writer, v);
                }
                writer.WriteEndObject();
                return;

            case IDictionary<object, object?> objectMap:
                // Dahomey.Cbor's default object-mode produces IDictionary<object, object?>
                // with object keys (CBOR allows any key type). Coerce keys to strings —
                // SurrealDB's response objects all use string keys in practice.
                writer.WriteStartObject();
                foreach (var entry in objectMap)
                {
                    writer.WritePropertyName(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                    WriteValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                return;

            case System.Collections.IDictionary genericMap:
                writer.WriteStartObject();
                foreach (System.Collections.DictionaryEntry entry in genericMap)
                {
                    writer.WritePropertyName(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty);
                    WriteValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                return;

            case string str:
                writer.WriteStringValue(str);
                return;

            case System.Collections.IEnumerable enumerable:
                writer.WriteStartArray();
                foreach (var item in enumerable)
                {
                    WriteValue(writer, item);
                }
                writer.WriteEndArray();
                return;

            default:
                WriteScalar(writer, value);
                return;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case sbyte i8:
                writer.WriteNumberValue(i8);
                break;
            case short i16:
                writer.WriteNumberValue(i16);
                break;
            case int i32:
                writer.WriteNumberValue(i32);
                break;
            case long i64:
                writer.WriteNumberValue(i64);
                break;
            case byte u8:
                writer.WriteNumberValue(u8);
                break;
            case ushort u16:
                writer.WriteNumberValue(u16);
                break;
            case uint u32:
                writer.WriteNumberValue(u32);
                break;
            case ulong u64:
                writer.WriteNumberValue(u64);
                break;
            case float f32:
                writer.WriteNumberValue(f32);
                break;
            case double f64:
                writer.WriteNumberValue(f64);
                break;
            case decimal dec:
                // SurrealDB decimals serialise as strings on the wire so precision is
                // preserved across JSON; HydrationJson reads them back as strings then
                // re-parses. Match that.
                writer.WriteStringValue(dec.ToString(CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan ts:
                // SurrealDB durations serialise as `Pn` ISO-8601-ish strings; use the
                // round-trip TimeSpan format. Consumers parsing these explicitly is rare
                // — most code reads scalars or record links, not durations.
                writer.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Guid guid:
                writer.WriteStringValue(guid.ToString("D"));
                break;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                break;
            default:
                // Last-resort: stringify whatever it is. Beats throwing — diagnostic
                // value is preserved and the runtime's JSON parsers won't blow up on
                // a string they don't recognise.
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>
    /// Render a SurrealDB <see cref="RecordId"/> as <c>"table:value"</c> — the canonical
    /// form Disruptor.Surface's <c>HydrationJson.ReadRecordId</c> parses. The SDK's
    /// <c>RecordIdJsonConverter</c> writes just the id portion which doesn't match
    /// what the runtime expects, so we go around it.
    /// </summary>
    private static string FormatRecordId(RecordId id)
    {
        var idValue = id switch
        {
            RecordIdOfString s => s.Id,
            _ => id.DeserializeId<object>()?.ToString() ?? string.Empty,
        };
        return $"{id.Table}:{idValue}";
    }

    private static string DescribeError(ISurrealDbErrorResult err)
    {
        // ISurrealDbErrorResult shapes: SurrealDbErrorResult (string Details),
        // SurrealDbProtocolErrorResult, SurrealDbUnknownResult. Reflect the most
        // useful field per type. The runtime's NormalizeStatementResults treats the
        // result text as a free-form diagnostic — exact format isn't load-bearing.
        return err switch
        {
            SurrealDbErrorResult e => e.Details ?? string.Empty,
            _ => err.ToString() ?? string.Empty,
        };
    }
}
