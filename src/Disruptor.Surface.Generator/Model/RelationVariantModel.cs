namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// One scalar/reference property on a relation variant class — name, snake-cased SurrealDB
/// field name, type info, role (In / Out / Id / Property), and (for In/Out only) the
/// reference-delete policy. Sibling of <see cref="PropertyModel"/> for entity tables; kept
/// separate so the variant emitter doesn't drag along entity-only fields (Children,
/// Parent's two backing fields, etc.).
/// </summary>
public sealed record RelationVariantPropertyModel(
    string Name,
    string FieldName,
    TypeRef Type,
    RelationVariantPropertyRole Role,
    ReferenceDeletePolicy DeletePolicy,
    bool IsPartial,
    bool HasSetter,
    bool HasInitOnlySetter,
    bool IsStatic,
    string DeclaredAccessibility);

/// <summary>Which annotation drove the property into the variant model.</summary>
public enum RelationVariantPropertyRole
{
    None,
    Id,
    In,
    Out,
    Property,
}

/// <summary>
/// One relation variant class — a class annotated with a <c>ForwardRelation</c>- or
/// <c>InverseRelation&lt;T&gt;</c>-derived attribute (e.g. <c>[Restricts]</c> /
/// <c>[RestrictedBy]</c>). Carries the kind it belongs to, the In and Out endpoint
/// declarations, optional explicit Id, and any payload [Property] members.
/// </summary>
/// <param name="FullName">Fully-qualified type name without the leading <c>global::</c>.</param>
/// <param name="Namespace">Namespace, or empty string for global namespace.</param>
/// <param name="Name">Short type name.</param>
/// <param name="KindAttributeFqn">FQN of the relation attribute applied (e.g. <c>Disruptor.Surface.Sample.Relations.RestrictsAttribute</c>).</param>
/// <param name="In">Snapshot of the <c>[In]</c>-annotated property. Exactly one per variant.</param>
/// <param name="Out">Snapshot of the <c>[Out]</c>-annotated property. Exactly one per variant.</param>
/// <param name="Id">Optional <c>[Id]</c>-annotated property. <c>null</c> when absent (generator emits the internal id anchor either way).</param>
/// <param name="PayloadProperties">Zero or more <c>[Property]</c>-annotated members carrying edge payload.</param>
/// <param name="IsPartial">Whether the class itself is declared <c>partial</c> (required to receive emitted IEntity members).</param>
/// <param name="DeclaredAccessibility">Class-level accessibility — emitted IEntity members match.</param>
public sealed record RelationVariantModel(
    string FullName,
    string Namespace,
    string Name,
    string KindAttributeFqn,
    RelationVariantPropertyModel In,
    RelationVariantPropertyModel Out,
    RelationVariantPropertyModel? Id,
    EquatableArray<RelationVariantPropertyModel> PayloadProperties,
    bool IsPartial,
    string DeclaredAccessibility);
