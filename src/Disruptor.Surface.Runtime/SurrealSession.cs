using System.Text.Json;

namespace Disruptor.Surface.Runtime;

public interface ISurrealTransport : IAsyncDisposable
{
    Task<JsonDocument> ExecuteAsync(string sql, object? vars = null, CancellationToken ct = default);
}

/// <summary>
/// Distinguishes the three flavours of single-field writes the session handles. Lets a
/// single <see cref="SurrealSession.SetField"/> / <see cref="SurrealSession.UnsetField"/> pair cover
/// scalar properties, parent links, and reference links — each updates a different local
/// dict, but the SurrealQL command is the same shape.
/// </summary>
public enum FieldKind
{
    Property,
    Parent,
    Reference,
}

/// <summary>
/// The hydration-time write surface a loader uses to populate session state. Implemented
/// explicitly by <see cref="SurrealSession"/> so domain code with a session reference
/// doesn't see <c>Track</c> / <c>Parent</c> / <c>Reference</c> / <c>Edge</c> in IntelliSense
/// — getting at them requires a deliberate <c>(IHydrationSink)session</c> cast, which is
/// loud enough that nobody calls them by accident. Loaders + <see cref="HydrationJson"/>
/// are the legitimate callers.
/// </summary>
public interface IHydrationSink
{
    /// <summary>Marks an entity as loaded-at-start in the snapshot and binds it to the session on first sight.</summary>
    void Track(IEntity entity);

    /// <summary>Records a parent link from <paramref name="childId"/> to <paramref name="parentId"/>.</summary>
    void Parent(RecordId childId, RecordId parentId);

    /// <summary>Records a reference link <c>(owner, fieldName) → refId</c>, including the at-start snapshot used by the commit planner.</summary>
    void Reference(RecordId ownerId, string fieldName, RecordId refId);

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
/// Common shape of every generated entity. The five session-side hooks (<see cref="Bind"/>,
/// <see cref="Initialize"/>, <see cref="Flush"/>, <see cref="Hydrate"/>,
/// <see cref="OnDeleting"/>) are explicit-interface implementations on every generated
/// entity so they don't pollute the user-facing surface; the session is the only caller.
/// </summary>
public interface IEntity
{
    RecordId Id { get; }

    /// <summary>
    /// The session this entity is bound to, or <c>null</c> if it hasn't been tracked yet.
    /// Mutating an unbound entity buffers the writes locally — they're replayed when
    /// <see cref="Bind"/> + <see cref="Flush"/> run.
    /// </summary>
    SurrealSession? Session { get; }

    /// <summary>
    /// Wires the session into the entity's <c>_session</c> field so subsequent writes go
    /// straight to the session. Called by <see cref="SurrealSession.Track{T}"/> for fresh
    /// entities and by <see cref="Hydrate"/> for loaded ones — runs exactly once per
    /// entity instance.
    /// </summary>
    void Bind(SurrealSession session);

    /// <summary>
    /// SurrealSession-only entry point; declared on the interface so the session can call it
    /// without reflection. The generator emits this body to seed mandatory
    /// <c>[Reference]</c> targets via the user's <c>OnCreate*</c> hooks.
    /// </summary>
    void Initialize(SurrealSession session);

    /// <summary>
    /// Drains writes that were buffered while the entity was unbound (object-initializer
    /// values etc.) into <paramref name="session"/>'s pending state. <see cref="SurrealSession.Track{T}"/>
    /// invokes this after <see cref="Bind"/> + <see cref="Initialize"/> so the create
    /// command lands first, then the user's initializer values, then the mandatory-ref
    /// seeds. No-op for hydrated entities (they hit <see cref="Hydrate"/> instead).
    /// </summary>
    void Flush(SurrealSession session);

    /// <summary>
    /// Loader-only entry point. Reads the row's JSON payload and writes the entity's
    /// backing fields plus the corresponding session dicts (parent / reference) via the
    /// supplied hydration sink. Edges and children are loaded by the per-aggregate loader
    /// separately.
    /// </summary>
    void Hydrate(JsonElement json, IHydrationSink sink);

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
}

/// <summary>
/// Snapshot-isolated entity store. Every read on the model — properties, parent/children,
/// references, relation collections — is a synchronous lookup against the in-memory
/// dictionaries the per-aggregate loader (generator-emitted, invoked from
/// <c>Sessions.Load{Root}Async</c>) populated. Writes mutate the same dictionaries and
/// append to a per-entity dirty batch which <see cref="CommitAsync"/> flushes to Surreal
/// as a single SurrealQL script. The only async boundaries are the boundary methods on
/// this class — nothing inside the model surface returns <see cref="Task"/>.
/// <para>
/// Each entity carries an explicit reference to its bound session via <see cref="IEntity.Session"/>;
/// there is no ambient context. Pre-bind writes (object-initializer values) buffer on
/// the entity itself and replay through <see cref="IEntity.Flush"/> when
/// <see cref="Track{T}"/> binds the entity to this session.
/// </para>
/// <para>
/// The session knows nothing about persistence permissions. Domain code can mutate it
/// freely; until <see cref="CommitAsync"/> runs, every change is purely in-memory. Cross-
/// process write coordination lives in <see cref="WriterLease"/>, which the caller holds
/// alongside the session and passes into <see cref="CommitAsync"/> for renewal.
/// </para>
/// </summary>
public sealed class SurrealSession : IHydrationSink
{
    private readonly Dictionary<RecordId, IEntity> entities = [];
    private readonly Dictionary<RecordId, RecordId> parents = [];
    private readonly Dictionary<(RecordId Owner, string Field), RecordId> references = [];
    private readonly Dictionary<(RecordId Source, string Edge, RecordId Target), bool> edges = [];

    // Per-entity load-shape tracking. Each set lists the field names on that entity
    // whose slice has been hydrated (or freshly authored via Track<T>). Generator-emitted
    // property reads consult this map and throw LoadShapeViolationException if a slice
    // they need wasn't loaded — the strict-with-escape contract that PR8's Fetch
    // extends.
    private readonly Dictionary<RecordId, HashSet<string>> loadedSlices = [];

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

    // Filled by the loader's HydrateTrack / HydrateEdge — used by PendingState to
    // distinguish "record (or edge) already in the DB" from "new in this packet". The
    // commit planner needs that bit to decide whether a Delete actually emits a DELETE
    // and whether a Create-then-Delete is a no-op (§3.4 / §8.3 of the spec).
    private readonly HashSet<RecordId> loadedAtStart = [];
    private readonly HashSet<(string Kind, RecordId Source, RecordId Target)> relationsAtStart = [];

    /// <summary>Chronological history of recorded model commands. Diagnostic-only — committed via <see cref="Pending"/> + <see cref="CommitPlanner"/>.</summary>
    public CommandLog Log { get; } = new();

    /// <summary>Indexed write intent. Updated as commands arrive; consumed by <see cref="CommitPlanner.Build"/> at commit time.</summary>
    public PendingState Pending { get; }

    /// <summary>
    /// The model's reference-field registry — used by the commit planner to resolve
    /// <c>[Reject]</c> / <c>[Unset]</c> / <c>[Cascade]</c> behavior against incoming
    /// references. Pass <c>{CompositionRoot}.ReferenceRegistry</c> from your generated
    /// partial; sessions created without one see no reference metadata, which makes the
    /// planner permissive on deletes (acceptable for tests, not real consumers).
    /// </summary>
    public IReferenceRegistry ReferenceRegistry { get; }

    public SurrealSession() : this(NullReferenceRegistry.Instance) { }

    public SurrealSession(IReferenceRegistry referenceRegistry)
    {
        ReferenceRegistry = referenceRegistry;
        Pending = new PendingState(loadedAtStart, relationsAtStart);
    }

    // ──────────────────────────── reads (sync) ───────────────────────────────

    public T GetParent<T>(IEntity owner) where T : class, IEntity
    {
        ThrowIfClosed();
        if (parents.TryGetValue(owner.Id, out var parentId)
            && entities.TryGetValue(parentId, out var parent)
            && parent is T typed)
        {
            return typed;
        }
        throw new InvalidOperationException($"Entity {owner.Id} has no registered parent of type {typeof(T).Name}.");
    }

    public T GetReference<T>(IEntity owner, string fieldName) where T : class, IEntity
        => GetReferenceOrDefault<T>(owner, fieldName)
           ?? throw new InvalidOperationException($"Mandatory reference '{fieldName}' on {owner.Id} is not set.");

    public T? GetReferenceOrDefault<T>(IEntity owner, string fieldName) where T : class, IEntity
    {
        ThrowIfClosed();
        if (references.TryGetValue((owner.Id, fieldName), out var refId)
            && entities.TryGetValue(refId, out var entity)
            && entity is T typed)
        {
            return typed;
        }
        return null;
    }

    /// <summary>
    /// Look up a hydrated entity by id. Returns <c>null</c> when the session doesn't
    /// hold an entity for the id, or when the loaded entity isn't assignable to
    /// <typeparamref name="T"/>. Primary use case: get a typed handle to an aggregate
    /// root (or any other known entity) after <c>Sessions.Load{Root}Async</c>.
    /// </summary>
    public T? Get<T>(IRecordId id) where T : class, IEntity
    {
        ThrowIfClosed();
        var rid = RecordId.From(id);
        return entities.TryGetValue(rid, out var entity) && entity is T typed ? typed : null;
    }

    public IReadOnlyCollection<T> QueryChildren<T>(IEntity owner, string childTable)
        where T : class, IEntity
    {
        ThrowIfClosed();
        var results = new List<T>();
        foreach (var kv in parents)
        {
            if (kv.Value == owner.Id
                && kv.Key.IsForTable(childTable)
                && entities.TryGetValue(kv.Key, out var child)
                && child is T typed)
            {
                results.Add(typed);
            }
        }
        return results;
    }

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
        foreach (var kv in edges)
        {
            if (!kv.Value || kv.Key.Edge != edgeKind || kv.Key.Source != owner.Id)
            {
                continue;
            }
            if (entities.TryGetValue(kv.Key.Target, out var other) && other is T typed)
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
        foreach (var kv in edges)
        {
            if (!kv.Value || kv.Key.Edge != edgeKind || kv.Key.Target != owner.Id)
            {
                continue;
            }
            if (entities.TryGetValue(kv.Key.Source, out var other) && other is T typed)
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
        foreach (var kv in edges)
        {
            if (!kv.Value || kv.Key.Edge != edgeKind)
            {
                continue;
            }

            if (kv.Key.Source == owner.Id)
            {
                results.Add(kv.Key.Target);
            }
        }
        return results;
    }

    /// <summary>Cross-aggregate inverse-side read: edges that land on <paramref name="owner"/>.</summary>
    public IReadOnlyCollection<IRecordId> QueryInverseRelatedIds(IEntity owner, string edgeKind)
    {
        ThrowIfClosed();
        var results = new List<IRecordId>();
        foreach (var kv in edges)
        {
            if (!kv.Value || kv.Key.Edge != edgeKind)
            {
                continue;
            }

            if (kv.Key.Target == owner.Id)
            {
                results.Add(kv.Key.Source);
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
    /// Register a fresh entity with this session. The Create is queued, <c>Bind</c> wires
    /// the session into the instance, <c>Initialize</c> seeds mandatory references, and
    /// <c>Flush</c> drains object-initializer writes that were buffered while the entity
    /// was unbound. Idempotent on the same instance; throws if a different instance with
    /// the same id is already tracked. Hydrated entities are registered separately via
    /// <see cref="IHydrationSink.Track"/> — they don't go through this path. Tracking an
    /// id that was previously loaded-and-then-deleted opens a fresh lifecycle segment;
    /// the planner will emit <c>DELETE; CREATE</c>.
    /// </summary>
    public T Track<T>(T entity) where T : class, IEntity
    {
        ThrowIfClosed();
        if (entities.TryGetValue(entity.Id, out var existing))
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

        entities[entity.Id] = entity;
        entity.Bind(this);
        Record(Command.Create(entity.Id));
        entity.Initialize(this);
        entity.Flush(this);
        // Fresh-entity Track: the user owns the entire state, so every slice is
        // implicitly "loaded" — there's no DB row to compare against. Mark all of them
        // so subsequent reads of [Children] / [Reference] / relations return the local
        // dicts (which may be empty for a brand-new entity, and that's fine).
        entity.MarkAllSlicesLoaded(this);
        return entity;
    }

    /// <summary>True iff <paramref name="id"/> is currently tracked (loaded or freshly minted) in this session.</summary>
    public bool IsTracked(IRecordId id) => entities.ContainsKey(RecordId.From(id));

    /// <summary>
    /// True iff the slice rooted at <c>(<paramref name="owner"/>, <paramref name="fieldName"/>)</c>
    /// has been marked loaded — by the legacy aggregate loader (which marks every slice),
    /// by <see cref="Track{T}"/> (fresh-entity, marks every slice), or by the compiler-
    /// driven traversal path (which marks only Included slices). Generator-emitted
    /// property reads consult this before walking the in-memory cache.
    /// </summary>
    public bool IsSliceLoaded(IRecordId owner, string fieldName)
        => loadedSlices.TryGetValue(RecordId.From(owner), out var set)
           && set.Contains(fieldName);

    /// <summary>
    /// Stub for the strict-with-escape extension query landing in PR8. Throws
    /// <see cref="NotImplementedException"/> for now — the typed
    /// <see cref="LoadShapeViolationException"/> raised by unloaded property reads
    /// points users at this method. PR8 will run <paramref name="query"/> through the
    /// transport, hydrate the response into <c>this</c> session via a partial-merge
    /// sink, and mark the newly-loaded slices.
    /// </summary>
    public Task FetchAsync<T>(Query.Query<T> query, ISurrealTransport transport, CancellationToken ct = default)
        where T : class, IEntity, new()
        => throw new NotImplementedException(
            "SurrealSession.FetchAsync is not implemented yet (PR8). For now, design the load shape up-front via Workspace.Query.{Root}.WithId(id).Include*(...).LoadAsync(...).");

    /// <summary>
    /// Single-field write. <paramref name="value"/> may be any scalar, an
    /// <see cref="IRecordId"/>, or an <see cref="IEntity"/> — entity values are tracked
    /// and substituted with their id before the command is recorded. <paramref name="kind"/>
    /// tells the session which dict (Parents / References / nothing) to update.
    /// </summary>
    public void SetField(IRecordId owner, string field, object? value, FieldKind kind = FieldKind.Property)
    {
        ThrowIfClosed();
        var ownerId = RecordId.From(owner);
        // Entity → cascade-track + use its id; IRecordId → canonicalise; anything else
        // passes through verbatim (scalars, strings, bools, surreal-array snapshots).
        // The cascade is what makes nested object initialisers — `new Design { Details =
        // new Details { … } }` — work without an explicit Track on every fresh ref.
        object? canonical = value;
        if (value is IEntity entityValue)
        {
            Track(entityValue);
            canonical = entityValue.Id;
        }
        else if (value is IRecordId recordValue)
        {
            canonical = RecordId.From(recordValue);
        }

        switch (kind)
        {
            case FieldKind.Parent:
                parents[ownerId] = (RecordId)canonical!;
                break;
            case FieldKind.Reference:
                var refId = (RecordId)canonical!;
                references[(ownerId, field)] = refId;
                Pending.SetReferenceTarget(ownerId, field, refId);
                break;
        }

        Record(Command.Set(ownerId, field, canonical));
    }

    /// <summary>Clears a single field. <paramref name="kind"/> picks the local-state dict to evict from.</summary>
    public void UnsetField(IRecordId owner, string field, FieldKind kind = FieldKind.Property)
    {
        ThrowIfClosed();
        var ownerId = RecordId.From(owner);
        switch (kind)
        {
            case FieldKind.Parent:
                parents.Remove(ownerId);
                break;
            case FieldKind.Reference:
                references.Remove((ownerId, field));
                Pending.UnsetReferenceTarget(ownerId, field);
                break;
        }

        Record(Command.Unset(ownerId, field));
    }

    /// <summary>
    /// Adds an edge of the given <paramref name="edgeKind"/> from <paramref name="source"/>
    /// to <paramref name="target"/>. Endpoints are id-typed so the same call covers
    /// within-aggregate (both endpoints loaded as entities) and cross-aggregate (one or
    /// both endpoints live elsewhere).
    /// </summary>
    public void Relate(IRecordId source, IRecordId target, string edgeKind)
    {
        ThrowIfClosed();
        var src = RecordId.From(source);
        var tgt = RecordId.From(target);
        edges[(src, edgeKind, tgt)] = true;
        Record(Command.Relate(src, edgeKind, tgt));
    }

    /// <summary>Removes a single specific edge. No-op if the edge isn't currently tracked.</summary>
    public void Unrelate(IRecordId source, IRecordId target, string edgeKind)
    {
        ThrowIfClosed();
        var src = RecordId.From(source);
        var tgt = RecordId.From(target);
        edges.Remove((src, edgeKind, tgt));
        Record(Command.Unrelate(src, edgeKind, tgt));
    }

    /// <summary>
    /// Removes every edge of <paramref name="edgeKind"/> originating at <paramref name="source"/>,
    /// loaded or not. Renders as <c>DELETE edgeKind WHERE in = source</c> at commit
    /// time, so persisted edges that weren't hydrated into this session are cleared too.
    /// Drops matching loaded edges from the in-memory snapshot so reads stay accurate.
    /// </summary>
    public void UnrelateAllFrom(IRecordId source, string edgeKind)
    {
        ThrowIfClosed();
        var src = RecordId.From(source);
        foreach (var key in edges.Keys.ToList())
        {
            if (key.Source == src && key.Edge == edgeKind)
            {
                edges.Remove(key);
            }
        }
        Record(Command.UnrelateAllFrom(src, edgeKind));
    }

    /// <summary>
    /// Removes every edge of <paramref name="edgeKind"/> landing on <paramref name="target"/>,
    /// loaded or not. Renders as <c>DELETE edgeKind WHERE out = target</c> at commit time.
    /// </summary>
    public void UnrelateAllTo(IRecordId target, string edgeKind)
    {
        ThrowIfClosed();
        var tgt = RecordId.From(target);
        foreach (var key in edges.Keys.ToList())
        {
            if (key.Target == tgt && key.Edge == edgeKind)
            {
                edges.Remove(key);
            }
        }
        Record(Command.UnrelateAllTo(tgt, edgeKind));
    }

    // Entity-typed convenience overloads — generator-emitted within-aggregate code calls
    // these so it can pass entity references straight through without an explicit .Id
    // accessor. Cross-aggregate emission keeps using the IRecordId surface.
    public void Relate(IEntity source, IEntity target, string edgeKind)         => Relate(source.Id, target.Id, edgeKind);
    public void Unrelate(IEntity source, IEntity target, string edgeKind)       => Unrelate(source.Id, target.Id, edgeKind);
    public void UnrelateAllFrom(IEntity source, string edgeKind)                => UnrelateAllFrom(source.Id, edgeKind);
    public void UnrelateAllTo(IEntity target, string edgeKind)                  => UnrelateAllTo(target.Id, edgeKind);

    // Typed-kind overloads — domain code calls Relate<Restricts>(c, u) and the kind class's
    // static EdgeName feeds the string-keyed core. Compile-time guarantee that the edge
    // name is one the schema knows about; future-friendly for edge-with-payload.
    public void Relate<TKind>(IRecordId source, IRecordId target) where TKind : IRelationKind
        => Relate(source, target, TKind.EdgeName);
    public void Relate<TKind>(IEntity source, IEntity target) where TKind : IRelationKind
        => Relate(source.Id, target.Id, TKind.EdgeName);
    public void Unrelate<TKind>(IRecordId source, IRecordId target) where TKind : IRelationKind
        => Unrelate(source, target, TKind.EdgeName);
    public void Unrelate<TKind>(IEntity source, IEntity target) where TKind : IRelationKind
        => Unrelate(source.Id, target.Id, TKind.EdgeName);
    public void UnrelateAllFrom<TKind>(IRecordId source) where TKind : IRelationKind
        => UnrelateAllFrom(source, TKind.EdgeName);
    public void UnrelateAllFrom<TKind>(IEntity source) where TKind : IRelationKind
        => UnrelateAllFrom(source.Id, TKind.EdgeName);
    public void UnrelateAllTo<TKind>(IRecordId target) where TKind : IRelationKind
        => UnrelateAllTo(target, TKind.EdgeName);
    public void UnrelateAllTo<TKind>(IEntity target) where TKind : IRelationKind
        => UnrelateAllTo(target.Id, TKind.EdgeName);

    public IReadOnlyCollection<TElement> QueryOutgoing<TKind, TElement>(IEntity owner) where TKind : IRelationKind where TElement : class
        => QueryOutgoing<TElement>(owner, TKind.EdgeName);
    public IReadOnlyCollection<TElement> QueryIncoming<TKind, TElement>(IEntity owner) where TKind : IRelationKind where TElement : class
        => QueryIncoming<TElement>(owner, TKind.EdgeName);
    public IReadOnlyCollection<IRecordId> QueryRelatedIds<TKind>(IEntity owner) where TKind : IRelationKind
        => QueryRelatedIds(owner, TKind.EdgeName);
    public IReadOnlyCollection<IRecordId> QueryInverseRelatedIds<TKind>(IEntity owner) where TKind : IRelationKind
        => QueryInverseRelatedIds(owner, TKind.EdgeName);

    /// <summary>
    /// Tombstones an entity. Runs <see cref="IEntity.OnDeleting"/> first so user-side
    /// cleanup (child deletes, reference clears) lands before the entity's own DELETE
    /// command, then queues the DELETE and removes every local-state reference to the
    /// id (entity dict, parent links pointing at it, references whose value is it,
    /// edges touching it on either side).
    /// </summary>
    public void Delete(IEntity entity)
    {
        ThrowIfClosed();
        if (!entities.TryGetValue(entity.Id, out var tracked) || !ReferenceEquals(tracked, entity))
        {
            // Catches: unbound entities, entities from a different session, the same id
            // tracked under a different instance, and double-deletes (CleanupLocalState
            // already removed the entry from `entities`). Without this, OnDeleting fires
            // on an entity whose `Session` may point elsewhere — split-brain at best.
            throw new InvalidOperationException(
                $"Cannot delete {entity.Id}: entity is not currently tracked in this session. " +
                $"Use Delete(IRecordId) for id-only deletes.");
        }
        entity.OnDeleting();
        Record(Command.Delete(entity.Id));
        CleanupLocalState(entity.Id);
    }

    /// <summary>
    /// Id-only delete — for cross-aggregate cleanup or when the caller doesn't have
    /// the entity loaded. No <see cref="IEntity.OnDeleting"/> hook (no entity to dispatch
    /// to); just queues DELETE and clears local-state mentions.
    /// </summary>
    public void Delete(IRecordId id)
    {
        ThrowIfClosed();
        var canonical = RecordId.From(id);
        Record(Command.Delete(canonical));
        CleanupLocalState(canonical);
    }

    // ──────────────────────────── boundary (async) ───────────────────────────

    /// <summary>
    /// Snapshot pending state as a SurrealQL script without clearing state — useful for
    /// diagnostics and tests. Returns an empty script when nothing has been recorded.
    /// </summary>
    public (string Sql, IReadOnlyDictionary<string, object?> Parameters) RenderBatch()
    {
        ThrowIfClosed();
        var plan = CommitPlanner.Build(Pending, ReferenceRegistry);
        return SurrealCommandEmitter.Emit(plan);
    }

    /// <summary>
    /// Flushes pending writes as a single SurrealQL script. The session is closed on
    /// return regardless of outcome — the one-shot invariant is "load → mutate →
    /// commit (or fail), then loop." If <paramref name="lease"/> is supplied, the
    /// lease's CAS clause (BEGIN TRANSACTION / seq-check / UPSERT seq+1) is spliced
    /// around the data writes so the commit either lands atomically with a sequence
    /// advance or — if another writer beat us to it — aborts the whole transaction
    /// with <see cref="WriterLeaseStolenException"/>. The session doesn't own the
    /// lease; the caller manages its lifetime separately.
    /// <para>
    /// Single exception boundary for the whole session: any exception thrown during
    /// commit (RenderBatch or the transport call) marks the session closed and
    /// propagates to the caller. The domain decides whether to reload-and-retry or
    /// give up; the session itself never recovers — there is no half-committed state
    /// to reason about. Nothing else in the runtime catches exceptions; everything
    /// else is allowed to throw freely and lands here.
    /// </para>
    /// </summary>
    public async Task CommitAsync(ISurrealTransport transport, WriterLease? lease = null, CancellationToken ct = default)
    {
        ThrowIfClosed();
        try
        {
            var (sql, parameters) = RenderBatch();
            var hasWork = !string.IsNullOrEmpty(sql) || lease is not null;
            if (hasWork)
            {
                var fullSql = lease is not null
                    ? lease.RenderPreCommitFragment() + sql + WriterLease.PostCommitFragment
                    : sql;

                try
                {
                    await transport.ExecuteAsync(fullSql, parameters, ct);
                }
                catch (Exception ex) when (lease is not null && WriterLease.IsStolen(ex))
                {
                    throw new WriterLeaseStolenException(lease.AggregateName, lease.ExpectedSequence);
                }

                lease?.OnCommitSucceeded();
            }
        }
        catch
        {
            // Fail-closed at the transport boundary. Don't pretend we can recover —
            // the domain catches the rethrown exception and decides what to do.
            closed = true;
            throw;
        }

        Log.Clear();
        Pending.Clear();
        closed = true;
    }

    /// <summary>Drop pending writes without flushing. Closes the session — once abandoned, it's done.</summary>
    public void Abandon()
    {
        Log.Clear();
        Pending.Clear();
        closed = true;
    }


    // ──────────────────────────── loader hooks ──────────────────────────────
    //
    // These bypass the dirty batch — loaded data is "the snapshot" and shouldn't appear
    // as a write at the next commit. Generator-emitted loaders + entity <c>Hydrate</c>
    // methods are the intended callers (and live in the consumer assembly, hence public);
    // ordinary writes go through the Track/SetField/Relate surface.

    // ──────────────────────────── IHydrationSink (explicit-interface) ──────
    //
    // Loader-side write surface. Explicit-interface so domain code with a SurrealSession
    // reference doesn't see Track / Parent / Reference / Edge in IntelliSense — getting
    // at them requires a deliberate `(IHydrationSink)session` cast. The four ops keep
    // the lifecycle invariant: closed sessions reject hydration too, since hydration is
    // load-time and a closed session shouldn't be receiving any more state.

    void IHydrationSink.Track(IEntity entity)
    {
        ThrowIfClosed();
        if (entities.TryGetValue(entity.Id, out var existing))
        {
            if (!ReferenceEquals(existing, entity))
            {
                throw new InvalidOperationException(
                    $"Cannot hydrate {entity.Id}: a different entity instance is already tracked under this id.");
            }
        }
        else
        {
            entities[entity.Id] = entity;
            entity.Bind(this);
        }
        loadedAtStart.Add(entity.Id);
    }

    void IHydrationSink.Parent(RecordId childId, RecordId parentId)
    {
        ThrowIfClosed();
        parents[childId] = parentId;
    }

    void IHydrationSink.Reference(RecordId ownerId, string fieldName, RecordId refId)
    {
        ThrowIfClosed();
        references[(ownerId, fieldName)] = refId;
        Pending.HydrateReference(ownerId, fieldName, refId);
    }

    void IHydrationSink.Edge(RecordId source, string edgeKind, RecordId target)
    {
        ThrowIfClosed();
        edges[(source, edgeKind, target)] = true;
        relationsAtStart.Add((edgeKind, source, target));
    }

    void IHydrationSink.MarkSliceLoaded(RecordId ownerId, string fieldName)
    {
        ThrowIfClosed();
        if (!loadedSlices.TryGetValue(ownerId, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            loadedSlices[ownerId] = set;
        }
        set.Add(fieldName);
    }

    // ──────────────────────────── private helpers ───────────────────────────

    /// <summary>
    /// Single chokepoint for "a model command happened" — appends to the chronological
    /// <see cref="Log"/> AND folds into the indexed <see cref="Pending"/> state. Per
    /// spec §6 these are separate concerns: log preserves history, pending compacts.
    /// </summary>
    private void Record(Command c)
    {
        Log.Append(c);
        Pending.ApplyCommand(c);
    }

    /// <summary>
    /// Wipes every in-memory mention of <paramref name="id"/> — the entity itself, any
    /// parent link declaring this as the parent (children become locally orphaned),
    /// references with this id as their value, and edges with this on either endpoint.
    /// Keeps subsequent reads honest about what's gone.
    /// </summary>
    private void CleanupLocalState(RecordId id)
    {
        entities.Remove(id);
        parents.Remove(id);

        foreach (var k in parents.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList())
        {
            parents.Remove(k);
        }

        // Clear OUTBOUND references — entries where `id` is the owner. Otherwise a
        // recreate-after-delete (Track of a fresh instance with the same id) would see
        // the dead entity's optional refs bleed through GetReferenceOrDefault, since
        // entities[id] now hits the new instance.
        //
        // INBOUND references — entries where `id` is the target — are preserved on
        // purpose. The commit planner reads the inbound graph to resolve
        // [Reject] / [Unset] / [Cascade]; erasing those at session-side made the
        // decisions unreliable.
        foreach (var k in references.Keys.Where(k => k.Owner == id).ToList())
        {
            references.Remove(k);
        }

        foreach (var k in edges.Keys.Where(k => k.Source == id || k.Target == id).ToList())
        {
            edges.Remove(k);
        }
    }
}
