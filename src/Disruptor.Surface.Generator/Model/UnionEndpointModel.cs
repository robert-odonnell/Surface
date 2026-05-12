namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// Which side of a relation variant a <see cref="UnionEndpointModel"/> covers — the
/// source ("in") or the target ("out"). Mirrors the <c>In&lt;TKind&gt;</c> /
/// <c>Out&lt;TKind&gt;</c> attribute base the user's union attribute derived from.
/// </summary>
public enum UnionEndpointDirection
{
    In,
    Out,
}

/// <summary>
/// A user-declared record-type-union endpoint usable as the <c>[In]</c> or <c>[Out]</c>
/// property type on a relation variant. The user attributes an interface deriving from
/// <c>IRecordId</c> with an attribute that itself derives from <c>In&lt;TKind&gt;</c> or
/// <c>Out&lt;TKind&gt;</c>; participating <c>[Table]</c> classes opt in by extending
/// their per-table marker (<c>partial interface I{Name}RecordId : IFooTarget</c>).
/// </summary>
/// <param name="InterfaceFullName">FQN of the user-declared union interface (e.g. <c>M.IFooTarget</c>).</param>
/// <param name="KindFullName">FQN of the forward relation kind this union pins to (e.g. <c>M.RestrictsAttribute</c>).</param>
/// <param name="Direction">Whether the union is usable as an <c>[In]</c> or an <c>[Out]</c> endpoint.</param>
/// <param name="MemberTableFullNames">FQNs of the <c>[Table]</c> classes whose per-table marker extends this union.</param>
public sealed record UnionEndpointModel(
    string InterfaceFullName,
    string KindFullName,
    UnionEndpointDirection Direction,
    EquatableArray<string> MemberTableFullNames);
