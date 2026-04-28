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

        var compositionRoots = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                AnnotationsMetadata.CompositionRoot,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => CompositionRootExtractor.TryExtract(ctx, ct))
            .Where(static c => c is not null)
            .Select(static (c, _) => c!);

        var graph = tables.Collect()
            .Combine(forwardKinds.Collect())
            .Combine(inverseKinds.Collect())
            .Combine(compositionRoots.Collect())
            .Select(static (combined, _) =>
                RelationLinker.Build(
                    combined.Left.Left.Left,
                    combined.Left.Left.Right,
                    combined.Left.Right,
                    combined.Right));

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
            var idCount = 0;
            foreach (var p in table.Properties)
            {
                if (p.Kinds.HasFlag(PropertyKind.Id)) idCount++;
            }
            if (idCount > 1)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.TableHasMultipleIds, Location.None, table.FullName, idCount));
                continue;
            }

            IdEmitter.Emit(spc, table, graph);

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
                if (p.IsPartial) continue;
                var attrName = p.Kinds switch
                {
                    var k when k.HasFlag(PropertyKind.Id)        => "Id",
                    var k when k.HasFlag(PropertyKind.Property)  => "Property",
                    var k when k.HasFlag(PropertyKind.Parent)    => "Parent",
                    var k when k.HasFlag(PropertyKind.Children)  => "Children",
                    var k when k.HasFlag(PropertyKind.Reference) => "Reference",
                    _ => p.RelationRole != RelationRole.None ? "Relation" : null,
                };
                if (attrName is null) continue;
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.AnnotatedMemberMustBePartial,
                    Location.None,
                    table.FullName,
                    p.Name,
                    attrName));
                valid = false;
            }

            foreach (var (memberName, memberType) in EnumerateReadSideTypes(table, PropertyKind.Children))
            {
                var content = UnwrapTask(memberType);
                var element = content.ElementType ?? content;
                if (element.IsTypeParameter)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.ChildrenElementMustBeConcrete,
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
                if (!target.IsTableType)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.ReferenceMustTargetTable,
                        Location.None,
                        table.FullName,
                        memberName,
                        memberType.DisplayName));
                    valid = false;
                }
            }
            if (!valid)
            {
                continue;
            }

            PartialEmitter.Emit(spc, table, graph);
        }
    }

    private static TypeRef UnwrapTask(TypeRef t)
    {
        if (t.FullyQualifiedName.StartsWith("global::System.Threading.Tasks.Task<") && t.TypeArguments.Count > 0)
        {
            return t.TypeArguments[0];
        }

        return t;
    }

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
                if (reached.Contains(memberFullName)) continue;
                if (!byFullName.TryGetValue(memberFullName, out var member)) continue;

                foreach (var p in member.Properties)
                {
                    if (!p.Kinds.HasFlag(PropertyKind.Parent)) continue;
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
    private static IEnumerable<(string Name, TypeRef Type)> EnumerateReadSideTypes(TableModel table, PropertyKind kind)
    {
        foreach (var prop in table.Properties)
        {
            if (prop.Kinds.HasFlag(kind))
            {
                yield return (prop.Name, prop.Type);
            }
        }
    }
}
