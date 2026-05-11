using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits a partial fragment of the user's <c>[CompositionRoot]</c> exposing
/// <c>Workspace.Hydrate.{Table}(ids)</c> entry points — one
/// <see cref="HydrationQuery{T}"/> factory per <c>[Table]</c>. The shape mirrors
/// <see cref="QueryRootEmitter"/>: the public accessor lives on the user's partial,
/// the actual table catalogue lives on an internal companion class.
/// <para>
/// Each per-table accessor is a method (not a property) because hydration takes a
/// concrete id list — typed <c>{Table}Id</c> overload + raw <c>RecordId</c> overload
/// for callers working in the canonical id space (e.g. cross-aggregate edge ids).
/// </para>
/// <para>
/// Skipped when no <c>[CompositionRoot]</c> is declared (or it isn't partial) — the
/// emitted body needs <c>Workspace.ReferenceRegistry</c> for session construction.
/// </para>
/// </summary>
internal static class HydrateRootEmitter
{
    private const string GeneratedClass = "GeneratedHydrationRoot";

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

        if (graph.Tables.Count == 0)
        {
            return;
        }

        var ordered = graph.Tables.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var refRegistryFqn = string.IsNullOrEmpty(root.Namespace)
            ? $"global::{root.Name}.ReferenceRegistry"
            : $"global::{root.Namespace}.{root.Name}.ReferenceRegistry";

        var writer = new CodeWriter().Header();
        using (writer.Namespace(root.Namespace))
        {
            // Partial fragment of [CompositionRoot] — adds the static Hydrate accessor.
            using (writer.Block(FormatTypeDeclaration(root.DeclaredAccessibility, root.Name)))
            {
                writer.Line($"public static {GeneratedClass} Hydrate => {GeneratedClass}.Instance;");
            }
            
            using (writer.Block($"public sealed class {GeneratedClass}"))
            {
                writer.Line($"public static readonly {GeneratedClass} Instance = new {GeneratedClass}();");
                writer.Line($"private {GeneratedClass}() {{ }}");

                foreach (var table in ordered)
                {
                    var tableName = SurrealNaming.ToTableName(table.Name);
                    var methodName = PascalPluralize(table.Name);
                    var entityFqn = string.IsNullOrEmpty(table.Namespace)
                        ? $"global::{table.Name}"
                        : $"global::{table.Namespace}.{table.Name}";
                    var idFqn = $"{entityFqn}Id";

                    using (writer.Block($"public {Namespaces.HydrationQueryFqn}<{entityFqn}> {methodName}({Namespaces.IEnumerableFqn}<{idFqn}> ids)"))
                    {
                        writer.Line("global::System.ArgumentNullException.ThrowIfNull(ids);");
                        writer.Line("var snap = SnapshotIds(ids);");
                        writer.Line($"return new {Namespaces.HydrationQueryFqn}<{entityFqn}>(\"{tableName}\", snap, {refRegistryFqn});");
                    }

                    using (writer.Block($"public {Namespaces.HydrationQueryFqn}<{entityFqn}> {methodName}({Namespaces.IEnumerableFqn}<{Namespaces.IRecordIdFqn}> ids)"))
                    {
                        writer.Line("global::System.ArgumentNullException.ThrowIfNull(ids);");
                        writer.Line("var snap = SnapshotIds(ids);");
                        writer.Line($"return new {Namespaces.HydrationQueryFqn}<{entityFqn}>(\"{tableName}\", snap, {refRegistryFqn});");
                    }
                }

                // Shared helper — collapses any id-typed enumerable into an IReadOnlyList<RecordId>.
                // Each typed {Table}Id implicitly converts to RecordId; raw IRecordId converts via
                // RecordId.From. Snapshotting up front means the HydrationQuery sees a stable list
                // even if the caller mutates the source enumerable mid-flight.
                using (writer.Block($"private static {Namespaces.IReadOnlyListFqn}<{Namespaces.RecordIdFqn}> SnapshotIds<TId>({Namespaces.IEnumerableFqn}<TId> ids) where TId : {Namespaces.IRecordIdFqn}"))
                {
                    using (writer.Block("if (ids is global::System.Collections.Generic.ICollection<TId> col)"))
                    {
                        writer.Line($"var arr = new {Namespaces.RecordIdFqn}[col.Count];");
                        writer.Line("var i = 0;");
                        using (writer.Block("foreach (var id in col)"))
                        {
                            writer.Line($"arr[i++] = {Namespaces.RecordIdFqn}.From(id);");
                        }

                        writer.Line("return arr;");
                    }

                    writer.Line($"var list = new global::System.Collections.Generic.List<{Namespaces.RecordIdFqn}>();");
                    using (writer.Block("foreach (var id in ids)"))
                    {
                        writer.Line($"list.Add({Namespaces.RecordIdFqn}.From(id));");
                    }

                    writer.Line("return list;");
                }
            }
        }

        spc.AddSource($"{root.FullName}.Hydrate.g.cs", writer.ToSourceText());
    }

    private static string PascalPluralize(string typeName)
        => Humanizer.InflectorExtensions.Pluralize(typeName, inputIsKnownToBeSingular: false);

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
