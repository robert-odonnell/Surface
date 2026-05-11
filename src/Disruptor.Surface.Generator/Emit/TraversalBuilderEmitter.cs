using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits a per-<c>[Table]</c> <c>{Name}TraversalBuilder</c> class — the typed sub-builder
/// passed to <c>Include*</c> methods on <c>Query&lt;T&gt;</c>. Each builder carries:
/// <list type="bullet">
///   <item><c>Where(IPredicate)</c> — accumulates filter (AND-merge across calls).</item>
///   <item>One <c>Include{Name}()</c> per <c>[Reference, Inline]</c> — adds an inline-ref
///         expansion to the projection.</item>
///   <item>One <c>Include{Name}(Action&lt;{Child}TraversalBuilder&gt;? configure = null)</c>
///         per <c>[Children]</c> — recursively descends with its own builder.</item>
///   <item><c>Build()</c> (internal) — snapshots accumulated state into <c>(IPredicate? Filter, IReadOnlyList&lt;IIncludeNode&gt; Nested)</c>.</item>
/// </list>
/// <para>
/// Forward and inverse relation traversals are also exposed: per relation property the
/// builder gets one <c>Include{Name}</c> method. Single-target relations (target side
/// has exactly one [Table]) take a <c>configure</c> lambda for the target's traversal
/// builder and accept a target-side filter; multi-target unions are leaves with no
/// configure argument. Cross-aggregate relations are leaves regardless of target arity
/// and project edge ids only — matching the entity-side <c>QueryRelatedIds</c> /
/// <c>QueryInverseRelatedIds</c> semantics.
/// </para>
/// </summary>
internal static class TraversalBuilderEmitter
{
    private const string IncludeNodeFqn = "global::Disruptor.Surface.Runtime.Query.IIncludeNode";
    private const string IncludeChildrenNodeFqn = "global::Disruptor.Surface.Runtime.Query.IncludeChildrenNode";
    private const string IncludeInlineRefNodeFqn = "global::Disruptor.Surface.Runtime.Query.IncludeInlineRefNode";
    private const string IncludeRelationNodeFqn = "global::Disruptor.Surface.Runtime.Query.IncludeRelationNode";
    private const string PredicateFqn = "global::Disruptor.Surface.Runtime.Query.IPredicate";
    private const string PredicateHelperFqn = "global::Disruptor.Surface.Runtime.Query.Predicate";

    public static void Emit(SourceProductionContext spc, TableModel table, ModelGraph graph)
    {
        var inlineRefs = CollectInlineRefs(table);
        var children = CollectChildren(table, graph);
        var relations = CollectRelations(table, graph);

        // Always emit the builder type — even with zero traversable members, the user can
        // still call .Where(...) on it. Keeps the codegen surface uniform across tables.
        var typeName = $"{table.Name}TraversalBuilder";

        var writer = new CodeWriter().Header();
        using (writer.Namespace(table.Namespace))
        {
            writer.Line($"/// <summary>Traversal builder for <see cref=\"{table.Name}\"/>. Emitted by <c>TraversalBuilderEmitter</c>; passed as the configure-action argument to <c>Query&lt;{table.Name}&gt;.Include*</c> extensions.</summary>");
            using (writer.Block($"public sealed class {typeName}"))
            {
                // Mutable accumulators. Builders are throw-away (per-Include invocation) so a
                // tiny per-instance List<> is fine; no concern about cross-call aliasing.
                writer.Line($"private {PredicateFqn}? _filter;");
                writer.Line($"private readonly System.Collections.Generic.List<{IncludeNodeFqn}> _nested = new();");
                writer.Line();

                // Where(predicate) — AND-merges into _filter, returns this for fluent chaining.
                using (writer.Block($"public {typeName} Where({PredicateFqn} predicate)"))
                {
                    writer.Line($"_filter = _filter is null ? predicate : {PredicateHelperFqn}.And(_filter, predicate);");
                    writer.Line("return this;");
                }

                foreach (var inline in inlineRefs)
                {
                    EmitInlineRefInclude(writer, typeName, inline);
                }

                foreach (var child in children)
                {
                    EmitChildrenInclude(writer, typeName, child);
                }

                foreach (var rel in relations)
                {
                    EmitRelationInclude(writer, typeName, rel);
                }

                // Build() — snapshot accumulator state for the AST. Called by the per-table
                // Include extension on Query<T>.
                writer.Line();
                writer.Line("/// <summary>Snapshot the accumulated state. Called by the per-table <c>Include</c> extension on <c>Query&lt;T&gt;</c>.</summary>");
                writer.Line($"internal ({PredicateFqn}? Filter, System.Collections.Generic.IReadOnlyList<{IncludeNodeFqn}> Nested) Build()");
                using (writer.Indent())
                {
                    writer.Line("=> (_filter, _nested.ToArray());");
                }
            }

            // Sibling class: extension methods on Query<{Table}> with the same Include
            // surface as the traversal builder above. Lets the user pivot from
            // "Workspace.Query.Designs.IncludeConstraints(...)" at the root to the
            // identically-named "...IncludeConstraints(c => c.IncludeDetails())" inside a
            // configure lambda — same vocabulary at every level.
            if (inlineRefs.Count > 0 || children.Count > 0 || relations.Count > 0)
            {
                EmitQueryExtensions(writer, table, inlineRefs, children, relations);
            }
        }

        var hint = string.IsNullOrEmpty(table.Namespace)
            ? $"{typeName}.g.cs"
            : $"{table.Namespace}.{typeName}.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }

    /// <summary>
    /// Emit a static extension class hosting <c>Query&lt;{Table}&gt;.Include*</c> methods.
    /// Mirrors the traversal-builder shape but anchors on the root <see cref="Query{T}"/>
    /// — every <c>Include</c> ends with <c>query.WithInclude(node)</c> rather than
    /// accumulating into a builder field.
    /// </summary>
    private static void EmitQueryExtensions(
        CodeWriter writer,
        TableModel table,
        IReadOnlyList<InlineRefMember> inlineRefs,
        IReadOnlyList<ChildrenMember> children,
        IReadOnlyList<RelationMember> relations)
    {
        var entityFqn = string.IsNullOrEmpty(table.Namespace)
            ? $"global::{table.Name}"
            : $"global::{table.Namespace}.{table.Name}";
        var queryFqn = $"global::Disruptor.Surface.Runtime.Query.SurfaceQuery<{entityFqn}>";
        var extName = $"{table.Name}QueryIncludes";

        writer.Line();
        writer.Line($"/// <summary>Root-level <c>Include*</c> extensions on <c>Query&lt;{table.Name}&gt;</c>. Mirror the per-level methods on <see cref=\"{table.Name}TraversalBuilder\"/>.</summary>");
        using (writer.Block($"public static class {extName}"))
        {
            var first = true;
            foreach (var inline in inlineRefs)
            {
                if (!first)
                {
                    writer.Line();
                }

                first = false;
                writer.Line($"/// <summary>Pulls the inline <c>[Reference, Inline]</c> record at <c>{inline.Field}</c> into the projection (<c>{inline.Field}.*</c>).</summary>");
                using (writer.Block($"public static {queryFqn} Include{inline.PropertyName}(this {queryFqn} query)"))
                {
                    writer.Line($"return query.WithInclude(new {IncludeInlineRefNodeFqn}(\"{inline.Field}\"));");
                }
            }

            foreach (var child in children)
            {
                if (!first)
                {
                    writer.Line();
                }

                first = false;

                var childBuilderFqn = string.IsNullOrEmpty(child.ChildNamespace)
                    ? $"global::{child.ChildTypeName}TraversalBuilder"
                    : $"global::{child.ChildNamespace}.{child.ChildTypeName}TraversalBuilder";

                writer.Line($"/// <summary>Pulls children rows from <c>{child.ChildTable}</c> (parent link <c>{child.ParentField}</c>). Pass <paramref name=\"configure\"/> to filter or descend further.</summary>");
                using (writer.Block($"public static {queryFqn} Include{child.PropertyName}(this {queryFqn} query, global::System.Action<{childBuilderFqn}>? configure = null)"))
                {
                    writer.Line($"var __sub = new {childBuilderFqn}();");
                    writer.Line("configure?.Invoke(__sub);");
                    writer.Line("var (__filter, __nested) = __sub.Build();");
                    writer.Line($"return query.WithInclude(new {IncludeChildrenNodeFqn}(\"{child.ChildTable}\", \"{child.ParentField}\", __filter, __nested, {child.HydratorExpression}, \"{child.SliceKey}\"));");
                }
            }

            foreach (var rel in relations)
            {
                if (!first)
                {
                    writer.Line();
                }

                first = false;
                EmitRelationIncludeExtension(writer, queryFqn, rel);
            }
        }
    }

    private static void EmitInlineRefInclude(CodeWriter writer, string typeName, InlineRefMember inline)
    {
        writer.Line();
        writer.Line($"/// <summary>Pulls the inline <c>[Reference, Inline]</c> record at <c>{inline.Field}</c> into the projection (<c>{inline.Field}.*</c>).</summary>");
        using (writer.Block($"public {typeName} Include{inline.PropertyName}()"))
        {
            writer.Line($"_nested.Add(new {IncludeInlineRefNodeFqn}(\"{inline.Field}\"));");
            writer.Line("return this;");
        }
    }

    private static void EmitChildrenInclude(CodeWriter writer, string typeName, ChildrenMember child)
    {
        var childBuilderFqn = string.IsNullOrEmpty(child.ChildNamespace)
            ? $"global::{child.ChildTypeName}TraversalBuilder"
            : $"global::{child.ChildNamespace}.{child.ChildTypeName}TraversalBuilder";

        writer.Line();
        writer.Line($"/// <summary>Pulls children rows from <c>{child.ChildTable}</c> (parent link <c>{child.ParentField}</c>). Pass <paramref name=\"configure\"/> to filter or descend further.</summary>");
        using (writer.Block($"public {typeName} Include{child.PropertyName}(global::System.Action<{childBuilderFqn}>? configure = null)"))
        {
            writer.Line($"var __sub = new {childBuilderFqn}();");
            writer.Line("configure?.Invoke(__sub);");
            writer.Line("var (__filter, __nested) = __sub.Build();");
            writer.Line($"_nested.Add(new {IncludeChildrenNodeFqn}(\"{child.ChildTable}\", \"{child.ParentField}\", __filter, __nested, {child.HydratorExpression}, \"{child.SliceKey}\"));");
            writer.Line("return this;");
        }
    }

    /// <summary>
    /// Inline-ref members for traversal: <c>[Reference, Inline]</c> properties on the
    /// table. The field name is the snake-cased SurrealDB column; the property name is
    /// the C# member that becomes <c>Include{PropertyName}()</c>.
    /// </summary>
    private static List<InlineRefMember> CollectInlineRefs(TableModel table)
    {
        var result = new List<InlineRefMember>();
        foreach (var p in table.Properties)
        {
            if (!p.Kinds.HasFlag(PropertyKind.Reference))
            {
                continue;
            }

            if (!p.IsInline)
            {
                continue;
            }

            result.Add(new InlineRefMember(p.Name, SurrealNaming.ToFieldName(p.Name)));
        }
        return result;
    }

    /// <summary>
    /// Children members for traversal: <c>[Children]</c> properties on the table whose
    /// element type is another <c>[Table]</c>, paired with the matching <c>[Parent]</c>
    /// field on the child that points back at this table. If no matching <c>[Parent]</c>
    /// exists the [Children] entry is skipped (mirrors <see cref="SchemaEmitter.EmitChildrenField"/>'s
    /// "no parent path → field omitted" guard).
    /// </summary>
    private static List<ChildrenMember> CollectChildren(TableModel parent, ModelGraph graph)
    {
        var result = new List<ChildrenMember>();
        foreach (var p in parent.Properties)
        {
            if (!p.Kinds.HasFlag(PropertyKind.Children))
            {
                continue;
            }

            var elementType = p.Type.ElementType ?? p.Type;
            var childTypeName = SurrealNaming.SimpleName(elementType.FullyQualifiedName);
            var childTable = graph.Tables.FirstOrDefault(t => t.Name == childTypeName);
            if (childTable is null)
            {
                continue;
            }

            var parentField = FindParentFieldName(parent, childTable);
            if (parentField is null)
            {
                continue;
            }

            var childEntityFqn = string.IsNullOrEmpty(childTable.Namespace)
                ? $"global::{childTable.Name}"
                : $"global::{childTable.Namespace}.{childTable.Name}";
            // Static lambda — no captures, gets cached as a delegate field once per
            // include node. The cast threads the entity through IEntity so we hit the
            // explicit-impl Hydrate emitted by PartialEmitter.
            var hydratorExpr =
                $"static (row, sink) => {{ var __e = new {childEntityFqn}(); ((global::Disruptor.Surface.Runtime.IEntity)__e).Hydrate(row, sink); }}";

            result.Add(new ChildrenMember(
                PropertyName: p.Name,
                ChildTable: SurrealNaming.ToTableName(childTable.Name),
                ChildTypeName: childTable.Name,
                ChildNamespace: childTable.Namespace,
                ParentField: parentField,
                HydratorExpression: hydratorExpr,
                SliceKey: SurrealNaming.ToFieldName(p.Name)));
        }
        return result;
    }

    /// <summary>
    /// Emit a relation <c>Include</c> on the traversal builder. Single-target
    /// within-aggregate gets a <c>configure</c> lambda for the target's own builder;
    /// multi-target (within-aggregate) and cross-aggregate are no-arg leaves.
    /// </summary>
    private static void EmitRelationInclude(CodeWriter writer, string typeName, RelationMember rel)
    {
        writer.Line();
        writer.Line($"/// <summary>{RelationDocBlurb(rel)}</summary>");

        if (rel.SingleTargetBuilderFqn is { } targetBuilder)
        {
            // Single-target within-aggregate: configure lambda + filter at target.
            using (writer.Block($"public {typeName} Include{rel.PropertyName}(global::System.Action<{targetBuilder}>? configure = null)"))
            {
                writer.Line($"var __sub = new {targetBuilder}();");
                writer.Line("configure?.Invoke(__sub);");
                writer.Line("var (__filter, __nested) = __sub.Build();");
                writer.Line($"_nested.Add(new {IncludeRelationNodeFqn}({RelationCtorArgs(rel, "__filter", "__nested")}));");
                writer.Line("return this;");
            }
            return;
        }

        // Multi-target or cross-aggregate: no configure lambda, no filter, no nesting.
        using (writer.Block($"public {typeName} Include{rel.PropertyName}()"))
        {
            writer.Line($"_nested.Add(new {IncludeRelationNodeFqn}({RelationCtorArgs(rel, "null", $"global::System.Array.Empty<{IncludeNodeFqn}>()")}));");
            writer.Line("return this;");
        }
    }

    /// <summary>Same shape as <see cref="EmitRelationInclude"/>, but on the Query&lt;T&gt; extension class.</summary>
    private static void EmitRelationIncludeExtension(CodeWriter writer, string queryFqn, RelationMember rel)
    {
        writer.Line($"/// <summary>{RelationDocBlurb(rel)}</summary>");

        if (rel.SingleTargetBuilderFqn is { } targetBuilder)
        {
            using (writer.Block($"public static {queryFqn} Include{rel.PropertyName}(this {queryFqn} query, global::System.Action<{targetBuilder}>? configure = null)"))
            {
                writer.Line($"var __sub = new {targetBuilder}();");
                writer.Line("configure?.Invoke(__sub);");
                writer.Line("var (__filter, __nested) = __sub.Build();");
                writer.Line($"return query.WithInclude(new {IncludeRelationNodeFqn}({RelationCtorArgs(rel, "__filter", "__nested")}));");
            }
            return;
        }

        using (writer.Block($"public static {queryFqn} Include{rel.PropertyName}(this {queryFqn} query)"))
        {
            writer.Line($"return query.WithInclude(new {IncludeRelationNodeFqn}({RelationCtorArgs(rel, "null", $"global::System.Array.Empty<{IncludeNodeFqn}>()")}));");
        }
    }

    /// <summary>
    /// Builds the positional ctor args for <c>IncludeRelationNode</c> — keeps the include
    /// emission consistent between the builder and the query extension.
    /// </summary>
    private static string RelationCtorArgs(RelationMember rel, string filterExpr, string nestedExpr)
        => $"\"{rel.EdgeName}\", {(rel.IsOutgoing ? "true" : "false")}, \"{rel.SliceKey}\", {(rel.IdsOnly ? "true" : "false")}, {(rel.SingleTargetTable is { } t ? $"\"{t}\"" : "null")}, {filterExpr}, {nestedExpr}, {rel.HydratorExpression}";

    private static string RelationDocBlurb(RelationMember rel)
    {
        var direction = rel.IsOutgoing ? "outgoing" : "incoming";
        var shape = rel.IdsOnly
            ? "edges only — id-typed (cross-aggregate)"
            : rel.SingleTargetBuilderFqn is null
                ? "target rows (multi-target — leaf, no further nesting)"
                : "target rows (single-target — supports nested traversal and target-side filter)";
        return $"Pulls {direction} <c>{rel.EdgeName}</c> relation {shape}.";
    }

    /// <summary>
    /// Relation members for traversal: every <c>[Forward]</c> / <c>[Inverse]</c>
    /// relation property on the table. Resolves the traversed side (target for outgoing,
    /// source for incoming), determines single vs multi target, and decides
    /// within-aggregate (graph projection, full target hydration) vs cross-aggregate
    /// (edge subselect, ids only).
    /// </summary>
    private static List<RelationMember> CollectRelations(TableModel table, ModelGraph graph)
    {
        var result = new List<RelationMember>();
        foreach (var p in table.Properties)
        {
            if (p.RelationRole == RelationRole.None)
            {
                continue;
            }

            if (p.RelationKindFullName is null)
            {
                continue;
            }

            var memberKind = graph.FindKind(p.RelationKindFullName);
            if (memberKind is null)
            {
                continue;
            }

            var forward = memberKind.Direction == RelationDirection.Forward
                ? memberKind
                : graph.FindKind(memberKind.PairedForwardFullName);
            if (forward is null)
            {
                continue;
            }

            var isOutgoing = p.RelationRole == RelationRole.ForwardRelation;
            var isCross = graph.IsCrossAggregate(forward.FullName);
            var sliceKey = SurrealNaming.ToFieldName(p.Name);
            var edgeName = SurrealNaming.ToEdgeName(forward.Name);

            // Cross-aggregate: edges-only emission; no single-target metadata needed.
            if (isCross)
            {
                result.Add(new RelationMember(
                    PropertyName: p.Name,
                    EdgeName: edgeName,
                    IsOutgoing: isOutgoing,
                    SliceKey: sliceKey,
                    IdsOnly: true,
                    SingleTargetTable: null,
                    SingleTargetBuilderFqn: null,
                    HydratorExpression: "null"));
                continue;
            }

            // Within-aggregate: figure out single vs multi target from the relation
            // unions. Direction Source = relation's source-side members (where
            // [Forward] attribute lives); Target = inverse-side members.
            var traversedDirection = isOutgoing ? UnionDirection.Target : UnionDirection.Source;
            var union = graph.Unions.FirstOrDefault(u =>
                u.Direction == traversedDirection && u.ForwardKindFullName == forward.FullName);

            if (union is not null)
            {
                // Multi-target: emit a switch-on-id-table-prefix hydrator dispatching
                // each row to the right concrete entity type.
                var hydrator = BuildMultiTargetHydrator(union, graph);
                result.Add(new RelationMember(
                    PropertyName: p.Name,
                    EdgeName: edgeName,
                    IsOutgoing: isOutgoing,
                    SliceKey: sliceKey,
                    IdsOnly: false,
                    SingleTargetTable: null,
                    SingleTargetBuilderFqn: null,
                    HydratorExpression: hydrator));
                continue;
            }

            // Single-target (no union → exactly one member on the traversed side).
            var single = FindSingleTraversedMember(forward.FullName, traversedDirection, graph);
            if (single is null)
            {
                continue; // No participants; skip silently.
            }

            var entityFqn = string.IsNullOrEmpty(single.Namespace)
                ? $"global::{single.Name}"
                : $"global::{single.Namespace}.{single.Name}";
            var builderFqn = string.IsNullOrEmpty(single.Namespace)
                ? $"global::{single.Name}TraversalBuilder"
                : $"global::{single.Namespace}.{single.Name}TraversalBuilder";
            var targetTable = SurrealNaming.ToTableName(single.Name);
            var hydratorExpr =
                $"static (row, sink) => {{ var __e = new {entityFqn}(); ((global::Disruptor.Surface.Runtime.IEntity)__e).Hydrate(row, sink); }}";

            result.Add(new RelationMember(
                PropertyName: p.Name,
                EdgeName: edgeName,
                IsOutgoing: isOutgoing,
                SliceKey: sliceKey,
                IdsOnly: false,
                SingleTargetTable: targetTable,
                SingleTargetBuilderFqn: builderFqn,
                HydratorExpression: hydratorExpr));
        }
        return result;
    }

    private static TableModel? FindSingleTraversedMember(string forwardKindFullName, UnionDirection traversed, ModelGraph graph)
    {
        TableModel? sole = null;
        foreach (var t in graph.Tables)
        {
            if (!HasRelationProperty(t, forwardKindFullName, traversed, graph))
            {
                continue;
            }

            if (sole is not null)
            {
                return null; // ambiguous — should be a multi-member union
            }

            sole = t;
        }
        return sole;
    }

    private static bool HasRelationProperty(TableModel table, string forwardKindFullName, UnionDirection side, ModelGraph graph)
    {
        // Source side of the edge = forward attribute holders.
        // Target side of the edge = inverse attribute holders (paired via the kind).
        var role = side == UnionDirection.Source ? RelationRole.ForwardRelation : RelationRole.InverseRelation;
        string? wanted;
        if (side == UnionDirection.Source)
        {
            wanted = forwardKindFullName;
        }
        else
        {
            var inverse = graph.RelationKinds.FirstOrDefault(k =>
                k.Direction == RelationDirection.Inverse && k.PairedForwardFullName == forwardKindFullName);
            wanted = inverse?.FullName;
        }
        if (wanted is null)
        {
            return false;
        }

        foreach (var p in table.Properties)
        {
            if (p.RelationRole == role && p.RelationKindFullName == wanted)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Multi-target hydrator: a single static lambda that switches on the row's
    /// <c>id:&lt;table&gt;</c> prefix to instantiate the right entity. Each member of
    /// the union gets a case; unmatched table prefixes silently skip (the row probably
    /// belongs to a target the user's model doesn't include — defensive, not load-bearing).
    /// </summary>
    private static string BuildMultiTargetHydrator(RelationUnion union, ModelGraph graph)
    {
        var cases = new List<string>();
        foreach (var fqn in union.MemberFullNames)
        {
            var member = graph.Tables.FirstOrDefault(t => t.FullName == fqn);
            if (member is null)
            {
                continue;
            }

            var entityFqn = string.IsNullOrEmpty(member.Namespace)
                ? $"global::{member.Name}"
                : $"global::{member.Namespace}.{member.Name}";
            var tableName = SurrealNaming.ToTableName(member.Name);
            cases.Add($"case \"{tableName}\": {{ var __e = new {entityFqn}(); ((global::Disruptor.Surface.Runtime.IEntity)__e).Hydrate(row, sink); break; }}");
        }

        return "static (row, sink) => { "
            + "if (row is not global::Disruptor.Surreal.Values.SurrealObjectValue __rowObj) return; "
            + "if (!global::Disruptor.Surface.Runtime.HydrationValue.TryReadRecordId(__rowObj, \"id\", out var __rid)) return; "
            + "switch (__rid.Table) { "
            + string.Join(" ", cases)
            + " } }";
    }

    private static string? FindParentFieldName(TableModel parent, TableModel child)
    {
        foreach (var cp in child.Properties)
        {
            if (!cp.Kinds.HasFlag(PropertyKind.Parent))
            {
                continue;
            }

            var parentTypeName = SurrealNaming.SimpleName(cp.Type.FullyQualifiedName);
            if (parentTypeName == parent.Name)
            {
                return SurrealNaming.ToFieldName(cp.Name);
            }
        }
        return null;
    }

    private readonly record struct InlineRefMember(string PropertyName, string Field);

    private readonly record struct ChildrenMember(
        string PropertyName,
        string ChildTable,
        string ChildTypeName,
        string ChildNamespace,
        string ParentField,
        string HydratorExpression,
        string SliceKey);

    private readonly record struct RelationMember(
        string PropertyName,
        string EdgeName,
        bool IsOutgoing,
        string SliceKey,
        bool IdsOnly,
        string? SingleTargetTable,
        string? SingleTargetBuilderFqn,
        string HydratorExpression);
}
