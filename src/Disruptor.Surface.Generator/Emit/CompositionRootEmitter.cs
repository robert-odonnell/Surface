using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits a partial declaration of the user's <c>[CompositionRoot]</c>-tagged class with
/// one instance <c>Load{Root}Async(ISurrealTransport, {Root}Id, CancellationToken)</c>
/// method per <c>[AggregateRoot]</c>. The user owns construction (transport wiring,
/// caches, telemetry, …) entirely — this emitter only contributes the load methods, no
/// ctor, no fields, no base class. No <c>[CompositionRoot]</c> in the compilation → no
/// <c>.g.cs</c> emitted at all.
/// </summary>
internal static class CompositionRootEmitter
{
    private const string SessionFqn   = "global::Disruptor.Surface.Runtime.SurrealSession";
    private const string CtFqn        = "global::System.Threading.CancellationToken";
    private const string TaskFqn      = "global::System.Threading.Tasks.Task";

    public static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        if (graph.CompositionRoots.Count == 0)
        {
            return;
        }

        var root = graph.CompositionRoots[0];

        var aggregateRoots = new List<TableModel>();
        foreach (var t in graph.Tables)
        {
            if (t.IsAggregateRoot)
            {
                aggregateRoots.Add(t);
            }
        }

        if (aggregateRoots.Count == 0)
        {
            return;
        }

        var writer = new CodeWriter().Header();
        using (writer.Namespace(root.Namespace))
        {
            var declaration = FormatTypeDeclaration(root.DeclaredAccessibility, root.Name);
            using (writer.Block(declaration))
            {
                for (var i = 0; i < aggregateRoots.Count; i++)
                {
                    if (i > 0)
                    {
                        writer.Line();
                    }

                    var aggRoot = aggregateRoots[i];
                    var idType = $"global::{aggRoot.FullName}Id";
                    var rootName = aggRoot.Name;
                    var loaderFqn = $"global::Disruptor.Surface.Runtime.{rootName}AggregateLoader";

                    writer.Line($"/// <summary>Hydrates a snapshot of the {rootName} aggregate rooted at <paramref name=\"rootId\"/>. Read-only — for write-mode, use the <c>Transaction</c> overload and pass the same txn into <c>SaveAsync</c>.</summary>");
                    using (writer.Block($"public async {TaskFqn}<{SessionFqn}> Load{rootName}Async(global::Disruptor.Surreal.SurrealClient db, {idType} rootId, {CtFqn} ct = default)"))
                    {
                        writer.Line($"var ws = new {SessionFqn}(ReferenceRegistry);");
                        writer.Line($"await {loaderFqn}.PopulateAsync(ws, db, rootId, ct);");
                        writer.Line("return ws;");
                    }

                    writer.Line();
                    writer.Line($"/// <summary>Hydrates a snapshot of the {rootName} aggregate inside <paramref name=\"tx\"/>. Reads see in-txn writes from the same transaction; pass the same <paramref name=\"tx\"/> into <c>SaveAsync</c> for writes that participate in the same atomic unit.</summary>");
                    using (writer.Block($"public async {TaskFqn}<{SessionFqn}> Load{rootName}Async(global::Disruptor.Surreal.SurrealTransaction tx, {idType} rootId, {CtFqn} ct = default)"))
                    {
                        writer.Line($"var ws = new {SessionFqn}(ReferenceRegistry);");
                        writer.Line($"await {loaderFqn}.PopulateAsync(ws, tx, rootId, ct);");
                        writer.Line("return ws;");
                    }
                }
            }
        }

        var hint = $"{root.FullName}.CompositionRoot.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
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
}
