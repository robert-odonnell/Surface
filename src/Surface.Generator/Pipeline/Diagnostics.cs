using Microsoft.CodeAnalysis;

namespace Surface.Generator.Pipeline;

internal static class Diagnostics
{
    private const string Category = "Surface";

    public static readonly DiagnosticDescriptor TableMustBePartial = new(
        id: "CG001",
        title: "Table classes must be partial",
        messageFormat: "'{0}' is annotated with [Table] but is not declared partial",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RelationVerbUnknown = new(
        id: "CG003",
        title: "Unsupported relation method verb",
        messageFormat: "Relation method '{0}' must start with Add, Remove, Clear, Set, List or Get",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RelationKindMissingBase = new(
        id: "CG004",
        title: "Relation kind attribute has wrong base",
        messageFormat: "'{0}' is marked as a relation kind but does not derive from ForwardRelationAttribute or InverseRelationAttribute<T>",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InverseRelationMissingForward = new(
        id: "CG005",
        title: "Inverse relation kind missing forward pair",
        messageFormat: "Inverse kind '{0}' references forward kind '{1}' which is not annotated with [ForwardRelationKind]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RelationReturnTypeInvalid = new(
        id: "CG006",
        title: "Relation method return type invalid",
        messageFormat: "Relation method '{0}' must return void, a [Table] type, or IReadOnlyCollection<T> where T is a [Table] type",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TableMissingId = new(
        id: "CG007",
        title: "Table is missing its [Id] property",
        messageFormat: "[Table] '{0}' must declare exactly one [Id] property; none were found",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TableHasMultipleIds = new(
        id: "CG008",
        title: "Table has more than one [Id] property",
        messageFormat: "[Table] '{0}' declares {1} [Id] properties; exactly one is required",
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
}
