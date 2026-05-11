using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits a per-<c>[Table]</c> <c>{Name}Q</c> static class carrying one
/// <c>PropertyExpr&lt;T&gt;</c> field per <c>[Property]</c>/<c>[Id]</c>. The factory is the
/// type-safe entry point for predicate composition:
/// <code>
/// var p = ConstraintQ.Description.Eq("security");
/// var q = workspace.Query.Constraints.Where(p);
/// </code>
/// <para>
/// Only scalar columns get accessors today — <c>[Reference]</c> / <c>[Parent]</c> /
/// <c>[Children]</c> / relation properties are skipped because the runtime predicate
/// vocabulary doesn't yet know how to express graph-walking constraints. They'll come back
/// when traversal-aware predicates land.
/// </para>
/// <para>
/// The accessor for <c>[Id]</c> is emitted as <c>PropertyExpr&lt;{Name}Id&gt;</c> so users
/// pass strongly-typed ids; <see cref="QueryCompiler"/> normalises to canonical
/// <c>RecordId</c> at parameter-binding time.
/// </para>
/// </summary>
internal static class PredicateFactoryEmitter
{
    private static bool IsElementCollection(TypeRef t) =>
        t.MetadataName is "System.Collections.Generic.IReadOnlyList`1"
                       or "System.Collections.Generic.IList`1"
                       or "System.Collections.Generic.List`1";

    public static void Emit(SourceProductionContext spc, TableModel table)
    {
        var members = CollectMembers(table);
        if (members.Count == 0)
        {
            // No scalar columns — nothing to emit (the {Name}Q class would be empty).
            return;
        }

        var qTypeName = $"{table.Name}Q";

        var writer = new CodeWriter().Header();
        using (writer.Namespace(table.Namespace))
        {
            using (writer.Block($"public static class {qTypeName}"))
            {
                foreach (var (propertyName, surrealField, csharpType) in members)
                {
                    writer.Line($"public static readonly {Namespaces.PropertyExprFqn}<{csharpType}> {propertyName} = new(\"{surrealField}\");");
                }
            }
        }

        var hint = string.IsNullOrEmpty(table.Namespace)
            ? $"{qTypeName}.g.cs"
            : $"{table.Namespace}.{qTypeName}.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }

    /// <summary>
    /// Returns the (property name, snake-cased SurrealDB field, fully-qualified C# type)
    /// for every property that should appear on the <c>{Name}Q</c> factory. <c>[Id]</c> is
    /// rendered against the SurrealDB built-in <c>id</c> field with the typed
    /// <c>{Name}Id</c> as its C# operand type. Other supported kinds are scalar
    /// <c>[Property]</c>; element-collection <c>[Property]</c> / <c>[Reference]</c> / <c>[Parent]</c>
    /// / <c>[Children]</c> / relation properties are skipped.
    /// </summary>
    private static List<(string PropertyName, string SurrealField, string CSharpType)> CollectMembers(TableModel table)
    {
        var result = new List<(string, string, string)>();
        var idFqn = string.IsNullOrEmpty(table.Namespace)
            ? $"global::{table.Name}Id"
            : $"global::{table.Namespace}.{table.Name}Id";

        foreach (var p in table.Properties)
        {
            if (p.RelationRole != RelationRole.None)
            {
                continue;
            }

            if (p.Kinds.HasFlag(PropertyKind.Id))
            {
                result.Add((p.Name, "id", idFqn));
                continue;
            }

            if (!p.Kinds.HasFlag(PropertyKind.Property))
            {
                continue;
            }

            // Element-collection [Property] stores as array<object> — equality predicates
            // against the whole array don't have a sensible SurrealQL shape yet. Skip
            // until we have a collection-aware predicate (Contains-element, Length, …).
            if (IsElementCollection(p.Type))
            {
                continue;
            }

            // Unmapped scalar types are flagged at validation time (CG025); skip them
            // here too so a `PropertyExpr<Uri>` doesn't show up in IntelliSense even
            // when the user has temporarily suppressed the diagnostic.
            if (!SchemaEmitter.IsMappableScalar(p.Type))
            {
                continue;
            }

            result.Add((p.Name, SurrealNaming.ToFieldName(p.Name), p.Type.FullyQualifiedName));
        }

        return result;
    }
}
