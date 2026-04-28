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
/// field is null until <see cref="Disruptor.Surface.Generator.Pipeline.RelationLinker"/> fills it
/// in (with the matching inverse's full name, when one exists).
/// </para>
/// </summary>
public sealed record RelationKindModel(
    string FullName,
    string Namespace,
    string Name,
    RelationDirection Direction,
    string? PairedForwardFullName);
