using System.Text;
using Disruptor.Surface.Runtime.Query;
using Disruptor.Surreal;
using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// The hydration-time write surface a loader uses to populate session-side state.
/// Entity-side state (parent links, reference links, scalar fields) is written directly
/// into the entity's backing fields by emitted <see cref="IEntity.Hydrate"/> bodies — no
/// sink call needed. The sink only carries cross-entity / session-scoped state: identity
/// map registration, edge index, slice-loaded marks.
/// <para>
/// Implemented explicitly by <see cref="SurrealSession"/> so domain code with a session
/// reference doesn't see <c>Track</c> / <c>Edge</c> in IntelliSense — getting at them
/// requires a deliberate <c>(IHydrationSink)session</c> cast, which is loud enough that
/// nobody calls them by accident.
/// </para>
/// </summary>
public interface IHydrationSink
{
    /// <summary>Marks an entity as loaded-at-start in the snapshot and binds it to the session on first sight.</summary>
    void Track(IEntity entity);

    /// <summary>Records an edge <c>(source, edgeKind, target)</c> as already present in the DB at load time.</summary>
    void Edge(RecordId source, string edgeKind, RecordId target);

    /// <summary>True iff <paramref name="id"/> is already tracked in the session — used by hydration helpers to dedup multi-owner inline-reference expansions.</summary>
    bool IsTracked(IRecordId id);

    /// <summary>
    /// Records that the slice rooted at <c>(ownerId, fieldName)</c> has been hydrated.
    /// Generator-emitted property reads check this flag before walking the in-memory
    /// cache; absence throws <see cref="LoadShapeViolationException"/> with a hint at
    /// <see cref="SurrealSession.FetchAsync{T}"/>. The legacy aggregate loader marks
    /// every known slice; the compiler-driven path in <see cref="Query{T}.ExecuteIntoSessionAsync"/>
    /// marks only what the user explicitly Included.
    /// </summary>
    void MarkSliceLoaded(RecordId ownerId, string fieldName);
}

/// <summary>
/// Common shape of every generated entity. The session-side hooks (<see cref="Bind"/>,
/// <see cref="Initialize"/>, <see cref="Hydrate"/>, <see cref="OnDeleting"/>,
/// <see cref="MarkAllSlicesLoaded"/>, <see cref="SaveAsync"/>) are explicit-interface
/// implementations on every generated entity so they don't pollute the user-facing
/// surface; the session is the only caller.
/// </summary>
public interface IEntity
{
    RecordId Id { get; }

    /// <summary>
    /// The session this entity is bound to, or <c>null</c> if it hasn't been tracked yet.
    /// Bound entities can resolve session-mediated reads (children, relations,
    /// reference fall-through to the identity map). Sync setters write straight into
    /// backing fields and need no session — pre-bind property writes are pure.
    /// </summary>
    SurrealSession? Session { get; }

    /// <summary>
    /// Wires the session into the entity's <c>_session</c> field so subsequent
    /// session-mediated reads (children, relations, lazy reference resolution) work.
    /// Called by <see cref="SurrealSession.Track{T}"/> for fresh entities and by
    /// <see cref="Hydrate"/> for loaded ones — runs exactly once per entity instance.
    /// </summary>
    void Bind(SurrealSession session);

    /// <summary>
    /// SurrealSession-only entry point; declared on the interface so the session can call
    /// it without reflection. The generator emits this body to seed mandatory
    /// <c>[Reference]</c> targets via the user's <c>OnCreate*</c> hooks. Idempotent —
    /// re-invocation skips already-set references, so <see cref="SurrealSession.SaveAsync"/>'s
    /// auto-bind path can call it safely on entities that have already been initialised.
    /// </summary>
    void Initialize(SurrealSession session);

    /// <summary>
    /// Loader-only entry point. Reads the row's <see cref="Disruptor.Surreal.Values.SurrealValue"/>
    /// payload and writes directly into the entity's backing fields. Edges and children
    /// are loaded by the per-aggregate loader separately. Default-interface no-op for
    /// hand-written stubs that don't go through hydration.
    /// </summary>
    void Hydrate(SurrealValue row, IHydrationSink sink) { }

    /// <summary>
    /// Returns this entity's parent record id, or <c>null</c> when no <c>[Parent]</c>
    /// is set (or no <c>[Parent]</c> exists on the type). Generator-emitted on every
    /// table with a <c>[Parent]</c> property; default no-op covers entities without one
    /// and hand-written stubs. Used by <see cref="SurrealSession.QueryChildren{T}"/> to
    /// match a child against its parent owner.
    /// </summary>
    RecordId? GetParentId() => null;

    /// <summary>
    /// SurrealSession calls this immediately before queueing the entity's own DELETE command,
    /// giving the user's <c>partial void OnDeleting()</c> hook a window to queue child
    /// deletes / clears. Anything queued from inside lands BEFORE the entity's own delete
    /// in the commit script — exactly the order the schema's <c>ON DELETE REJECT</c>
    /// references need (delete records that reference me, then me, then optionally any
    /// records I reference).
    /// </summary>
    void OnDeleting();

    /// <summary>
    /// Calls <see cref="IHydrationSink.MarkSliceLoaded"/> once per known slice on this
    /// entity ([Property], [Parent], [Reference], [Children], and forward/inverse
    /// relation collections). Used by the legacy aggregate loader and
    /// <see cref="SurrealSession.Track{T}"/> to declare "I own all this entity's
    /// state". The compiler-driven query path marks slices selectively via
    /// <see cref="IHydrationSink.MarkSliceLoaded"/> directly, so this method isn't
    /// called there.
    /// </summary>
    void MarkAllSlicesLoaded(IHydrationSink sink);

    /// <summary>
    /// Per-entity Save dispatch — generator-emitted on every <c>[Table]</c> entity. Walks
    /// forward dependencies (via <see cref="ISaveContext.SaveAsync"/>), then dispatches a
    /// <c>CREATE/UPDATE record:id CONTENT { … }</c> through <see cref="ISaveContext.Transaction"/>,
    /// then walks new children, then marks itself saved via <see cref="ISaveContext.MarkSaved"/>.
    /// Default-interface no-op throws — every <c>[Table]</c> entity gets a real body emitted;
    /// hand-written stubs that don't go through Save are unaffected.
    /// </summary>
    Task SaveAsync(ISaveContext ctx, CancellationToken ct)
        => throw new NotImplementedException(
            $"{GetType().FullName} does not implement IEntity.SaveAsync. "
            + "If this is a [Table] entity, the generator should emit it. "
            + "If it's a hand-written test stub or non-Table IEntity implementation, "
            + "either implement SaveAsync explicitly or avoid going through SurrealSession.SaveAsync.");

    /// <summary>
    /// Yields one entry per <c>[Reference]</c> / <c>[Parent]</c> backing field on the
    /// entity, with the snake-cased SurrealDB field name and the currently-set target id
    /// (<c>null</c> when the reference is unset). Used by
    /// <see cref="SurrealSession.DeleteAsync"/>'s pre-flight cascade resolve to find which
    /// entities point at the delete target. Default-interface empty implementation covers
    /// entities with no references and hand-written stubs.
    /// </summary>
    IEnumerable<(string FieldName, RecordId? Target)> EnumerateReferences() => [];

    /// <summary>
    /// Writes <paramref name="value"/> into the matching <c>[Reference]</c> backing field
    /// (snake-cased field name match). Used by <see cref="SurrealSession.DeleteAsync"/>'s
    /// Unset phase to mirror the substrate's <c>REFERENCE ON DELETE UNSET</c> into the
    /// in-memory entity, so subsequent reads off the snapshot don't hand back a stale id
    /// pointing at a cascaded-away record. Generator emits a switch over field names;
    /// non-nullable <c>[Reference]</c>s are skipped (CG012 already gates Unset on those at
    /// compile time). Default-interface no-op covers entities with no references.
    /// </summary>
    void SetReferenceTo(string fieldName, RecordId? value) { }
}

/// <summary>
/// Snapshot-isolated entity store. Every read on the model — properties, parent/children,
/// references, relation collections — resolves against the entity's own backing fields
/// or, for navigation across entities, the in-memory dictionaries the per-aggregate
/// loader populated. Sync writes mutate backing fields directly. Async dispatch
/// (<see cref="SaveAsync"/>, <see cref="DeleteAsync"/>, <see cref="RelateAsync"/>,
/// <see cref="UnrelateAsync"/>) is the only path that touches Surreal.
/// <para>
/// Each entity carries an explicit reference to its bound session via <see cref="IEntity.Session"/>;
/// there is no ambient context. Pre-bind property setters write straight into backing
/// fields — no buffer, no flush. The cascade-track in a <c>[Parent]</c> setter is the
/// one place sync code calls back into the session: assigning a parent that's bound to
/// a session pulls the child into that session so the parent's <c>[Children]</c> sees it.
/// </para>
/// <para>
/// The session knows nothing about persistence permissions. Domain code can mutate it
/// freely; until <see cref="SaveAsync"/> runs, every change is purely in-memory. Concurrent
/// writers collide at COMMIT as <see cref="SurrealConflictException"/> from the SDK —
/// the substrate owns concurrency, no application-level lease.
/// </para>
/// </summary>
public sealed class SurrealSession(IReferenceRegistry referenceRegistry) : IHydrationSink
{
    private class MaterializedSessionState
    {
        public readonly Dictionary<RecordId, IEntity> Entities = [];

        // Read-side index of edges visible in this session. Populated by IHydrationSink.Edge
        // at load time and by RelateAsync after dispatch. Pure presence — Query{Outgoing,
        // Incoming,RelatedIds,InverseRelatedIds} only ask "is this edge in the snapshot?".
        // No write buffering: with sync Relate gone, every edge mutation goes straight
        // through the user's transaction via RelateAsync / UnrelateAsync, and the index
        // updates as those calls complete.
        public readonly HashSet<(RecordId Source, string Edge, RecordId Target)> Edges = [];

        // Per-entity load-shape tracking. Each set lists the field names on that entity
        // whose slice has been hydrated (or freshly authored via Track<T>). Generator-emitted
        // property reads consult this map and throw LoadShapeViolationException if a slice
        // they need wasn't loaded — the strict-with-escape contract that PR8's Fetch
        // extends.
        public readonly Dictionary<RecordId, HashSet<string>> LoadedSlices = [];
    }

    private readonly MaterializedSessionState state = new();


    // One-shot lifecycle invariant: a session represents one loaded snapshot plus one
    // pending mutation batch. Successful CommitAsync or AbandonAsync flips `_closed` to
    // true, after which every public method throws. Hydrate-side helpers stay open
    // because they only ever run during initial load — before the user gets a handle.
    private bool closed;

    private void ThrowIfClosed()
    {
        if (closed)
        {
            throw new InvalidOperationException(
                "This SurrealSession is closed. Load a new session for further work.");
        }
    }

    /// <summary>True after <see cref="CommitAsync"/> or <see cref="Abandon"/> has run; further reads or writes throw.</summary>
    public bool IsClosed => closed;

    // Filled by the loader's IHydrationSink.Track. ISaveContext.IsTracked checks
    // loadedAtStart to distinguish "in DB" from "constructed in this session" so the
    // emitted SaveAsync can pick CREATE vs UPSERT semantics. Edges no longer need a
    // start-time set — there's no snapshot-diff dispatch anymore; RelateAsync ships
    // each edge straight through the user's transaction.
    private readonly HashSet<RecordId> loadedAtStart = [];

    /// <summary>Chronological history of recorded model commands. Diagnostic-only.</summary>
    public CommandLog Log { get; } = new();

    /// <summary>
    /// The model's reference-field registry. Used today as a placeholder for cascade
    /// re-anchoring (preview.36) — sessions created with NullReferenceRegistry skip
    /// reference-aware delete cascade. Pass <c>{CompositionRoot}.ReferenceRegistry</c>
    /// from your generated partial.
    /// </summary>
    public IReferenceRegistry ReferenceRegistry { get; } = referenceRegistry;

    public SurrealSession() : this(NullReferenceRegistry.Instance) { }

    // ──────────────────────────── reads (sync) ───────────────────────────────

    /// <summary>
    /// Look up a hydrated entity by id. Returns <c>null</c> when the session doesn't
    /// hold an entity for the id, or when the loaded entity isn't assignable to
    /// <typeparamref name="T"/>. Primary use case: get a typed handle to an aggregate
    /// root (or any other known entity) after <c>Sessions.Load{Root}Async</c>. Also
    /// used by generator-emitted <c>[Parent]</c> / <c>[Reference]</c> getters for
    /// the fallback resolution path (when the entity holds an id but no cached entity
    /// reference).
    /// </summary>
    public T? Get<T>(IRecordId id) where T : class, IEntity
    {
        ThrowIfClosed();
        var rid = RecordId.From(id);
        return state.Entities.TryGetValue(rid, out var entity) && entity is T typed ? typed : null;
    }

    /// <summary>
    /// Walks the in-memory entity index for children of <paramref name="owner"/> in
    /// <paramref name="childTable"/>. Children are filtered by their typed-id table
    /// match and by their <see cref="IEntity"/>-side <c>Parent</c> matching
    /// <paramref name="owner"/>. The match is done by reading the candidate's
    /// generator-emitted <c>__ParentIdFor</c> helper; a mismatch (or missing parent)
    /// excludes the candidate.
    /// </summary>
    public IReadOnlyCollection<T> QueryChildren<T>(IEntity owner, string childTable)
        where T : class, IEntity
    {
        ThrowIfClosed();
        var results = new List<T>();
        foreach (var kv in state.Entities)
        {
            if (!kv.Key.IsForTable(childTable))
            {
                continue;
            }

            if (kv.Value is not T typed)
            {
                continue;
            }

            if (TryGetParentId(typed) is { } pid && pid == owner.Id)
            {
                results.Add(typed);
            }
        }
        return results;
    }

    private static RecordId? TryGetParentId(IEntity child) => child.GetParentId();

    /// <summary>
    /// Walks the in-memory edge index along <paramref name="edgeKind"/> with
    /// <paramref name="owner"/> on the SOURCE side; returns the entity-typed targets.
    /// The forward/inverse split is explicit so same-table relations and overlapping
    /// role types don't end up reading "the other endpoint" both ways at once.
    /// </summary>
    public IReadOnlyCollection<T> QueryOutgoing<T>(IEntity owner, string edgeKind)
        where T : class
    {
        ThrowIfClosed();
        var results = new List<T>();
        foreach (var key in state.Edges)
        {
            if (key.Edge != edgeKind || key.Source != owner.Id)
            {
                continue;
            }
            if (state.Entities.TryGetValue(key.Target, out var other) && other is T typed)
            {
                results.Add(typed);
            }
        }
        return results;
    }

    /// <summary>
    /// Walks the in-memory edge index along <paramref name="edgeKind"/> with
    /// <paramref name="owner"/> on the TARGET side; returns the entity-typed sources.
    /// </summary>
    public IReadOnlyCollection<T> QueryIncoming<T>(IEntity owner, string edgeKind)
        where T : class
    {
        ThrowIfClosed();
        var results = new List<T>();
        foreach (var key in state.Edges)
        {
            if (key.Edge != edgeKind || key.Target != owner.Id)
            {
                continue;
            }
            if (state.Entities.TryGetValue(key.Source, out var other) && other is T typed)
            {
                results.Add(typed);
            }
        }
        return results;
    }

    /// <summary>
    /// Cross-aggregate read: returns the IDs of edge endpoints reachable from
    /// <paramref name="owner"/> on the source side (along <paramref name="edgeKind"/>).
    /// Targets aren't joined to <see cref="entities"/> because they live in a different
    /// aggregate snapshot — caller hydrates separately if needed.
    /// </summary>
    public IReadOnlyCollection<IRecordId> QueryRelatedIds(IEntity owner, string edgeKind)
    {
        ThrowIfClosed();
        var results = new List<IRecordId>();
        foreach (var key in state.Edges)
        {
            if (key.Edge != edgeKind)
            {
                continue;
            }

            if (key.Source == owner.Id)
            {
                results.Add(key.Target);
            }
        }
        return results;
    }

    /// <summary>Cross-aggregate inverse-side read: edges that land on <paramref name="owner"/>.</summary>
    public IReadOnlyCollection<IRecordId> QueryInverseRelatedIds(IEntity owner, string edgeKind)
    {
        ThrowIfClosed();
        var results = new List<IRecordId>();
        foreach (var key in state.Edges)
        {
            if (key.Edge != edgeKind)
            {
                continue;
            }

            if (key.Target == owner.Id)
            {
                results.Add(key.Source);
            }
        }
        return results;
    }

    // ──────────────────────────── writes ─────────────────────────────────────
    //
    // All writes mutate the in-memory snapshot and append to the dirty batch. Nothing
    // touches Surreal until CommitAsync runs, so the session doesn't gate writes on
    // any "is this writable?" flag — domain code is free to mutate freely. Persistence
    // permission is the caller's concern, expressed through whether they hold a
    // WriterLease and choose to call CommitAsync.

    /// <summary>
    /// Register a fresh entity with this session. <c>Bind</c> wires the session into the
    /// instance and <c>Initialize</c> seeds mandatory references via <c>OnCreate*</c> hooks
    /// (idempotent — no-op if the references are already set). Idempotent on the same
    /// instance; throws if a different instance with the same id is already tracked.
    /// Hydrated entities are registered separately via <see cref="IHydrationSink.Track"/>
    /// — they don't go through this path.
    /// </summary>
    public T Track<T>(T entity) where T : class, IEntity
    {
        ThrowIfClosed();
        if (state.Entities.TryGetValue(entity.Id, out var existing))
        {
            // Same instance — idempotent Track call, just return.
            if (ReferenceEquals(existing, entity))
            {
                return entity;
            }

            // Different instance with the same id is identity-map poison: the rest of the
            // session would refer to one instance while user code holds another.
            throw new InvalidOperationException(
                $"Cannot track {entity.Id}: a different entity instance is already tracked under this id.");
        }

        // Bind FIRST — the emitted Bind throws InvalidOperationException if the entity is
        // already bound to a different session. Doing this before the identity-map assign
        // means a cross-session attempt fails cleanly without leaving a dangling entry in
        // state.Entities pointing at an instance whose Session belongs to someone else.
        entity.Bind(this);
        state.Entities[entity.Id] = entity;
        Record(Command.Create(entity.Id));
        entity.Initialize(this);
        // Fresh-entity Track: the user owns the entire state, so every slice is
        // implicitly "loaded" — there's no DB row to compare against. Mark all of them
        // so subsequent reads of [Children] / [Reference] / relations return the local
        // state (which may be empty for a brand-new entity, and that's fine).
        entity.MarkAllSlicesLoaded(this);
        return entity;
    }

    /// <summary>True iff <paramref name="id"/> is currently tracked (loaded or freshly minted) in this session.</summary>
    public bool IsTracked(IRecordId id) => state.Entities.ContainsKey(RecordId.From(id));

    /// <summary>
    /// True iff the slice rooted at <c>(<paramref name="owner"/>, <paramref name="fieldName"/>)</c>
    /// has been marked loaded — by the legacy aggregate loader (which marks every slice),
    /// by <see cref="Track{T}"/> (fresh-entity, marks every slice), or by the compiler-
    /// driven traversal path (which marks only Included slices). Generator-emitted
    /// property reads consult this before walking the in-memory cache.
    /// </summary>
    public bool IsSliceLoaded(IRecordId owner, string fieldName)
        => state.LoadedSlices.TryGetValue(RecordId.From(owner), out var set)
           && set.Contains(fieldName);

    /// <summary>
    /// Strict-with-escape extension query: runs <paramref name="query"/> and re-Hydrates
    /// rows back into <c>this</c> session. Slices listed in <see cref="Query{T}.Includes"/>
    /// are marked loaded on their owners — subsequent reads against those slices stop
    /// throwing <see cref="LoadShapeViolationException"/>.
    /// <para>
    /// <b>Caveat:</b> Fetch is a slice widener, not a polite refresh. Re-Hydrate of an
    /// entity already in the session overwrites its scalar fields with whatever the DB
    /// returns. If you've mutated an entity in memory, save first or accept the clobber —
    /// per-field "did the user touch this" tracking is gone.
    /// </para>
    /// <para>
    /// Typical usage: a property read raises a <see cref="LoadShapeViolationException"/>;
    /// catch it (or pre-empt it) and call <c>session.FetchAsync(Workspace.Query.{Root}.WithId(id).Include*(...))</c>
    /// to extend the load shape. The query must root at the same entity whose slice you
    /// want to top up — Fetch never invents new aggregate roots.
    /// </para>
    /// </summary>
    public Task FetchAsync<T>(SurfaceQuery<T> query, SurrealClient db, CancellationToken ct = default)
        where T : class, IEntity, new()
        => FetchAsync(query, db.QueryAsync, ct);

    /// <inheritdoc cref="FetchAsync{T}(SurfaceQuery{T}, SurrealClient, CancellationToken)"/>
    public Task FetchAsync<T>(SurfaceQuery<T> query, SurrealTransaction tx, CancellationToken ct = default)
        where T : class, IEntity, new()
        => FetchAsync(query, tx.QueryAsync, ct);

    private async Task FetchAsync<T>(
        SurfaceQuery<T> query,
        Func<string, SurrealObject?, CancellationToken, Task<SurrealQueryResponse>> queryFn,
        CancellationToken ct)
        where T : class, IEntity, new()
    {
        ThrowIfClosed();

        var (sql, bindings) = query.Compile();
        var response = await queryFn(sql, bindings, ct);
        var rows = response.Count > 0 ? response.Take(0) : null;

        IHydrationSink sink = this;

        if (rows is SurrealListValue arr)
        {
            foreach (var row in arr.List)
            {
                if (row is SurrealObjectValue obj)
                {
                    HydrateMergingRoot<T>(obj, sink, query.Includes);
                }
            }
        }
        else if (rows is SurrealObjectValue single)
        {
            HydrateMergingRoot<T>(single, sink, query.Includes);
        }
    }

    /// <summary>
    /// Hydrate one root row (or re-Hydrate over an existing tracked entity), then recurse
    /// through <paramref name="includes"/>. Re-Hydrate clobbers existing scalar fields —
    /// the slice-widening contract documented on <see cref="FetchAsync{T}(SurfaceQuery{T}, SurrealClient, CancellationToken)"/>.
    /// </summary>
    private void HydrateMergingRoot<T>(SurrealObjectValue row, IHydrationSink sink, IReadOnlyList<IIncludeNode> includes)
        where T : class, IEntity, new()
    {
        if (!HydrationValue.TryReadRecordId(row, "id", out var id))
        {
            return;
        }

        if (state.Entities.TryGetValue(id, out var existing))
        {
            existing.Hydrate(row, sink);
        }
        else
        {
            var entity = new T();
            entity.Hydrate(row, sink);
        }

        HydrateMergingNested(row, includes, sink);
    }

    /// <summary>
    /// Recursively hydrate nested includes. Existing tracked rows re-Hydrate over the same
    /// instance (slice-widening; scalar clobber per the FetchAsync contract); brand-new
    /// rows go through the include's generator-emitted hydrator.
    /// </summary>
    private void HydrateMergingNested(SurrealObjectValue row, IReadOnlyList<IIncludeNode> nodes, IHydrationSink sink)
    {
        var hasOwnerId = HydrationValue.TryReadRecordId(row, "id", out var ownerId);

        foreach (var node in nodes)
        {
            switch (node)
            {
                case IncludeInlineRefNode inlineRef:
                    if (hasOwnerId)
                    {
                        sink.MarkSliceLoaded(ownerId, inlineRef.Field);
                    }

                    break;

                case IncludeChildrenNode children:
                    if (hasOwnerId && children.ParentSliceKey is { } sliceKey)
                    {
                        sink.MarkSliceLoaded(ownerId, sliceKey);
                    }

                    if (!row.Object.TryGetValue(children.ChildTable, out var arrVal))
                    {
                        continue;
                    }

                    if (arrVal is not SurrealListValue arr)
                    {
                        continue;
                    }

                    foreach (var childVal in arr.List)
                    {
                        if (childVal is not SurrealObjectValue childObj)
                        {
                            continue;
                        }

                        if (HydrationValue.TryReadRecordId(childObj, "id", out var childId)
                            && state.Entities.TryGetValue(childId, out var existingChild))
                        {
                            existingChild.Hydrate(childObj, sink);
                        }
                        else
                        {
                            children.Hydrator?.Invoke(childObj, sink);
                        }
                        HydrateMergingNested(childObj, children.Nested, sink);
                    }
                    break;

                case IncludeRelationNode relation:
                    if (hasOwnerId)
                    {
                        sink.MarkSliceLoaded(ownerId, relation.ParentSliceKey);
                    }

                    if (!row.Object.TryGetValue(relation.ParentSliceKey, out var relVal))
                    {
                        continue;
                    }

                    if (relVal is not SurrealListValue relArr)
                    {
                        continue;
                    }

                    if (relation.IdsOnly)
                    {
                        foreach (var edgeVal in relArr.List)
                        {
                            if (edgeVal is not SurrealObjectValue edgeObj)
                            {
                                continue;
                            }

                            if (!HydrationValue.TryReadRecordId(edgeObj, "in", out var src))
                            {
                                continue;
                            }

                            if (!HydrationValue.TryReadRecordId(edgeObj, "out", out var dst))
                            {
                                continue;
                            }

                            sink.Edge(src, relation.EdgeName, dst);
                        }
                    }
                    else
                    {
                        foreach (var targetVal in relArr.List)
                        {
                            if (targetVal is not SurrealObjectValue targetObj)
                            {
                                continue;
                            }

                            if (HydrationValue.TryReadRecordId(targetObj, "id", out var targetId)
                                && state.Entities.TryGetValue(targetId, out var existingTarget))
                            {
                                existingTarget.Hydrate(targetObj, sink);
                            }
                            else
                            {
                                relation.Hydrator?.Invoke(targetObj, sink);
                            }
                            if (!hasOwnerId)
                            {
                                continue;
                            }

                            if (!HydrationValue.TryReadRecordId(targetObj, "id", out var tgtForEdge))
                            {
                                continue;
                            }

                            var src = relation.IsOutgoing ? ownerId : tgtForEdge;
                            var dst = relation.IsOutgoing ? tgtForEdge : ownerId;
                            sink.Edge(src, relation.EdgeName, dst);
                            HydrateMergingNested(targetObj, relation.Nested, sink);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Cascade-track a child entity into this session. Called from generator-emitted
    /// <c>[Parent]</c> setters: when the user does <c>new Constraint { Design = design }</c>,
    /// the Constraint's <c>Design</c> setter calls <c>design.Session?.AdoptIfUnbound(this)</c>
    /// so the constraint joins the design's session and shows up in
    /// <c>design.Constraints</c> at Save time. No-op when the child is already bound.
    /// </summary>
    public void AdoptIfUnbound(IEntity child)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(child);
        if (child.Session is not null)
        {
            return;
        }

        Track(child);
    }

    // Sync Relate / Unrelate were removed — they buffered edge intent into state.Edges
    // for the emitted SaveAsync to drain via a snapshot-diff dispatch, which is the
    // exact write-buffering pattern the per-entity-Save pivot ripped out for property
    // writes. Edge mutations now go through RelateAsync<TKind> / UnrelateAsync<TKind>
    // which dispatch immediately against the user's transaction; no buffer to drain,
    // no snapshot to diff, no payload/edge-id silently dropped at save time.

    public IReadOnlyCollection<TElement> QueryOutgoing<TKind, TElement>(IEntity owner) where TKind : IRelationKind where TElement : class
        => QueryOutgoing<TElement>(owner, TKind.EdgeName);
    public IReadOnlyCollection<TElement> QueryIncoming<TKind, TElement>(IEntity owner) where TKind : IRelationKind where TElement : class
        => QueryIncoming<TElement>(owner, TKind.EdgeName);
    public IReadOnlyCollection<IRecordId> QueryRelatedIds<TKind>(IEntity owner) where TKind : IRelationKind
        => QueryRelatedIds(owner, TKind.EdgeName);
    public IReadOnlyCollection<IRecordId> QueryInverseRelatedIds<TKind>(IEntity owner) where TKind : IRelationKind
        => QueryInverseRelatedIds(owner, TKind.EdgeName);

    // ──────────────────────────── boundary (async) ───────────────────────────

    /// <summary>
    /// Flushes pending writes through a streamed server-side transaction. Each rendered
    /// command is dispatched as its own RPC inside the txn (begin → N queries → commit),
    /// so wire-side batch limits no longer cap commit size. The session closes on return
    /// regardless of outcome — the one-shot invariant is "load → mutate → commit (or
    /// fail), then loop."
    /// <para>
    /// Concurrency surfaces natively: if another writer's commit lands first and conflicts
    /// with ours, SurrealDB raises a <see cref="SurrealConflictException"/> at COMMIT
    /// (or earlier, on a conflicting write). The domain catches and decides whether to
    /// reload-and-retry. No application-level lease, no CAS-on-sequence — the substrate
    /// owns concurrency now.
    /// </para>
    /// <para>
    /// Single exception boundary: any exception during commit marks the session closed
    /// and rethrows. Nothing else in the runtime catches exceptions; everything else
    /// throws freely and lands here.
    /// </para>
    /// </summary>
    /// <summary>
    /// Per-entity Save: dispatches <paramref name="entity"/> (and its forward dependencies +
    /// new children, transitively) into <paramref name="tx"/>. Whole-entity always — every
    /// dispatched row is a <c>CREATE/UPDATE record:id CONTENT { … }</c>. Dependency-first
    /// ordering: forward references (and parents) save before the entity that points at
    /// them; new children save after the entity that owns them. Existing entities (already
    /// in the identity map) only save when explicitly passed — their references are reused
    /// by id, not re-dispatched.
    /// <para>
    /// The recursion is generator-driven: each entity's emitted <see cref="IEntity.SaveAsync"/>
    /// describes its own structure and recurses through <see cref="ISaveContext.SaveAsync"/>
    /// callbacks back into this orchestration. The session enforces cycle detection (each
    /// entity is visited at most once per Save pass).
    /// </para>
    /// </summary>
    public async Task SaveAsync(IEntity entity, SurrealTransaction tx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(tx);
        ThrowIfClosed();

        var ctx = new SaveContext(this, tx);
        try
        {
            await ctx.SaveAsync(entity, ct).ConfigureAwait(false);
        }
        catch
        {
            // Fail-closed: any dispatch failure marks the session done. The app catches
            // and cancels the txn on its own.
            closed = true;
            throw;
        }
    }

    /// <summary>
    /// Auto-binds <paramref name="entity"/> for Save: binds the session into the entity's
    /// <c>_session</c> field, runs <c>Initialize</c> (idempotently — the emitted body
    /// guards each <c>OnCreate*</c> hook so already-set mandatory references aren't
    /// re-minted), and marks every slice loaded (the user is asserting they own the
    /// entire state of this fresh entity). Crucially does <b>not</b> add the owner to
    /// the identity map — that's <see cref="ISaveContext.MarkSaved"/>'s job, post-
    /// dispatch, so the generator-emitted body's CREATE-vs-UPDATE check sees the entity
    /// as new.
    /// </summary>
    private void EnsureBoundForSave(IEntity entity)
    {
        ThrowIfClosed();
        if (entity.Session is null)
        {
            entity.Bind(this);
            entity.Initialize(this);
            entity.MarkAllSlicesLoaded(this);
        }
        else if (!ReferenceEquals(entity.Session, this))
        {
            // Cross-session SaveAsync: the generated body would dispatch writes through
            // ctx.Transaction (this session's tx) while reading children/relations through
            // the entity's *original* session. That split is silent contamination; reject
            // it cleanly. User can transfer the entity by abandoning its original session
            // and Tracking on the new one (or restructure their flow to keep one session).
            throw new InvalidOperationException(
                $"Cannot SaveAsync entity {entity.Id} through this session: it is bound to a different session. "
                + "Generated Save bodies read entity-graph slices via the entity's bound Session — "
                + "dispatching writes through a different session's transaction would split reads and writes.");
        }
    }

    /// <summary>
    /// Internal orchestration: tracks visited-this-pass (cycle break + saved-this-pass
    /// signal for the entity body's IsTracked / CREATE-vs-UPDATE check).
    /// </summary>
    private sealed class SaveContext(SurrealSession session, SurrealTransaction tx) : ISaveContext
    {
        private readonly HashSet<RecordId> visited = [];
        private readonly HashSet<RecordId> savedThisPass = [];

        public SurrealTransaction Transaction { get; } = tx;

        /// <summary>
        /// True iff <paramref name="id"/> is known to exist in the DB — either loaded
        /// from a prior <c>Hydrate</c> (so it's in the session's <c>loadedAtStart</c>
        /// set) or already CREATEd in this Save pass. NOT a check on the in-memory
        /// identity map: a freshly-constructed entity that's been Bound for Save sits
        /// in <c>state.Entities</c> too, and we need to distinguish "in session" from
        /// "in DB" so the entity body chooses CREATE vs UPDATE correctly.
        /// </summary>
        public bool IsTracked(IRecordId id)
        {
            var rid = RecordId.From(id);
            return session.loadedAtStart.Contains(rid) || savedThisPass.Contains(rid);
        }

        public async Task SaveAsync(IEntity entity, CancellationToken ct)
        {
            // Cycle break — an entity reachable through multiple paths in one Save pass
            // dispatches exactly once. The generator's per-entity body checks IsTracked
            // for forward refs, but a cycle through new entities would still recur here
            // without the visited set.
            if (!visited.Add(entity.Id))
            {
                return;
            }

            session.EnsureBoundForSave(entity);
            await entity.SaveAsync(this, ct).ConfigureAwait(false);
        }

        public void MarkSaved(IEntity entity)
        {
            // Identity map registration (idempotent — Bind / Track cascade may have
            // added it already) plus the saved-this-pass flag that drives subsequent
            // IsTracked checks to "yes, in DB now".
            session.state.Entities[entity.Id] = entity;
            savedThisPass.Add(entity.Id);
        }
    }

    /// <summary>
    /// Idempotently creates an edge of kind <typeparamref name="TKind"/> from
    /// <paramref name="source"/> to <paramref name="target"/> through <paramref name="tx"/>.
    /// Updates the in-memory edge index too so subsequent reads see the new edge.
    /// <para>
    /// No explicit edge id and no payload — the edge id defaults to
    /// <see cref="RecordId.Idempotent"/> (deterministic hash of <c>{source}|{table}|{target}</c>),
    /// dispatched as <c>INSERT RELATION IGNORE INTO {edge} { id, in, out }</c>. Re-running
    /// the same triple is a substrate-side no-op (IGNORE absorbs the duplicate id and
    /// the UNIQUE INDEX violation). Use the four-arg overload to override the edge id,
    /// or pass a <c>payload</c> dictionary for the equivalent of <c>RELATE … CONTENT</c>.
    /// </para>
    /// </summary>
    public Task RelateAsync<TKind>(IRecordId source, IRecordId target, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        => RelateAsyncCore<TKind>(source, target, explicitEdge: null, payload: null, tx, ct);

    /// <summary>Idempotent RELATE with a caller-specified edge id (Random Ulid, Slug, or explicit Idempotent). IGNORE absorbs duplicate-id and UNIQUE INDEX (in, out) conflicts.</summary>
    public Task RelateAsync<TKind>(IRecordId source, IRecordId target, RecordId edge, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        => RelateAsyncCore<TKind>(source, target, edge, payload: null, tx, ct);

    /// <summary>RELATE with payload — first call wins; a re-call with different payload is a no-op (IGNORE semantics). UPDATE the edge id directly if you need to mutate payload.</summary>
    public Task RelateAsync<TKind>(IRecordId source, IRecordId target, IReadOnlyDictionary<string, object?> payload, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
    {
        ArgumentNullException.ThrowIfNull(payload);
        return RelateAsyncCore<TKind>(source, target, explicitEdge: null, payload, tx, ct);
    }

    /// <summary>RELATE with an explicit edge id and payload — full control over both fields the snapshot diff carries.</summary>
    public Task RelateAsync<TKind>(IRecordId source, IRecordId target, RecordId edge, IReadOnlyDictionary<string, object?> payload, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
    {
        ArgumentNullException.ThrowIfNull(payload);
        return RelateAsyncCore<TKind>(source, target, edge, payload, tx, ct);
    }

    // IEntity convenience flavours — collapse straight to the IRecordId core via .Id.
    public Task RelateAsync<TKind>(IEntity source, IEntity target, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        => RelateAsync<TKind>(source.Id, target.Id, tx, ct);
    public Task RelateAsync<TKind>(IEntity source, IEntity target, RecordId edge, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        => RelateAsync<TKind>(source.Id, target.Id, edge, tx, ct);
    public Task RelateAsync<TKind>(IEntity source, IEntity target, IReadOnlyDictionary<string, object?> payload, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        => RelateAsync<TKind>(source.Id, target.Id, payload, tx, ct);
    public Task RelateAsync<TKind>(IEntity source, IEntity target, RecordId edge, IReadOnlyDictionary<string, object?> payload, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        => RelateAsync<TKind>(source.Id, target.Id, edge, payload, tx, ct);

    /// <summary>
    /// Generator-emission target for typed-payload RelateAsync. The per-kind extension
    /// methods (emitted alongside the marker class for every <c>ForwardRelation&lt;TPayload&gt;</c>)
    /// build a <see cref="SurrealObject"/> from the typed payload via
    /// <see cref="ContentValue"/> and call this helper. The dispatch shape is
    /// <c>INSERT RELATION INTO {edge} $_content ON DUPLICATE KEY UPDATE field1 = $_p_field1, …</c>:
    /// re-running the same triple replaces every payload field on the existing edge,
    /// because the generator passed every TPayload field as a key in
    /// <paramref name="payload"/>. Empty payloads fall back to <c>INSERT RELATION IGNORE</c>
    /// (no-op on duplicate, matching the no-payload <see cref="RelateAsync{TKind}(IRecordId, IRecordId, SurrealTransaction, CancellationToken)"/> overload).
    /// <para>
    /// Public so emitted code in the consumer assembly can call it. End users normally
    /// call the typed extension methods directly (e.g. <c>session.RelateAsync(src, tgt, payload, tx)</c>);
    /// this overload is the dispatch core.
    /// </para>
    /// </summary>
    public async Task RelateAsyncReplace<TKind>(
        IRecordId source,
        IRecordId target,
        RecordId? explicitEdge,
        SurrealObject payload,
        SurrealTransaction tx,
        CancellationToken ct = default) where TKind : IRelationKind
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(tx);
        ThrowIfClosed();
        try
        {
            var src = RecordId.From(source);
            var tgt = RecordId.From(target);
            var edge = (explicitEdge ?? RecordId.Idempotent(TKind.EdgeName)).Resolve(src, tgt);
            state.Edges.Add((src, TKind.EdgeName, tgt));

            // Build the full content (id, in, out, plus the typed payload's fields). The
            // generator passed payload as a SurrealObject containing only the user's
            // payload keys; we wrap in id/in/out before dispatch.
            var content = new SurrealObject
            {
                ["id"] = new SurrealRecordIdValue(edge.ToSdk()),
                ["in"] = new SurrealRecordIdValue(src.ToSdk()),
                ["out"] = new SurrealRecordIdValue(tgt.ToSdk()),
            };
            foreach (var kv in payload)
            {
                content[kv.Key] = kv.Value;
            }

            var bindings = new SurrealObject { ["_content"] = new SurrealObjectValue(content) };

            string sql;
            if (payload.Count == 0)
            {
                // No payload — IGNORE-shape no-op on duplicate, same as the no-payload
                // RelateAsync overload. No SET clause to write.
                sql = $"INSERT RELATION IGNORE INTO {TKind.EdgeName.Identifier()} $_content;";
            }
            else
            {
                // Bind each payload field separately as $_p_{field} so the SET clause
                // can reference them. Slight redundancy with $_content (each value is
                // bound twice), unambiguous and avoids depending on whether SurrealQL
                // supports $_content.field property access in UPDATE expressions.
                var setClauses = new StringBuilder();
                foreach (var kv in payload)
                {
                    var bindKey = $"_p_{kv.Key}";
                    bindings[bindKey] = kv.Value;
                    if (setClauses.Length > 0)
                    {
                        setClauses.Append(", ");
                    }
                    setClauses.Append(kv.Key.Identifier()).Append(" = $").Append(bindKey);
                }
                sql = $"INSERT RELATION INTO {TKind.EdgeName.Identifier()} $_content ON DUPLICATE KEY UPDATE {setClauses};";
            }

            var response = await tx.QueryAsync(sql, bindings, ct).ConfigureAwait(false);
            response.EnsureSuccess();
            // Diagnostic log carries the edge id; payload values stay typed-CBOR so we
            // skip the dict copy for the typed path (caller has the original object in
            // their own scope).
            Record(Command.Relate(src, edge, tgt));
        }
        catch
        {
            closed = true;
            throw;
        }
    }

    private async Task RelateAsyncCore<TKind>(
        IRecordId source,
        IRecordId target,
        RecordId? explicitEdge,
        IReadOnlyDictionary<string, object?>? payload,
        SurrealTransaction tx,
        CancellationToken ct) where TKind : IRelationKind
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tx);
        ThrowIfClosed();
        try
        {
            var src = RecordId.From(source);
            var tgt = RecordId.From(target);
            var edge = (explicitEdge ?? RecordId.Idempotent(TKind.EdgeName)).Resolve(src, tgt);
            state.Edges.Add((src, TKind.EdgeName, tgt));

            // INSERT RELATION IGNORE — SurrealDB's substrate-native idempotent edge
            // create. The IGNORE clause makes a duplicate id (or a duplicate (in, out)
            // tripping the UNIQUE INDEX) a silent no-op rather than an error, so
            // re-running RelateAsync on the same triple is safe. The RELATION keyword
            // satisfies TYPE RELATION ENFORCED schemas (which UPSERT does NOT — UPSERT
            // creates a regular row that the substrate then refuses to recognise as a
            // valid edge; preview.46 was wrong about this).
            //
            // Semantic note: with IGNORE, the FIRST RelateAsync call wins. A second
            // call with a different payload is a no-op — payload is not updated. Users
            // wanting to change payload should UPDATE the edge id directly. This
            // matches the "Relate is idempotent" contract: re-calling is safe and
            // doesn't surprise-write.
            //
            // For caller-minted edge ids (Random Ulid / Slug), behaviour is the same:
            // a different id for the same (in, out) pair trips the unique index and
            // IGNORE silently absorbs it — no error, no duplicate row.
            var content = new SurrealObject
            {
                ["id"] = new SurrealRecordIdValue(edge.ToSdk()),
                ["in"] = new SurrealRecordIdValue(src.ToSdk()),
                ["out"] = new SurrealRecordIdValue(tgt.ToSdk()),
            };
            if (payload is not null)
            {
                foreach (var kv in payload)
                {
                    content[kv.Key] = SurfaceQueryCompiler.WrapAsSurrealValue(kv.Value);
                }
            }

            var sql = $"INSERT RELATION IGNORE INTO {TKind.EdgeName.Identifier()} $_content;";
            var bindings = new SurrealObject { ["_content"] = new SurrealObjectValue(content) };
            var response = await tx.QueryAsync(sql, bindings, ct).ConfigureAwait(false);
            response.EnsureSuccess();
            Record(Command.Relate(src, edge, tgt, payload));
        }
        catch
        {
            closed = true;
            throw;
        }
    }

    /// <summary>
    /// Dispatches a DELETE-edge through <paramref name="tx"/>: removes edges of kind
    /// <typeparamref name="TKind"/> matching the supplied endpoints. At least one of
    /// <paramref name="source"/> / <paramref name="target"/> must be non-null; both
    /// non-null targets a single edge, one-side-null is the bulk form (every matching
    /// edge of the kind, persisted-or-not).
    /// </summary>
    public async Task UnrelateAsync<TKind>(
        IRecordId? source,
        IRecordId? target,
        SurrealTransaction tx,
        CancellationToken ct = default) where TKind : IRelationKind
    {
        if (source is null && target is null)
        {
            throw new ArgumentException(
                "UnrelateAsync requires at least one of source or target to be non-null.");
        }
        ArgumentNullException.ThrowIfNull(tx);
        ThrowIfClosed();
        try
        {
            var src = source is null ? (RecordId?)null : RecordId.From(source);
            var tgt = target is null ? (RecordId?)null : RecordId.From(target);
            // Drop matching edges from the in-memory read index too so subsequent
            // QueryOutgoing/Incoming/RelatedIds calls in this session don't see ghosts.
            foreach (var key in state.Edges.ToList())
            {
                if (key.Edge != TKind.EdgeName)
                {
                    continue;
                }

                if (src is { } s && key.Source != s)
                {
                    continue;
                }

                if (tgt is { } t && key.Target != t)
                {
                    continue;
                }

                state.Edges.Remove(key);
            }

            // Typed dispatch: identifier (edge table) inlined into SurrealQL, endpoint
            // ids carried as typed SurrealRecordIdValue bindings. No string escape rules.
            var bindings = new SurrealObject();
            var sql = new StringBuilder("DELETE ").Append(TKind.EdgeName.Identifier());
            var hasWhere = false;
            if (src is { } sId)
            {
                sql.Append(" WHERE in = $_src");
                bindings["_src"] = new SurrealRecordIdValue(sId.ToSdk());
                hasWhere = true;
            }
            if (tgt is { } tId)
            {
                sql.Append(hasWhere ? " AND out = $_tgt" : " WHERE out = $_tgt");
                bindings["_tgt"] = new SurrealRecordIdValue(tId.ToSdk());
            }
            sql.Append(';');
            var response = await tx.QueryAsync(sql.ToString(), bindings, ct).ConfigureAwait(false);
            response.EnsureSuccess();
            Record(Command.Unrelate(src, TKind.EdgeName, tgt));
        }
        catch
        {
            closed = true;
            throw;
        }
    }

    /// <summary>
    /// Dispatches a DELETE for <paramref name="entity"/> through <paramref name="tx"/>,
    /// honouring <c>[Reject]</c>/<c>[Cascade]</c>/<c>[Unset]</c>/<c>[Ignore]</c> reference
    /// semantics by walking the in-memory snapshot first.
    /// <para>
    /// Pre-flight cascade resolve (<see cref="PlanDelete"/>): walks <c>state.Entities</c>
    /// via <see cref="IEntity.EnumerateReferences"/> + <see cref="IReferenceRegistry"/>,
    /// classifies every incoming reference, runs three phases — Cascade + Unset to
    /// fixpoint, then Reject blockers from the steady-state graph. If any blockers
    /// remain (referencers that point at a doomed record with <see cref="ReferenceDeleteBehavior.Reject"/>
    /// AND aren't themselves cascading), throws <see cref="CascadeRejectException"/>
    /// before any wire dispatch.
    /// </para>
    /// <para>
    /// On a clean plan: <see cref="IEntity.OnDeleting"/> fires on every entity in the
    /// cascade set; Unset actions mirror the substrate's <c>REFERENCE ON DELETE UNSET</c>
    /// into the in-memory entity backing fields via <see cref="IEntity.SetReferenceTo"/>;
    /// a single <c>tx.DeleteAsync(target.Id)</c> dispatches — the substrate cascades the
    /// rest under <c>REFERENCE ON DELETE CASCADE</c>; <see cref="CleanupLocalState"/>
    /// runs for every cascaded id.
    /// </para>
    /// <para>
    /// The library is the planner; the substrate is the executor. Prediction is
    /// deterministic from the schema we emitted, so no follow-up read-back is needed to
    /// reconcile.
    /// </para>
    /// </summary>
    public async Task DeleteAsync(IEntity entity, SurrealTransaction tx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(tx);
        ThrowIfClosed();
        try
        {
            var plan = PlanDelete(entity.Id);

            foreach (var id in plan.CascadeSet)
            {
                if (state.Entities.TryGetValue(id, out var e))
                {
                    e.OnDeleting();
                }
            }

            foreach (var (owner, fieldName) in plan.UnsetActions)
            {
                owner.SetReferenceTo(fieldName, null);
            }

            // Typed SDK dispatch — substrate's REFERENCE ON DELETE clauses cascade the
            // rest. No per-entity wire calls; the schema does the work.
            await tx.DeleteAsync(entity.Id.ToSdk(), ct).ConfigureAwait(false);
            Record(Command.Delete(entity.Id));

            foreach (var id in plan.CascadeSet)
            {
                CleanupLocalState(id);
            }
        }
        catch
        {
            closed = true;
            throw;
        }
    }

    /// <summary>
    /// Cascade-resolve plan: which ids the substrate will delete (directly + cascaded),
    /// and which (referencer, field-name) pairs need their backing field nulled in the
    /// in-memory snapshot to mirror the substrate's <c>REFERENCE ON DELETE UNSET</c>.
    /// </summary>
    private readonly record struct DeletePlan(
        IReadOnlyList<RecordId> CascadeSet,
        IReadOnlyList<(IEntity Owner, string FieldName)> UnsetActions);

    /// <summary>
    /// Three-phase pre-flight cascade resolve. Phase 1: BFS from <paramref name="target"/>,
    /// classify every incoming reference by <see cref="ReferenceDeleteBehavior"/>
    /// (Cascade enqueues; Unset records a pending null-write; Reject collects a blocker;
    /// Ignore is skipped). Cascade + Unset run to fixpoint. Phase 2: filter rejecters
    /// whose owner is itself cascading away (transitively) — they don't block. Phase 3:
    /// throw <see cref="CascadeRejectException"/> if any steady-state blockers remain.
    /// </summary>
    private DeletePlan PlanDelete(RecordId target)
    {
        var deleted = new HashSet<RecordId> { target };
        var queue = new Queue<RecordId>();
        queue.Enqueue(target);

        var unsetActions = new List<(IEntity Owner, string FieldName)>();
        // Provisional rejecters — re-checked after Cascade reaches fixpoint, since a
        // rejecter that's itself cascading away doesn't block.
        var rejecters = new List<(IEntity Owner, string FieldName, RecordId BlockedTarget)>();

        while (queue.TryDequeue(out var current))
        {
            var policiesForCurrent = ReferenceRegistry.IncomingReferencesTo(current.Table);
            if (policiesForCurrent.Count == 0)
            {
                continue;
            }

            foreach (var entity in state.Entities.Values)
            {
                var entityTable = entity.Id.Table;
                ReferenceFieldInfo? hitInfo = null;
                string? hitField = null;

                foreach (var (fieldName, refTarget) in entity.EnumerateReferences())
                {
                    if (refTarget != current)
                    {
                        continue;
                    }

                    // Pick the policy entry that matches this entity's table + field. The
                    // registry is keyed on the referenced table; we still have to filter
                    // by the (referencer table, field name) on this end.
                    foreach (var info in policiesForCurrent)
                    {
                        if (info.ReferencerTable == entityTable && info.FieldName == fieldName)
                        {
                            hitInfo = info;
                            hitField = fieldName;
                            break;
                        }
                    }

                    if (hitInfo is null)
                    {
                        continue;
                    }

                    switch (hitInfo.Behavior)
                    {
                        case ReferenceDeleteBehavior.Cascade:
                            if (deleted.Add(entity.Id))
                            {
                                queue.Enqueue(entity.Id);
                            }
                            break;
                        case ReferenceDeleteBehavior.Unset:
                            unsetActions.Add((entity, hitField!));
                            break;
                        case ReferenceDeleteBehavior.Reject:
                            rejecters.Add((entity, hitField!, current));
                            break;
                        case ReferenceDeleteBehavior.Ignore:
                            // Schema declared "leave dangling" — substrate does nothing,
                            // and we don't need to either.
                            break;
                    }

                    hitInfo = null;
                    hitField = null;
                }
            }
        }

        // Phase 2: filter rejecters whose owner is cascading away — they're going with
        // the cascade so their reference goes too; not a steady-state blocker.
        var steadyBlockers = new List<CascadeRejectBlocker>();
        foreach (var (owner, fieldName, blockedTarget) in rejecters)
        {
            if (deleted.Contains(owner.Id))
            {
                continue;
            }
            steadyBlockers.Add(new CascadeRejectBlocker(owner.Id, fieldName, blockedTarget));
        }

        // Phase 3: throw if any blockers remain.
        if (steadyBlockers.Count > 0)
        {
            throw new CascadeRejectException(steadyBlockers);
        }

        // Filter Unset actions whose owner is cascading away — same reason.
        var liveUnsets = new List<(IEntity Owner, string FieldName)>();
        foreach (var pair in unsetActions)
        {
            if (deleted.Contains(pair.Owner.Id))
            {
                continue;
            }
            liveUnsets.Add(pair);
        }

        return new DeletePlan(deleted.ToArray(), liveUnsets);
    }

    /// <summary>Closes the session — once abandoned, it's done.</summary>
    public void Abandon()
    {
        Log.Clear();
        closed = true;
    }


    // ──────────────────────────── IHydrationSink (explicit-interface) ──────
    //
    // Loader-side write surface. Explicit-interface so domain code with a SurrealSession
    // reference doesn't see Track / Edge in IntelliSense — getting at them requires a
    // deliberate `(IHydrationSink)session` cast. Closed sessions reject hydration too,
    // since hydration is load-time and a closed session shouldn't be receiving more state.

    void IHydrationSink.Track(IEntity entity)
    {
        ThrowIfClosed();
        if (state.Entities.TryGetValue(entity.Id, out var existing))
        {
            // Same instance — idempotent, fall through to the loadedAtStart bump.
            // Different instance — common during include-heavy queries where the same
            // row reaches the hydrator through multiple relation paths (A.Calls and
            // A.Uses both targeting B, etc). The hydrator's `new T() + Hydrate` runs
            // per row and would conflict with the first-hit instance; we silently
            // drop the new one. The DB row is the same regardless of traversal path,
            // so the existing instance already carries the right state.
            //
            // Distinct from the public Track<T> path, which DOES throw on instance
            // conflict — user code holding two live instances for the same id is
            // identity-map poison and we want to surface that loudly. Hydration dups
            // are accidental and benign.
            if (!ReferenceEquals(existing, entity))
            {
                loadedAtStart.Add(entity.Id);
                return;
            }
        }
        else
        {
            state.Entities[entity.Id] = entity;
            entity.Bind(this);
        }
        loadedAtStart.Add(entity.Id);
    }

    void IHydrationSink.Edge(RecordId source, string edgeKind, RecordId target)
    {
        ThrowIfClosed();
        // Read-side index only — Query{Outgoing,Incoming,RelatedIds,InverseRelatedIds}
        // consult this set so loaded edges show up at .Constraints / .Relations / etc.
        // No "loaded vs new" diff anymore (sync Relate is gone), so no second set is
        // needed to track what was on disk at start.
        state.Edges.Add((source, edgeKind, target));
    }

    void IHydrationSink.MarkSliceLoaded(RecordId ownerId, string fieldName)
    {
        ThrowIfClosed();
        if (!state.LoadedSlices.TryGetValue(ownerId, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            state.LoadedSlices[ownerId] = set;
        }
        set.Add(fieldName);
    }

    // ──────────────────────────── private helpers ───────────────────────────

    /// <summary>
    /// Append-to-log chokepoint for diagnostic-grade tracing of model commands. Track
    /// records a Create command here; RelateAsync / UnrelateAsync / DeleteAsync record
    /// their own. Property setters do not go through this path under the explicit-Save
    /// model.
    /// </summary>
    private void Record(Command c)
    {
        Log.Append(c);
    }

    /// <summary>
    /// Wipes every in-memory mention of <paramref name="id"/> — the entity itself and
    /// any edges with this on either endpoint. Parent / reference back-resolution is
    /// per-entity now (via <see cref="IEntity.GetParentId"/> + entity backing fields),
    /// so deleted entities naturally drop out of <see cref="QueryChildren{T}"/> when
    /// state.Entities loses them.
    /// </summary>
    private void CleanupLocalState(RecordId id)
    {
        state.Entities.Remove(id);

        foreach (var k in state.Edges.Where(k => k.Source == id || k.Target == id).ToList())
        {
            state.Edges.Remove(k);
        }
    }
}
