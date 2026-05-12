using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Per user-declared <see cref="SharedShapeModel"/>, emits a partial interface fragment
/// carrying a single static factory method:
/// <code>
/// public static IShared Create&lt;TKind&gt;(Action&lt;IShared&gt; init)
///     where TKind : IRelationKind { … }
/// </code>
/// — an if-chain dispatching on <c>typeof(TKind)</c> to <c>new ConcreteVariant()</c>,
/// then running the user's initialiser before returning the instance.
/// <para>
/// Polymorphic query terminals are intentionally NOT emitted — the per-kind dispatch on
/// the read side stays on the user. The factory removes the corresponding boilerplate
/// from the write side without inventing a kind-union feature; relation kinds remain
/// distinct edge tables and concrete variant classes remain the substrate of writes.
/// </para>
/// <para>
/// Skipped (with a CG diagnostic) for: interfaces not declared <c>partial</c> (CG033)
/// — we can't graft a static method onto a non-partial interface — and interfaces with
/// zero enrolled variants (CG035, warning) — emitting a Create that always throws would
/// just be noise.
/// </para>
/// </summary>
internal static class SharedShapeEmitter
{
    public static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        if (graph.SharedShapes.Count == 0)
        {
            return;
        }

        foreach (var shape in graph.SharedShapes)
        {
            // Non-partial interface can't host a generated static method. CG033 is
            // surfaced from ModelGenerator.Emit so the user gets the diagnostic; the
            // interface still works as a marker via the regular implements check.
            if (!shape.IsPartial)
            {
                continue;
            }

            // Dead interface with no enrolled variants — CG035 fires as a warning from
            // ModelGenerator.Emit. Skip emit so we don't ship a `Create<TKind>` that
            // always throws.
            if (shape.Variants.Count == 0)
            {
                continue;
            }

            EmitFactoryFragment(spc, shape);
        }
    }

    private static void EmitFactoryFragment(SourceProductionContext spc, SharedShapeModel shape)
    {
        var interfaceFqn = string.IsNullOrEmpty(shape.Namespace)
            ? $"global::{shape.Name}"
            : $"global::{shape.Namespace}.{shape.Name}";
        var accessibility = FormatAccessibility(shape.DeclaredAccessibility);

        var writer = new CodeWriter().Header();
        using (writer.Namespace(shape.Namespace))
        {
            var declaration = string.IsNullOrEmpty(accessibility)
                ? $"partial interface {shape.Name}"
                : $"{accessibility} partial interface {shape.Name}";
            using (writer.Block(declaration))
            {
                EmitCreateMethod(writer, shape, interfaceFqn);
            }
        }

        var hint = string.IsNullOrEmpty(shape.Namespace)
            ? $"{shape.Name}.SharedShape.g.cs"
            : $"{shape.Namespace}.{shape.Name}.SharedShape.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }

    /// <summary>
    /// Emits the factory body: an if-chain over <c>typeof(TKind)</c> per enrolled
    /// variant, falling through to <c>ArgumentException</c> for unknown kinds. The
    /// user's <see cref="System.Action{T}"/> initialiser runs against the constructed
    /// instance so the call site reads as a single expression:
    /// <code>var edge = IShared.Create&lt;Calls&gt;(e => { e.Source = s; e.Target = t; });</code>
    /// </summary>
    private static void EmitCreateMethod(CodeWriter writer, SharedShapeModel shape, string interfaceFqn)
    {
        // `where TKind : IRelationKind` pins the generic argument to the per-kind marker
        // class (e.g. `Calls`, `References`) — the type witness emitted by RelationKindEmitter.
        var signature = $"public static {interfaceFqn} Create<TKind>(global::System.Action<{interfaceFqn}> init) where TKind : global::Disruptor.Surface.Runtime.IRelationKind";
        using (writer.Block(signature))
        {
            writer.Line("global::System.ArgumentNullException.ThrowIfNull(init);");
            writer.Line($"{interfaceFqn} __instance;");

            var first = true;
            foreach (var binding in shape.Variants)
            {
                var marker = binding.KindMarkerFullName; // already global::-prefixed
                var variantFqn = $"global::{binding.VariantFullName}";
                var keyword = first ? "if" : "else if";
                writer.Line($"{keyword} (typeof(TKind) == typeof({marker}))");
                using (writer.BracedBlock())
                {
                    writer.Line($"__instance = new {variantFqn}();");
                }
                first = false;
            }

            writer.Line("else");
            using (writer.BracedBlock())
            {
                writer.Line($"throw new global::System.ArgumentException(\"Relation kind '\" + typeof(TKind).Name + \"' has no variant implementing {EscapeForString(shape.InterfaceFullName)}.\", nameof(TKind));");
            }

            writer.Line("init(__instance);");
            writer.Line("return __instance;");
        }
    }

    private static string EscapeForString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatAccessibility(string raw) => raw switch
    {
        "Public" => "public",
        "Internal" => "internal",
        "NotApplicable" => string.Empty,
        _ => raw.ToLowerInvariant(),
    };
}
