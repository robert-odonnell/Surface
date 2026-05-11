using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Common shape for all record ids — the canonical <see cref="RecordId"/> and every
/// per-table generated <c>{Name}Id</c> struct. SurrealSession public API accepts this so
/// callers can reference records by id without hydrating the entity. <see cref="ToLiteral"/>
/// yields the string half of the Surreal id (<c>{Table}:{ToLiteral()}</c>).
/// </summary>
public interface IRecordId
{
    /// <summary>Table name (e.g. <c>epic</c>). Pairs with <see cref="ToLiteral"/> to form a Surreal record id.</summary>
    string Table { get; }

    /// <summary>String form of the id value — whatever the underlying typed value serialises to.</summary>
    string ToLiteral();
}

/// <summary>
/// Bridges Surface's <see cref="IRecordId"/> to the SDK's <see cref="SurrealRecordId"/>
/// for typed-CBOR dispatch. The wire path is end-to-end typed: no SurrealQL string
/// formatting, no escape rules, no JSON. Surface only ever uses string-keyed record ids
/// (Ulid stringifications / slugs / content hashes), so the bridge always lands as a
/// <see cref="SurrealStringRecordIdKey"/>.
/// </summary>
public static class RecordIdSdkBridge
{
    /// <summary>Convert any <see cref="IRecordId"/> to a typed SDK <see cref="SurrealRecordId"/>.</summary>
    public static SurrealRecordId ToSdk(this IRecordId id)
        => new(id.Table, id.ToLiteral());
}
