using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits the per-relation-kind marker interface — one per multi-member union side. The
/// interface inherits from <see cref="IEntity"/> so session methods that already
/// accept <c>IEntity</c> still take any union member.
/// <para>
/// File names follow the same hint convention as everything else in the generator
/// (<c>{FullName}.g.cs</c>) so the consumer can find them in the generated output
/// folder alongside the entity partials.
/// </para>
/// </summary>
internal static class UnionInterfaceEmitter
{
    private const string EntityInterface = "global::Disruptor.Surface.Runtime.IEntity";
    private const string RecordIdInterface = "global::Disruptor.Surface.Runtime.IRecordId";

    public static void Emit(SourceProductionContext spc, RelationUnion union)
    {
        EmitInterface(spc, union, union.InterfaceName, union.InterfaceFullName, EntityInterface, isIdSide: false);
        EmitInterface(spc, union, union.IdInterfaceName, union.IdInterfaceFullName, RecordIdInterface, isIdSide: true);
    }

    private static void EmitInterface(
        SourceProductionContext spc,
        RelationUnion union,
        string interfaceName,
        string interfaceFullName,
        string baseInterface,
        bool isIdSide)
    {
        var sideLabel = union.Direction == UnionDirection.Target ? "target" : "source";
        var kindLabel = isIdSide ? "id-side" : "entity-side";

        var writer = new CodeWriter().Header();
        using (writer.Namespace(union.Namespace))
        {
            writer.Line($"/// <summary>Generated {kindLabel} marker for the {sideLabel} union of the relation kind <c>{union.ForwardKindFullName}</c>.</summary>");
            writer.Line($"public interface {interfaceName} : {baseInterface} {{ }}");
        }

        spc.AddSource($"{interfaceFullName}.g.cs", writer.ToSourceText());
    }
}
