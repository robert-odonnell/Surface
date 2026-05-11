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
    private const string HydrationQueryFqn = "global::Disruptor.Surface.Runtime.Query.HydrationQuery";
    private const string RecordIdFqn = "global::Disruptor.Surface.Runtime.RecordId";
    private const string IRecordIdFqn = "global::Disruptor.Surface.Runtime.IRecordId";
    private const string IEnumerableFqn = "global::System.Collections.Generic.IEnumerable";
    private const string IReadOnlyListFqn = "global::System.Collections.Generic.IReadOnlyList";
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
                writer.Line("/// <summary>Hydration entry points — one per <c>[Table]</c>. Pass a list of ids; chain <c>.WithInclude(...)</c> to widen the slice; terminate with <c>.ExecuteAsync(transport, [lease])</c> to materialise into a tracked session.</summary>");
                writer.Line($"public static {GeneratedClass} Hydrate => {GeneratedClass}.Instance;");
            }

            writer.Line();

            using (writer.Block($"public sealed class {GeneratedClass}"))
            {
                writer.Line($"public static readonly {GeneratedClass} Instance = new {GeneratedClass}();");
                writer.Line();
                writer.Line($"private {GeneratedClass}() {{ }}");

                foreach (var table in ordered)
                {
                    var tableName = SurrealNaming.ToTableName(table.Name);
                    var methodName = PascalPluralize(table.Name);
                    var entityFqn = string.IsNullOrEmpty(table.Namespace)
                        ? $"global::{table.Name}"
                        : $"global::{table.Namespace}.{table.Name}";
                    var idFqn = $"{entityFqn}Id";

                    writer.Line();
                    writer.Line($"/// <summary>Hydrate the matching <see cref=\"{entityFqn["global::".Length..]}\"/> rows into a fresh tracked session. Typed-id overload — preferred call site shape.</summary>");
                    using (writer.Block($"public {HydrationQueryFqn}<{entityFqn}> {methodName}({IEnumerableFqn}<{idFqn}> ids)"))
                    {
                        writer.Line("global::System.ArgumentNullException.ThrowIfNull(ids);");
                        writer.Line("var snap = SnapshotIds(ids);");
                        writer.Line();
                        writer.Line($"return new {HydrationQueryFqn}<{entityFqn}>(\"{tableName}\", snap, {refRegistryFqn});");
                    }

                    writer.Line();
                    writer.Line("/// <summary>Raw-id overload — accepts any <see cref=\"global::Disruptor.Surface.Runtime.IRecordId\"/>; useful for cross-aggregate edge endpoints already collapsed to canonical record ids.</summary>");
                    using (writer.Block($"public {HydrationQueryFqn}<{entityFqn}> {methodName}({IEnumerableFqn}<{IRecordIdFqn}> ids)"))
                    {
                        writer.Line("global::System.ArgumentNullException.ThrowIfNull(ids);");
                        writer.Line("var snap = SnapshotIds(ids);");
                        writer.Line();
                        writer.Line($"return new {HydrationQueryFqn}<{entityFqn}>(\"{tableName}\", snap, {refRegistryFqn});");
                    }
                }

                // Shared helper — collapses any id-typed enumerable into an IReadOnlyList<RecordId>.
                // Each typed {Table}Id implicitly converts to RecordId; raw IRecordId converts via
                // RecordId.From. Snapshotting up front means the HydrationQuery sees a stable list
                // even if the caller mutates the source enumerable mid-flight.
                writer.Line();
                using (writer.Block($"private static {IReadOnlyListFqn}<{RecordIdFqn}> SnapshotIds<TId>({IEnumerableFqn}<TId> ids) where TId : {IRecordIdFqn}"))
                {
                    using (writer.Block("if (ids is global::System.Collections.Generic.ICollection<TId> col)"))
                    {
                        writer.Line($"var arr = new {RecordIdFqn}[col.Count];");
                        writer.Line("var i = 0;");
                        using (writer.Block("foreach (var id in col)"))
                        {
                            writer.Line($"arr[i++] = {RecordIdFqn}.From(id);");
                        }

                        writer.Line("return arr;");
                    }

                    writer.Line();
                    writer.Line($"var list = new global::System.Collections.Generic.List<{RecordIdFqn}>();");
                    using (writer.Block("foreach (var id in ids)"))
                    {
                        writer.Line($"list.Add({RecordIdFqn}.From(id));");
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
