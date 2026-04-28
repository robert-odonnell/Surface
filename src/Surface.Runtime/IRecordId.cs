namespace Surface.Runtime;

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
