using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Typed-content builders for per-entity <see cref="IEntity.SaveAsync"/> emission. The
/// generator emits <c>__content.Set("title", _title)</c>-shaped lines per persisted
/// field; the helpers here apply the right <see cref="SurrealValue"/> wrapping for
/// each scalar shape and omit the field when the value is <c>null</c> (so SurrealDB's
/// schema-level <c>DEFAULT</c> applies). The mirror of <see cref="HydrationValue"/>:
/// HydrationValue is the read side, <see cref="ContentValue"/> the write side. Both
/// keep the wire path typed-and-CBOR — no JSON, no SurrealQL string formatting.
/// </summary>
public static class ContentValue
{
    public static void Set(this SurrealObject obj, string key, string? value)
    {
        if (value is not null) obj[key] = value;
    }

    public static void Set(this SurrealObject obj, string key, bool value) => obj[key] = value;
    public static void Set(this SurrealObject obj, string key, bool? value)
    {
        if (value is { } v) obj[key] = v;
    }

    public static void Set(this SurrealObject obj, string key, int value) => obj[key] = value;
    public static void Set(this SurrealObject obj, string key, int? value)
    {
        if (value is { } v) obj[key] = v;
    }

    public static void Set(this SurrealObject obj, string key, long value) => obj[key] = value;
    public static void Set(this SurrealObject obj, string key, long? value)
    {
        if (value is { } v) obj[key] = v;
    }

    public static void Set(this SurrealObject obj, string key, double value) => obj[key] = value;
    public static void Set(this SurrealObject obj, string key, double? value)
    {
        if (value is { } v) obj[key] = v;
    }

    public static void Set(this SurrealObject obj, string key, decimal value) => obj[key] = value;
    public static void Set(this SurrealObject obj, string key, decimal? value)
    {
        if (value is { } v) obj[key] = v;
    }

    public static void Set(this SurrealObject obj, string key, DateTime value) => obj[key] = (DateTimeOffset)value;
    public static void Set(this SurrealObject obj, string key, DateTime? value)
    {
        if (value is { } v) obj[key] = (DateTimeOffset)v;
    }

    public static void Set(this SurrealObject obj, string key, DateTimeOffset value) => obj[key] = value;
    public static void Set(this SurrealObject obj, string key, DateTimeOffset? value)
    {
        if (value is { } v) obj[key] = v;
    }

    public static void Set(this SurrealObject obj, string key, Guid value) => obj[key] = value;
    public static void Set(this SurrealObject obj, string key, Guid? value)
    {
        if (value is { } v) obj[key] = v;
    }

    /// <summary>Ulid serialises as its 26-char Crockford base32 string (matches SurrealDB's <c>TYPE string</c> mapping in <c>SchemaEmitter</c>).</summary>
    public static void Set(this SurrealObject obj, string key, Ulid value) => obj[key] = value.ToString();
    public static void Set(this SurrealObject obj, string key, Ulid? value)
    {
        if (value is { } v) obj[key] = v.ToString();
    }

    /// <summary>Writes a typed FK as a <see cref="SurrealRecordIdValue"/> — preserves Thing typing through CBOR.</summary>
    public static void SetRef(this SurrealObject obj, string key, RecordId? value)
    {
        if (value is { } v) obj[key] = new SurrealRecordIdValue(v.ToSdk());
    }
}
