using Disruptor.Surreal.Values;
using SdkRecordId = Disruptor.Surreal.Values.SurrealRecordId;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Value-based hydration helpers — the SDK-native equivalent of the deleted
/// <c>HydrationJson</c>. Generator-emitted <see cref="IEntity.Hydrate"/> bodies and the
/// runtime's loader / query / fetch consumers all read through this.
/// </summary>
/// <remarks>
/// The functions accept the SDK's <see cref="SurrealValue"/> tree directly. RecordIds
/// round-trip via <see cref="SurrealRecordIdValue"/> (the lossless wire shape) but a
/// flat <see cref="StringSurrealValue"/> ("table:value") is also accepted for
/// compatibility with any path that still emits the legacy text form.
/// </remarks>
public static class HydrationValue
{
    /// <summary>
    /// Read a <see cref="RecordId"/> from any of the wire forms hydration encounters:
    /// <list type="bullet">
    ///   <item><see cref="SurrealRecordIdValue"/> — CBOR tag 8, the canonical SDK shape.</item>
    ///   <item><see cref="StringSurrealValue"/> — flat <c>"table:value"</c> string form.</item>
    ///   <item><see cref="SurrealObjectValue"/> with an <c>"id"</c> field — inline-record form
    ///         from a <c>field.*</c> projection (the rest of the object is the linked
    ///         entity's content).</item>
    /// </list>
    /// </summary>
    public static RecordId ReadRecordId(SurrealValue v) => v switch
    {
        SurrealRecordIdValue r => FromSdk(r.SurrealRecordId),
        StringSurrealValue s => RecordId.Parse(s.Value),
        SurrealObjectValue o when o.Object.TryGetValue("id", out var idV) => ReadRecordId(idV),
        _ => throw new InvalidOperationException($"Cannot read RecordId from Value kind {v.Kind}."),
    };

    /// <summary>
    /// Like <see cref="ReadRecordId(SurrealValue)"/> but on a named field of a row. Returns
    /// false (and leaves <paramref name="result"/> at default) when the field is missing,
    /// null/none, or unrecognisable as a RecordId.
    /// </summary>
    public static bool TryReadRecordId(SurrealObjectValue parent, string field, out RecordId result)
    {
        result = default;
        if (!parent.Object.TryGetValue(field, out var v)) return false;
        if (v is SurrealNullValue or SurrealNoneValue) return false;
        try { result = ReadRecordId(v); return true; }
        catch { return false; }
    }

    /// <summary>String field read with a fallback when missing / not-a-string.</summary>
    public static string ReadString(SurrealObjectValue parent, string field, string fallback = "")
    {
        if (parent.Object.TryGetValue(field, out var v) && v is StringSurrealValue s)
            return s.Value;
        return fallback;
    }

    /// <summary>
    /// Generic field read for a scalar / collection / record type. Switches on the
    /// runtime <see cref="SurrealValue"/> shape to materialise <typeparamref name="T"/>;
    /// for records and arrays-of-records, walks public properties via reflection. Returns
    /// <c>default</c> when the field is missing or null/none.
    /// </summary>
    public static T ReadOrDefault<T>(SurrealObjectValue parent, string field)
    {
        if (!parent.Object.TryGetValue(field, out var v) || v is SurrealNullValue or SurrealNoneValue)
            return default!;
        return (T)ConvertValue(v, typeof(T))!;
    }

    /// <summary>
    /// Reads a <c>[Reference]</c> field's id from <paramref name="parent"/>, returning
    /// <c>null</c> when the field is missing or null/none. Used by emitted Hydrate
    /// bodies on plain (non-Inline) <c>[Reference]</c> fields and by all <c>[Parent]</c>
    /// fields — both store the id directly in the entity's backing field; entity
    /// resolution falls through to <see cref="SurrealSession.Get{T}"/> on read.
    /// </summary>
    public static RecordId? TryReadReferenceId(SurrealObjectValue parent, string field)
    {
        if (!parent.Object.TryGetValue(field, out var v)) return null;
        if (v is SurrealNullValue or SurrealNoneValue) return null;
        try { return ReadRecordId(v); }
        catch { return null; }
    }

    /// <summary>
    /// Hydrates an inline-expanded <c>[Reference, Inline]</c> field from <paramref name="parent"/>:
    /// constructs a <typeparamref name="T"/>, runs its
    /// <see cref="IEntity.Hydrate(SurrealValue, IHydrationSink)"/> against the inlined
    /// payload, and returns the populated entity. Returns <c>null</c> when the field is
    /// missing, null, or contains only an id (not the full inline object). The sink's
    /// <see cref="IHydrationSink.IsTracked"/> dedups multi-owner inline expansion: a
    /// second hit returns the entity already in the session.
    /// </summary>
    public static T? HydrateInlineReference<T>(SurrealObjectValue parent, string field, IHydrationSink sink)
        where T : class, IEntity, new()
    {
        if (!parent.Object.TryGetValue(field, out var v)) return null;
        if (v is SurrealNullValue or SurrealNoneValue) return null;
        if (v is not SurrealObjectValue inline || !inline.Object.ContainsKey("id")) return null;

        var refId = ReadRecordId(inline);
        if (sink.IsTracked(refId))
        {
            // Another owner reached this inline target first — its hydrated entity is
            // already in the session. Caller can pull it out via Session.Get<T>(refId).
            return null;
        }

        var entity = new T();
        entity.Hydrate(inline, sink);
        return entity;
    }

    // ──────────────────────────── conversions ────────────────────────────────

    /// <summary>
    /// Convert an SDK <see cref="SdkRecordId"/> to Surface's <see cref="RecordId"/>.
    /// Surface only uses string keys today; integer/uuid keys format identically to
    /// what the legacy SurrealSdkTransport.FormatKey used.
    /// </summary>
    private static RecordId FromSdk(SdkRecordId sdk)
        => new(sdk.Table.Name, FormatKey(sdk.Key));

    private static string FormatKey(SurrealRecordIdKey key) => key switch
    {
        SurrealStringRecordIdKey s => s.Value,
        SurrealIntegerRecordIdKey i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SurrealUuidRecordIdKey u => u.Value.ToString("D"),
        _ => throw new NotSupportedException(
            $"RecordIdKey kind {key.Kind} is not used by Disruptor.Surface."),
    };

    /// <summary>
    /// Coerce a <see cref="SurrealValue"/> into the target CLR type. Supports primitives,
    /// nullable wrappers, and arrays / List&lt;T&gt; of primitives. Record / POCO
    /// hydration is no longer routed through here — generator-emitted Hydrate bodies
    /// build records typed-and-direct (see <see cref="PartialEmitter"/>'s element
    /// collection handling), eliminating the reflection path that used to live here.
    /// </summary>
    private static object? ConvertValue(SurrealValue v, Type targetType)
    {
        // Strip Nullable<T> wrapping once.
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        switch (v)
        {
            case StringSurrealValue sv when underlying == typeof(string): return sv.Value;
            case StringSurrealValue sv when underlying == typeof(Guid): return Guid.Parse(sv.Value);
            case StringSurrealValue sv when underlying == typeof(Ulid): return Ulid.Parse(sv.Value);

            case SurrealBoolValue bv when underlying == typeof(bool): return bv.Value;

            case SurrealNumberValue nv:
                if (underlying == typeof(long)) return nv.SurrealNumber.AsInt();
                if (underlying == typeof(int)) return (int)nv.SurrealNumber.AsInt();
                if (underlying == typeof(short)) return (short)nv.SurrealNumber.AsInt();
                if (underlying == typeof(byte)) return (byte)nv.SurrealNumber.AsInt();
                if (underlying == typeof(double)) return nv.SurrealNumber.AsFloat();
                if (underlying == typeof(float)) return (float)nv.SurrealNumber.AsFloat();
                if (underlying == typeof(decimal)) return nv.SurrealNumber.AsDecimal();
                break;

            case SurrealDateTimeValue dv:
                if (underlying == typeof(DateTimeOffset)) return dv.SurrealDateTime.ToDateTimeOffset();
                if (underlying == typeof(DateTime)) return dv.SurrealDateTime.ToDateTimeOffset().UtcDateTime;
                break;

            case SurrealUuidValue uv:
                if (underlying == typeof(Guid)) return uv.Value;
                if (underlying == typeof(string)) return uv.Value.ToString("D");
                break;

            case SurrealListValue av:
                return ConvertArray(av, underlying);

            case SurrealObjectValue:
                throw new NotSupportedException(
                    $"HydrationValue.ReadOrDefault<{targetType.FullName}> received an object payload. "
                    + "Reflection-based POCO/record hydration was removed under the explicit-Save model. "
                    + "Use a typed [Property] declaration so the generator emits the per-field reads, "
                    + "or call HydrationValue.HydrateInlineReference<T> for [Reference, Inline] fields.");
        }

        throw new InvalidOperationException(
            $"Cannot convert Value of kind {v.Kind} to {targetType.FullName}.");
    }

    private static object ConvertArray(SurrealListValue av, Type targetType)
    {
        // T[] → element type from GetElementType; List<T>/IList<T>/IReadOnlyList<T> →
        // single generic argument. Anything else is unsupported.
        Type elementType;
        if (targetType.IsArray)
        {
            elementType = targetType.GetElementType()!;
        }
        else if (targetType.IsGenericType)
        {
            elementType = targetType.GetGenericArguments()[0];
        }
        else
        {
            throw new NotSupportedException(
                $"Cannot convert SurrealListValue to {targetType.FullName}; only arrays and List<T>/IList<T>/IReadOnlyList<T> are supported.");
        }

        if (targetType.IsArray)
        {
            var arr = Array.CreateInstance(elementType, av.List.Count);
            for (var i = 0; i < av.List.Count; i++)
                arr.SetValue(ConvertValue(av.List[i], elementType), i);
            return arr;
        }
        else
        {
            var listType = typeof(List<>).MakeGenericType(elementType);
            var list = (System.Collections.IList)Activator.CreateInstance(listType)!;
            for (var i = 0; i < av.List.Count; i++)
                list.Add(ConvertValue(av.List[i], elementType));
            return list;
        }
    }
}
