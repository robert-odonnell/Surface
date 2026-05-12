using Disruptor.Surface.Sample.Relations;

namespace Disruptor.Surface.Sample.Models;

// Per-table opt-ins enrolling Constraint and UserStory in the IPertainsTarget union.
// Each per-table marker `I{Name}RecordId` is emitted by the generator alongside the
// `{Name}Id` struct (preview.54 phase 1); the user-side partial below extends it with
// the union interface, which transitively makes the {Name}Id struct implement the
// union and become assignable to an [Out] partial IPertainsTarget Target property.

public partial interface IConstraintRecordId : IPertainsTarget;
public partial interface IUserStoryRecordId : IPertainsTarget;
