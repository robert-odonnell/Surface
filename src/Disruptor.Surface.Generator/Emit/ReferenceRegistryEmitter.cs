using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits the per-consumer <see cref="ReferenceFieldInfo"/> registry. Two parts:
/// <list type="number">
///   <item>An <c>internal sealed class GeneratedReferenceRegistry : IReferenceRegistry</c>
///         in the user's <c>[CompositionRoot]</c> namespace, populated with every
///         <c>[Reference]</c> field bucketed by referenced-table name.</item>
///   <item>A partial fragment of the <c>[CompositionRoot]</c> class adding a
///         <c>public static IReferenceRegistry ReferenceRegistry</c> property that
///         returns the singleton instance.</item>
/// </list>
/// Skipped when no <c>[CompositionRoot]</c> is declared — without one there's nowhere
/// to hang the static surface, and the caller can construct sessions with the
/// registry by hand.
/// <para>
/// No more <c>[ModuleInitializer]</c> + static facade. The registry is per-model,
/// passed into <see cref="SurrealSession"/>'s constructor by the user's emitted
/// <c>Load*Async</c>, so multiple Disruptor.Surface-generated models can coexist in the same
/// process.
/// </para>
/// </summary>
internal static class ReferenceRegistryEmitter
{
    private const string GeneratedClass = "GeneratedReferenceRegistry";

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

        var byReferenced = new SortedDictionary<string, List<(string Referencer, string Field, ReferenceDeletePolicy Behavior, bool Nullable)>>(StringComparer.Ordinal);

        foreach (var table in graph.Tables)
        {
            var referencerTable = SurrealNaming.ToTableName(table.Name);
            foreach (var p in table.Properties)
            {
                if (!p.Kinds.HasFlag(PropertyKind.Reference))
                {
                    continue;
                }

                if (!p.Type.IsTableType && p.Type.ElementType is null)
                {
                    continue;
                }

                var targetTypeFqn = StripNullableMarker(UnwrapTask(p.Type.FullyQualifiedName));
                var simpleTargetName = SurrealNaming.SimpleName(targetTypeFqn);
                var referencedTable = SurrealNaming.ToTableName(simpleTargetName);
                var fieldName = SurrealNaming.ToFieldName(p.Name);

                if (!byReferenced.TryGetValue(referencedTable, out var list))
                {
                    list = [];
                    byReferenced[referencedTable] = list;
                }
                list.Add((referencerTable, fieldName, p.ReferenceDelete, p.Type.IsNullable));
            }
        }

        var writer = new CodeWriter().Header();
        using (writer.Namespace(root.Namespace))
        {
            // Partial fragment of the user's [CompositionRoot] — adds the static accessor.
            using (writer.Block(FormatTypeDeclaration(root.DeclaredAccessibility, root.Name)))
            {
                writer.Line($"public static global::Disruptor.Surface.Runtime.IReferenceRegistry ReferenceRegistry => {GeneratedClass}.Instance;");
            }

            
            // Impl class — internal to the consumer assembly.
            using (writer.Block($"internal sealed class {GeneratedClass} : global::Disruptor.Surface.Runtime.IReferenceRegistry"))
            {
                writer.Line($"public static readonly {GeneratedClass} Instance = new {GeneratedClass}();");
                writer.Line("private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<global::Disruptor.Surface.Runtime.ReferenceFieldInfo>> _byReferencedTable = new(System.StringComparer.Ordinal)");
                writer.Line("{");
                using (writer.Indent())
                {
                    foreach (var kv in byReferenced)
                    {
                        writer.Line($"[\"{kv.Key}\"] = new global::Disruptor.Surface.Runtime.ReferenceFieldInfo[]");
                        writer.Line("{");
                        using (writer.Indent())
                        {
                            foreach (var entry in kv.Value)
                            {
                                writer.Line($"new(\"{entry.Referencer}\", \"{entry.Field}\", \"{kv.Key}\", global::Disruptor.Surface.Runtime.ReferenceDeleteBehavior.{entry.Behavior}, {(entry.Nullable ? "true" : "false")}),");
                            }
                        }

                        writer.Line("},");
                    }
                }
                writer.Line("};");
                writer.Line("public System.Collections.Generic.IReadOnlyList<global::Disruptor.Surface.Runtime.ReferenceFieldInfo> IncomingReferencesTo(string referencedTable) =>");
                using (writer.Indent())
                {
                    writer.Line("_byReferencedTable.TryGetValue(referencedTable, out var refs) ? refs : System.Array.Empty<global::Disruptor.Surface.Runtime.ReferenceFieldInfo>();");
                }
            }
        }

        spc.AddSource($"{root.FullName}.ReferenceRegistry.g.cs", writer.ToSourceText());
    }

    private static string UnwrapTask(string typeName)
    {
        const string prefix = "global::System.Threading.Tasks.Task<";
        if (typeName.StartsWith(prefix) && typeName.EndsWith(">"))
        {
            return typeName.Substring(prefix.Length, typeName.Length - prefix.Length - 1);
        }

        return typeName;
    }

    private static string StripNullableMarker(string typeName)
        => typeName.EndsWith("?") ? typeName[..^1] : typeName;

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
}
