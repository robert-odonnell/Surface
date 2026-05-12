namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// One variant enrolled in a <see cref="SharedShapeModel"/>. Captures everything the
/// emitter needs to generate factory dispatch + per-kind query: the variant class FQN
/// (for instantiation), the per-kind marker type FQN (the <c>TKind</c> argument of
/// <c>Create&lt;TKind&gt;</c>), and the edge table name (for SQL).
/// </summary>
public sealed record SharedShapeVariantBinding(
    string VariantFullName,
    string KindMarkerFullName,
    string EdgeName);

/// <summary>
/// A user-declared interface deriving from <c>IRelationVariant</c> together with the
/// variants implementing it. The generator emits a partial fragment of the interface
/// carrying a static <c>Create&lt;TKind&gt;(Action&lt;I&gt; init)</c> factory plus
/// <c>QueryOutgoingAsync</c> / <c>QueryIncomingAsync</c> terminals that fan out across
/// every member kind's edge table.
/// </summary>
/// <param name="InterfaceFullName">FQN of the user-declared interface (no <c>global::</c>).</param>
/// <param name="Namespace">Namespace, or empty string for global namespace.</param>
/// <param name="Name">Short type name (with leading <c>I</c>).</param>
/// <param name="IsPartial">Whether the interface is declared <c>partial</c> — required to receive emitted static methods (CG033 otherwise).</param>
/// <param name="DeclaredAccessibility">Interface-level accessibility — emitted partial fragment matches.</param>
/// <param name="SourceTypeFullyQualifiedName">Common source endpoint type across every member variant (<c>global::</c>-prefixed), or <c>null</c> when variants disagree (CG034).</param>
/// <param name="TargetTypeFullyQualifiedName">Common target endpoint type across every member variant.</param>
/// <param name="Variants">Variants enrolled in this contract, sorted by FQN.</param>
public sealed record SharedShapeModel(
    string InterfaceFullName,
    string Namespace,
    string Name,
    bool IsPartial,
    string DeclaredAccessibility,
    string? SourceTypeFullyQualifiedName,
    string? TargetTypeFullyQualifiedName,
    EquatableArray<SharedShapeVariantBinding> Variants);
