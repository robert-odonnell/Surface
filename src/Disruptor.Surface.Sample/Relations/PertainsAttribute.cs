using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

// Demonstrates preview.54 union endpoints. A single variant of [Pertains] points at any
// of several Design-aggregate targets (Constraint / UserStory) via the IPertainsTarget
// union interface — avoiding the per-target-variant duplication you'd otherwise need.
public sealed class PertainsAttribute : ForwardRelation;
public sealed class PertainedByAttribute : InverseRelation<PertainsAttribute>;

/// <summary>
/// Marker attribute pinning the union interface <see cref="IPertainsTarget"/> to the
/// <see cref="PertainsAttribute"/> kind. Derives from <c>Out&lt;PertainsAttribute&gt;</c>
/// so the generator knows which side of the relation this union belongs to (target) and
/// which kind it's bound to. Applied to the union interface declaration.
/// </summary>
public sealed class PertainsTargetAttribute : Out<PertainsAttribute>;

/// <summary>
/// Union-endpoint interface for the target side of <see cref="PertainsAttribute"/>. The
/// participating record types opt in by extending their per-table marker:
/// <c>partial interface I{Name}RecordId : IPertainsTarget { }</c>. Any typed id whose
/// table is enrolled is accepted by an <c>[Out] partial IPertainsTarget Target</c>
/// property on a variant.
/// </summary>
[PertainsTarget]
public partial interface IPertainsTarget : Disruptor.Surface.Runtime.IRecordId;
