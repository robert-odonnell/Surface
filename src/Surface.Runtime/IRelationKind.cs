namespace Surface.Runtime;

/// <summary>
/// Marker interface for the typed relation-kind classes the generator emits alongside
/// every forward relation attribute (<c>RestrictsAttribute</c> → emits a <c>Restricts</c>
/// class implementing this interface). The static <see cref="EdgeName"/> carries the
/// SurrealDB edge-table name so generic <see cref="SurrealSession.Relate{TKind}"/> /
/// <see cref="SurrealSession.QueryRelated{TKind, TElement}"/> calls don't need a string
/// literal.
/// <para>
/// Future-friendly: when relation tables grow payload, the same emitted marker class is
/// the natural target for the constructible "edge with content" form
/// (<c>new Restricts(src, tgt) { Severity = "high" }</c>); the static <c>EdgeName</c>
/// stays as the schema anchor, the instance side grows around it.
/// </para>
/// </summary>
public interface IRelationKind
{
    static abstract string EdgeName { get; }
}
