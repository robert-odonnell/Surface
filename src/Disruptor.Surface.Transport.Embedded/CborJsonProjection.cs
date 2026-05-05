using System.Globalization;
using System.Text;
using System.Text.Json;
using Dahomey.Cbor;
using Dahomey.Cbor.Serialization;
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
/// Walks the CBOR stream with <see cref="CborReader"/> directly rather than going
/// through the converter system. The SDK's typed converters (<c>RecordIdConverter</c>,
/// <c>DateTimeConverter</c>, …) are tuned for typed deserialization into CLR types
/// the consumer asked for — using them for "give me whatever's there as JSON" trips
/// up on shapes like top-level integers, arrays, or null payloads. Direct stream
/// walking handles every CBOR shape SurrealDB emits: scalars, arrays, maps, and
/// the SurrealDB-specific tagged values (record ids, datetimes, durations, decimals,
/// uuids).
/// </para>
/// </summary>
internal static class CborJsonProjection
{
    /// <summary>
    /// Build a <see cref="JsonDocument"/> matching the JSON-RPC response envelope
    /// from <paramref name="response"/>. Caller owns the document and disposes it.
    /// </summary>
    public static JsonDocument BuildEnvelope(SurrealDbResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        using var stream = new MemoryStream(capacity: 256);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var result in response)
            {
                WriteStatement(writer, result);
            }
            writer.WriteEndArray();
        }
        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static void WriteStatement(Utf8JsonWriter writer, ISurrealDbResult result)
    {
        writer.WriteStartObject();

        if (result is SurrealDbOkResult ok)
        {
            writer.WritePropertyName("result");
            var binary = ExtractBinaryResult(ok);
            if (binary.IsEmpty)
            {
                writer.WriteNullValue();
            }
            else
            {
                var reader = new CborReader(binary.Span);
                WriteValue(writer, ref reader);
            }
            writer.WriteString("status", "OK");
        }
        else if (result is ISurrealDbErrorResult err)
        {
            writer.WriteString("result", DescribeError(err));
            writer.WriteString("status", "ERR");
        }
        else
        {
            writer.WriteString("status", "ERR");
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Pull the raw CBOR bytes off the SDK's <see cref="SurrealDbOkResult"/>. The SDK
    /// keeps the field private; we reach for it reflectively so we can drive a typeless
    /// decode rather than going through <c>GetValue&lt;T&gt;</c> which needs a target
    /// CLR type the projection layer doesn't have.
    /// </summary>
    private static ReadOnlyMemory<byte> ExtractBinaryResult(SurrealDbOkResult ok)
    {
        var field = typeof(SurrealDbOkResult).GetField("_binaryResult",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var raw = field?.GetValue(ok);
        return raw is ReadOnlyMemory<byte> bytes ? bytes : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    /// Read one CBOR data item from <paramref name="reader"/> and emit equivalent JSON.
    /// Recurses through arrays and maps; collapses SurrealDB-tagged values to the
    /// scalar string forms the runtime parsers handle.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, ref CborReader reader)
    {
        // Semantic tags wrap a payload of a known shape — drain the tag first, then
        // either render via the tag-specific path or fall through to the type-driven
        // dispatch below for unrecognised tags (renders the payload as plain CBOR).
        if (reader.TryReadSemanticTag(out var tag))
        {
            if (TryWriteTagged(writer, ref reader, tag))
            {
                return;
            }
            // Unknown tag — fall through; the next read consumes the payload as-is.
        }

        var dataType = reader.GetCurrentDataItemType();
        switch (dataType)
        {
            case CborDataItemType.Null:
                reader.ReadNull();
                writer.WriteNullValue();
                break;

            case CborDataItemType.Boolean:
                writer.WriteBooleanValue(reader.ReadBoolean());
                break;

            case CborDataItemType.Unsigned:
                writer.WriteNumberValue(reader.ReadUInt64());
                break;

            case CborDataItemType.Signed:
                writer.WriteNumberValue(reader.ReadInt64());
                break;

            case CborDataItemType.Single:
                writer.WriteNumberValue(reader.ReadSingle());
                break;

            case CborDataItemType.Double:
                writer.WriteNumberValue(reader.ReadDouble());
                break;

            case CborDataItemType.Decimal:
                writer.WriteStringValue(reader.ReadDecimal().ToString(CultureInfo.InvariantCulture));
                break;

            case CborDataItemType.String:
                writer.WriteStringValue(reader.ReadString());
                break;

            case CborDataItemType.ByteString:
                writer.WriteBase64StringValue(reader.ReadByteString());
                break;

            case CborDataItemType.Array:
                reader.ReadBeginArray();
                var arrSize = reader.ReadSize();
                writer.WriteStartArray();
                for (var i = 0; i < arrSize; i++)
                {
                    WriteValue(writer, ref reader);
                }
                writer.WriteEndArray();
                break;

            case CborDataItemType.Map:
                reader.ReadBeginMap();
                var mapSize = reader.ReadSize();
                writer.WriteStartObject();
                for (var i = 0; i < mapSize; i++)
                {
                    var key = ReadKey(ref reader);
                    writer.WritePropertyName(key);
                    WriteValue(writer, ref reader);
                }
                writer.WriteEndObject();
                break;

            default:
                // Break / unsupported — skip and emit null so the envelope shape stays
                // intact. Better than throwing on diagnostic edge cases.
                reader.SkipDataItem();
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// Render a SurrealDB-tagged CBOR payload to JSON. Returns <c>true</c> when the tag
    /// was understood; <c>false</c> means the caller should treat the payload as plain
    /// CBOR. Tag list is mirrored from the SDK's <c>CborTagConstants</c>.
    /// </summary>
    private static bool TryWriteTagged(Utf8JsonWriter writer, ref CborReader reader, ulong tag)
    {
        switch (tag)
        {
            case SurrealCborTag.None:
                // SurrealDB's NONE wraps a null payload — semantically `null`.
                reader.ReadNull();
                writer.WriteNullValue();
                return true;

            case SurrealCborTag.RecordId:
                WriteRecordId(writer, ref reader);
                return true;

            case SurrealCborTag.StringDecimal:
                writer.WriteStringValue(reader.ReadString());
                return true;

            case SurrealCborTag.CustomDateTime:
                WriteDateTime(writer, ref reader);
                return true;

            case SurrealCborTag.CustomDuration:
                WriteDuration(writer, ref reader);
                return true;

            case SurrealCborTag.Uuid:
                WriteUuid(writer, ref reader);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Record ids encode as a 2-element CBOR array <c>[table, idValue]</c>. We render
    /// <c>"table:value"</c> matching <see cref="HydrationJson.ReadRecordId"/>'s
    /// expected shape — string and integer ids inlined, anything more exotic falls
    /// back to a stringified form.
    /// </summary>
    private static void WriteRecordId(Utf8JsonWriter writer, ref CborReader reader)
    {
        reader.ReadBeginArray();
        var size = reader.ReadSize();
        if (size < 2)
        {
            // Malformed — drain whatever remains so the outer reader stays balanced.
            for (var i = 0; i < size; i++)
            {
                reader.SkipDataItem();
            }
            writer.WriteNullValue();
            return;
        }

        var table = reader.ReadString() ?? string.Empty;

        var idType = reader.GetCurrentDataItemType();
        string id = idType switch
        {
            CborDataItemType.String => reader.ReadString() ?? string.Empty,
            CborDataItemType.Unsigned => reader.ReadUInt64().ToString(CultureInfo.InvariantCulture),
            CborDataItemType.Signed => reader.ReadInt64().ToString(CultureInfo.InvariantCulture),
            _ => StringifyOpaque(ref reader),
        };

        // Drain any extra elements (defensive — the SDK's RecordIdConverter rejects
        // size != 2, but we don't want to leave the reader unbalanced if it ever
        // comes back as a longer tuple).
        for (var i = 2; i < size; i++)
        {
            reader.SkipDataItem();
        }

        writer.WriteStringValue($"{table}:{id}");
    }

    /// <summary>
    /// Datetimes encode as <c>[seconds, nanos]</c>. Re-emit as ISO 8601 UTC so
    /// HydrationJson can read them with <c>JsonElement.GetDateTime</c>.
    /// </summary>
    private static void WriteDateTime(Utf8JsonWriter writer, ref CborReader reader)
    {
        reader.ReadBeginArray();
        var size = reader.ReadSize();
        var seconds = size > 0 ? ReadInt64Loose(ref reader) : 0;
        var nanos = size > 1 ? ReadInt64Loose(ref reader) : 0;
        for (var i = 2; i < size; i++)
        {
            reader.SkipDataItem();
        }
        var dt = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
        writer.WriteStringValue(dt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Durations encode as <c>[seconds, nanos]</c>. Rendered with <see cref="TimeSpan"/>'s
    /// round-trip format ("c") — the runtime doesn't currently parse these directly, so
    /// the format is mostly for diagnostic legibility.
    /// </summary>
    private static void WriteDuration(Utf8JsonWriter writer, ref CborReader reader)
    {
        reader.ReadBeginArray();
        var size = reader.ReadSize();
        var seconds = size > 0 ? ReadInt64Loose(ref reader) : 0;
        var nanos = size > 1 ? ReadInt64Loose(ref reader) : 0;
        for (var i = 2; i < size; i++)
        {
            reader.SkipDataItem();
        }
        var ts = TimeSpan.FromSeconds(seconds) + TimeSpan.FromTicks(nanos / 100);
        writer.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// UUIDs encode as 16-byte byte strings (with semantic tag 37 already drained).
    /// </summary>
    private static void WriteUuid(Utf8JsonWriter writer, ref CborReader reader)
    {
        var bytes = reader.ReadByteString();
        writer.WriteStringValue(bytes.Length == 16
            ? new Guid(bytes).ToString("D")
            : Convert.ToHexStringLower(bytes));
    }

    /// <summary>
    /// Read a CBOR map key as a string. SurrealDB's CBOR responses use string keys
    /// throughout; if we ever see something exotic, stringify defensively.
    /// </summary>
    private static string ReadKey(ref CborReader reader) => reader.GetCurrentDataItemType() switch
    {
        CborDataItemType.String => reader.ReadString() ?? string.Empty,
        CborDataItemType.Unsigned => reader.ReadUInt64().ToString(CultureInfo.InvariantCulture),
        CborDataItemType.Signed => reader.ReadInt64().ToString(CultureInfo.InvariantCulture),
        _ => StringifyOpaque(ref reader),
    };

    /// <summary>
    /// Loose integer read — accepts both signed and unsigned CBOR ints.
    /// </summary>
    private static long ReadInt64Loose(ref CborReader reader) => reader.GetCurrentDataItemType() switch
    {
        CborDataItemType.Unsigned => (long)reader.ReadUInt64(),
        CborDataItemType.Signed => reader.ReadInt64(),
        _ => 0,
    };

    /// <summary>
    /// Last-resort scalarisation — captures the raw bytes of a data item and renders
    /// them as a hex string. Used for record-id parts and map keys we can't decode
    /// natively (rare; SurrealDB uses string ids and string keys in practice).
    /// </summary>
    private static string StringifyOpaque(ref CborReader reader)
    {
        var raw = reader.ReadDataItem(false);
        var sb = new StringBuilder(raw.Length * 2);
        foreach (var b in raw)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string DescribeError(ISurrealDbErrorResult err) => err switch
    {
        SurrealDbErrorResult e => e.Details ?? string.Empty,
        _ => err.ToString() ?? string.Empty,
    };

    /// <summary>
    /// SurrealDB CBOR semantic tags. Mirrored from the SDK's internal
    /// <c>CborTagConstants</c> so we don't reflect into a non-public type.
    /// </summary>
    private static class SurrealCborTag
    {
        public const ulong None = 6;
        public const ulong RecordId = 8;
        public const ulong StringDecimal = 10;
        public const ulong CustomDateTime = 12;
        public const ulong CustomDuration = 14;
        public const ulong Uuid = 37;
    }
}
