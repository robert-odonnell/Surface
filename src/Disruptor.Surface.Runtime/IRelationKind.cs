namespace Disruptor.Surface.Runtime;

/// <summary>
/// Marker interface for the typed relation-kind classes the generator emits alongside
/// every forward relation attribute (<c>RestrictsAttribute</c> → emits a <c>Restricts</c>
/// class implementing this interface). The static <see cref="EdgeName"/> carries the
/// SurrealDB edge-table name so generic session calls (e.g. <c>UnrelateAsync&lt;TKind&gt;</c>,
/// the variant-query family, sync <c>QueryRelatedIds&lt;TKind&gt;</c>) don't need a
/// string literal. The class itself has no instance API today; relation payloads carry
/// on per-variant classes (annotated <c>[Restricts]</c>-on-class with <c>[In]</c> /
/// <c>[Out]</c> / <c>[Property]</c> members), not on this marker.
/// </summary>
public interface IRelationKind
{
    static abstract string EdgeName { get; }
}
