using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits the <c>Workspace.Query.Edges</c> read-mode entry points — one
/// <see cref="EdgeQuery{TIn,TOut}"/> per forward <see cref="RelationKindModel"/>. The
/// type parameters resolve to id-side types: a single-member side gets the concrete
/// <c>{Name}Id</c>; a multi-member side gets the generated id-side union marker
/// (<c>IRestrictedById</c>, etc.) emitted by <see cref="UnionInterfaceEmitter"/>.
/// <para>
/// Surface lives on the same partial <c>GeneratedQueryRoot</c> that
/// <see cref="QueryRootEmitter"/> creates — a separate fragment that grafts an
/// <c>Edges</c> property pointing at a sibling <c>GeneratedEdgeQueryRoot</c> singleton.
/// Skipped when no <c>[CompositionRoot]</c> is declared (the query root anchor is missing)
/// or no forward relation kinds exist (nothing to expose).
/// </para>
/// </summary>
internal static class EdgeQueryRootEmitter
{
    private const string GeneratedRootClass = "GeneratedQueryRoot";
    private const string GeneratedEdgesClass = "GeneratedEdgeQueryRoot";

    public static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        if (graph.CompositionRoots.Count != 1)
        {
            return;
        }

        var root = graph.CompositionRoots[0];
        if (!root.IsPartial)
        {
            return;
        }

        var forwardKinds = graph.RelationKinds
            .Where(k => k.Direction == RelationDirection.Forward)
            .OrderBy(k => k.Name, StringComparer.Ordinal)
            .ToList();
        if (forwardKinds.Count == 0)
        {
            return;
        }

        var writer = new CodeWriter().Header();
        using (writer.Namespace(root.Namespace))
        {
            // Partial fragment of GeneratedQueryRoot — adds the Edges accessor. The base
            // partial (from QueryRootEmitter) defines the singleton instance + the per-table
            // accessors; this fragment just hangs an Edges entry off the same class.
            using (writer.Block($"public sealed partial class {GeneratedRootClass}"))
            {
                writer.Line($"public {GeneratedEdgesClass} Edges => {GeneratedEdgesClass}.Instance;");
            }
            
            // Sibling singleton holding the per-kind accessors. Stateless, mirrors the table
            // catalogue's shape.
            using (writer.Block($"public sealed class {GeneratedEdgesClass}"))
            {
                writer.Line($"public static readonly {GeneratedEdgesClass} Instance = new {GeneratedEdgesClass}();");
                writer.Line($"private {GeneratedEdgesClass}() {{ }}");

                foreach (var kind in forwardKinds)
                {
                    var propertyName = SurrealNaming.StripAttributeSuffix(kind.Name);
                    var edgeTable = SurrealNaming.ToEdgeName(kind.Name);
                    var sourceIdType = ResolveSourceIdType(graph, kind.FullName);
                    var targetIdType = ResolveTargetIdType(graph, kind.FullName);

                    if (sourceIdType is null || targetIdType is null)
                    {
                        // The kind's source or target set is empty — no entities carry the matching
                        // attribute. Skip silently; SchemaEmitter already comments the same scenario
                        // on the DDL side.
                        continue;
                    }

                    writer.Line($"public {Namespaces.EdgeQueryFqn}<{sourceIdType}, {targetIdType}> {propertyName} => new(\"{edgeTable}\");");
                }
            }
        }

        spc.AddSource($"{root.FullName}.QueryEdges.g.cs", writer.ToSourceText());
    }

    /// <summary>
    /// Resolves the id-side type for the source (forward-attribute carrier) side of a
    /// relation kind. Multi-member side → id-side union marker; single member → that
    /// table's typed id; empty → null.
    /// </summary>
    private static string? ResolveSourceIdType(ModelGraph graph, string forwardKindFullName)
    {
        // Source-side union (forward attribute holders).
        foreach (var union in graph.Unions)
        {
            if (union.Direction == UnionDirection.Source && union.ForwardKindFullName == forwardKindFullName)
            {
                return $"global::{union.IdInterfaceFullName}";
            }
        }

        return SingleMemberIdType(graph, t => HasForwardAttribute(t, forwardKindFullName));
    }

    /// <summary>
    /// Resolves the id-side type for the target (inverse-attribute carrier) side of a
    /// relation kind. Mirrors <see cref="ResolveSourceIdType"/>.
    /// </summary>
    private static string? ResolveTargetIdType(ModelGraph graph, string forwardKindFullName)
    {
        // Target-side union (inverse attribute holders).
        foreach (var union in graph.Unions)
        {
            if (union.Direction == UnionDirection.Target && union.ForwardKindFullName == forwardKindFullName)
            {
                return $"global::{union.IdInterfaceFullName}";
            }
        }

        var inverseKind = graph.RelationKinds.FirstOrDefault(k =>
            k.Direction == RelationDirection.Inverse && k.PairedForwardFullName == forwardKindFullName);
        if (inverseKind is null)
        {
            return null;
        }

        return SingleMemberIdType(graph, t => HasInverseAttribute(t, inverseKind.FullName));
    }

    private static string? SingleMemberIdType(ModelGraph graph, Func<TableModel, bool> predicate)
    {
        TableModel? single = null;
        foreach (var table in graph.Tables)
        {
            if (!predicate(table))
            {
                continue;
            }

            if (single is not null)
            {
                return null; // ambiguous — would be a union, but we already checked unions
            }

            single = table;
        }
        if (single is null)
        {
            return null;
        }

        return $"global::{single.FullName}Id";
    }

    private static bool HasForwardAttribute(TableModel table, string forwardKindFullName)
    {
        foreach (var p in table.Properties)
        {
            if (p.RelationRole == RelationRole.ForwardRelation && p.RelationKindFullName == forwardKindFullName)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasInverseAttribute(TableModel table, string inverseKindFullName)
    {
        foreach (var p in table.Properties)
        {
            if (p.RelationRole == RelationRole.InverseRelation && p.RelationKindFullName == inverseKindFullName)
            {
                return true;
            }
        }
        return false;
    }
}
