using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Pipeline;

internal static class Diagnostics
{
    private const string Category = "Disruptor.Surface";

    public static readonly DiagnosticDescriptor TableMustBePartial = new(
        id: "CG001",
        title: "Table classes must be partial",
        messageFormat: "'{0}' is annotated with [Table] but is not declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TableHasMultipleIds = new(
        id: "CG008",
        title: "Table has more than one [Id] property",
        messageFormat: "[Table] '{0}' declares {1} [Id] properties; at most one is allowed",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ChildrenElementMustBeConcrete = new(
        id: "CG009",
        title: "[Children] element type cannot be a generic type parameter",
        messageFormat: "[Children] property '{0}.{1}' has element type '{2}' which is a generic type parameter; use a named type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityInMultipleAggregates = new(
        id: "CG011",
        title: "Entity reachable from multiple aggregate roots",
        messageFormat: "Entity '{0}' is reachable via [Children] from multiple [AggregateRoot] entities ({1}). Each entity may belong to at most one aggregate.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReferenceMustTargetTable = new(
        id: "CG010",
        title: "[Reference] target must be a [Table] type",
        messageFormat: "[Reference] property '{0}.{1}' targets '{2}' which is not annotated with [Table]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsetRequiresNullable = new(
        id: "CG012",
        title: "[Unset] requires a nullable reference",
        messageFormat: "[Reference, Unset] on '{0}.{1}' requires nullable storage (T?); non-nullable references can't be unset safely",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MultipleDeleteBehaviors = new(
        id: "CG013",
        title: "Multiple reference-delete behaviors",
        messageFormat: "[Reference] property '{0}.{1}' declares more than one of [Reject]/[Unset]/[Cascade]/[Ignore]; only one delete behavior is allowed per reference",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CascadeCycle = new(
        id: "CG014",
        title: "Cascade-only reference cycle",
        messageFormat: "[Reference, Cascade] forms a cycle ({0}); break it by changing at least one edge to [Reject], [Unset], or [Ignore]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DeleteBehaviorOnParent = new(
        id: "CG015",
        title: "Delete behavior attribute on [Parent]",
        messageFormat: "[Parent] property '{0}.{1}' carries a delete-behavior attribute; parent deletion uses structural containment, not reference-delete behavior",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor IgnoreDanglingWarning = new(
        id: "CG017",
        title: "[Ignore] may produce a dangling reference",
        messageFormat: "[Reference, Ignore] on '{0}.{1}' targets known table '{2}'; the reference is left unchanged on target deletion and may dangle",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MultipleCompositionRoots = new(
        id: "CG018",
        title: "Multiple [CompositionRoot] classes",
        messageFormat: "More than one [CompositionRoot] class declared ({0}); exactly one is allowed per compilation",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor CompositionRootMustBePartial = new(
        id: "CG019",
        title: "[CompositionRoot] class must be partial",
        messageFormat: "'{0}' is annotated with [CompositionRoot] but is not declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ChildMissingParentPath = new(
        id: "CG020",
        title: "[Children] member requires a [Parent] path back to the aggregate root",
        messageFormat: "'{0}' is reachable from aggregate root '{1}' via [Children] but does not declare a [Parent] property linking back into the chain. Add a [Parent] {1} or [Parent] {{intermediate}} property so the loader can scope the row by parent path.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ReferenceCrossesAggregate = new(
        id: "CG021",
        title: "[Reference] target must be in the same aggregate (or in no aggregate)",
        messageFormat: "[Reference] property '{0}.{1}' targets '{2}', which belongs to aggregate '{3}' — different from the owner's aggregate '{4}'. Cross-aggregate links should be expressed as a relation kind (forward/inverse attribute pair) instead. Same-aggregate references and references to shared records (no aggregate) are fine.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AnnotatedMemberMustBePartial = new(
        id: "CG022",
        title: "Annotated property must be declared partial",
        messageFormat: "'{0}.{1}' carries [{2}] but is not declared partial; the generator emits the implementation, so the user-side declaration must use the partial keyword",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ConflictingRoleAttributes = new(
        id: "CG024",
        title: "Property has multiple role attributes",
        messageFormat: "'{0}.{1}' carries multiple role attributes ({2}); a property's role IS its emit shape, and the five role attributes ([Id]/[Property]/[Parent]/[Children]/[Reference]) are mutually exclusive — pick one",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PropertyTypeNotMappable = new(
        id: "CG025",
        title: "[Property] type has no SurrealDB scalar mapping",
        messageFormat: "[Property] '{0}.{1}' has type '{2}' which has no SurrealDB scalar mapping; the schema would omit the field and reads/writes would fail at the database. Map the type to one of: string, int/long, bool, float/double, decimal, DateTime/DateTimeOffset, Guid, Ulid — or mark this as a [Reference]/[Children] if it's a record.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ChildrenElementMustBeTable = new(
        id: "CG026",
        title: "[Children] element type must be a [Table]",
        messageFormat: "[Children] property '{0}.{1}' uses element type '{2}' which is not a [Table] class; the generated <c>QueryChildren&lt;T&gt;</c> body requires <c>T : IEntity, new()</c>",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ParentMustTargetTable = new(
        id: "CG027",
        title: "[Parent] target must be a [Table]",
        messageFormat: "[Parent] property '{0}.{1}' targets type '{2}' which is not a [Table] class; parent links must point at another aggregate-graph entity",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AnnotatedMemberMustNotBeStatic = new(
        id: "CG028",
        title: "Annotated property must not be static",
        messageFormat: "'{0}.{1}' carries [{2}] but is declared static; annotations only apply to instance members — the emitted backing field, session-binding, and identity-map plumbing are all per-instance",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor VariantMustBePartial = new(
        id: "CG029",
        title: "Relation variant classes must be partial",
        messageFormat: "'{0}' is annotated with a relation kind attribute (e.g. [Restricts]) on the class itself but is not declared partial; the generator emits the implementation half (IEntity scaffolding, Hydrate, SaveAsync), so the user-side declaration must use the partial keyword.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor VariantEndpointPairCollision = new(
        id: "CG030",
        title: "Two relation variants share the same (In, Out) endpoint pair",
        messageFormat: "Relation kind '{0}' has multiple variants with the same ([In] type, [Out] type) pair ({1}); the hydration dispatcher discriminates variants by (in.tb, out.tb), so duplicate endpoint pairs would be ambiguous",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
