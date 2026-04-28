#nullable enable
using Surface.Runtime;
using System.Text.Json;

namespace Surface.Runtime;

/// <summary>
/// Tiny JSON helpers used by generator-emitted <c>IEntity.Hydrate</c> implementations
/// and per-aggregate loaders. Surreal returns id-typed fields in three shapes that
/// callers shouldn't have to branch on:
/// <list type="bullet">
///   <item>flat <c>"table:value"</c> string — the typical /sql response shape;</item>
///   <item>inline-record object <c>{ id: "table:value", ...other_fields }</c> — what
///         the <c>field.*</c> expansion produces;</item>
///   <item>RPC-style <c>{ tb: "table", id: "value" }</c> — the WS/RPC envelope.</item>
/// </list>
/// </summary>
public static class HydrationJson
{
    public static RecordId ReadRecordId(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return RecordId.Parse(element.GetString());
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Inline-record form: `{ id: "table:value", ...other_fields }` — what the
            // `field.*` projection emits. The id field carries the full "table:value"
            // string and the rest of the object is the linked record's content.
            if (element.TryGetProperty("id", out var idE) && idE.ValueKind == JsonValueKind.String)
            {
                var idString = idE.GetString();
                if (!string.IsNullOrEmpty(idString) && idString!.IndexOf(':') >= 0)
                {
                    return RecordId.Parse(idString);
                }
            }

            // RPC-style: `{ tb: "table", id: "value" }`.
            if (element.TryGetProperty("tb", out var tbE))
            {
                var tb = tbE.GetString() ?? string.Empty;
                var id = element.TryGetProperty("id", out var idE2) ? idE2.GetString() ?? string.Empty : string.Empty;
                return new RecordId(tb, id);
            }
        }
        throw new InvalidOperationException($"Cannot read RecordId from JSON kind {element.ValueKind}.");
    }

    /// <summary>
    /// Like <see cref="TryReadRecordId(JsonElement, string, out RecordId)"/> but for an
    /// already-extracted element — used when the loader has the field's <c>JsonElement</c>
    /// in hand (e.g. after a <c>TryGetProperty</c> that also needed to inspect the
    /// element's kind for inline-vs-id-only branching).
    /// </summary>
    public static bool TryReadRecordId(JsonElement element, out RecordId value)
    {
        value = default;
        if (element.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        try
        {
            value = ReadRecordId(element);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static Ulid ReadUlidIdValue(JsonElement idElement) =>
        Ulid.Parse(ReadRecordId(idElement).Value);

    /// <summary>Mirrors <c>IdEmitter.LiteralExpr</c> for <c>Guid</c> — formats as <c>"N"</c> on emit, parses with the same format here.</summary>
    public static Guid ReadGuidIdValue(JsonElement idElement) =>
        Guid.ParseExact(ReadRecordId(idElement).Value, "N");

    /// <summary>Mirrors <c>IdEmitter.LiteralExpr</c> for <c>string</c> — the literal half of the record id is the value verbatim.</summary>
    public static string ReadStringIdValue(JsonElement idElement) =>
        ReadRecordId(idElement).Value;

    public static string ReadString(JsonElement parent, string field, string fallback = "") =>
        parent.TryGetProperty(field, out var elem) && elem.ValueKind == JsonValueKind.String
            ? elem.GetString() ?? fallback
            : fallback;

    /// <summary>
    /// Reads a property of any type <see cref="System.Text.Json.JsonSerializer"/> can
    /// deserialise — scalars (int, bool, DateTime, …), arrays (<c>int[]</c>,
    /// <c>List&lt;T&gt;</c>), records, dictionaries — using <c>SurrealJson.SerializerOptions</c>
    /// (snake_case naming for record types). Returns <c>default</c> when the field is
    /// missing or null; caller handles fallback.
    /// </summary>
    public static T? ReadOrDefault<T>(JsonElement parent, string field)
    {
        if (!parent.TryGetProperty(field, out var elem) || elem.ValueKind == JsonValueKind.Null)
        {
            return default;
        }
        return JsonSerializer.Deserialize<T>(elem, SurrealJson.SerializerOptions);
    }

    public static bool TryReadRecordId(JsonElement parent, string field, out RecordId value)
    {
        value = default;
        if (!parent.TryGetProperty(field, out var elem))
        {
            return false;
        }
        if (elem.ValueKind == JsonValueKind.Null)
        {
            return false;
        }
        value = ReadRecordId(elem);
        return true;
    }

    /// <summary>
    /// Hydrates a <c>[Reference]</c> field. Always registers the link in
    /// <see cref="SurrealSession.HydrateReference"/>; when the loader's projection
    /// inline-expanded the referenced record (the <c>field.*</c> form, producing
    /// <c>{ id: "table:value", …content }</c>) we also construct a
    /// <typeparamref name="T"/> and run its <c>IEntity.Hydrate</c>, which in turn calls
    /// <see cref="SurrealSession.HydrateTrack"/> so subsequent reads see a fully populated
    /// entity. Without this, references would carry only an id and reads would resolve
    /// to <c>null</c> because <see cref="SurrealSession.GetReferenceOrDefault{T}"/> joins
    /// against the entities dict.
    /// </summary>
    public static void HydrateReference<T>(JsonElement parent, string field, RecordId ownerId, SurrealSession session)
        where T : class, IEntity, new()
    {
        if (!parent.TryGetProperty(field, out var elem)) return;
        if (elem.ValueKind == JsonValueKind.Null) return;

        var refId = ReadRecordId(elem);
        session.HydrateReference(ownerId, field, refId);

        // Inline-record form — hydrate the linked record from the same payload. The
        // RPC envelope `{ tb, id }` and the bare id-string form carry no content, so we
        // skip them and rely on the loader having a separate row for that record.
        // Also skip if the referenced id is already tracked: an inline expansion in two
        // places (e.g. multiple constraints all referencing the same Details) would
        // otherwise allocate, hydrate, and discard a duplicate instance per occurrence.
        if (elem.ValueKind == JsonValueKind.Object
            && elem.TryGetProperty("id", out var idE)
            && idE.ValueKind == JsonValueKind.String
            && !session.IsTracked(refId))
        {
            var entity = new T();
            ((IEntity)entity).Hydrate(elem, session);
        }
    }
}
