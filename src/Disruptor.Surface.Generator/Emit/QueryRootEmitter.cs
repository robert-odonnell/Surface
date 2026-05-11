using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits a partial fragment of the user's <c>[CompositionRoot]</c> exposing
/// <c>Workspace.Query.{Table}</c> entry points — one <see cref="Query{T}"/> per
/// <c>[Table]</c>. The shape mirrors <see cref="SchemaEmitter"/> /
/// <see cref="ReferenceRegistryEmitter"/>: the public accessor lives on the user's
/// partial, the actual table-name catalogue lives on an internal companion class so the
/// composition root surface stays uncluttered.
/// <para>
/// Skipped when no <c>[CompositionRoot]</c> is declared (or it isn't partial) — like the
/// other root-anchored emitters, there's nowhere to hang the accessor without one.
/// </para>
/// <para>
/// The accessor is static (matches <see cref="SchemaEmitter"/> / <see cref="ReferenceRegistryEmitter"/>),
/// so callers may write either <c>Workspace.Query.Constraints</c> or
/// <c>workspace.Query.Constraints</c>; the latter triggers CS0176 on access of static via
/// instance — prefer the type-qualified form.
/// </para>
/// </summary>
internal static class QueryRootEmitter
{
    private const string QueryFqn = "global::Disruptor.Surface.Runtime.Query.SurfaceQuery";
    private const string GeneratedClass = "GeneratedQueryRoot";

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

        var writer = new CodeWriter().Header();
        using (writer.Namespace(root.Namespace))
        {
            // Partial fragment of [CompositionRoot] — adds the static Query accessor.
            using (writer.Block(FormatTypeDeclaration(root.DeclaredAccessibility, root.Name)))
            {
                writer.Line("/// <summary>Read-mode query roots — one per <c>[Table]</c>. Compose with <c>.Where(predicate)</c> / <c>.WithId(id)</c>; terminate with <c>.ExecuteAsync(transport)</c>.</summary>");
                writer.Line($"public static {GeneratedClass} Query => {GeneratedClass}.Instance;");
            }

            writer.Line();

            // Internal companion — keeps the {CompositionRoot} surface clean. Singleton: query
            // factories are stateless, so a per-process instance is fine. Marked partial so
            // sibling emitters (EdgeQueryRootEmitter) can graft their own accessors onto the
            // same class.
            using (writer.Block($"public sealed partial class {GeneratedClass}"))
            {
                writer.Line($"public static readonly {GeneratedClass} Instance = new {GeneratedClass}();");
                writer.Line();
                writer.Line($"private {GeneratedClass}() {{ }}");

                foreach (var table in ordered)
                {
                    var tableName = SurrealNaming.ToTableName(table.Name);
                    var propertyName = PascalPluralize(table.Name);
                    var entityFqn = string.IsNullOrEmpty(table.Namespace)
                        ? $"global::{table.Name}"
                        : $"global::{table.Namespace}.{table.Name}";

                    writer.Line();
                    writer.Line($"/// <summary>Query root for <see cref=\"{entityFqn["global::".Length..]}\"/>.</summary>");
                    writer.Line($"public {QueryFqn}<{entityFqn}> {propertyName} => new(\"{tableName}\");");
                }
            }
        }

        spc.AddSource($"{root.FullName}.Query.g.cs", writer.ToSourceText());
    }

    /// <summary>
    /// Pascal-cased pluralisation of the C# type name (e.g. <c>Constraint</c> → <c>Constraints</c>,
    /// <c>Details</c> stays <c>Details</c>, <c>AcceptanceCriteria</c> stays
    /// <c>AcceptanceCriteria</c>). Mirrors what <see cref="SurrealNaming.ToTableName"/> does
    /// minus the snake-casing — the query-root property names must stay valid C# identifiers.
    /// </summary>
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
