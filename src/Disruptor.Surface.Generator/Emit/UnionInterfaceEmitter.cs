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
    public static void Emit(SourceProductionContext spc, RelationUnion union)
    {
        EmitInterface(spc, union, union.InterfaceName, union.InterfaceFullName, Namespaces.EntityInterface);
        EmitInterface(spc, union, union.IdInterfaceName, union.IdInterfaceFullName, Namespaces.RecordIdInterface);
    }

    private static void EmitInterface(
        SourceProductionContext spc,
        RelationUnion union,
        string interfaceName,
        string interfaceFullName,
        string baseInterface)
    {
        var writer = new CodeWriter().Header();
        using (writer.Namespace(union.Namespace))
        {
            writer.Line($"public interface {interfaceName} : {baseInterface};");
        }

        spc.AddSource($"{interfaceFullName}.g.cs", writer.ToSourceText());
    }
}
