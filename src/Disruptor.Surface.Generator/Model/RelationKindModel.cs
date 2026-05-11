namespace Disruptor.Surface.Generator.Model;

public enum RelationDirection
{
    Forward,
    Inverse,
}

/// <summary>
/// A user-defined attribute class that participates in the relation system by deriving
/// from <c>ForwardRelation</c> or <c>InverseRelation&lt;TForward&gt;</c>.
/// <para>
/// For inverse kinds, <see cref="PairedForwardFullName"/> is lifted from the generic
/// type argument of <c>InverseRelation&lt;TForward&gt;</c>. For forward kinds the
/// field is null — the matching inverse (if any) is found by walking
/// <see cref="Disruptor.Surface.Generator.Model.ModelGraph.RelationKinds"/> from the linker.
/// </para>
/// <para>
/// Edge payload now lives on per-variant relation classes (<c>[Restricts]</c>-on-class
/// with <c>[Property]</c> members), not on the attribute. See
/// <see cref="RelationVariantModel"/>.
/// </para>
/// </summary>
public sealed record RelationKindModel(
    string FullName,
    string Namespace,
    string Name,
    RelationDirection Direction,
    string? PairedForwardFullName);
