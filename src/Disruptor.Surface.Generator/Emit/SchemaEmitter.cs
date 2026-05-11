using System.Text;
using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits the SurrealDB DDL covering every <c>[Table]</c> in the consuming assembly as an
/// ordered list of chunks. Each chunk is a self-contained statement (or small group of
/// related statements) suitable for one transport call. Iterate
/// <c>GeneratedSchema.Script</c> at boot:
/// <code>
/// foreach (var chunk in GeneratedSchema.Script)
///     await transport.ExecuteAsync(chunk);
/// </code>
/// All chunks are idempotent (<c>DEFINE … IF NOT EXISTS</c>); reapplications are safe.
/// <para>
/// Chunk layout (in order): entity-tables block →
/// per-<c>[Table]</c> field block → per-relation-kind table definition. Splitting the
/// script lets callers run pieces in separate transactions, filter / log / introspect
/// individual chunks, or apply only the bits they need for a migration.
/// </para>
/// <para>
/// Mapping (when adding a new shape, mirror it here too):
/// <list type="bullet">
///   <item>Entity table → <c>DEFINE TABLE {pluralised} SCHEMAFULL</c>.</item>
///   <item>Scalar <c>[Property]</c> → <c>TYPE string|int|bool|float|decimal|datetime</c>;
///         non-nullable types get a <c>DEFAULT</c>, nullable types wrap in <c>option&lt;T&gt;</c>.</item>
///   <item>Inline-element collection <c>[Property]</c> (<c>IReadOnlyList&lt;T&gt;</c> /
///         <c>IList&lt;T&gt;</c> / <c>List&lt;T&gt;</c> of records) → <c>TYPE array&lt;object&gt;
///         DEFAULT []</c> plus per-member <c>field.*.member</c> sub-field DDL.</item>
///   <item><c>[Reference]</c> → <c>TYPE record&lt;target&gt;</c> (or <c>option&lt;record&lt;…&gt;&gt;</c> when nullable),
///         plus <c>REFERENCE ON DELETE {behavior}</c> matching the <c>[Reject]</c>/<c>[Unset]</c>/
///         <c>[Cascade]</c>/<c>[Ignore]</c> attribute.</item>
///   <item><c>[Parent]</c> → <c>TYPE option&lt;record&lt;parent&gt;&gt; REFERENCE ON DELETE REJECT</c>.</item>
///   <item><c>[Children]</c> → <c>COMPUTED &lt;~(child_table FIELD parent_field)</c>.</item>
///   <item>Forward relation kind → <c>DEFINE TABLE {edge} TYPE RELATION FROM source TO target ENFORCED</c>.</item>
/// </list>
/// </para>
/// </summary>
internal static class SchemaEmitter
{
    // Collection shapes recognised as inline-element [Property] columns. Detection
    // matches what TableExtractor.ResolveInlineMembers walks: any of these three
    // System.Collections.Generic types with a record/POCO element.
    private static bool IsElementCollection(TypeRef t) =>
        t.MetadataName is "System.Collections.Generic.IReadOnlyList`1"
                       or "System.Collections.Generic.IList`1"
                       or "System.Collections.Generic.List`1";

    public static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        if (graph.Tables.Count == 0)
        {
            return;
        }
        if (graph.CompositionRoots.Count != 1)
        {
            return;
        }

        var root = graph.CompositionRoots[0];
        if (!root.IsPartial)
        {
            return;
        }

        var chunks = BuildChunks(graph);

        var writer = new CodeWriter().Header();
        using (writer.Namespace(root.Namespace))
        {
            // Partial fragment of the [CompositionRoot]: the public Schema accessor + the
            // convenience ApplySchemaAsync that just iterates and dispatches to transport.
            using (writer.Block(FormatTypeDeclaration(root.DeclaredAccessibility, root.Name)))
            {
                writer.Line("public static System.Collections.Generic.IReadOnlyList<string> Schema => DisruptorSurfaceSchema._chunks;");
                using (writer.Block("public static async global::System.Threading.Tasks.Task ApplySchemaAsync(global::Disruptor.Surreal.SurrealClient db, global::System.Threading.CancellationToken ct = default)"))
                {
                    using (writer.Block("foreach (var chunk in Schema)"))
                    {
                        writer.Line("var __resp = await db.QueryAsync(chunk, bindings: null, ct);");
                        writer.Line("__resp.EnsureSuccess();");
                    }
                }

                using (writer.Block("public static async global::System.Threading.Tasks.Task ApplySchemaAsync(global::Disruptor.Surreal.SurrealTransaction tx, global::System.Threading.CancellationToken ct = default)"))
                {
                    using (writer.Block("foreach (var chunk in Schema)"))
                    {
                        writer.Line("var __resp = await tx.QueryAsync(chunk, bindings: null, ct);");
                        writer.Line("__resp.EnsureSuccess();");
                    }
                }
            }

            // Internal companion class holds the actual chunk array — keeps the user's
            // partial surface uncluttered while still living in the same namespace so the
            // accessor can reach it without a global:: qualifier.
            using (writer.Block("internal static class DisruptorSurfaceSchema"))
            {
                writer.Line("internal static readonly string[] _chunks = new[]");
                writer.Line("{");
                using (writer.Indent())
                {
                    foreach (var chunk in chunks)
                    {
                        writer.Line("\"\"\"");
                        WriteRawStringContent(writer, chunk);
                        writer.Line("\"\"\",");
                    }
                }
                writer.Line("};");
            }
        }

        spc.AddSource($"{root.FullName}.Schema.g.cs", writer.ToSourceText());
    }

    private static void WriteRawStringContent(CodeWriter writer, string content)
    {
        var normalised = content.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalised.EndsWith("\n"))
        {
            normalised = normalised[..^1];
        }

        foreach (var line in normalised.Split('\n'))
        {
            writer.Line(line);
        }
    }

    private static string FormatTypeDeclaration(string accessibility, string typeName)
    {
        var formatted = FormatAccessibility(accessibility);
        return string.IsNullOrEmpty(formatted)
            ? $"partial class {typeName}"
            : $"{formatted} partial class {typeName}";
    }

    private static string FormatAccessibility(string raw) => raw switch
    {
        "Public" => "public",
        "Internal" => "internal",
        "Private" => "private",
        "Protected" => "protected",
        "ProtectedOrInternal" => "protected internal",
        "ProtectedAndInternal" => "private protected",
        "NotApplicable" => string.Empty,
        _ => raw.ToLowerInvariant(),
    };

    /// <summary>
    /// Build the per-chunk DDL strings. Order is fixed: entity-tables block first (so
    /// fields can reference them), then one chunk per <c>[Table]</c> for its fields,
    /// then one chunk per forward-relation kind for the edge table.
    /// </summary>
    private static List<string> BuildChunks(ModelGraph graph)
    {
        var chunks = new List<string>();
        var orderedTables = graph.Tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();

        // Chunk: entity table declarations.
        var entityTablesSb = new StringBuilder();
        foreach (var t in orderedTables)
        {
            entityTablesSb.Append("DEFINE TABLE IF NOT EXISTS ")
                          .Append(SurrealNaming.ToTableName(t.Name))
                          .AppendLine(" SCHEMAFULL;");
        }
        chunks.Add(entityTablesSb.ToString());

        // Chunks: per-entity field blocks.
        foreach (var t in orderedTables)
        {
            var fieldsSb = new StringBuilder();
            EmitTableFields(fieldsSb, t, graph);
            var content = fieldsSb.ToString();
            if (!string.IsNullOrWhiteSpace(content))
            {
                chunks.Add(content);
            }
        }

        // Chunks: per-relation-kind table definitions. Variants are grouped by the
        // attribute they carry (e.g. every [Restricts]-on-class variant lives under
        // RestrictsAttribute's FQN); the lookup keys exactly match RelationKindModel.FullName.
        var variantsByKind = graph.RelationVariants.ToLookup(v => v.KindAttributeFqn, StringComparer.Ordinal);

        var fwdKinds = graph.RelationKinds
            .Where(k => k.Direction == RelationDirection.Forward)
            .OrderBy(k => k.Name, StringComparer.Ordinal);
        foreach (var fwdKind in fwdKinds)
        {
            var variantsForKind = variantsByKind[fwdKind.FullName].ToList();
            var relSb = new StringBuilder();
            EmitRelationTable(relSb, fwdKind, graph, variantsForKind);
            var content = relSb.ToString();
            if (!string.IsNullOrWhiteSpace(content))
            {
                chunks.Add(content);
            }
        }

        return chunks;
    }

    private static void EmitTableFields(StringBuilder sb, TableModel table, ModelGraph graph)
    {
        var tableName = SurrealNaming.ToTableName(table.Name);
        var any = false;

        foreach (var p in table.Properties)
        {
            // [Id] is implicit on every Surreal record. Relation properties (forward/
            // inverse collections) are edge-table reads, not entity columns.
            if (p.Kinds.HasFlag(PropertyKind.Id))
            {
                continue;
            }

            if (p.RelationRole != RelationRole.None)
            {
                continue;
            }

            if (!any)
            {
                sb.Append("// ").AppendLine(tableName);
                any = true;
            }

            var fieldName = SurrealNaming.ToFieldName(p.Name);

            if (p.Kinds.HasFlag(PropertyKind.Reference))
            {
                EmitReferenceField(sb, tableName, fieldName, p);
            }
            else if (p.Kinds.HasFlag(PropertyKind.Parent))
            {
                EmitParentField(sb, tableName, fieldName, p);
            }
            else if (p.Kinds.HasFlag(PropertyKind.Children))
            {
                EmitChildrenField(sb, tableName, fieldName, table, p, graph);
            }
            else if (p.Kinds.HasFlag(PropertyKind.Property))
            {
                EmitPropertyField(sb, tableName, fieldName, p);
            }
        }
    }

    private static void EmitReferenceField(StringBuilder sb, string tableName, string fieldName, PropertyModel p)
    {
        var targetTable = SurrealNaming.ToTableName(SurrealNaming.SimpleName(p.Type.FullyQualifiedName));
        var typePart = p.Type.IsNullable
            ? $"option<record<{targetTable}>>"
            : $"record<{targetTable}>";
        var deletePart = ReferenceDeleteClause(p.ReferenceDelete);
        sb.Append("DEFINE FIELD IF NOT EXISTS ").Append(fieldName)
          .Append(" ON ").Append(tableName)
          .Append(" TYPE ").Append(typePart)
          .Append(' ').Append(deletePart).AppendLine(";");
    }

    private static void EmitParentField(StringBuilder sb, string tableName, string fieldName, PropertyModel p)
    {
        // Parent links are always option<record<...>> in Surreal — the field needs to
        // be nullable at the schema level so a child can be inserted before its parent
        // ref is set, even though the C# surface treats it as non-nullable in the
        // steady state. Delete behaviour is REJECT: you can't drop a parent that still
        // has children pointing at it.
        var parentTable = SurrealNaming.ToTableName(SurrealNaming.SimpleName(p.Type.FullyQualifiedName));
        sb.Append("DEFINE FIELD IF NOT EXISTS ").Append(fieldName)
          .Append(" ON ").Append(tableName)
          .Append(" TYPE option<record<").Append(parentTable).Append(">>")
          .AppendLine(" REFERENCE ON DELETE REJECT;");
    }

    private static void EmitChildrenField(StringBuilder sb, string tableName, string fieldName, TableModel parent, PropertyModel p, ModelGraph graph)
    {
        // Computed reverse-fk: <~(child_table FIELD parent_field). Find the [Parent] on
        // the child element type that points back at this parent table.
        var elementType = p.Type.ElementType ?? p.Type;
        var childTypeName = SurrealNaming.SimpleName(elementType.FullyQualifiedName);
        var childTable = graph.Tables.FirstOrDefault(t => t.Name == childTypeName);
        if (childTable is null)
        {
            sb.Append("// SCHEMA: child element type '").Append(childTypeName).Append("' for ")
              .Append(tableName).Append('.').Append(fieldName).AppendLine(" not a [Table]; field omitted.");
            return;
        }

        var parentField = FindParentFieldName(parent, childTable);
        if (parentField is null)
        {
            sb.Append("// SCHEMA: no [Parent] on '").Append(childTable.Name).Append("' pointing at '")
              .Append(parent.Name).Append("' for ").Append(tableName).Append('.').Append(fieldName)
              .AppendLine("; field omitted.");
            return;
        }

        sb.Append("DEFINE FIELD IF NOT EXISTS ").Append(fieldName)
          .Append(" ON ").Append(tableName)
          .Append(" COMPUTED <~(").Append(SurrealNaming.ToTableName(childTable.Name))
          .Append(" FIELD ").Append(parentField).AppendLine(");");
    }

    private static void EmitPropertyField(StringBuilder sb, string tableName, string fieldName, PropertyModel p)
    {
        // Inline-element collection — array<object> at the field, plus {field}.*.{member}
        // sub-field DDL for each public instance property of the element type.
        // InlineMembers is populated by TableExtractor.ResolveInlineMembers.
        if (IsElementCollection(p.Type) && p.InlineMembers.Count > 0)
        {
            sb.Append("DEFINE FIELD IF NOT EXISTS ").Append(fieldName)
              .Append(" ON ").Append(tableName).AppendLine(" TYPE array<object> DEFAULT [];");

            foreach (var im in p.InlineMembers)
            {
                var memberFieldName = SurrealNaming.ToFieldName(im.Name);
                var (memberType, memberDefault) = MapScalarType(im.Type);
                if (memberType is null)
                {
                    sb.Append("// SCHEMA: inline member type '").Append(im.Type.FullyQualifiedName)
                      .Append("' on ").Append(tableName).Append('.').Append(fieldName)
                      .Append(".*.").Append(memberFieldName).AppendLine(" not mapped; sub-field omitted.");
                    continue;
                }

                sb.Append("DEFINE FIELD IF NOT EXISTS ").Append(fieldName).Append(".*.").Append(memberFieldName)
                  .Append(" ON ").Append(tableName)
                  .Append(" TYPE ").Append(memberType);
                if (memberDefault is not null)
                {
                    sb.Append(" DEFAULT ").Append(memberDefault);
                }
                sb.AppendLine(";");
            }
            return;
        }

        var (surrealType, defaultExpr) = MapScalarType(p.Type);
        if (surrealType is null)
        {
            sb.Append("// SCHEMA: scalar type '").Append(p.Type.FullyQualifiedName)
              .Append("' on ").Append(tableName).Append('.').Append(fieldName)
              .AppendLine(" not mapped; field omitted.");
            return;
        }

        sb.Append("DEFINE FIELD IF NOT EXISTS ").Append(fieldName)
          .Append(" ON ").Append(tableName)
          .Append(" TYPE ").Append(surrealType);
        if (defaultExpr is not null)
        {
            sb.Append(" DEFAULT ").Append(defaultExpr);
        }
        sb.AppendLine(";");
    }

    /// <summary>
    /// True iff <paramref name="type"/> has a SurrealDB scalar mapping (string, int,
    /// float, decimal, bool, datetime, ulid, …). Validation passes call this from
    /// <see cref="ModelGenerator.Emit"/> to flag <c>[Property]</c> fields whose type
    /// would otherwise compile as CLR but produce no schema column — the query/write
    /// path would then fail only at the database, not at build time.
    /// </summary>
    public static bool IsMappableScalar(TypeRef type) => MapScalarType(type).Type is not null;

    internal static (string? Type, string? Default) MapScalarType(TypeRef type)
    {
        var fqn = StripGlobal(type.FullyQualifiedName);
        if (fqn.EndsWith("?"))
        {
            fqn = fqn[..^1];
        }

        var (raw, def) = fqn switch
        {
            "string" or "System.String"               => ("string",   "\"\""),
            "int" or "System.Int32"                   => ("int",      "0"),
            "long" or "System.Int64"                  => ("int",      "0"),
            "bool" or "System.Boolean"                => ("bool",     "false"),
            "double" or "System.Double"               => ("float",    "0"),
            "float" or "System.Single"                => ("float",    "0"),
            "decimal" or "System.Decimal"             => ("decimal",  "0"),
            "System.DateTime" or "System.DateTimeOffset" => ("datetime", null),
            "System.Guid" or "System.Ulid"            => ("string",   null),
            _ => (null, null),
        };

        if (raw is null)
        {
            return (null, null);
        }
        if (type.IsNullable)
        {
            return ($"option<{raw}>", null);
        }
        return (raw, def);
    }

    private static string ReferenceDeleteClause(ReferenceDeletePolicy policy) => policy switch
    {
        ReferenceDeletePolicy.Reject  => "REFERENCE ON DELETE REJECT",
        ReferenceDeletePolicy.Unset   => "REFERENCE ON DELETE UNSET",
        ReferenceDeletePolicy.Cascade => "REFERENCE ON DELETE CASCADE",
        ReferenceDeletePolicy.Ignore  => "REFERENCE ON DELETE IGNORE",
        _ => "REFERENCE ON DELETE REJECT",
    };

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

    private static void EmitRelationTable(
        StringBuilder sb,
        RelationKindModel fwdKind,
        ModelGraph graph,
        IReadOnlyList<RelationVariantModel> variantsForKind)
    {
        var edgeName = SurrealNaming.ToEdgeName(fwdKind.Name);
        var sourceTables = FindRelationInTables(graph, fwdKind, variantsForKind)
            .Select(t => SurrealNaming.ToTableName(t.Name))
            .ToList();
        var targetTables = FindRelationOutTables(graph, fwdKind, variantsForKind)
            .Select(t => SurrealNaming.ToTableName(t.Name))
            .ToList();

        if (sourceTables.Count == 0 || targetTables.Count == 0)
        {
            sb.Append("// SCHEMA: relation '").Append(edgeName)
              .AppendLine("' missing source or target tables; relation table omitted.");
            return;
        }

        // Multi-variant kinds (e.g. EpicRestriction + FeatureRestriction both [Restricts])
        // need SCHEMALESS so each variant's payload columns can coexist on the same edge
        // table — SCHEMAFULL would force one rigid column set. Single-variant and
        // zero-variant (legacy entity-property-only) kinds keep SCHEMAFULL.
        var tableMode = variantsForKind.Count <= 1 ? "SCHEMAFULL" : "SCHEMALESS";

        sb.Append("DEFINE TABLE IF NOT EXISTS ").Append(edgeName).Append(' ').AppendLine(tableMode)
          .AppendLine("TYPE RELATION")
          .Append("FROM ").AppendLine(string.Join("|", sourceTables))
          .Append("TO ").AppendLine(string.Join("|", targetTables))
          .AppendLine("ENFORCED;");

        // Schema-level uniqueness on (in, out) — duplicate edges between the same pair
        // are rejected at the index. The runtime relies on this as the sole uniqueness
        // guard: a duplicate RELATE errors against the index, so idempotent re-imports
        // require loading the aggregate first (the commit planner skips RELATE for
        // edges already present at session start).
        sb.Append("DEFINE INDEX IF NOT EXISTS unique_relationship ON TABLE ")
          .Append(edgeName).AppendLine(" COLUMNS in, out UNIQUE;");

        // Per-variant payload fields. Only emit when there's exactly one variant — the
        // SCHEMAFULL path requires a stable column set and multiple variants would
        // disagree on shape. Multi-variant kinds fall through with no DEFINE FIELD; the
        // SCHEMALESS table accepts each variant's payload at write time.
        if (variantsForKind.Count == 1)
        {
            foreach (var p in variantsForKind[0].PayloadProperties)
            {
                if (p.Role != RelationVariantPropertyRole.Property)
                {
                    // Defensive: PayloadProperties only ever holds Property-role entries
                    // by construction, but skipping non-Property keeps the emitter robust
                    // against any future role expansion.
                    continue;
                }

                var (fieldType, fieldDefault) = MapScalarType(p.Type);
                if (fieldType is null)
                {
                    continue;
                }

                sb.Append("DEFINE FIELD IF NOT EXISTS ").Append(p.FieldName)
                  .Append(" ON ").Append(edgeName)
                  .Append(" TYPE ").Append(fieldType);

                if (fieldDefault is not null)
                {
                    sb.Append(" DEFAULT ").Append(fieldDefault);
                }

                sb.AppendLine(";");
            }
        }
    }

    /// <summary>
    /// Resolves the set of source tables for a forward relation kind. Unions two
    /// information channels: (1) the variant classes that carry the kind attribute
    /// (each <c>[In]</c> endpoint names a source entity or a typed id), and (2) the
    /// legacy entity-property scan (entities that themselves carry the forward
    /// attribute on a read-side collection property). The legacy scan is a transitional
    /// fallback — kinds without variant classes (e.g. every kind in the current Sample)
    /// still need to emit a correct schema.
    /// </summary>
    private static List<TableModel> FindRelationInTables(
        ModelGraph graph,
        RelationKindModel fwdKind,
        IReadOnlyList<RelationVariantModel> variantsForKind)
        => CollectEndpointTables(
            graph,
            variantsForKind,
            takeIn: true,
            legacyScan: () => ScanEntityPropertiesForRole(graph, fwdKind.FullName, RelationRole.ForwardRelation));

    /// <summary>
    /// Resolves the set of target tables for a forward relation kind. Mirrors
    /// <see cref="FindRelationInTables"/> — variant <c>[Out]</c> endpoints unioned with
    /// the legacy entity-property scan over the paired inverse attribute.
    /// </summary>
    private static List<TableModel> FindRelationOutTables(
        ModelGraph graph,
        RelationKindModel fwdKind,
        IReadOnlyList<RelationVariantModel> variantsForKind)
    {
        var inverseKind = graph.RelationKinds.FirstOrDefault(k =>
            k.Direction == RelationDirection.Inverse && k.PairedForwardFullName == fwdKind.FullName);

        return CollectEndpointTables(
            graph,
            variantsForKind,
            takeIn: false,
            legacyScan: () => inverseKind is null
                ? []
                : ScanEntityPropertiesForRole(graph, inverseKind.FullName, RelationRole.InverseRelation));
    }

    private static List<TableModel> CollectEndpointTables(
        ModelGraph graph,
        IReadOnlyList<RelationVariantModel> variantsForKind,
        bool takeIn,
        Func<List<TableModel>> legacyScan)
    {
        // Dedupe by table FullName so a variant declaring the same endpoint twice (or
        // the legacy scan catching what a variant already named) doesn't double-emit
        // in the FROM / TO list.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TableModel>();

        foreach (var variant in variantsForKind)
        {
            var endpoint = takeIn ? variant.In : variant.Out;
            var resolved = ResolveEndpointTable(graph, endpoint.Type);
            if (resolved is null)
            {
                continue;
            }

            if (seen.Add(resolved.FullName))
            {
                result.Add(resolved);
            }
        }

        foreach (var t in legacyScan())
        {
            if (seen.Add(t.FullName))
            {
                result.Add(t);
            }
        }

        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        return result;
    }

    /// <summary>
    /// Resolves the [Table] a variant endpoint type points at. Entity-typed endpoints
    /// (within-aggregate) resolve by simple name; typed-id endpoints (cross-aggregate,
    /// e.g. <c>EpicId</c>) resolve after stripping the trailing <c>Id</c>. The first-hit
    /// match always wins — a literal <c>FooId</c> [Table] (rare but legal) shadows the
    /// stripped lookup so we don't surprise the user by collapsing two distinct tables.
    /// </summary>
    private static TableModel? ResolveEndpointTable(ModelGraph graph, TypeRef endpointType)
    {
        var simpleName = SurrealNaming.SimpleName(endpointType.FullyQualifiedName);

        var exactMatch = graph.Tables.FirstOrDefault(t => t.Name == simpleName);
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        if (simpleName.EndsWith("Id"))
        {
            var stripped = simpleName[..^"Id".Length];
            var strippedMatch = graph.Tables.FirstOrDefault(t => t.Name == stripped);
            if (strippedMatch is not null)
            {
                return strippedMatch;
            }
        }

        return null;
    }

    /// <summary>
    /// Walks every table's property list looking for one that carries the given relation
    /// role + kind attribute (forward or inverse). Same logic as the pre-variant
    /// <c>FindSourceTables</c> / <c>FindTargetTables</c>, kept as a transitional
    /// fallback so kinds without a variant class still emit a correct schema.
    /// </summary>
    private static List<TableModel> ScanEntityPropertiesForRole(ModelGraph graph, string kindFullName, RelationRole role)
    {
        var result = new List<TableModel>();
        foreach (var t in graph.Tables)
        {
            foreach (var p in t.Properties)
            {
                if (p.RelationRole == role && p.RelationKindFullName == kindFullName)
                {
                    result.Add(t);
                    break;
                }
            }
        }
        return result;
    }

    private static string StripGlobal(string fqn) =>
        fqn.StartsWith("global::") ? fqn[8..] : fqn;
}
