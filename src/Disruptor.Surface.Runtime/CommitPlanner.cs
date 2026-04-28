#nullable enable

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Converts <see cref="PendingState"/> into a phased, ordered <see cref="Command"/> list
/// ready for the SurrealQL emitter. Implements the reference-delete planner (§11) and
/// the phase ordering (§16) from the commit-command-emission spec.
/// <list type="number">
///   <item>Resolve reference delete behavior for every record marked for deletion —
///         walks the <see cref="ReferenceRegistry"/>, applies Reject / Unset / Cascade /
///         Ignore against the effective final state, and folds generated Unset/Delete
///         intents back into pending state. Iterates until no new commands appear.</item>
///   <item>If any Reject blockers remain, throw <see cref="CommitPlanRejectException"/>.</item>
///   <item>Emit deletes-before-recreate (closed segments where the record existed at
///         the start of the segment), then creates/upserts, then field set/unset on
///         surviving records, then relation removals, then relation additions, then
///         final-segment record deletes.</item>
/// </list>
/// </summary>
public static class CommitPlanner
{
    public static IReadOnlyList<Command> Build(PendingState pending, IReferenceRegistry registry)
    {
        // Reference-delete resolution mutates pending state (records get ApplyDelete,
        // ApplyUnset for cascades / unsets). Operate on a deep copy so the caller's
        // PendingState stays untouched — RenderBatch becomes idempotent and safe to
        // call repeatedly for diagnostics.
        pending = pending.Clone();
        ResolveReferenceDeletes(pending, registry);

        var preDeletes        = new List<Command>(); // §16 phase 5
        var creates           = new List<Command>(); // §16 phase 6
        var fieldUpdates      = new List<Command>(); // §16 phase 7
        var relationRemovals  = new List<Command>(); // §16 phase 8
        var relationAdditions = new List<Command>(); // §16 phase 9
        var finalDeletes      = new List<Command>(); // §16 phase 10

        // Cache "is X used as a reference value somewhere?" for the dead-weight check
        // below. Built once across the whole pending set, queried per-record.
        var referencedIds = BuildReferencedIdSet(pending);

        foreach (var rec in pending.Records.Values)
        {
            EmitRecord(rec, referencedIds, preDeletes, creates, fieldUpdates, finalDeletes);
        }

        foreach (var rel in pending.Relations.Values)
        {
            EmitRelation(rel, relationRemovals, relationAdditions);
        }

        // Bulk unrelates run in the removal phase so additions queued for the same
        // (kind, source) / (kind, target) pair re-establish the edge after the DELETE.
        foreach (var (kind, source) in pending.BulkUnrelateFrom)
        {
            relationRemovals.Add(Command.UnrelateAllFrom(source, kind));
        }
        foreach (var (kind, target) in pending.BulkUnrelateTo)
        {
            relationRemovals.Add(Command.UnrelateAllTo(target, kind));
        }

        var plan = new List<Command>(preDeletes.Count + creates.Count + fieldUpdates.Count
                                     + relationRemovals.Count + relationAdditions.Count + finalDeletes.Count);
        plan.AddRange(preDeletes);
        plan.AddRange(creates);
        plan.AddRange(fieldUpdates);
        plan.AddRange(relationRemovals);
        plan.AddRange(relationAdditions);
        plan.AddRange(finalDeletes);
        return plan;
    }

    // ──────────────────────────── reference-delete resolution ────────────────
    //
    // Three phases (from the punch-list spec):
    //   1. Explicit deletes are already in pending state — the user called Delete().
    //   2. Apply Cascade + Unset to fixpoint (no Reject collection here — anything that
    //      gets cascade-deleted in a later iteration shouldn't have produced a Reject
    //      blocker in an earlier one).
    //   3. After the graph stabilises, scan once more for surviving Reject blockers.
    //   4. If any blockers, throw — otherwise the plan emits cleanly.
    //
    // Walks pending.References (the at-end snapshot) — never the session's mutable live
    // dict. The live dict gets cleaned up as a read-side concern; the planner needs the
    // immutable transition state to reason about the reference graph correctly.

    private static void ResolveReferenceDeletes(PendingState pending, IReferenceRegistry registry)
    {
        // Phase 2: cascade + unset to fixpoint.
        var processed = new HashSet<RecordId>();
        bool madeProgress;
        do
        {
            madeProgress = false;
            foreach (var rec in pending.Records.Values.ToList())
            {
                if (!IsEffectivelyDeleted(rec)) continue;
                if (!processed.Add(rec.Id)) continue;

                foreach (var (ownerId, fieldName, info) in EffectiveIncomingReferences(rec.Id, pending, registry))
                {
                    switch (info.Behavior)
                    {
                        case ReferenceDeleteBehavior.Unset:
                            // Generated Unset folded back into pending state. Mirror in
                            // References so subsequent fixpoint iterations don't see the
                            // owner as still pointing at the deleted target.
                            pending.GetOrCreateRecord(ownerId).ApplyUnset(fieldName);
                            pending.UnsetReferenceTarget(ownerId, fieldName);
                            madeProgress = true;
                            break;

                        case ReferenceDeleteBehavior.Cascade:
                            var owner = pending.GetOrCreateRecord(ownerId);
                            if (!IsEffectivelyDeleted(owner))
                            {
                                owner.ApplyDelete();
                                madeProgress = true;
                            }
                            break;

                        // Reject deferred to phase 3; Ignore is by design a no-op.
                    }
                }
            }
        } while (madeProgress);

        // Phase 3: collect Reject blockers from the steady-state graph. Anything reached
        // by Cascade in phase 2 is now Deleted itself and won't appear here.
        var blockers = new List<string>();
        foreach (var rec in pending.Records.Values)
        {
            if (!IsEffectivelyDeleted(rec)) continue;

            foreach (var (ownerId, fieldName, info) in EffectiveIncomingReferences(rec.Id, pending, registry))
            {
                if (info.Behavior == ReferenceDeleteBehavior.Reject)
                {
                    blockers.Add($"Cannot delete {rec.Id}. Referenced by {ownerId} through field '{fieldName}'.");
                }
            }
        }

        if (blockers.Count > 0)
        {
            throw new CommitPlanRejectException(blockers);
        }
    }

    private static bool IsEffectivelyDeleted(RecordPendingState rec)
    {
        // A record is effectively deleted iff its final state isn't existent. New-in-
        // packet records that get Created+Deleted in the same segment are effectively
        // deleted from the database's POV as well.
        return !rec.ExistsAtEnd;
    }

    /// <summary>
    /// Yields every <c>(owner, field, info)</c> whose at-end target is <paramref name="targetId"/>,
    /// the owner is not itself effectively deleted, and the registry knows the field's
    /// delete behavior. Walks the immutable <see cref="PendingState.References"/> snapshot.
    /// </summary>
    private static IEnumerable<(RecordId Owner, string Field, ReferenceFieldInfo Info)> EffectiveIncomingReferences(
        RecordId targetId,
        PendingState pending,
        IReferenceRegistry registry)
    {
        var infos = registry.IncomingReferencesTo(targetId.Table);
        if (infos.Count == 0) yield break;

        foreach (var transition in pending.References.Values)
        {
            if (transition.TargetAtEnd is not { } endTarget) continue;
            if (!endTarget.Equals(targetId)) continue;

            // Owner is itself going away — its outgoing reference doesn't matter.
            if (pending.Records.TryGetValue(transition.Owner, out var ownerRec) && IsEffectivelyDeleted(ownerRec))
                continue;

            ReferenceFieldInfo? match = null;
            foreach (var info in infos)
            {
                if (info.ReferencerTable == transition.Owner.Table && info.FieldName == transition.Field)
                {
                    match = info;
                    break;
                }
            }
            if (match is null) continue;

            yield return (transition.Owner, transition.Field, match);
        }
    }

    // ──────────────────────────── per-record emission ────────────────────────

    /// <summary>
    /// Walks every record's pending Sets and every relation's endpoints to assemble the
    /// set of ids that other commands depend on. Used by <see cref="EmitRecord"/> to
    /// decide whether a freshly-tracked record with no own writes is dead weight (skip)
    /// or a reference target someone else still needs (emit).
    /// </summary>
    private static HashSet<RecordId> BuildReferencedIdSet(PendingState pending)
    {
        var refs = new HashSet<RecordId>();
        foreach (var rec in pending.Records.Values)
        {
            foreach (var kv in rec.Current.Sets)
            {
                if (kv.Value is RecordId rid) refs.Add(rid);
            }
        }
        foreach (var rel in pending.Relations.Values)
        {
            refs.Add(rel.Source);
            refs.Add(rel.Target);
        }
        return refs;
    }

    private static void EmitRecord(
        RecordPendingState rec,
        HashSet<RecordId> referencedIds,
        List<Command> preDeletes,
        List<Command> creates,
        List<Command> fieldUpdates,
        List<Command> finalDeletes)
    {
        var existing = rec.ExistedAtStart;

        // Closed segments (everything but the last) are by construction terminated by a
        // Delete — that's the only way new segments start. For each, if the record
        // existed entering the segment, we emit a DELETE before the next-segment
        // CREATE. Field changes inside a closed-deleted segment are dropped per §7.5.
        for (var i = 0; i < rec.Segments.Count - 1; i++)
        {
            if (existing)
            {
                preDeletes.Add(Command.Delete(rec.Id));
            }

            existing = false;
        }

        var final = rec.Current;

        if (final.Deleted)
        {
            // Emit DELETE only when the record actually exists in the DB at the start
            // of this segment. Create + Delete (or Create + Set* + Delete) on a fresh
            // record in the same packet is a no-op — the row never reached SurrealDB
            // so there's nothing to delete; emitting `DELETE` against a non-existent
            // row would also be a no-op at the DB but adds noise to the script.
            if (existing)
            {
                finalDeletes.Add(Command.Delete(rec.Id));
            }

            // Drop any pending field changes — record is gone at commit.
            return;
        }

        if (final.Created)
        {
            // Dead-weight elision: a freshly-tracked record (not loaded from the DB) with
            // no own field writes that nothing else points at is just bookkeeping noise —
            // typically the orphan-id ghost left behind when the auto-Track ctor fires
            // before Hydrate sets the real id. Drop the Create entirely.
            if (!rec.ExistedAtStart
                && final.Sets.Count == 0
                && final.Unsets.Count == 0
                && !referencedIds.Contains(rec.Id))
            {
                return;
            }

            // Fold all sets into a CREATE CONTENT { … } payload via Upsert (the emitter
            // writes UPSERT … CONTENT, which works as create-or-update; safer for
            // re-create-in-same-packet sequences than a bare CREATE that would error
            // if the record happened to still exist).
            creates.Add(final.Sets.Count > 0
                ? Command.Upsert(rec.Id, final.Sets)
                : Command.Create(rec.Id));
            foreach (var unsetField in final.Unsets)
            {
                fieldUpdates.Add(Command.Unset(rec.Id, unsetField));
            }

            return;
        }

        if (final.Upserted)
        {
            // Upserted intent always emits UPSERT — even with no fields. Downgrading to
            // CREATE here would change semantics: CREATE errors if the record already
            // exists, while UPSERT is create-or-update which is what the caller asked for.
            creates.Add(Command.Upsert(rec.Id, final.Sets.Count > 0 ? final.Sets : null));
            foreach (var unsetField in final.Unsets)
            {
                fieldUpdates.Add(Command.Unset(rec.Id, unsetField));
            }

            return;
        }

        // No lifecycle change — just field mutations on a surviving record.
        foreach (var (k, v) in final.Sets)
        {
            fieldUpdates.Add(Command.Set(rec.Id, k, v));
        }

        foreach (var unsetField in final.Unsets)
        {
            fieldUpdates.Add(Command.Unset(rec.Id, unsetField));
        }
    }

    private static void EmitRelation(
        RelationPendingState rel,
        List<Command> removals,
        List<Command> additions)
    {
        switch (rel.State)
        {
            case RelationFinalState.Untouched:
                // Pending state recorded only payload edits — emit as a RELATE upsert
                // if there's payload, otherwise nothing to do.
                if (rel.PayloadSets.Count > 0 || rel.PayloadUnsets.Count > 0)
                {
                    additions.Add(Command.Relate(rel.Source, rel.Kind, rel.Target,
                        rel.PayloadSets.Count > 0 ? new Dictionary<string, object?>(rel.PayloadSets) : null));
                }

                break;

            case RelationFinalState.Related:
                // No-op when the relation already existed AND no payload changes.
                if (rel.ExistedAtStart && rel.PayloadSets.Count == 0 && rel.PayloadUnsets.Count == 0)
                {
                    break;
                }

                additions.Add(Command.Relate(rel.Source, rel.Kind, rel.Target,
                    rel.PayloadSets.Count > 0 ? new Dictionary<string, object?>(rel.PayloadSets) : null));
                break;

            case RelationFinalState.Unrelated:
                // No-op when the relation never existed in the first place.
                if (!rel.ExistedAtStart)
                {
                    break;
                }

                removals.Add(Command.Unrelate(rel.Source, rel.Kind, rel.Target));
                break;
        }
    }
}

/// <summary>Thrown by <see cref="CommitPlanner.Build"/> when one or more <c>[Reject]</c> incoming references would block a pending delete.</summary>
public sealed class CommitPlanRejectException : Exception
{
    public IReadOnlyList<string> Blockers { get; }

    public CommitPlanRejectException(IReadOnlyList<string> blockers)
        : base($"Commit plan rejected: {string.Join(" | ", blockers)}")
    {
        Blockers = blockers;
    }
}
