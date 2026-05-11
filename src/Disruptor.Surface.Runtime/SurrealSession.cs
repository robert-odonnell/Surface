using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Disruptor.Surface.Annotations;
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
/// (<see cref="SaveAsync"/>, <see cref="DeleteAsync"/>, <see cref="UnrelateAsync"/>)
/// is the only path that touches Surreal. Edge writes flow through
/// <see cref="SaveAsync"/> against a relation-variant entity (the variant's emitted
/// <c>IEntity.SaveAsync</c> body dispatches <c>INSERT RELATION INTO …</c>).
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
/// <para>
/// Sync edge reads (<see cref="QueryOutgoing{T}"/> / <see cref="QueryIncoming{T}"/> /
/// <see cref="QueryRelatedIds"/> / <see cref="QueryInverseRelatedIds"/>) consult the
/// in-memory snapshot. The async variant-query family
/// (<see cref="QueryVariantsOutgoingAsync{TVariant}(IRecordId, SurrealTransaction, CancellationToken)"/> /
/// <see cref="QueryVariantsIncomingAsync{TVariant}(IRecordId, SurrealTransaction, CancellationToken)"/> /
/// <see cref="QueryVariantsAsync{TVariant}(string, SurrealObject?, SurrealTransaction, CancellationToken)"/> /
/// <see cref="QueryOutgoingAsync{TKind, TTarget}(IRecordId, SurrealTransaction, CancellationToken)"/> /
/// <see cref="QueryIncomingAsync{TKind, TTarget}(IRecordId, SurrealTransaction, CancellationToken)"/>)
/// dispatches fresh SurrealQL against the user's transaction (or read-only
/// <see cref="SurrealClient"/>) — use the variant family when the snapshot doesn't carry
/// the edges (cross-aggregate, large fan-outs, ad-hoc traversal).
/// </para>
/// </summary>
public sealed class SurrealSession(IReferenceRegistry referenceRegistry) : IHydrationSink
{
    private class MaterializedSessionState
    {
        public readonly Dictionary<RecordId, IEntity> Entities = [];

        // Read-side index of edges visible in this session. Populated by IHydrationSink.Edge
        // at load time and by SaveContext.MarkSaved when a variant's IEntity.SaveAsync
        // returns (RecordVariantEdge mirrors the (in, edge, out) tuple here). Pure
        // presence — Query{Outgoing,Incoming,RelatedIds,InverseRelatedIds} only ask
        // "is this edge in the snapshot?". No write buffering: with sync Relate gone,
        // every edge mutation goes straight through the user's transaction via
        // SaveAsync(variant) / UnrelateAsync, and the index updates as those calls complete.
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
    // start-time set — there's no snapshot-diff dispatch anymore; variant SaveAsync
    // ships each edge straight through the user's transaction.
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
    // writes. Edge mutations now go through SaveAsync<variantInstance> / UnrelateAsync,
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

            // Promote into loadedAtStart so subsequent SaveContexts (e.g. a later
            // session.SaveAsync(variant) call whose forward-dep walk reaches an entity
            // already saved in an earlier call) see IsTracked=true and dispatch UPDATE
            // instead of re-CREATE. Without this, every fresh SaveContext starts blind
            // to anything we've previously saved in the same session and the substrate
            // rejects the duplicate CREATE with "record already exists".
            session.loadedAtStart.Add(entity.Id);

            // Variant SaveAsync dispatches INSERT RELATION INTO {edge} … against the
            // substrate but doesn't itself touch state.Edges — the variant's emitted
            // Hydrate intentionally calls sink.Track(this) only (no sink.Edge). Mirror
            // the new edge into the read-side index here so subsequent sync reads
            // (QueryOutgoing / QueryRelatedIds) see it before the session is reloaded.
            if (entity is IRelationVariant)
            {
                session.RecordVariantEdge(entity);
            }
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

    // ──────────────────────────── variant queries (async) ───────────────────
    //
    // Three flavours, all dispatched fresh against the user's transport (no snapshot
    // read):
    //   * QueryVariantsOutgoingAsync<TVariant> / QueryVariantsIncomingAsync<TVariant>
    //     — option A: return hydrated typed-variant rows for one side filter. Tracks
    //     the variants in the session AND records each (in, edge, out) tuple in
    //     state.Edges so subsequent sync reads off the entity-side [Restricts] /
    //     [Restrictions] collection see the new edges.
    //   * QueryOutgoingAsync<TKind, TTarget> / QueryIncomingAsync<TKind, TTarget>
    //     — option B: skip the variant entirely, fetch the endpoint records directly
    //     via SELECT VALUE out.* (or in.*). Read-side targets are NOT auto-tracked —
    //     this is the ad-hoc "give me the targets" path. To track them in the
    //     session, route through FetchAsync(Workspace.Query.{Table}.…) instead.
    //   * QueryVariantsAsync<TVariant>(sql, bindings) — option E: caller-supplied
    //     SQL, hydrated as typed variants. The escape hatch for queries the typed
    //     surface doesn't model (multi-side, custom predicates, raw aggregations).

    /// <summary>
    /// Dispatches <c>SELECT * FROM {edge} WHERE in = $_src;</c> against
    /// <paramref name="tx"/> and hydrates each row into a fresh
    /// <typeparamref name="TVariant"/>. The variants are tracked in this session and
    /// each <c>(in, edge, out)</c> tuple is added to the in-memory edge index, so
    /// subsequent sync reads off the entity-side relation collection (e.g.
    /// <c>constraint.Restrictions</c>) see the freshly loaded edges.
    /// <para>
    /// Use this when the snapshot doesn't carry the edges (cross-aggregate, large
    /// fan-outs, ad-hoc traversal). For in-aggregate edges already loaded by
    /// <c>Workspace.Load{Root}Async</c>, the entity's <c>[Restricts]</c> /
    /// <c>[Restrictions]</c> property is the sync alternative.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<TVariant>> QueryVariantsOutgoingAsync<TVariant>(
        IRecordId source, SurrealTransaction tx, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsAsyncCore<TVariant>(source, isSource: true, tx.QueryAsync, ct);

    /// <inheritdoc cref="QueryVariantsOutgoingAsync{TVariant}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TVariant>> QueryVariantsOutgoingAsync<TVariant>(
        IRecordId source, SurrealClient db, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsAsyncCore<TVariant>(source, isSource: true, db.QueryAsync, ct);

    /// <inheritdoc cref="QueryVariantsOutgoingAsync{TVariant}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TVariant>> QueryVariantsOutgoingAsync<TVariant>(
        IEntity source, SurrealTransaction tx, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsOutgoingAsync<TVariant>(source.Id, tx, ct);

    /// <inheritdoc cref="QueryVariantsOutgoingAsync{TVariant}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TVariant>> QueryVariantsOutgoingAsync<TVariant>(
        IEntity source, SurrealClient db, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsOutgoingAsync<TVariant>(source.Id, db, ct);

    /// <summary>
    /// Dispatches <c>SELECT * FROM {edge} WHERE out = $_tgt;</c> against
    /// <paramref name="tx"/> and hydrates each row into a fresh
    /// <typeparamref name="TVariant"/>. Same tracking + edge-index population as
    /// <see cref="QueryVariantsOutgoingAsync{TVariant}(IRecordId, SurrealTransaction, CancellationToken)"/>
    /// — see that overload for the contract.
    /// </summary>
    public Task<IReadOnlyList<TVariant>> QueryVariantsIncomingAsync<TVariant>(
        IRecordId target, SurrealTransaction tx, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsAsyncCore<TVariant>(target, isSource: false, tx.QueryAsync, ct);

    /// <inheritdoc cref="QueryVariantsIncomingAsync{TVariant}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TVariant>> QueryVariantsIncomingAsync<TVariant>(
        IRecordId target, SurrealClient db, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsAsyncCore<TVariant>(target, isSource: false, db.QueryAsync, ct);

    /// <inheritdoc cref="QueryVariantsIncomingAsync{TVariant}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TVariant>> QueryVariantsIncomingAsync<TVariant>(
        IEntity target, SurrealTransaction tx, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsIncomingAsync<TVariant>(target.Id, tx, ct);

    /// <inheritdoc cref="QueryVariantsIncomingAsync{TVariant}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TVariant>> QueryVariantsIncomingAsync<TVariant>(
        IEntity target, SurrealClient db, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsIncomingAsync<TVariant>(target.Id, db, ct);

    /// <summary>
    /// Escape hatch for queries the typed variant surface doesn't model — caller
    /// supplies SurrealQL + bindings, every row is hydrated as
    /// <typeparamref name="TVariant"/>. Same tracking + edge-index update as the
    /// typed overloads. The caller's SQL is opaque to the library: get the SELECT
    /// shape right (the row needs <c>id</c>, <c>in</c>, <c>out</c>, plus payload
    /// fields) or hydration silently drops fields.
    /// </summary>
    public Task<IReadOnlyList<TVariant>> QueryVariantsAsync<TVariant>(
        string sql, SurrealObject? bindings, SurrealTransaction tx, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsRawAsyncCore<TVariant>(sql, bindings, tx.QueryAsync, ct);

    /// <inheritdoc cref="QueryVariantsAsync{TVariant}(string, SurrealObject?, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TVariant>> QueryVariantsAsync<TVariant>(
        string sql, SurrealObject? bindings, SurrealClient db, CancellationToken ct = default)
        where TVariant : class, IEntity, new()
        => QueryVariantsRawAsyncCore<TVariant>(sql, bindings, db.QueryAsync, ct);

    /// <summary>
    /// Dispatches <c>SELECT VALUE out.* FROM {edge} WHERE in = $_src AND out.tb = $_outTable;</c>
    /// against <paramref name="tx"/> and hydrates each row into a fresh
    /// <typeparamref name="TTarget"/>. The targets are NOT auto-tracked — option B is
    /// the ad-hoc "give me the targets" path; to bring them into the session, route
    /// through <c>FetchAsync(Workspace.Query.{Table}.…)</c> instead.
    /// </summary>
    public Task<IReadOnlyList<TTarget>> QueryOutgoingAsync<TKind, TTarget>(
        IRecordId source, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        where TTarget : class, IEntity, new()
        => QueryEdgeTraversalAsyncCore<TTarget>(source, isSource: true, TKind.EdgeName, tx.QueryAsync, ct);

    /// <inheritdoc cref="QueryOutgoingAsync{TKind, TTarget}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TTarget>> QueryOutgoingAsync<TKind, TTarget>(
        IRecordId source, SurrealClient db, CancellationToken ct = default)
        where TKind : IRelationKind
        where TTarget : class, IEntity, new()
        => QueryEdgeTraversalAsyncCore<TTarget>(source, isSource: true, TKind.EdgeName, db.QueryAsync, ct);

    /// <inheritdoc cref="QueryOutgoingAsync{TKind, TTarget}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TTarget>> QueryOutgoingAsync<TKind, TTarget>(
        IEntity source, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        where TTarget : class, IEntity, new()
        => QueryOutgoingAsync<TKind, TTarget>(source.Id, tx, ct);

    /// <inheritdoc cref="QueryOutgoingAsync{TKind, TTarget}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TTarget>> QueryOutgoingAsync<TKind, TTarget>(
        IEntity source, SurrealClient db, CancellationToken ct = default)
        where TKind : IRelationKind
        where TTarget : class, IEntity, new()
        => QueryOutgoingAsync<TKind, TTarget>(source.Id, db, ct);

    /// <summary>
    /// Dispatches <c>SELECT VALUE in.* FROM {edge} WHERE out = $_tgt AND in.tb = $_inTable;</c>
    /// — incoming-side mirror of
    /// <see cref="QueryOutgoingAsync{TKind, TTarget}(IRecordId, SurrealTransaction, CancellationToken)"/>.
    /// Targets are not tracked.
    /// </summary>
    public Task<IReadOnlyList<TTarget>> QueryIncomingAsync<TKind, TTarget>(
        IRecordId target, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        where TTarget : class, IEntity, new()
        => QueryEdgeTraversalAsyncCore<TTarget>(target, isSource: false, TKind.EdgeName, tx.QueryAsync, ct);

    /// <inheritdoc cref="QueryIncomingAsync{TKind, TTarget}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TTarget>> QueryIncomingAsync<TKind, TTarget>(
        IRecordId target, SurrealClient db, CancellationToken ct = default)
        where TKind : IRelationKind
        where TTarget : class, IEntity, new()
        => QueryEdgeTraversalAsyncCore<TTarget>(target, isSource: false, TKind.EdgeName, db.QueryAsync, ct);

    /// <inheritdoc cref="QueryIncomingAsync{TKind, TTarget}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TTarget>> QueryIncomingAsync<TKind, TTarget>(
        IEntity target, SurrealTransaction tx, CancellationToken ct = default)
        where TKind : IRelationKind
        where TTarget : class, IEntity, new()
        => QueryIncomingAsync<TKind, TTarget>(target.Id, tx, ct);

    /// <inheritdoc cref="QueryIncomingAsync{TKind, TTarget}(IRecordId, SurrealTransaction, CancellationToken)"/>
    public Task<IReadOnlyList<TTarget>> QueryIncomingAsync<TKind, TTarget>(
        IEntity target, SurrealClient db, CancellationToken ct = default)
        where TKind : IRelationKind
        where TTarget : class, IEntity, new()
        => QueryIncomingAsync<TKind, TTarget>(target.Id, db, ct);

    private async Task<IReadOnlyList<TVariant>> QueryVariantsAsyncCore<TVariant>(
        IRecordId endpoint,
        bool isSource,
        Func<string, SurrealObject?, CancellationToken, Task<SurrealQueryResponse>> queryFn,
        CancellationToken ct)
        where TVariant : class, IEntity, new()
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ThrowIfClosed();
        try
        {
            var edgeName = ResolveVariantEdgeName(typeof(TVariant));
            var rid = RecordId.From(endpoint);
            var bindings = new SurrealObject();
            string sql;
            if (isSource)
            {
                bindings["_src"] = new SurrealRecordIdValue(rid.ToSdk());
                sql = $"SELECT * FROM {edgeName.Identifier()} WHERE in = $_src;";
            }
            else
            {
                bindings["_tgt"] = new SurrealRecordIdValue(rid.ToSdk());
                sql = $"SELECT * FROM {edgeName.Identifier()} WHERE out = $_tgt;";
            }

            var response = await queryFn(sql, bindings, ct).ConfigureAwait(false);
            response.EnsureSuccess();
            return HydrateVariants<TVariant>(response, edgeName);
        }
        catch
        {
            // Fail-closed: any dispatch failure marks the session done.
            closed = true;
            throw;
        }
    }

    private async Task<IReadOnlyList<TVariant>> QueryVariantsRawAsyncCore<TVariant>(
        string sql,
        SurrealObject? bindings,
        Func<string, SurrealObject?, CancellationToken, Task<SurrealQueryResponse>> queryFn,
        CancellationToken ct)
        where TVariant : class, IEntity, new()
    {
        ArgumentNullException.ThrowIfNull(sql);
        ThrowIfClosed();
        try
        {
            var edgeName = ResolveVariantEdgeName(typeof(TVariant));
            var response = await queryFn(sql, bindings, ct).ConfigureAwait(false);
            response.EnsureSuccess();
            return HydrateVariants<TVariant>(response, edgeName);
        }
        catch
        {
            closed = true;
            throw;
        }
    }

    /// <summary>
    /// Hydrates each row of the (single-statement) response as a fresh
    /// <typeparamref name="TVariant"/>, tracks the variant in the session via
    /// <see cref="IHydrationSink.Track"/>, and feeds the <c>(in, edge, out)</c>
    /// tuple to <see cref="IHydrationSink.Edge"/> so the in-memory edge index stays
    /// consistent with what the substrate just returned. The variant emitter calls
    /// <c>sink.Track(this)</c> from its own Hydrate body but does NOT add the edge
    /// tuple — that's this helper's job, since the entity-side relation collections
    /// are read off <c>state.Edges</c>.
    /// </summary>
    private List<TVariant> HydrateVariants<TVariant>(SurrealQueryResponse response, string edgeName)
        where TVariant : class, IEntity, new()
    {
        var list = new List<TVariant>();
        if (response.Count == 0)
        {
            return list;
        }
        var rows = response.Take(0);
        IHydrationSink sink = this;
        if (rows is SurrealListValue arr)
        {
            foreach (var row in arr.List)
            {
                if (row is SurrealObjectValue obj)
                {
                    HydrateOneVariant(obj, sink, edgeName, list);
                }
            }
        }
        else if (rows is SurrealObjectValue single)
        {
            HydrateOneVariant(single, sink, edgeName, list);
        }
        return list;
    }

    private static void HydrateOneVariant<TVariant>(
        SurrealObjectValue row, IHydrationSink sink, string edgeName, List<TVariant> list)
        where TVariant : class, IEntity, new()
    {
        var v = new TVariant();
        v.Hydrate(row, sink);

        // Variant Hydrate already called sink.Track(this); now mirror the (in, edge, out)
        // tuple into the snapshot edge index so subsequent sync reads off the entity-side
        // relation collection (which queries state.Edges) see this freshly-loaded edge.
        if (HydrationValue.TryReadRecordId(row, "in", out var src)
            && HydrationValue.TryReadRecordId(row, "out", out var tgt))
        {
            sink.Edge(src, edgeName, tgt);
        }

        list.Add(v);
    }

    private async Task<IReadOnlyList<TTarget>> QueryEdgeTraversalAsyncCore<TTarget>(
        IRecordId endpoint,
        bool isSource,
        string edgeName,
        Func<string, SurrealObject?, CancellationToken, Task<SurrealQueryResponse>> queryFn,
        CancellationToken ct)
        where TTarget : class, IEntity, new()
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ThrowIfClosed();
        try
        {
            var rid = RecordId.From(endpoint);
            var targetTable = ResolveTargetTable<TTarget>();
            var bindings = new SurrealObject();
            string sql;
            if (isSource)
            {
                bindings["_src"] = new SurrealRecordIdValue(rid.ToSdk());
                bindings["_outTable"] = new StringSurrealValue(targetTable);
                sql = $"SELECT VALUE out.* FROM {edgeName.Identifier()} WHERE in = $_src AND out.tb = $_outTable;";
            }
            else
            {
                bindings["_tgt"] = new SurrealRecordIdValue(rid.ToSdk());
                bindings["_inTable"] = new StringSurrealValue(targetTable);
                sql = $"SELECT VALUE in.* FROM {edgeName.Identifier()} WHERE out = $_tgt AND in.tb = $_inTable;";
            }

            var response = await queryFn(sql, bindings, ct).ConfigureAwait(false);
            response.EnsureSuccess();
            return HydrateTraversalTargets<TTarget>(response);
        }
        catch
        {
            closed = true;
            throw;
        }
    }

    /// <summary>
    /// Hydrates each row as a fresh <typeparamref name="TTarget"/> through a no-op sink
    /// — option B is "ad-hoc give me the targets", so endpoints are not tracked in the
    /// session. Callers that want the targets in the session should route through
    /// <see cref="FetchAsync{T}(SurfaceQuery{T}, SurrealClient, CancellationToken)"/>
    /// instead.
    /// </summary>
    private static List<TTarget> HydrateTraversalTargets<TTarget>(SurrealQueryResponse response)
        where TTarget : class, IEntity, new()
    {
        var list = new List<TTarget>();
        if (response.Count == 0)
        {
            return list;
        }
        var rows = response.Take(0);
        IHydrationSink sink = NullHydrationSink.Instance;
        if (rows is SurrealListValue arr)
        {
            foreach (var row in arr.List)
            {
                if (row is SurrealObjectValue obj)
                {
                    var t = new TTarget();
                    t.Hydrate(obj, sink);
                    list.Add(t);
                }
            }
        }
        else if (rows is SurrealObjectValue single)
        {
            var t = new TTarget();
            t.Hydrate(single, sink);
            list.Add(t);
        }
        return list;
    }

    /// <summary>
    /// No-op <see cref="IHydrationSink"/> used by option-B traversal hydration so the
    /// returned target entities don't get tracked in the session. Generated entity
    /// Hydrate bodies call <c>sink.Track(this)</c> unconditionally; routing through
    /// the null sink keeps the option-B contract ("ad-hoc give me the targets") intact.
    /// </summary>
    private sealed class NullHydrationSink : IHydrationSink
    {
        public static readonly NullHydrationSink Instance = new();
        public void Track(IEntity entity) { }
        public void Edge(RecordId source, string edgeKind, RecordId target) { }
        public bool IsTracked(IRecordId id) => false;
        public void MarkSliceLoaded(RecordId ownerId, string fieldName) { }
    }

    /// <summary>
    /// Resolves the SurrealDB table name for <typeparamref name="TTarget"/> by
    /// instantiating once and reading <c>Id.Table</c>. Cached per <typeparamref name="TTarget"/>
    /// type (process-wide; <see cref="ConcurrentDictionary{TKey, TValue}"/> for the
    /// concurrent case where two sessions on different threads first see the same type).
    /// </summary>
    private static string ResolveTargetTable<TTarget>() where TTarget : class, IEntity, new()
    {
        return TargetTableCache.GetOrAdd(typeof(TTarget), _ =>
        {
            var probe = new TTarget();
            return probe.Id.Table;
        });
    }

    private static readonly ConcurrentDictionary<Type, string> TargetTableCache = new();

    /// <summary>
    /// Resolves the SurrealDB edge name for a relation variant
    /// <typeparamref name="TVariant"/>: walks the type's attributes for one inheriting
    /// <see cref="Disruptor.Surface.Annotations.RelationAttribute"/>, looks up the
    /// sibling kind class (attribute name minus <c>Attribute</c> suffix, same
    /// namespace, same assembly), and reads its static
    /// <see cref="IRelationKind.EdgeName"/>. Cached per variant <see cref="Type"/> —
    /// Type identity is stable, no invalidation needed.
    /// </summary>
    private static string ResolveVariantEdgeName(Type variantType)
        => VariantEdgeNameCache.GetOrAdd(variantType, ResolveVariantEdgeNameUncached);

    private static string ResolveVariantEdgeNameUncached(Type variantType)
    {
        // Walk the variant's attributes for the relation kind. Variants only ever carry
        // one — see RelationVariantExtractor — but the AttributeUsage allows multiple,
        // so we take the first that derives from RelationAttribute.
        Type? attrType = null;
        foreach (var attr in variantType.GetCustomAttributes(inherit: false))
        {
            if (attr is RelationAttribute)
            {
                attrType = attr.GetType();
                break;
            }
        }
        if (attrType is null)
        {
            throw new InvalidOperationException(
                $"Variant type {variantType.FullName} carries no [RelationAttribute]-derived attribute. "
                + "Ensure the class is annotated with the relation kind (e.g. [Restricts]).");
        }

        // Strip "Attribute" suffix and look the marker class up in the same namespace +
        // assembly. The generator emits the kind class as a sibling type alongside the
        // attribute (e.g. RestrictsAttribute → Restricts).
        var attrName = attrType.Name;
        if (!attrName.EndsWith("Attribute", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Relation attribute {attrType.FullName} does not end in 'Attribute'; cannot resolve sibling kind class.");
        }
        var markerName = attrName[..^"Attribute".Length];
        var markerFullName = string.IsNullOrEmpty(attrType.Namespace)
            ? markerName
            : $"{attrType.Namespace}.{markerName}";

        var markerType = attrType.Assembly.GetType(markerFullName, throwOnError: false);
        if (markerType is null || !typeof(IRelationKind).IsAssignableFrom(markerType))
        {
            throw new InvalidOperationException(
                $"Could not resolve relation kind marker class '{markerFullName}' for variant {variantType.FullName}. "
                + "Expected a sibling type implementing IRelationKind alongside the attribute.");
        }

        // Read the static abstract EdgeName via a generic dispatch (cleaner than
        // BindingFlags.Public | Static reflection, which works but isn't the canonical
        // path for static-virtual interface members).
        var method = GetEdgeNameMethod.MakeGenericMethod(markerType);
        return (string)method.Invoke(null, null)!;
    }

    private static readonly ConcurrentDictionary<Type, string> VariantEdgeNameCache = new();
    private static readonly MethodInfo GetEdgeNameMethod = typeof(SurrealSession)
        .GetMethod(nameof(GetEdgeName), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not locate GetEdgeName helper.");

    private static string GetEdgeName<TKind>() where TKind : IRelationKind => TKind.EdgeName;

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
    /// records a Create command here; UnrelateAsync / DeleteAsync record their own.
    /// Property setters do not go through this path under the explicit-Save model.
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
    /// <para>
    /// When the deleted id is itself a relation variant, drop its specific
    /// <c>(in, edge, out)</c> tuple from the edge index first so deleting the variant
    /// also clears the corresponding edge from sync reads. The endpoint-walk below
    /// only fires when the deleted id is one of the endpoints, not when it's the edge
    /// row itself.
    /// </para>
    /// </summary>
    private void CleanupLocalState(RecordId id)
    {
        if (state.Entities.TryGetValue(id, out var ent) && ent is IRelationVariant)
        {
            DropVariantEdge(ent);
        }

        state.Entities.Remove(id);

        foreach (var k in state.Edges.Where(k => k.Source == id || k.Target == id).ToList())
        {
            state.Edges.Remove(k);
        }
    }

    /// <summary>
    /// Mirrors a freshly-saved relation variant into the read-side edge index. Reads the
    /// variant's two endpoints off <see cref="IEntity.EnumerateReferences"/> (which yields
    /// <c>("in", inId)</c> / <c>("out", outId)</c> per the variant emitter contract) and
    /// adds the <c>(in, edgeName, out)</c> tuple — the edge name comes from
    /// <c>Id.Table</c>, which the variant's per-kind <c>{Marker}Id</c> struct sets to the
    /// edge-table name (e.g. <c>"restricts"</c>).
    /// <para>Either endpoint missing means the variant isn't fully formed yet — bail
    /// silently rather than synthesise a partial edge.</para>
    /// </summary>
    private void RecordVariantEdge(IEntity variant)
    {
        RecordId? src = null;
        RecordId? tgt = null;
        foreach (var (fieldName, target) in variant.EnumerateReferences())
        {
            if (target is null)
            {
                continue;
            }
            if (fieldName == "in")
            {
                src = target;
            }
            else if (fieldName == "out")
            {
                tgt = target;
            }
        }
        if (src is null || tgt is null)
        {
            return;
        }
        state.Edges.Add((src.Value, variant.Id.Table, tgt.Value));
    }

    /// <summary>
    /// Inverse of <see cref="RecordVariantEdge"/>: removes the variant's
    /// <c>(in, edge, out)</c> tuple from the edge index. Called from
    /// <see cref="CleanupLocalState"/> when a variant is being deleted, so subsequent
    /// sync reads stop seeing the edge before the session is reloaded.
    /// </summary>
    private void DropVariantEdge(IEntity variant)
    {
        RecordId? src = null;
        RecordId? tgt = null;
        foreach (var (fieldName, target) in variant.EnumerateReferences())
        {
            if (target is null)
            {
                continue;
            }
            if (fieldName == "in")
            {
                src = target;
            }
            else if (fieldName == "out")
            {
                tgt = target;
            }
        }
        if (src is null || tgt is null)
        {
            return;
        }
        state.Edges.Remove((src.Value, variant.Id.Table, tgt.Value));
    }
}
