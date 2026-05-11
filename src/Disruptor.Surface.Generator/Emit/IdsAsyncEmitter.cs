using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits a per-<c>[Table]</c> <c>{Name}QueryIds</c> static class carrying the
/// <c>IdsAsync</c> extension on <see cref="Query{T}"/>:
/// <code>
/// IReadOnlyList&lt;CodeSymbolId&gt; ids = await workspace.Query.CodeSymbols
///     .Where(CodeSymbolQ.Name.Contains("Parser"))
///     .OrderBy(CodeSymbolQ.QualifiedName)
///     .Limit(50)
///     .IdsAsync(transport, ct);
/// </code>
/// <para>
/// The terminal verb for "give me the ids that match — nothing else." Compiles to
/// <c>SELECT id FROM table WHERE … ORDER BY … LIMIT … START …</c> via
/// <c>QueryCompiler.CompileIdsOnly</c> and projects each returned <see cref="RecordId"/>
/// into the typed <c>{Table}Id</c>. Includes are not supported on this path: id-only
/// selection is flat by definition; callers wanting traversal use <c>ExecuteAsync</c>.
/// </para>
/// <para>
/// Sits alongside <see cref="LoadEntryEmitter"/> (which handles the write-mode aggregate
/// load) and the per-table <see cref="PredicateFactoryEmitter"/> output. The body inlines
/// the compile + transport + materialise steps so the runtime doesn't need a same-named
/// instance method on <see cref="Query{T}"/> that would shadow this extension.
/// </para>
/// </summary>
internal static class IdsAsyncEmitter
{
    public static void Emit(SourceProductionContext spc, TableModel table)
    {
        var entityFqn = string.IsNullOrEmpty(table.Namespace)
            ? $"global::{table.Name}"
            : $"global::{table.Namespace}.{table.Name}";
        var idFqn = $"{entityFqn}Id";
        var className = $"{table.Name}QueryIds";

        var writer = new CodeWriter().Header();
        using (writer.Namespace(table.Namespace))
        {
            using (writer.Block($"public static class {className}"))
            {
                // Two overloads: Surreal db + Transaction tx. Both call QueryAsync directly
                // and consume the SDK's SurrealQueryResponse / SurrealValue tree — no JSON bridge.
                EmitOverload(Namespaces.SurrealFqn, "db");
                EmitOverload(Namespaces.TransactionFqn, "tx");
            }

            void EmitOverload(string paramTypeFqn, string paramName)
            {
                using (writer.Block($"public static async {Namespaces.TaskFqn}<{Namespaces.ReadOnlyListFqn}<{idFqn}>> IdsAsync(this {Namespaces.QueryFqn}<{entityFqn}> query, {paramTypeFqn} {paramName}, {Namespaces.CtFqn} ct = default)"))
                {
                    writer.Line("var (sql, __bindings) = query.CompileIdsOnly();");
                    writer.Line($"var __response = await {paramName}.QueryAsync(sql, __bindings, ct);");
                    writer.Line("var rows = __response.Count > 0 ? __response.Take(0) : null;");
                    
                    writer.Line($"var list = new {Namespaces.ListFqn}<{idFqn}>();");
                    using (writer.Block("if (rows is global::Disruptor.Surreal.Values.SurrealListValue __arr)"))
                    {
                        using (writer.Block("foreach (var __item in __arr.List)"))
                        {
                            writer.Line("if (__item is not global::Disruptor.Surreal.Values.SurrealObjectValue __row) continue;");
                            writer.Line("if (global::Disruptor.Surface.Runtime.HydrationValue.TryReadRecordId(__row, \"id\", out var rid))");
                            using (writer.Indent())
                            {
                                writer.Line($"list.Add(new {idFqn}(rid.Value));");
                            }
                        }
                    }

                    writer.Line("else if (rows is global::Disruptor.Surreal.Values.SurrealObjectValue __single)");
                    using (writer.BracedBlock())
                    {
                        writer.Line("if (global::Disruptor.Surface.Runtime.HydrationValue.TryReadRecordId(__single, \"id\", out var rid))");
                        using (writer.Indent())
                        {
                            writer.Line($"list.Add(new {idFqn}(rid.Value));");
                        }
                    }

                    writer.Line("return list;");
                }
            }
        }

        var hint = string.IsNullOrEmpty(table.Namespace)
            ? $"{className}.g.cs"
            : $"{table.Namespace}.{className}.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }
}
