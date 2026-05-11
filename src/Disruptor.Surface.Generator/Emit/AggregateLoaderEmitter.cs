using System.Text;
using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// For each <c>[AggregateRoot]</c>, emits an internal static loader class that hydrates
/// the entire aggregate in a single SurrealQL round-trip.
/// <para>
/// The emitted query is one nested <c>SELECT</c> rooted at the aggregate's record id:
/// <list type="bullet">
///   <item>The root row pulls <c>*</c> plus <c>field.*</c> for each <c>[Reference]</c>
///         so the linked records (Details et al.) come back inlined.</item>
///   <item>One subselect per non-root member, scoped via the dotted parent path back to
///         the root: <c>WHERE feature.epic.design = $parent.id</c>. Each subselect
///         inlines its own <c>field.*</c> references.</item>
///   <item>One subselect per relation kind that touches this aggregate. Within-aggregate
///         and source-side cross-aggregate edges scope by <c>in.&lt;source-path&gt; =
///         $parent.id</c>; target-side cross-aggregate edges scope by an OR over the
///         distinct target paths into this aggregate.</item>
/// </list>
/// </para>
/// <para>
/// Why one query: separate per-table loaders had two failure modes — a <c>SELECT * FROM
/// details</c> step that pulled every Details row in the database, and edge filters
/// like <c>in.id INSIDE (SELECT VALUE id FROM constraints)</c> that pulled in every
/// design's edges. Both vanish when the loader uses <c>$parent.id</c> scoping inside
/// nested subselects.
/// </para>
/// </summary>
internal static class AggregateLoaderEmitter
{
    private const string Namespace = "Disruptor.Surface.Runtime";

    public static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        foreach (var agg in graph.Aggregates)
        {
            var root = graph.Tables.FirstOrDefault(t => t.FullName == agg.RootFullName);
            if (root is null)
            {
                continue;
            }

            EmitForAggregate(spc, graph, root, agg);
        }
    }

    private static void EmitForAggregate(SourceProductionContext spc, ModelGraph graph, TableModel root, AggregateModel agg)
    {
        var pathToRoot = BuildPathToRoot(graph, root, agg);
        var byFullName = graph.Tables.ToDictionary(t => t.FullName);
        var sql = BuildLoadSql(graph, root, agg, byFullName, pathToRoot);

        var writer = new CodeWriter().Header();
        writer.Line("using System.Threading;");
        writer.Line("using System.Threading.Tasks;");
        writer.Line("using Disruptor.Surreal.Values;");
        writer.Line();

        var loaderName = $"{root.Name}AggregateLoader";
        var rootIdType = $"global::{root.FullName}Id";

        using (writer.Namespace(Namespace))
        {
            using (writer.Block($"internal static class {loaderName}"))
            {
                // Two PopulateAsync overloads (Surreal db / Transaction tx). Each calls the
                // appropriate QueryAsync directly — no JSON bridge, no ISurrealTransport.
                EmitPopulateOverload("global::Disruptor.Surreal.SurrealClient db", "db");
                writer.Line();
                EmitPopulateOverload("global::Disruptor.Surreal.SurrealTransaction tx", "tx");
                writer.Line();

                EmitHelpers(writer);
            }
        }

        void EmitPopulateOverload(string handleParam, string handleName)
        {
            using (writer.Block($"public static async Task PopulateAsync(global::Disruptor.Surface.Runtime.SurrealSession ws, {handleParam}, {rootIdType} rootId, CancellationToken ct = default)"))
            {
                // Typed-CBOR binding for the root id — preserves Thing typing through CBOR
                // instead of inlining a SurrealQL record-literal string. The SQL references
                // it as `$_rootId`.
                writer.Line("var __bindings = new global::Disruptor.Surreal.Values.SurrealObject");
                writer.Line("{");
                using (writer.Indent())
                {
                    writer.Line("[\"_rootId\"] = new global::Disruptor.Surreal.Values.SurrealRecordIdValue(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk(rootId)),");
                }
                writer.Line("};");
                writer.Line("const string sql = \"\"\"");
                foreach (var line in sql.Split('\n'))
                {
                    writer.Line(line.TrimEnd('\r'));
                }
                writer.Line("\"\"\";");
                writer.Line();
                writer.Line($"var __response = await {handleName}.QueryAsync(sql, __bindings, ct);");
                writer.Line("var rootRow = ExtractFirstResultRow(__response);");
                writer.Line("if (rootRow is null) return;");
                writer.Line();

                writer.Line("global::Disruptor.Surface.Runtime.IHydrationSink sink = ws;");
                writer.Line();

                writer.Line($"var __root = new global::{root.FullName}();");
                writer.Line("((global::Disruptor.Surface.Runtime.IEntity)__root).Hydrate(rootRow, sink);");
                writer.Line("((global::Disruptor.Surface.Runtime.IEntity)__root).MarkAllSlicesLoaded(sink);");
                writer.Line();

                var orderedMembers = OrderedMembers(agg, byFullName, pathToRoot);
                foreach (var member in orderedMembers)
                {
                    if (member.FullName == root.FullName)
                    {
                        continue;
                    }

                    var tableName = SurrealNaming.ToTableName(member.Name);
                    writer.Line($"HydrateChildren<global::{member.FullName}>(rootRow, \"{tableName}\", sink);");
                }

                foreach (var fwdKind in graph.RelationKinds.Where(k => k.Direction == RelationDirection.Forward).OrderBy(k => k.Name, StringComparer.Ordinal))
                {
                    var whereClause = BuildEdgeWhere(graph, agg, fwdKind, pathToRoot);
                    if (whereClause is null)
                    {
                        continue;
                    }

                    var edgeName = SurrealNaming.ToEdgeName(fwdKind.Name);
                    writer.Line($"HydrateEdges(rootRow, \"_{edgeName}\", \"{edgeName}\", sink);");
                }
            }
        }

        spc.AddSource($"{Namespace}.{loaderName}.g.cs", writer.ToSourceText());
    }

    // ──────────────────────────── SQL builder ────────────────────────────────

    private static string BuildLoadSql(ModelGraph graph, TableModel root, AggregateModel agg, Dictionary<string, TableModel> byFullName, Dictionary<string, string> pathToRoot)
    {
        SurrealNaming.ToTableName(root.Name);
        var sb = new StringBuilder();

        // Root projection — `*` plus inline-expansion for each [Reference] field.
        sb.Append("SELECT *");
        foreach (var refField in InlineReferenceFieldNames(root))
        {
            sb.Append(", ").Append(refField).Append(".*");
        }

        // Per-non-root-member subselect, ordered by depth so the SQL reads top-down.
        var orderedMembers = OrderedMembers(agg, byFullName, pathToRoot);
        foreach (var member in orderedMembers)
        {
            if (member.FullName == root.FullName)
            {
                continue;
            }

            var memberTable = SurrealNaming.ToTableName(member.Name);
            var path = pathToRoot[member.FullName];

            sb.AppendLine(",");
            sb.Append("    (SELECT *");
            foreach (var refField in InlineReferenceFieldNames(member))
            {
                sb.Append(", ").Append(refField).Append(".*");
            }
            sb.Append(" FROM ").Append(memberTable)
              .Append(" WHERE ").Append(path).Append(" = $parent.id) AS ").Append(memberTable);
        }

        // Per-relation-kind edge subselect — one per forward kind that touches this
        // aggregate (either source or target side).
        foreach (var fwdKind in graph.RelationKinds.Where(k => k.Direction == RelationDirection.Forward).OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            var whereClause = BuildEdgeWhere(graph, agg, fwdKind, pathToRoot);
            if (whereClause is null)
            {
                continue;
            }

            var edgeName = SurrealNaming.ToEdgeName(fwdKind.Name);
            sb.AppendLine(",");
            sb.Append("    (SELECT id, in, out FROM ").Append(edgeName)
              .Append(" WHERE ").Append(whereClause).Append(") AS _").Append(edgeName);
        }

        sb.AppendLine();
        // Root id flows in as the `$_rootId` binding (typed SurrealRecordIdValue, CBOR-
        // encoded). No SurrealQL string literal, no escape rules.
        sb.Append("FROM $_rootId;");
        return sb.ToString();
    }

    /// <summary>
    /// Decides if + how an edge subselect should scope into this aggregate. Three cases:
    /// <list type="number">
    ///   <item>Source table is in this aggregate — edge belongs to us via <c>in</c>.
    ///         <c>WHERE in.&lt;source-path&gt; = $parent.id</c>.</item>
    ///   <item>Cross-aggregate, target tables in this aggregate — edge points at us via
    ///         <c>out</c>. WHERE-clause is the OR over distinct target paths.</item>
    ///   <item>Neither — return null, no subselect emitted.</item>
    /// </list>
    /// </summary>
    private static string? BuildEdgeWhere(ModelGraph graph, AggregateModel agg, RelationKindModel fwdKind, Dictionary<string, string> pathToRoot)
    {
        // Source side — any table in this aggregate that carries the forward attribute.
        // Multi-source kinds (those with a source-side union) need an OR over distinct
        // paths back to the root; single-source kinds collapse to a single equality.
        var sourceTablesInAgg = graph.Tables
            .Where(t => HasForwardAttribute(t, fwdKind.FullName))
            .Where(t => agg.MemberFullNames.Contains(t.FullName))
            .ToList();
        if (sourceTablesInAgg.Count > 0)
        {
            var sourcePaths = sourceTablesInAgg
                .Select(t => pathToRoot[t.FullName])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            var sourceClauses = sourcePaths.Select(p => MakePathEquality("in", p)).ToList();
            return sourceClauses.Count == 1
                ? sourceClauses[0]
                : "(" + string.Join(" OR ", sourceClauses) + ")";
        }

        // No source in this aggregate — only relevant if the kind is cross-aggregate AND
        // we hold the target side.
        if (!graph.IsCrossAggregate(fwdKind.FullName))
        {
            return null;
        }

        var inverseKind = graph.RelationKinds.FirstOrDefault(k =>
            k.Direction == RelationDirection.Inverse && k.PairedForwardFullName == fwdKind.FullName);
        if (inverseKind is null)
        {
            return null;
        }

        var targetTablesInAgg = graph.Tables
            .Where(t => agg.MemberFullNames.Contains(t.FullName))
            .Where(t => HasInverseAttribute(t, inverseKind.FullName))
            .ToList();
        if (targetTablesInAgg.Count == 0)
        {
            return null;
        }

        // Dedupe by dotted path — different tables can share an ancestry to the root
        // (constraints, epics → "design"). Sort for deterministic emission.
        var paths = targetTablesInAgg
            .Select(t => pathToRoot[t.FullName])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var clauses = paths.Select(p => MakePathEquality("out", p)).ToList();
        return clauses.Count == 1
            ? clauses[0]
            : "(" + string.Join(" OR ", clauses) + ")";
    }

    private static string MakePathEquality(string column, string path) =>
        string.IsNullOrEmpty(path) ? $"{column} = $parent.id" : $"{column}.{path} = $parent.id";

    /// <summary>
    /// BFS from the root, collecting the dotted parent path back to root for every
    /// reachable member. Root itself maps to the empty string.
    /// </summary>
    private static Dictionary<string, string> BuildPathToRoot(ModelGraph graph, TableModel root, AggregateModel agg)
    {
        var byFullName = graph.Tables.ToDictionary(t => t.FullName);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [root.FullName] = string.Empty,
        };
        var queue = new Queue<TableModel>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentPath = paths[current.FullName];

            foreach (var memberFullName in agg.MemberFullNames)
            {
                if (paths.ContainsKey(memberFullName))
                {
                    continue;
                }

                if (!byFullName.TryGetValue(memberFullName, out var member))
                {
                    continue;
                }

                var parentLink = FindParentLink(member, current.FullName);
                if (parentLink is null)
                {
                    continue;
                }

                var parentField = SurrealNaming.ToFieldName(parentLink);
                var dottedPath = string.IsNullOrEmpty(currentPath)
                    ? parentField
                    : $"{parentField}.{currentPath}";
                paths[member.FullName] = dottedPath;
                queue.Enqueue(member);
            }
        }
        return paths;
    }

    private static List<TableModel> OrderedMembers(AggregateModel agg, Dictionary<string, TableModel> byFullName, Dictionary<string, string> pathToRoot)
    {
        return [..agg.MemberFullNames
            .Where(byFullName.ContainsKey)
            .Select(m => byFullName[m])
            .OrderBy(t => HopCount(pathToRoot.TryGetValue(t.FullName, out var p) ? p : string.Empty))
            .ThenBy(t => t.Name, StringComparer.Ordinal)];
    }

    private static int HopCount(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return 0;
        }

        var dots = 0;
        foreach (var c in path) if (c == '.')
        {
            dots++;
        }

        return dots + 1;
    }

    /// <summary>
    /// Field names whose <c>[Reference]</c> targets should be inline-expanded into the
    /// loader query as <c>field.*</c>. Restricted to references explicitly tagged
    /// <c>[Inline]</c> — the owned/compositional sidecar carve-out (see CLAUDE.md). Plain
    /// <c>[Reference]</c> hydrates as an id only; the referenced record is treated as a
    /// foreign pointer the caller resolves separately.
    /// </summary>
    private static IEnumerable<string> InlineReferenceFieldNames(TableModel table)
    {
        foreach (var p in table.Properties)
        {
            if (!p.Kinds.HasFlag(PropertyKind.Reference))
            {
                continue;
            }

            if (!p.Type.IsTableType)
            {
                continue;
            }

            if (!p.IsInline)
            {
                continue;
            }

            yield return SurrealNaming.ToFieldName(p.Name);
        }
    }

    // ──────────────────────────── helpers (emitted) ──────────────────────────

    private static void EmitHelpers(CodeWriter writer)
    {
        writer.Line("/// <summary>Pull the first row out of a SurrealQueryResponse, or null when no row matched. Throws SurrealRpcException for any errored statement so DB failures don't masquerade as empty loads.</summary>");
        using (writer.Block("private static SurrealObjectValue? ExtractFirstResultRow(global::Disruptor.Surreal.SurrealQueryResponse response)"))
        {
            writer.Line("response.EnsureSuccess();");
            using (writer.Block("for (var i = 0; i < response.Count; i++)"))
            {
                writer.Line("var stmt = response.Statements[i];");
                writer.Line("if (stmt.Result is null) continue;");
                writer.Line("SurrealValue target = stmt.Result;");
                using (writer.Block("if (target is SurrealListValue arr)"))
                {
                    using (writer.Block("foreach (var item in arr.List)"))
                    {
                        writer.Line("if (item is SurrealObjectValue row) return row;");
                    }
                }

                writer.Line("else if (target is SurrealObjectValue obj) return obj;");
            }

            writer.Line("return null;");
        }

        writer.Line();
        writer.Line("/// <summary>Walk an array attached to <paramref name=\"key\"/> on <paramref name=\"parent\"/> and Hydrate each element as a <typeparamref name=\"T\"/>.</summary>");
        writer.Line("private static void HydrateChildren<T>(SurrealObjectValue parent, string key, global::Disruptor.Surface.Runtime.IHydrationSink sink)");
        using (writer.Indent())
        {
            writer.Line("where T : class, global::Disruptor.Surface.Runtime.IEntity, new()");
        }
        using (writer.BracedBlock())
        {
            writer.Line("if (!parent.Object.TryGetValue(key, out var v) || v is not SurrealListValue arr) return;");
            using (writer.Block("foreach (var item in arr.List)"))
            {
                writer.Line("if (item is not SurrealObjectValue row) continue;");
                writer.Line("var entity = new T();");
                writer.Line("((global::Disruptor.Surface.Runtime.IEntity)entity).Hydrate(row, sink);");
                writer.Line("((global::Disruptor.Surface.Runtime.IEntity)entity).MarkAllSlicesLoaded(sink);");
            }
        }

        writer.Line();
        writer.Line("/// <summary>Walk an edge array {{id, in, out}} and call <c>sink.Edge(in, edgeName, out)</c> per row.</summary>");
        using (writer.Block("private static void HydrateEdges(SurrealObjectValue parent, string key, string edgeName, global::Disruptor.Surface.Runtime.IHydrationSink sink)"))
        {
            writer.Line("if (!parent.Object.TryGetValue(key, out var v) || v is not SurrealListValue arr) return;");
            using (writer.Block("foreach (var item in arr.List)"))
            {
                writer.Line("if (item is not SurrealObjectValue row) continue;");
                writer.Line("if (!global::Disruptor.Surface.Runtime.HydrationValue.TryReadRecordId(row, \"in\", out var src)) continue;");
                writer.Line("if (!global::Disruptor.Surface.Runtime.HydrationValue.TryReadRecordId(row, \"out\", out var dst)) continue;");
                writer.Line("sink.Edge(src, edgeName, dst);");
            }
        }
    }

    // ──────────────────────────── lookup helpers (compile-time) ──────────────

    private static string? FindParentLink(TableModel child, string parentFullName)
    {
        foreach (var p in child.Properties)
        {
            if (!p.Kinds.HasFlag(PropertyKind.Parent))
            {
                continue;
            }

            var typeFqn = StripGlobalAndNullable(p.Type.FullyQualifiedName);
            if (typeFqn == parentFullName)
            {
                return p.Name;
            }
        }
        return null;
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
}
