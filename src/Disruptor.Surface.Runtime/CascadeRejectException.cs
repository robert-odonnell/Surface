namespace Disruptor.Surface.Runtime;

/// <summary>
/// One blocker recorded by <see cref="SurrealSession.DeleteAsync"/>'s pre-flight cascade
/// resolve: the entity (and field) that points at <see cref="BlockedTarget"/> with a
/// <see cref="ReferenceDeleteBehavior.Reject"/> policy and is itself NOT being cascaded
/// away in the same delete plan.
/// </summary>
public readonly record struct CascadeRejectBlocker(
    RecordId Referencer,
    string FieldName,
    RecordId BlockedTarget);

/// <summary>
/// Thrown by <see cref="SurrealSession.DeleteAsync"/> when the pre-flight cascade resolve
/// finds at least one <see cref="ReferenceDeleteBehavior.Reject"/>-tagged reference
/// pointing at a record that would be deleted (directly or transitively). No DELETE is
/// dispatched — the substrate never sees the request. Inspect <see cref="Blockers"/> to
/// surface the conflict to the user (which entities, which fields, which targets).
/// <para>
/// The three-phase resolve runs Cascade + Unset to fixpoint first, so a Reject blocker
/// whose owner is itself cascading away (transitively) does NOT throw — only steady-state
/// blockers count.
/// </para>
/// </summary>
public sealed class CascadeRejectException(IReadOnlyList<CascadeRejectBlocker> blockers)
    : Exception(BuildMessage(blockers))
{
    /// <summary>The list of (referencer, field, blocked target) triples that prevented the delete.</summary>
    public IReadOnlyList<CascadeRejectBlocker> Blockers { get; } = blockers;

    private static string BuildMessage(IReadOnlyList<CascadeRejectBlocker> blockers)
    {
        var first = blockers[0];
        var extra = blockers.Count > 1 ? $" (and {blockers.Count - 1} more)" : "";
        return $"Cascade-delete blocked by Reject reference: {first.Referencer}.{first.FieldName} → {first.BlockedTarget}{extra}.";
    }
}
