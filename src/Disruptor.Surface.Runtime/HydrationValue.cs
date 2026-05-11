using System.Reflection;
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
    /// Hydrates a <c>[Reference]</c> field. Always registers the link via
    /// <see cref="IHydrationSink.Reference"/>; when the loader's projection inline-
    /// expanded the referenced record (a <see cref="SurrealObjectValue"/> rather than
    /// just an id), constructs a <typeparamref name="T"/> and runs its
    /// <see cref="IEntity.Hydrate(SurrealValue, IHydrationSink)"/>. The sink's
    /// <see cref="IHydrationSink.IsTracked"/> dedups multi-owner inline expansion.
    /// </summary>
    public static void HydrateReference<T>(SurrealObjectValue parent, string field, RecordId ownerId, IHydrationSink sink)
        where T : class, IEntity, new()
    {
        if (!parent.Object.TryGetValue(field, out var v)) return;
        if (v is SurrealNullValue or SurrealNoneValue) return;

        var refId = ReadRecordId(v);
        sink.Reference(ownerId, field, refId);

        if (v is SurrealObjectValue inline && inline.Object.ContainsKey("id") && !sink.IsTracked(refId))
        {
            var entity = new T();
            entity.Hydrate(inline, sink);
        }
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
    /// nullable wrappers, arrays / List&lt;T&gt;, and POCOs / records. Records are
    /// constructed by matching SurrealObjectValue fields to public properties via
    /// snake_case naming (matches the deleted SurrealJson.SerializerOptions convention).
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

            case SurrealObjectValue ov:
                return ConvertObject(ov, underlying);
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

    /// <summary>
    /// Construct a POCO / record from a <see cref="SurrealObjectValue"/>. Matches snake_case
    /// field names to public properties via <c>ToCamelCase</c> reverse, then either
    /// passes the values to a primary ctor (records) or sets them via property
    /// initialisers. Preferred constructor: the one whose parameter names match
    /// existing field-name keys in the SurrealObjectValue.
    /// </summary>
    private static object ConvertObject(SurrealObjectValue ov, Type targetType)
    {
        // Prefer a constructor with parameters matching object keys (records).
        var ctors = targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length);
        foreach (var ctor in ctors)
        {
            var pars = ctor.GetParameters();
            if (pars.Length == 0)
            {
                // Default ctor — populate via property setters / inits.
                var obj = ctor.Invoke([]);
                PopulateProperties(ov, obj, targetType);
                return obj;
            }

            // Try to match every parameter to a key. snake_case key ↔ camelCase
            // parameter name (records use the property name as ctor parameter).
            var args = new object?[pars.Length];
            var ok = true;
            for (var i = 0; i < pars.Length; i++)
            {
                var snake = ToSnakeCase(pars[i].Name!);
                if (ov.Object.TryGetValue(snake, out var fieldValue) && fieldValue is not SurrealNullValue and not SurrealNoneValue)
                {
                    args[i] = ConvertValue(fieldValue, pars[i].ParameterType);
                }
                else if (pars[i].HasDefaultValue)
                {
                    args[i] = pars[i].DefaultValue;
                }
                else if (Nullable.GetUnderlyingType(pars[i].ParameterType) is not null
                         || !pars[i].ParameterType.IsValueType)
                {
                    args[i] = null;
                }
                else
                {
                    ok = false;
                    break;
                }
            }
            if (ok) return ctor.Invoke(args);
        }
        throw new InvalidOperationException(
            $"Cannot construct {targetType.FullName} from SurrealObjectValue — no matching constructor.");
    }

    private static void PopulateProperties(SurrealObjectValue ov, object obj, Type targetType)
    {
        foreach (var prop in targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            var snake = ToSnakeCase(prop.Name);
            if (ov.Object.TryGetValue(snake, out var fieldValue)
                && fieldValue is not SurrealNullValue and not SurrealNoneValue)
            {
                prop.SetValue(obj, ConvertValue(fieldValue, prop.PropertyType));
            }
        }
    }

    /// <summary>Trivial PascalCase → snake_case. Mirrors what SurrealNaming.ToFieldName produced.</summary>
    private static string ToSnakeCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
