namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// The "Hydrate" terminal: takes a list of record ids and a slice shape, materialises
/// them into a tracked <see cref="SurrealSession"/>. Pairs with the
/// <see cref="Query{T}.IdsAsync"/> selection terminal to support the
/// <c>Load → Hydrate → Mutate → Commit</c> flow:
/// <code>
/// var ids = await workspace.Query.CodeSymbols
///     .Where(CodeSymbolQ.Name.Contains("Parser")).Limit(20).IdsAsync(transport);
///
/// var session = await workspace.Hydrate.CodeSymbols(ids)
///     .WithInclude(myInclude)
///     .ExecuteAsync(transport);
///
/// foreach (var symbol in session.GetAll&lt;CodeSymbol&gt;()) { … }
/// </code>
/// <para>
/// The terminal returns a populated session: the ids identify the root rows; the
/// includes describe which neighbouring slices to pull alongside. The library does
/// not generate the per-table <c>{Hydration}.{Table}(ids)</c> entry point's body
/// directly — that's <c>HydrateRootEmitter</c>'s job — but the runtime constructs
/// reuse <see cref="Query{T}"/>'s compiler + sink machinery, so the wire SQL stays
/// identical to today's read-mode query for the same shape.
/// </para>
/// </summary>
public sealed class HydrationQuery<T>
    where T : class, IEntity, new()
{
    private readonly string table;
    private readonly IReadOnlyList<RecordId> ids;
    private readonly IReadOnlyList<IIncludeNode> includes;
    private readonly IReferenceRegistry referenceRegistry;

    /// <summary>Generator entry point. <paramref name="ids"/> may be empty — terminal then returns an empty session.</summary>
    public HydrationQuery(string table, IReadOnlyList<RecordId> ids, IReferenceRegistry referenceRegistry)
        : this(table, ids, [], referenceRegistry) { }

    private HydrationQuery(
        string table,
        IReadOnlyList<RecordId> ids,
        IReadOnlyList<IIncludeNode> includes,
        IReferenceRegistry referenceRegistry)
    {
        this.table = table;
        this.ids = ids;
        this.includes = includes;
        this.referenceRegistry = referenceRegistry;
    }

    /// <summary>Snake-cased SurrealDB table name this hydration targets.</summary>
    public string Table => table;

    /// <summary>The ids the terminal will materialise.</summary>
    public IReadOnlyList<RecordId> Ids => ids;

    /// <summary>Traversal nodes added via <see cref="WithInclude"/>. Empty when the slice is just the root rows.</summary>
    public IReadOnlyList<IIncludeNode> Includes => includes;

    /// <summary>
    /// Append a traversal node to the slice shape. Mirrors <see cref="Query{T}.WithInclude"/> —
    /// the underlying AST is shared, so any <see cref="IIncludeNode"/> a read-mode query
    /// would accept also slots into the hydration plan.
    /// </summary>
    public HydrationQuery<T> WithInclude(IIncludeNode node)
    {
        var next = new IIncludeNode[includes.Count + 1];
        for (var i = 0; i < includes.Count; i++)
        {
            next[i] = includes[i];
        }
        next[includes.Count] = node;
        return new HydrationQuery<T>(table, ids, next, referenceRegistry);
    }

    /// <summary>
    /// Materialises the requested rows + slices into a fresh
    /// <see cref="SurrealSession"/>. Caller commits via
    /// <see cref="SurrealSession.SaveAsync(IEntity, Disruptor.Surreal.Transaction, CancellationToken)"/>;
    /// concurrent commits surface as <c>SurrealConflictException</c> from the SDK.
    /// </summary>
    public Task<SurrealSession> ExecuteAsync(Disruptor.Surreal.Surreal db, CancellationToken ct = default)
        => ExecuteCoreAsync(new SurrealSession(referenceRegistry), new SurrealSdkTransport(db), ct);

    /// <inheritdoc cref="ExecuteAsync(Disruptor.Surreal.Surreal, CancellationToken)"/>
    public Task<SurrealSession> ExecuteAsync(Disruptor.Surreal.Transaction tx, CancellationToken ct = default)
        => ExecuteCoreAsync(new SurrealSession(referenceRegistry), new SurrealSdkTransport(tx), ct);

    public Task<SurrealSession> ExecuteAsync(ISurrealTransport transport, CancellationToken ct = default)
        => ExecuteCoreAsync(new SurrealSession(referenceRegistry), transport, ct);

    private async Task<SurrealSession> ExecuteCoreAsync(SurrealSession session, ISurrealTransport transport, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            // No ids = no root rows; the empty session is the right answer. Avoid
            // emitting `WHERE id IN []` which Surreal accepts but always returns empty
            // — round-trip with no payload is wasteful.
            return session;
        }

        // Reuse Query<T>'s compiler+sink path: hand it an InPredicate over the id
        // column and the same Includes the user added. Single round-trip, identical
        // wire SQL to today's `Query<T>.Where(IdIn(...)).WithInclude(...).ExecuteAsync`.
        var idValues = new object?[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            idValues[i] = ids[i];
        }

        var query = new Query<T>(table).Where(new InPredicate("id", idValues));
        for (var i = 0; i < includes.Count; i++)
        {
            query = query.WithInclude(includes[i]);
        }

        await query.ExecuteIntoSessionAsync(session, transport, ct);
        return session;
    }
}
