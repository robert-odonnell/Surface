#nullable enable

namespace Disruptor.Surface.Runtime;

/// <summary>
/// One contiguous period of record-existence intent inside a unit of work. A new segment
/// starts every time a record gets <see cref="RecordPendingState.ApplyCreate"/> called
/// after its current segment was deleted — preserving Create / Set / Delete / Create
/// sequences so the commit planner can decide whether they collapse to nothing or to a
/// real DELETE+CREATE pair.
/// </summary>
public sealed class LifecycleSegment
{
    public bool Created;
    public bool Upserted;
    public bool Deleted;
    public Dictionary<string, object?> Sets { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Unsets { get; } = new(StringComparer.Ordinal);

    public bool ExistsAtSegmentEnd => (Created || Upserted) && !Deleted;
}

/// <summary>
/// Accumulated mutation intent for one record across a unit of work. Holds the
/// <em>existed at start</em> bit (sourced from whether the loader saw the row) plus an
/// ordered list of <see cref="LifecycleSegment"/>s. New segments only appear when a
/// Create / Upsert is applied to an already-deleted current segment.
/// </summary>
public sealed class RecordPendingState
{
    public RecordId Id { get; }
    public bool ExistedAtStart { get; }
    public List<LifecycleSegment> Segments { get; } = [];

    public LifecycleSegment Current => Segments[Segments.Count - 1];

    /// <summary>True iff the record exists after applying every recorded command.</summary>
    public bool ExistsAtEnd => ExistedAtStart
        ? !Current.Deleted || Current.Created || Current.Upserted
        : Current.Created || Current.Upserted;

    public RecordPendingState(RecordId id, bool existedAtStart)
    {
        Id = id;
        ExistedAtStart = existedAtStart;
        Segments.Add(new LifecycleSegment());
    }

    public void ApplySet(string field, object? value)
    {
        Current.Unsets.Remove(field);
        Current.Sets[field] = value;
    }

    public void ApplyUnset(string field)
    {
        Current.Sets.Remove(field);
        Current.Unsets.Add(field);
    }

    public void ApplyCreate()
    {
        if (Current.Deleted)
        {
            Segments.Add(new LifecycleSegment { Created = true });
        }
        else
        {
            Current.Created = true;
        }
    }

    public void ApplyUpsert()
    {
        if (Current.Deleted)
        {
            Segments.Add(new LifecycleSegment { Upserted = true });
        }
        else
        {
            Current.Upserted = true;
        }
    }

    public void ApplyDelete() => Current.Deleted = true;
}

/// <summary>
/// Two-snapshot state for a single <c>[Reference]</c> field. <see cref="TargetAtStart"/>
/// captures what the loader saw; <see cref="TargetAtEnd"/> tracks what the user's writes
/// will leave behind. The commit planner resolves reference-delete behavior against
/// this snapshot — never against the session's mutable live <c>references</c> dict,
/// which is a read-side cache and gets cleared by post-Delete cleanup. That decoupling
/// is what makes <c>[Reject]</c>/<c>[Unset]</c>/<c>[Cascade]</c> reliable in the face
/// of mid-packet deletes.
/// </summary>
public sealed class ReferenceTransition
{
    public RecordId Owner { get; }
    public string Field { get; }
    public RecordId? TargetAtStart { get; }
    public RecordId? TargetAtEnd { get; set; }

    public ReferenceTransition(RecordId owner, string field, RecordId? targetAtStart, RecordId? targetAtEnd)
    {
        Owner = owner;
        Field = field;
        TargetAtStart = targetAtStart;
        TargetAtEnd = targetAtEnd;
    }
}

/// <summary>Final intent for a relation across a unit of work — never both <see cref="Related"/> and <see cref="Unrelated"/>; later writes overwrite earlier ones.</summary>
public enum RelationFinalState { Untouched, Related, Unrelated }

/// <summary>
/// Accumulated mutation intent for one canonical relation. The relation key is
/// <c>(Kind, Source, Target)</c> — inverse-side API calls must already have been
/// canonicalised before reaching pending state (no inverse facts).
/// </summary>
public sealed class RelationPendingState
{
    public string Kind { get; }
    public RecordId Source { get; }
    public RecordId Target { get; }
    public bool ExistedAtStart { get; }
    public RelationFinalState State { get; set; } = RelationFinalState.Untouched;
    public Dictionary<string, object?> PayloadSets { get; } = new(StringComparer.Ordinal);
    public HashSet<string> PayloadUnsets { get; } = new(StringComparer.Ordinal);

    public RelationPendingState(string kind, RecordId source, RecordId target, bool existedAtStart)
    {
        Kind = kind;
        Source = source;
        Target = target;
        ExistedAtStart = existedAtStart;
    }
}

/// <summary>
/// Indexed write intent for a unit of work — record states keyed by id, relation states
/// keyed by canonical edge key, plus a deduped set of pending bulk-clear intents
/// (<c>DELETE edge WHERE in = source</c> / <c>WHERE out = target</c>). Updated as
/// commands arrive; consumed by <see cref="CommitPlanner"/> at commit time.
/// </summary>
public sealed class PendingState
{
    public Dictionary<RecordId, RecordPendingState> Records { get; } = new();
    public Dictionary<(string Kind, RecordId Source, RecordId Target), RelationPendingState> Relations { get; } = new();
    public Dictionary<(RecordId Owner, string Field), ReferenceTransition> References { get; } = new();
    public HashSet<(string Kind, RecordId Source)> BulkUnrelateFrom { get; } = new();
    public HashSet<(string Kind, RecordId Target)> BulkUnrelateTo { get; } = new();

    private readonly HashSet<RecordId> loadedAtStart;
    private readonly HashSet<(string Kind, RecordId Source, RecordId Target)> relationsAtStart;

    public PendingState(
        HashSet<RecordId> loadedAtStart,
        HashSet<(string Kind, RecordId Source, RecordId Target)> relationsAtStart)
    {
        this.loadedAtStart = loadedAtStart;
        this.relationsAtStart = relationsAtStart;
    }

    public RecordPendingState GetOrCreateRecord(RecordId id)
    {
        if (!Records.TryGetValue(id, out var rec))
        {
            rec = new RecordPendingState(id, loadedAtStart.Contains(id));
            Records[id] = rec;
        }
        return rec;
    }

    public RelationPendingState GetOrCreateRelation(string kind, RecordId source, RecordId target)
    {
        var key = (kind, source, target);
        if (!Relations.TryGetValue(key, out var rel))
        {
            rel = new RelationPendingState(kind, source, target, relationsAtStart.Contains(key));
            Relations[key] = rel;
        }
        return rel;
    }

    /// <summary>Loader entry — records the at-start target. No-op if already known (loaders shouldn't double-emit).</summary>
    public void HydrateReference(RecordId owner, string field, RecordId target)
    {
        var key = (owner, field);
        if (!References.ContainsKey(key))
        {
            References[key] = new ReferenceTransition(owner, field, targetAtStart: target, targetAtEnd: target);
        }
    }

    /// <summary>User-side write — updates the at-end target while preserving the at-start snapshot.</summary>
    public void SetReferenceTarget(RecordId owner, string field, RecordId target)
    {
        var key = (owner, field);
        if (References.TryGetValue(key, out var existing))
        {
            existing.TargetAtEnd = target;
        }
        else
        {
            References[key] = new ReferenceTransition(owner, field, targetAtStart: null, targetAtEnd: target);
        }
    }

    /// <summary>User-side clear — at-end goes to null while at-start stays as it was.</summary>
    public void UnsetReferenceTarget(RecordId owner, string field)
    {
        var key = (owner, field);
        if (References.TryGetValue(key, out var existing))
        {
            existing.TargetAtEnd = null;
        }
        else
        {
            References[key] = new ReferenceTransition(owner, field, targetAtStart: null, targetAtEnd: null);
        }
    }

    public void ApplyCommand(Command c)
    {
        switch (c.Op)
        {
            case CommandOp.Create:
                GetOrCreateRecord(c.Target).ApplyCreate();
                break;
            case CommandOp.Upsert:
                var upsertRec = GetOrCreateRecord(c.Target);
                upsertRec.ApplyUpsert();
                if (c.Value is IReadOnlyDictionary<string, object?> upsertContent)
                {
                    foreach (var (k, v) in upsertContent)
                    {
                        upsertRec.ApplySet(k, v);
                    }
                }

                break;
            case CommandOp.Set:
                GetOrCreateRecord(c.Target).ApplySet(c.Key!, c.Value);
                break;
            case CommandOp.Unset:
                GetOrCreateRecord(c.Target).ApplyUnset(c.Key!);
                break;
            case CommandOp.Delete:
                GetOrCreateRecord(c.Target).ApplyDelete();
                break;
            case CommandOp.Relate:
            {
                var rel = GetOrCreateRelation(c.Key!, c.Target, (RecordId)c.Value!);
                rel.State = RelationFinalState.Related;
                if (c.EdgeContent is { } content)
                {
                    foreach (var (k, v) in content)
                    {
                        rel.PayloadUnsets.Remove(k);
                        rel.PayloadSets[k] = v;
                    }
                }

                break;
            }
            case CommandOp.Unrelate:
            {
                var rel = GetOrCreateRelation(c.Key!, c.Target, (RecordId)c.Value!);
                rel.State = RelationFinalState.Unrelated;
                rel.PayloadSets.Clear();
                rel.PayloadUnsets.Clear();
                break;
            }
            case CommandOp.UnrelateAllFrom:
            {
                // Bulk clear by source — drop any per-edge pending Relate entries that
                // would otherwise be re-added after the DB-level DELETE WHERE.
                var keysToRemove = Relations.Keys
                    .Where(k => k.Kind == c.Key && k.Source == c.Target)
                    .ToList();
                foreach (var k in keysToRemove)
                {
                    Relations.Remove(k);
                }
                BulkUnrelateFrom.Add((c.Key!, c.Target));
                break;
            }
            case CommandOp.UnrelateAllTo:
            {
                var keysToRemove = Relations.Keys
                    .Where(k => k.Kind == c.Key && k.Target == c.Target)
                    .ToList();
                foreach (var k in keysToRemove)
                {
                    Relations.Remove(k);
                }
                BulkUnrelateTo.Add((c.Key!, c.Target));
                break;
            }
        }
    }

    public void Clear()
    {
        Records.Clear();
        Relations.Clear();
        References.Clear();
        BulkUnrelateFrom.Clear();
        BulkUnrelateTo.Clear();
    }

    /// <summary>
    /// Deep-copy of this <see cref="PendingState"/>. The commit planner clones at entry
    /// so its cascade/unset resolution mutations don't leak back into the session's
    /// pending state — calling <see cref="SurrealSession.RenderBatch"/> twice should
    /// produce the same script both times. The <c>loadedAtStart</c> /
    /// <c>relationsAtStart</c> sets are shared by reference (immutable from the
    /// planner's POV).
    /// </summary>
    public PendingState Clone()
    {
        var copy = new PendingState(loadedAtStart, relationsAtStart);

        foreach (var kv in Records)
        {
            var src = kv.Value;
            var dst = new RecordPendingState(src.Id, src.ExistedAtStart);
            dst.Segments.Clear();
            foreach (var seg in src.Segments)
            {
                var segCopy = new LifecycleSegment
                {
                    Created = seg.Created,
                    Upserted = seg.Upserted,
                    Deleted = seg.Deleted,
                };
                foreach (var (k, v) in seg.Sets) segCopy.Sets[k] = v;
                foreach (var u in seg.Unsets) segCopy.Unsets.Add(u);
                dst.Segments.Add(segCopy);
            }
            copy.Records[kv.Key] = dst;
        }

        foreach (var kv in Relations)
        {
            var src = kv.Value;
            var dst = new RelationPendingState(src.Kind, src.Source, src.Target, src.ExistedAtStart)
            {
                State = src.State,
            };
            foreach (var (k, v) in src.PayloadSets) dst.PayloadSets[k] = v;
            foreach (var u in src.PayloadUnsets) dst.PayloadUnsets.Add(u);
            copy.Relations[kv.Key] = dst;
        }

        foreach (var kv in References)
        {
            var src = kv.Value;
            copy.References[kv.Key] = new ReferenceTransition(src.Owner, src.Field, src.TargetAtStart, src.TargetAtEnd);
        }

        foreach (var entry in BulkUnrelateFrom) copy.BulkUnrelateFrom.Add(entry);
        foreach (var entry in BulkUnrelateTo) copy.BulkUnrelateTo.Add(entry);

        return copy;
    }
}

/// <summary>
/// Append-only chronological history of model commands recorded during a unit of work.
/// Useful for diagnostics; never reordered. The compactable side of the system lives in
/// <see cref="PendingState"/>.
/// </summary>
public sealed class CommandLog
{
    private readonly List<Command> entries = [];

    public IReadOnlyList<Command> Entries => entries;
    public int Count => entries.Count;

    public void Append(Command c) => entries.Add(c);
    public void Clear() => entries.Clear();
}
