namespace Disruptor.Surface.Generator.Model;

public enum UnionDirection
{
    /// <summary>The forward side of a relation — entities that own the outgoing edge.</summary>
    Source,
    /// <summary>The inverse side of a relation — entities that the edge points at.</summary>
    Target,
}

/// <summary>
/// A computed set of entity types that participate on one side of a relation kind. Read
/// off <see cref="ModelGraph"/> after linking. The generator emits a marker interface
/// per union <em>only when there are 2+ members</em>; single-member unions stay typed
/// to the concrete entity (no extra noise).
/// <para>
/// Naming follows the schema's own language: target interfaces are named after the
/// inverse attribute (so <c>RestrictedByAttribute</c> → <c>IRestrictedBy</c> reads as
/// "this entity is restricted-by"), source interfaces after the forward attribute
/// (<c>RestrictsAttribute</c> → <c>IRestricts</c>).
/// </para>
/// </summary>
public sealed record RelationUnion(
    string ForwardKindFullName,
    string InterfaceName,
    string InterfaceFullName,
    string IdInterfaceName,
    string IdInterfaceFullName,
    string Namespace,
    UnionDirection Direction,
    EquatableArray<string> MemberFullNames);
