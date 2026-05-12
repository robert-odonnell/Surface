using Disruptor.Surface.Generator.Annotations;
using Disruptor.Surface.Generator.Emit;
using Disruptor.Surface.Generator.Model;
using Disruptor.Surface.Generator.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class ModelGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var tables = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AnnotationsMetadata.Table,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => TableExtractor.TryExtract(ctx, ct))
            .Where(static t => t is not null)
            .Select(static (t, _) => t!);

        // Relation kinds are discovered by base-walk, not by marker attribute — any class
        // that derives from ForwardRelation / InverseRelation<T> qualifies. Predicate is
        // syntax-only ("has a base list") so the incremental pipeline still caches well;
        // the transform uses the semantic model to confirm and split by direction.
        var allRelationKinds = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: RelationKindExtractor.IsClassWithBaseList,
                transform: static (ctx, ct) => RelationKindExtractor.TryExtract(ctx, ct))
            .Where(static k => k is not null)
            .Select(static (k, _) => k!);

        var forwardKinds = allRelationKinds.Where(static k => k.Direction == RelationDirection.Forward);
        var inverseKinds = allRelationKinds.Where(static k => k.Direction == RelationDirection.Inverse);

        // Relation variants — classes carrying a [Restricts] / [RestrictedBy] (etc.) attribute,
        // each declaring [In] / [Out] endpoints and optional [Property] payload fields. The
        // same attribute classes appear elsewhere as property-on-entity annotations (handled
        // by TableExtractor); here we pick up only the on-class usage.
        var relationVariants = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: RelationVariantExtractor.IsClassWithAttributeList,
                transform: static (ctx, ct) => RelationVariantExtractor.TryExtract(ctx, ct))
            .Where(static v => v is not null)
            .Select(static (v, _) => v!);

        var compositionRoots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AnnotationsMetadata.CompositionRoot,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => CompositionRootExtractor.TryExtract(ctx, ct))
            .Where(static c => c is not null)
            .Select(static (c, _) => c!);

        // Union endpoint discovery — two passes feed the linker. Pass (a) finds interfaces
        // attributed with anything deriving from In<TKind>/Out<TKind> (the union itself).
        // Pass (b) finds partial I{Name}RecordId decls with a non-empty base list (the
        // per-table opt-ins). The linker stitches them into UnionEndpointModels.
        var unionInterfaceCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: UnionEndpointExtractor.IsInterfaceWithAttributeList,
                transform: static (ctx, ct) => UnionEndpointExtractor.TryExtractUnionInterface(ctx, ct))
            .Where(static u => u is not null)
            .Select(static (u, _) => u!);

        var unionMembershipCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: UnionEndpointExtractor.IsPerTableMarkerWithBases,
                transform: static (ctx, ct) => UnionEndpointExtractor.TryExtractMembership(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        // Shared-shape interfaces: user-declared interfaces deriving from IRelationVariant
        // gain a generated static Create<TKind> factory so relation rows can be constructed
        // by kind without hand-maintaining a switch. Union-endpoint interfaces (derived
        // from IRecordId) are filtered out by the extractor.
        var sharedShapeCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: SharedShapeExtractor.IsInterfaceWithBaseList,
                transform: static (ctx, ct) => SharedShapeExtractor.TryExtract(ctx, ct))
            .Where(static s => s is not null)
            .Select(static (s, _) => s!);

        var graph = tables.Collect()
            .Combine(forwardKinds.Collect())
            .Combine(inverseKinds.Collect())
            .Combine(compositionRoots.Collect())
            .Combine(relationVariants.Collect())
            .Combine(unionInterfaceCandidates.Collect())
            .Combine(unionMembershipCandidates.Collect())
            .Combine(sharedShapeCandidates.Collect())
            .Select(static (combined, _) =>
                RelationLinker.Build(
                    combined.Left.Left.Left.Left.Left.Left.Left,    // tables
                    combined.Left.Left.Left.Left.Left.Left.Right,   // forwardKinds
                    combined.Left.Left.Left.Left.Left.Right,        // inverseKinds
                    combined.Left.Left.Left.Left.Right,             // compositionRoots
                    combined.Left.Left.Left.Right,                  // relationVariants
                    combined.Left.Left.Right,                       // unionInterfaceCandidates
                    combined.Left.Right,                            // unionMembershipCandidates
                    combined.Right));                               // sharedShapeCandidates

        context.RegisterSourceOutput(graph, static (spc, g) => Emit(spc, g));
    }

    private static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        foreach (var conflict in graph.AggregateConflicts)
        {
            // Format from RelationLinker.ComputeAggregates: "Member|Root1,Root2,...".
            var pipe = conflict.IndexOf('|');
            var member = pipe < 0 ? conflict : conflict[..pipe];
            var roots = pipe < 0 ? string.Empty : conflict[(pipe + 1)..];
            spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.EntityInMultipleAggregates,
                Location.None,
                member,
                roots));
        }

        // CG014 — cascade cycles among [Reference, Cascade] edges.
        foreach (var cycle in graph.CascadeCycles)
        {
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.CascadeCycle, Location.None, cycle));
        }

        // Per-table per-property reference-delete diagnostics.
        var tableLookup = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in graph.Tables)
        {
            tableLookup.Add(t.FullName);
        }

        foreach (var table in graph.Tables)
        {
            foreach (var p in table.Properties)
            {
                // CG015 — delete behavior attribute on [Parent].
                if (p.Kinds.HasFlag(PropertyKind.Parent) && p.HasExplicitDeleteBehavior)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.DeleteBehaviorOnParent, Location.None, table.FullName, p.Name));
                }

                if (!p.Kinds.HasFlag(PropertyKind.Reference))
                {
                    continue;
                }

                // CG013 — multiple delete behaviors on a single [Reference].
                if (p.HasMultipleDeleteBehaviors)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.MultipleDeleteBehaviors, Location.None, table.FullName, p.Name));
                }

                // CG012 — [Unset] requires nullable storage.
                if (p.ReferenceDelete == ReferenceDeletePolicy.Unset && !p.Type.IsNullable)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.UnsetRequiresNullable, Location.None, table.FullName, p.Name));
                }

                // CG017 — [Ignore] to a known table is dangling-prone (warning).
                if (p.ReferenceDelete == ReferenceDeletePolicy.Ignore && p.Type.IsTableType)
                {
                    var targetFqn = StripGlobalAndNullable(p.Type.FullyQualifiedName);
                    if (tableLookup.Contains(targetFqn))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.IgnoreDanglingWarning, Location.None,
                            table.FullName, p.Name, targetFqn));
                    }
                }

                // CG021 — [Reference] target must be in the same aggregate as the owner
                // (or in no aggregate at all, like a shared sidecar). Cross-aggregate
                // links go through forward/inverse relation kinds, not entity references.
                if (p.Type.IsTableType)
                {
                    var targetFqn = StripGlobalAndNullable(p.Type.FullyQualifiedName);
                    var ownerAggregate = graph.AggregateRootOf(table.FullName);
                    var targetAggregate = graph.AggregateRootOf(targetFqn);
                    if (ownerAggregate is not null
                        && targetAggregate is not null
                        && !string.Equals(ownerAggregate, targetAggregate, StringComparison.Ordinal))
                    {
                        spc.ReportDiagnostic(Diagnostic.Create(
                            Diagnostics.ReferenceCrossesAggregate, Location.None,
                            table.FullName, p.Name, targetFqn, targetAggregate, ownerAggregate));
                    }
                }
            }
        }

        foreach (var union in graph.Unions)
        {
            UnionInterfaceEmitter.Emit(spc, union);
        }

        // CG032 — union endpoint interface with zero enrolled tables. The user declared
        // a union (interface attributed with an In<TKind>/Out<TKind>-derived attribute)
        // but no [Table] opted in via a `partial interface I{Name}RecordId : IFooTarget`
        // declaration. Warning (not error) because the union still resolves as a type
        // and won't break compilation — but variants typing an endpoint to it can never
        // satisfy a substrate FROM/TO clause.
        foreach (var unionEndpoint in graph.UnionEndpoints)
        {
            if (unionEndpoint.MemberTableFullNames.Count > 0)
            {
                continue;
            }

            spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.DeadUnionEndpoint,
                Location.None,
                unionEndpoint.InterfaceFullName,
                unionEndpoint.KindFullName));
        }

        // CG018 — more than one [CompositionRoot] in the compilation. CG019 — the one
        // that's there isn't partial. Either case skips the emitter (avoids dragging
        // half-broken Load{Root}Async methods into the consumer compilation).
        var compositionRootValid = graph.CompositionRoots.Count <= 1;
        if (graph.CompositionRoots.Count > 1)
        {
            var names = string.Join(", ", graph.CompositionRoots.Select(c => c.FullName));
            spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.MultipleCompositionRoots, Location.None, names));
        }

        if (graph.CompositionRoots.Count == 1 && !graph.CompositionRoots[0].IsPartial)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.CompositionRootMustBePartial, Location.None, graph.CompositionRoots[0].FullName));
            compositionRootValid = false;
        }

        if (compositionRootValid)
        {
            CompositionRootEmitter.Emit(spc, graph);
        }
        RelationKindEmitter.Emit(spc, graph);

        // CG020 — every member of an aggregate must be reachable from the root via
        // [Parent] links so the loader's dotted-path WHERE clauses can scope each row
        // by parent. Aggregate membership is decided by [Children] reachability, so a
        // mismatch (Children says yes, [Parent] BFS says no) leaves the member silently
        // unloadable.
        var loaderValid = true;
        var byFullName = graph.Tables.ToDictionary(t => t.FullName);
        foreach (var agg in graph.Aggregates)
        {
            var reachable = ReachableViaParentLinks(agg, byFullName);
            foreach (var memberFullName in agg.MemberFullNames)
            {
                if (!reachable.Contains(memberFullName))
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.ChildMissingParentPath,
                        Location.None,
                        memberFullName,
                        agg.RootFullName));
                    loaderValid = false;
                }
            }
        }
        if (loaderValid)
        {
            AggregateLoaderEmitter.Emit(spc, graph);
        }
        ReferenceRegistryEmitter.Emit(spc, graph);
        SchemaEmitter.Emit(spc, graph);
        QueryRootEmitter.Emit(spc, graph);
        EdgeQueryRootEmitter.Emit(spc, graph);
        HydrateRootEmitter.Emit(spc, graph);
        LoadEntryEmitter.Emit(spc, graph);

        foreach (var table in graph.Tables)
        {
            if (!table.IsPartial)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.TableMustBePartial,
                    Location.None,
                    table.FullName));
                continue;
            }

            // CG008 — at most one [Id] property (the user's optional public-facing accessor).
            // [Id] is no longer required: the generator always emits the internal id anchor
            // and the IEntity.Id accessor on every [Table]; [Id] just opts the user into a
            // public partial property that delegates to the anchor.
            var idCount = table.Properties.Count(p => p.Kinds.HasFlag(PropertyKind.Id));
            if (idCount > 1)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.TableHasMultipleIds, Location.None, table.FullName, idCount));
                continue;
            }

            IdEmitter.Emit(spc, table, graph);
            PredicateFactoryEmitter.Emit(spc, table);
            IdsAsyncEmitter.Emit(spc, table);
            TraversalBuilderEmitter.Emit(spc, table, graph);

            var valid = true;

            // CG022 — every annotated property must be declared partial. Without it, the
            // generator can't emit the implementation half (backing field, getter/setter,
            // hydrate body all live in the partial fragment), so a non-partial member
            // tagged [Property]/[Reference]/[Parent]/[Children]/[Id] would produce
            // generated code referencing storage that was never emitted (CS0103). Catch
            // it here so the diagnostic explains the cause; without it the user gets a
            // confusing CS0103 in a .g.cs file they didn't write.
            foreach (var p in table.Properties)
            {
                if (p.IsPartial)
                {
                    continue;
                }

                var attrName = p.Kinds switch
                {
                    var k when k.HasFlag(PropertyKind.Id)        => "Id",
                    var k when k.HasFlag(PropertyKind.Property)  => "Property",
                    var k when k.HasFlag(PropertyKind.Parent)    => "Parent",
                    var k when k.HasFlag(PropertyKind.Children)  => "Children",
                    var k when k.HasFlag(PropertyKind.Reference) => "Reference",
                    _ => p.RelationRole != RelationRole.None ? "Relation" : null,
                };
                if (attrName is null)
                {
                    continue;
                }

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.AnnotatedMemberMustBePartial,
                    Location.None,
                    table.FullName,
                    p.Name,
                    attrName));
                valid = false;
            }

            // CG024 — at most one role attribute per property. The five role attributes
            // ([Id]/[Property]/[Parent]/[Children]/[Reference]) each select a distinct
            // emit shape — scalar field, structural parent link, child collection, record
            // reference, identity. Mixing two means the emitters silently disagree:
            // PartialEmitter prioritises [Property] over [Reference] while SchemaEmitter
            // prioritises [Reference] over [Property], so [Property][Reference] yields a
            // scalar CLR setter writing into a record<>-typed schema column. Reject the
            // combo at the model boundary instead of letting it ship.
            foreach (var p in table.Properties)
            {
                var roleNames = new List<string>();
                if (p.Kinds.HasFlag(PropertyKind.Id))
                {
                    roleNames.Add("Id");
                }

                if (p.Kinds.HasFlag(PropertyKind.Property))
                {
                    roleNames.Add("Property");
                }

                if (p.Kinds.HasFlag(PropertyKind.Parent))
                {
                    roleNames.Add("Parent");
                }

                if (p.Kinds.HasFlag(PropertyKind.Children))
                {
                    roleNames.Add("Children");
                }

                if (p.Kinds.HasFlag(PropertyKind.Reference))
                {
                    roleNames.Add("Reference");
                }

                if (roleNames.Count <= 1)
                {
                    continue;
                }

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.ConflictingRoleAttributes,
                    Location.None,
                    table.FullName,
                    p.Name,
                    string.Join(" + ", roleNames)));
                valid = false;
            }

            foreach (var (memberName, memberType) in EnumerateReadSideTypes(table, PropertyKind.Children))
            {
                var content = UnwrapTask(memberType);
                var element = content.ElementType ?? content;
                if (element.IsTypeParameter)
                {
                    // CG009 — type-parameter element. The child's concrete type isn't
                    // known at codegen time, so the loader can't pick the row-hydrator.
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.ChildrenElementMustBeConcrete,
                        Location.None,
                        table.FullName,
                        memberName,
                        element.DisplayName));
                    valid = false;
                }
                else if (!element.IsTableType)
                {
                    // CG026 — concrete-but-not-a-Table element (e.g. IReadOnlyCollection<string>).
                    // The emitted Session.QueryChildren<T>(...) call has `where T : IEntity, new()`,
                    // so this would surface as a generic-constraint CS error in generated code
                    // without the diagnostic.
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.ChildrenElementMustBeTable,
                        Location.None,
                        table.FullName,
                        memberName,
                        element.DisplayName));
                    valid = false;
                }
            }
            foreach (var (memberName, memberType) in EnumerateReadSideTypes(table, PropertyKind.Reference))
            {
                var target = UnwrapTask(memberType);
                if (target.IsTableType)
                {
                    continue;
                }

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.ReferenceMustTargetTable,
                    Location.None,
                    table.FullName,
                    memberName,
                    memberType.DisplayName));
                valid = false;
            }

            // CG027 — [Parent] target must be a [Table]. Same family as CG010 / CG026
            // (generic constraint violation in emitted code without the diagnostic).
            foreach (var p in table.Properties)
            {
                if (!p.Kinds.HasFlag(PropertyKind.Parent))
                {
                    continue;
                }

                if (p.Type.IsTableType)
                {
                    continue;
                }

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.ParentMustTargetTable,
                    Location.None,
                    table.FullName,
                    p.Name,
                    p.Type.DisplayName));
                valid = false;
            }

            // CG028 — annotated property must not be static. Every emit shape (backing
            // field, _session reference, identity-map plumbing, hydrate body) is
            // per-instance; a static partial property would compile to `static partial T
            // Foo` and the generator's emitted instance backing field wouldn't match.
            foreach (var p in table.Properties)
            {
                if (!p.IsStatic)
                {
                    continue;
                }

                var attrName = AnnotationLabel(p);
                if (attrName is null)
                {
                    continue;
                }

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.AnnotatedMemberMustNotBeStatic,
                    Location.None,
                    table.FullName,
                    p.Name,
                    attrName));
                valid = false;
            }

            // CG025 — [Property] type must map to a SurrealDB scalar. Unmapped types
            // (Uri, TimeSpan, custom value objects, …) compile fine on the CLR side but
            // SchemaEmitter would silently omit the field, so reads/writes would fail at
            // the database, not at build time. Skip element-collection [Property] shapes
            // (List<T> / IList<T> / IReadOnlyList<T> — handled separately in SchemaEmitter
            // via the array<object> + sub-field path) and skip relation role overlay
            // (they don't take the scalar-emission code path).
            foreach (var p in table.Properties)
            {
                if (!p.Kinds.HasFlag(PropertyKind.Property))
                {
                    continue;
                }

                if (p.RelationRole != RelationRole.None)
                {
                    continue;
                }

                if (p.Type.MetadataName is "System.Collections.Generic.IReadOnlyList`1"
                    or "System.Collections.Generic.IList`1"
                    or "System.Collections.Generic.List`1")
                {
                    continue;
                }

                if (SchemaEmitter.IsMappableScalar(p.Type))
                {
                    continue;
                }

                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.PropertyTypeNotMappable,
                    Location.None,
                    table.FullName,
                    p.Name,
                    p.Type.FullyQualifiedName));
                valid = false;
            }

            if (!valid)
            {
                continue;
            }

            PartialEmitter.Emit(spc, table, graph);
        }

        // Per-variant relation classes — emits IEntity scaffolding, [In]/[Out]/[Property]
        // backing fields, Hydrate / SaveAsync. Per-kind sidecars (variant marker interface,
        // hydration dispatcher) emit alongside.
        RelationVariantEmitter.Emit(spc, graph);

        // CG033 — shared-shape interface must be declared partial to receive the
        // emitted static Create<TKind> factory fragment. CG035 — interface attributed
        // as a shared shape (derived from IRelationVariant) but no variant implements
        // it; warning, not error (the interface still functions as a marker type).
        foreach (var shape in graph.SharedShapes)
        {
            if (!shape.IsPartial)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.SharedShapeMustBePartial,
                    Location.None,
                    shape.InterfaceFullName));
            }

            if (shape.IsPartial && shape.Variants.Count == 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.SharedShapeHasNoVariants,
                    Location.None,
                    shape.InterfaceFullName));
            }
        }

        // Per-shared-shape interface: emit a partial fragment carrying a typed
        // Create<TKind>(Action<I> init) factory keyed off the per-kind marker class.
        SharedShapeEmitter.Emit(spc, graph);
    }

    private static TypeRef UnwrapTask(TypeRef t) => t.FullyQualifiedName.StartsWith("global::System.Threading.Tasks.Task<") && t.TypeArguments.Count > 0
        ? t.TypeArguments[0]
        : t;

    /// <summary>
    /// BFS from the aggregate root through <c>[Parent]</c>-pointing tables, collecting
    /// every member of <paramref name="agg"/> that the loader can reach. The aggregate
    /// loader's dotted WHERE clauses depend on this exact set; anything in
    /// <c>agg.MemberFullNames</c> not in the result triggers CG020.
    /// </summary>
    private static HashSet<string> ReachableViaParentLinks(AggregateModel agg, Dictionary<string, TableModel> byFullName)
    {
        var reached = new HashSet<string>(StringComparer.Ordinal) { agg.RootFullName };
        bool added;
        do
        {
            added = false;
            foreach (var memberFullName in agg.MemberFullNames)
            {
                if (reached.Contains(memberFullName))
                {
                    continue;
                }

                if (!byFullName.TryGetValue(memberFullName, out var member))
                {
                    continue;
                }

                foreach (var p in member.Properties)
                {
                    if (!p.Kinds.HasFlag(PropertyKind.Parent))
                    {
                        continue;
                    }

                    var parentFqn = StripGlobalAndNullable(p.Type.FullyQualifiedName);
                    if (reached.Contains(parentFqn))
                    {
                        reached.Add(memberFullName);
                        added = true;
                        break;
                    }
                }
            }
        } while (added);
        return reached;
    }

    private static string StripGlobalAndNullable(string fqn)
    {
        const string prefix = "global::";
        if (fqn.StartsWith(prefix))
        {
            fqn = fqn[prefix.Length..];
        }

        if (fqn.EndsWith("?"))
        {
            fqn = fqn[..^1];
        }

        return fqn;
    }

    /// <summary>
    /// Collapses the read-side shape of a given kind (Children / References / etc.) across
    /// both property declarations and <c>Get*</c> methods that carry the same attribute.
    /// The signature yields <c>(memberName, returnType)</c> so validation errors point at
    /// the actual declaration site.
    /// </summary>
    private static IEnumerable<(string Name, TypeRef Type)> EnumerateReadSideTypes(TableModel table, PropertyKind kind) => table.Properties.Where(prop => prop.Kinds.HasFlag(kind))
        .Select(prop => (prop.Name, prop.Type));

    /// <summary>
    /// Best-fit attribute label for diagnostic messages — picks the role attribute the
    /// user's property carries, falling back to "Relation" for forward/inverse-relation
    /// members or null when the property has no model annotation.
    /// </summary>
    private static string? AnnotationLabel(PropertyModel p) => p.Kinds switch
    {
        var k when k.HasFlag(PropertyKind.Id)        => "Id",
        var k when k.HasFlag(PropertyKind.Property)  => "Property",
        var k when k.HasFlag(PropertyKind.Parent)    => "Parent",
        var k when k.HasFlag(PropertyKind.Children)  => "Children",
        var k when k.HasFlag(PropertyKind.Reference) => "Reference",
        _ => p.RelationRole != RelationRole.None ? "Relation" : null,
    };
}
