using Disruptor.Surreal;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Per-entity Save context. Generator-emitted <see cref="IEntity.SaveAsync"/> bodies use
/// this to recurse into dependencies, check whether an entity is already known, dispatch
/// SurrealQL into the active transaction, and mark themselves as saved.
/// </summary>
/// <remarks>
/// The session is the orchestration boundary; entities just describe their own structure.
/// <see cref="SurrealSession.SaveAsync(IEntity, Transaction, System.Threading.CancellationToken)"/>
/// constructs an <see cref="ISaveContext"/>, calls the root entity's
/// <see cref="IEntity.SaveAsync"/>, and lets recursion drive itself through the
/// <see cref="SaveAsync"/> callback back into the session.
/// </remarks>
public interface ISaveContext
{
    /// <summary>The transaction to dispatch into. Entities use this for their own RPC sends.</summary>
    Transaction Transaction { get; }

    /// <summary>
    /// True iff <paramref name="id"/> is in the session's identity map — either because it
    /// was loaded from the DB, or because Save has already dispatched a CREATE for it in
    /// this pass. Forward-reference walks check this to decide whether to recurse.
    /// </summary>
    bool IsTracked(IRecordId id);

    /// <summary>
    /// Recursive Save callback. Per-entity SaveAsync invokes this for forward dependencies
    /// and new children. The session orchestrates the recursion (cycle detection, ordering)
    /// so each entity stays a structural describer.
    /// </summary>
    Task SaveAsync(IEntity entity, CancellationToken ct);

    /// <summary>
    /// Mark <paramref name="entity"/> as saved in this Save pass. Adds the entity to the
    /// identity map (if not already present) so subsequent <see cref="IsTracked"/> checks
    /// see it. Generator-emitted SaveAsync calls this after dispatching its own CREATE/UPDATE.
    /// </summary>
    void MarkSaved(IEntity entity);
}
