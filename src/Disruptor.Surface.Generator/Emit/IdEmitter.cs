using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits the per-table <c>{Name}Id</c> — a <c>readonly record struct</c> wrapping a
/// validated <c>string</c> and implementing <c>IRecordId</c>. Kept zero-alloc so id values
/// can flow through SurrealSession primitives without hydrating an entity. Two and only
/// two value forms are accepted (validated in the ctor via
/// <c>Disruptor.Surface.Runtime.RecordIdFormat.Validate</c>):
/// <list type="bullet">
///   <item>Ulid stringifications — what <c>{Name}Id.New()</c> mints (the default).</item>
///   <item>Short lower_snake_case slugs (max 32 chars) — opt-in for stable-named records.</item>
/// </list>
/// </summary>
internal static class IdEmitter
{
    public static void Emit(SourceProductionContext spc, TableModel table, ModelGraph graph)
    {
        var idTypeName = $"{table.Name}Id";
        var perTableMarkerName = $"I{table.Name}RecordId";
        var perTableMarkerFqn = string.IsNullOrEmpty(table.Namespace)
            ? $"global::{perTableMarkerName}"
            : $"global::{table.Namespace}.{perTableMarkerName}";

        // Id-side union interfaces (one per multi-member union this table is in).
        // Mirrors PartialEmitter's entity-side base list — IRestrictedById alongside IRestrictedBy.
        // The per-table marker (I{Name}RecordId) is appended unconditionally — it's the
        // opt-in surface for record-type-union endpoints: a user wanting `[Foo] partial
        // interface IFooTarget : IRecordId` extends `partial interface I{Name}RecordId :
        // IFooTarget` to enrol this table in the union.
        var idUnions = graph.UnionsForTable(table.FullName)
            .Select(u => $"global::{u.IdInterfaceFullName}")
            .Append(perTableMarkerFqn);

        var writer = new CodeWriter().Header();
        using (writer.Namespace(table.Namespace))
        {
            // Per-table id-side marker interface — emitted unconditionally as the union
            // opt-in surface. Generator-emitted "primary" partial keeps the type
            // bindable from user partials that add union-interface bases.
            writer.Line($"public partial interface {perTableMarkerName} : {Namespaces.IRecordIdType} {{ }}");
            writer.Line("");
            WriteIdType(writer, idTypeName, SurrealNaming.ToTableName(table.Name), idUnions);
        }

        var hint = string.IsNullOrEmpty(table.Namespace)
            ? $"{idTypeName}.g.cs"
            : $"{table.Namespace}.{idTypeName}.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }

    /// <summary>
    /// Emits a <c>readonly record struct {idTypeName}(string Value) : IRecordId, …</c>
    /// at the writer's current indentation, with <paramref name="tableName"/> baked into
    /// the <c>Table</c> property. Shared with <see cref="RelationKindEmitter"/> so per-kind
    /// id types (<c>RestrictsId</c>) get the same validation + factory shape as per-table
    /// ids (<c>ConstraintId</c>). <paramref name="extraBaseInterfaces"/> are appended to
    /// the base list — empty for per-kind ids, the id-side union markers for per-table ids.
    /// </summary>
    internal static void WriteIdType(
        CodeWriter writer,
        string idTypeName,
        string tableName,
        IEnumerable<string> extraBaseInterfaces)
    {
        var baseInterfaces = new[] {
            Namespaces.IRecordIdType }.Concat(extraBaseInterfaces);
        var baseList = string.Join(", ", baseInterfaces);

        using (writer.Block($"public readonly record struct {idTypeName}(string Value) : {baseList}"))
        {
            writer.Line($"public string Value {{ get; }} = {Namespaces.FormatType}.Validate(Value);");
            writer.Line($"public string Table => \"{tableName}\";");
            writer.Line("public string ToLiteral() => Value;");
            writer.Line($"public static {idTypeName} New() => new(global::System.Ulid.NewUlid().ToString());");
            writer.Line("public override string ToString() => Table + \":\" + Value;");
            writer.Line($"public static implicit operator {Namespaces.RecordIdType}({idTypeName} id) => new(id.Table, id.Value);");
        }
    }
}
