namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// Which annotation drove this property into the model. A single property can, in principle,
/// appear multiple times in the owning table — once per kind — though in practice only one
/// kind is meaningful. Kept as a flag enum so future combinations are cheap.
/// </summary>
[System.Flags]
public enum PropertyKind
{
    None       = 0,
    Id         = 1 << 0,
    Property   = 1 << 1,
    Parent     = 1 << 2,
    Children   = 1 << 3,
    Reference  = 1 << 4,
}

/// <summary>
/// What happens to a referencing record when the referenced record is deleted. Mirrors
/// the runtime's <c>ReferenceDeleteBehavior</c> enum — names kept in lockstep so the
/// generator can emit references to the runtime type directly.
/// </summary>
public enum ReferenceDeletePolicy
{
    Reject,
    Unset,
    Cascade,
    Ignore,
}

/// <summary>
/// Equatable snapshot of a property declared on a <c>[Table]</c> type that carries at least
/// one model annotation. Holds only strings/value-types so it survives incremental caching.
/// </summary>
/// <param name="RelationRole">
/// Non-<see cref="MethodRole.None"/> when the property also carries a forward/inverse
/// relation attribute — e.g. <c>[RestrictedBy] IReadOnlyCollection&lt;Constraint&gt; Restrictions { get; }</c>.
/// Implied verb for relation properties is always <c>List</c>.
/// </param>
/// <param name="InlineMembers">
/// For <c>[Property] SurrealArray&lt;T&gt;</c> properties, the public instance members of
/// <c>T</c>. Drives <c>scenarios.*.kind</c>-style sub-field DDL in the schema emitter.
/// Empty for any other kind of property.
/// </param>
public sealed record PropertyModel(
    string Name,
    TypeRef Type,
    PropertyKind Kinds,
    MethodRole RelationRole,
    string? RelationKindFullName,
    ReferenceDeletePolicy ReferenceDelete,
    bool HasExplicitDeleteBehavior,
    bool HasMultipleDeleteBehaviors,
    bool HasGetter,
    bool HasSetter,
    bool HasInitOnlySetter,
    bool IsPartial,
    bool IsStatic,
    string DeclaredAccessibility,
    EquatableArray<InlineMember> InlineMembers,
    bool IsInline);
