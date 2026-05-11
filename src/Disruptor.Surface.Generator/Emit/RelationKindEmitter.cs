using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// For each forward relation attribute (e.g. <c>RestrictsAttribute : ForwardRelation</c>),
/// emits a sibling marker class without the <c>Attribute</c> suffix
/// (<c>Restricts : IRelationKind</c>) carrying the SurrealDB edge-table name as a static
/// abstract override. The class is the type witness used by
/// <c>SurrealSession.UnrelateAsync&lt;Restricts&gt;</c>, the static-virtual variant-query
/// terminals, and the per-kind variant-marker reflection — it gives the generic call a
/// compile-time anchor without threading a string literal through.
/// <para>
/// Inverse kinds get no marker — the edge is named after the forward, so the same
/// marker covers both directions; querying decides the side based on whether the owner
/// is the source or target.
/// </para>
/// <para>
/// The marker class has a private constructor — it's a type witness only, never
/// instantiated. Per-variant relation classes (<c>[Restricts]</c>-on-class with
/// <c>[In]</c> / <c>[Out]</c> / <c>[Property]</c> members) carry the edge data;
/// <see cref="RelationVariantEmitter"/> emits those.
/// </para>
/// <para>
/// Also emits the per-kind <c>{KindName}Id</c> typed-id struct (e.g. <c>RestrictsId</c>),
/// shared across every variant of the kind since they all live on the same edge table.
/// </para>
/// </summary>
internal static class RelationKindEmitter
{
    private const string IRelationKindFqn = "global::Disruptor.Surface.Runtime.IRelationKind";

    public static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        foreach (var kind in graph.RelationKinds)
        {
            if (kind.Direction != RelationDirection.Forward)
            {
                continue;
            }

            EmitForKind(spc, kind);
        }
    }

    private static void EmitForKind(SourceProductionContext spc, RelationKindModel kind)
    {
        var markerName = SurrealNaming.StripAttributeSuffix(kind.Name);
        var edgeName = SurrealNaming.ToEdgeName(kind.Name);

        var writer = new CodeWriter().Header();
        using (writer.Namespace(kind.Namespace))
        {
            using (writer.Block($"public sealed class {markerName} : {IRelationKindFqn}"))
            {
                writer.Line($"public static string EdgeName => \"{edgeName}\";");
                writer.Line($"private {markerName}() {{ }}");
            }

            // Per-kind {KindName}Id type — the typed id for the edge row itself. Single id type
            // shared across every variant of this kind, since every variant lives on the same
            // edge table (e.g. EpicRestriction and FeatureRestriction both have RestrictsId).
            // Variant-emitted SaveAsync mints via {KindName}Id.New(); the id flows as the edge
            // row's primary key. Per-variant ids would all carry Table => "restricts"
            // redundantly, so we emit one per kind, not one per variant.
            writer.Line();
            IdEmitter.WriteIdType(writer, $"{markerName}Id", edgeName, []);
        }

        var hint = string.IsNullOrEmpty(kind.Namespace)
            ? $"{markerName}.RelationKind.g.cs"
            : $"{kind.Namespace}.{markerName}.RelationKind.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }
}
