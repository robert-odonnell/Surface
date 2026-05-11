using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Read-mode query against a single SurrealDB table. Constructed by the generator's
/// <c>Workspace.Query.{Table}</c> partial fragment; chained via <see cref="Where"/> /
/// <see cref="WithId"/> / <see cref="WithInclude"/>; terminated by <see cref="ExecuteAsync"/>
/// which returns root-level entities populated through <c>IEntity.Hydrate</c>.
/// <para>
/// <b>Hydration model</b>: entities returned by <see cref="ExecuteAsync"/> are tracked in
/// an internal, never-committed <see cref="SurrealSession"/>. Reads against
/// <c>[Property]</c> / <c>[Id]</c> work directly off the entity's backing fields; reads
/// against <c>[Children]</c> / <c>[Reference]</c> / <c>[Parent]</c> / relations work iff
/// the relevant slice was pulled in via <see cref="WithInclude"/>. Slices that weren't
/// loaded throw at access time — same shape as load mode (PR7) will surface.
/// </para>
/// </summary>
public sealed class Query<T>
    where T : class, IEntity, new()
{
    /// <summary>Snake-cased SurrealDB table name this query targets.</summary>
    public string Table { get; }

    /// <summary>Accumulated WHERE-clause predicate, or <c>null</c> when no filter is set.</summary>
    public IPredicate? Filter { get; }

    /// <summary>Single-record pin set via <see cref="WithId"/>, or <c>null</c> when unpinned.</summary>
    public RecordId? PinnedId { get; }

    /// <summary>Traversal nodes added via <see cref="WithInclude"/>. Empty when the query is flat.</summary>
    public IReadOnlyList<IIncludeNode> Includes { get; }

    /// <summary>Order specs added via <see cref="OrderBy"/> / <see cref="ThenBy"/>. Empty when no ordering is set.</summary>
    public IReadOnlyList<OrderClause> OrderClauses { get; }

    /// <summary>Maximum row count via <see cref="Limit"/>. Renders as SurrealQL <c>LIMIT n</c>; <c>null</c> means no cap.</summary>
    public int? LimitCount { get; }

    /// <summary>Row offset via <see cref="Start"/>. Renders as SurrealQL <c>START n</c>; <c>null</c> means start at zero.</summary>
    public int? StartAt { get; }

    /// <summary>Generator entry point. <paramref name="table"/> is the snake-cased SurrealDB table name.</summary>
    public Query(string table) : this(table, filter: null, pinnedId: null, includes: [], orderClauses: [], limitCount: null, startAt: null) { }

    private Query(
        string table,
        IPredicate? filter,
        RecordId? pinnedId,
        IReadOnlyList<IIncludeNode> includes,
        IReadOnlyList<OrderClause> orderClauses,
        int? limitCount,
        int? startAt)
    {
        Table = table;
        Filter = filter;
        PinnedId = pinnedId;
        Includes = includes;
        OrderClauses = orderClauses;
        LimitCount = limitCount;
        StartAt = startAt;
    }

    /// <summary>
    /// Adds <paramref name="predicate"/> to the WHERE clause. Multiple <see cref="Where"/>
    /// calls AND-merge — wrap with <see cref="Predicate.Or"/> in the predicate factory if
    /// you want disjunction.
    /// </summary>
    public Query<T> Where(IPredicate predicate)
    {
        var combined = Filter is null ? predicate : Predicate.And(Filter, predicate);
        return new Query<T>(Table, combined, PinnedId, Includes, OrderClauses, LimitCount, StartAt);
    }

    /// <summary>
    /// Pins the query to a single record id. Subsequent <see cref="Where"/> calls still
    /// apply, AND-merged with the id pin. Throws <see cref="ArgumentException"/> when the
    /// supplied id's table doesn't match the query's table — without this guard a
    /// <c>Query.Designs.WithId(constraintId)</c> would silently rewrite the lookup to
    /// <c>designs:&lt;constraintId.Value&gt;</c> and either return nothing or hit the
    /// wrong row.
    /// </summary>
    public Query<T> WithId(IRecordId id)
    {
        if (!string.Equals(id.Table, Table, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Id table mismatch: query targets '{Table}' but id was '{id.Table}:{id.ToLiteral()}'. " +
                "Pass an id whose table matches the query's table.",
                nameof(id));
        }
        return new(Table, Filter, RecordId.From(id), Includes, OrderClauses, LimitCount, StartAt);
    }

    /// <summary>
    /// Adds a traversal node to the query. The generated <c>Include*</c> extension
    /// methods on <see cref="Query{T}"/> are the ergonomic surface — this is the
    /// underlying primitive they call into.
    /// </summary>
    public Query<T> WithInclude(IIncludeNode node)
    {
        var next = new IIncludeNode[Includes.Count + 1];
        for (var i = 0; i < Includes.Count; i++)
        {
            next[i] = Includes[i];
        }
        next[Includes.Count] = node;
        return new Query<T>(Table, Filter, PinnedId, next, OrderClauses, LimitCount, StartAt);
    }

    /// <summary>
    /// Adds an ordering specification — the first call seeds the sort, subsequent calls
    /// (or <see cref="ThenBy"/>) tie-break on the previous one. Renders as SurrealQL
    /// <c>ORDER BY field ASC|DESC, …</c>. The <see cref="PropertyExpr{TValue}"/> arg
    /// matches the same factory used for predicates, so generator-emitted
    /// <c>{Table}Q.Field</c> accessors flow through here without ceremony.
    /// </summary>
    public Query<T> OrderBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending)
        => Append(new OrderClause(property.Field, direction));

    /// <summary>Tie-break on a secondary field. Equivalent to chaining another <see cref="OrderBy"/>; named for readability at the call site.</summary>
    public Query<T> ThenBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending)
        => Append(new OrderClause(property.Field, direction));

    private Query<T> Append(OrderClause clause)
    {
        var next = new OrderClause[OrderClauses.Count + 1];
        for (var i = 0; i < OrderClauses.Count; i++)
        {
            next[i] = OrderClauses[i];
        }
        next[OrderClauses.Count] = clause;
        return new Query<T>(Table, Filter, PinnedId, Includes, next, LimitCount, StartAt);
    }

    /// <summary>
    /// Caps the result set at <paramref name="count"/> rows server-side — renders as
    /// SurrealQL <c>LIMIT n</c>. Multiple calls overwrite (last wins). Pass a
    /// non-positive value to remove the cap.
    /// </summary>
    public Query<T> Limit(int count)
        => new(Table, Filter, PinnedId, Includes, OrderClauses, count > 0 ? count : null, StartAt);

    /// <summary>
    /// Skips the first <paramref name="count"/> rows server-side — renders as SurrealQL
    /// <c>START n</c>. Pair with <see cref="Limit"/> for paged reads. Multiple calls
    /// overwrite. Pass zero to remove the offset.
    /// </summary>
    public Query<T> Start(int count)
        => new(Table, Filter, PinnedId, Includes, OrderClauses, LimitCount, count > 0 ? count : null);

    /// <summary>
    /// Compiles the AST to SurrealQL, executes via <paramref name="transport"/>, and
    /// hydrates each row into a fresh entity. Returns the root-level entities; nested
    /// rows from <see cref="WithInclude"/> are tracked alongside in an internal session
    /// reachable via the entities' navigation properties.
    /// </summary>
    public Task<IReadOnlyList<T>> ExecuteAsync(Disruptor.Surreal.SurrealClient db, CancellationToken ct = default)
        => ExecuteIntoSessionAsync(new SurrealSession(), db, ct);

    /// <inheritdoc cref="ExecuteAsync(Disruptor.Surreal.SurrealClient, CancellationToken)"/>
    public Task<IReadOnlyList<T>> ExecuteAsync(Disruptor.Surreal.SurrealTransaction tx, CancellationToken ct = default)
        => ExecuteIntoSessionAsync(new SurrealSession(), tx, ct);

    /// <summary>
    /// Bind a typed projection to this query. The returned <see cref="ProjectionQuery{T, TRow}"/>
    /// preserves the chain (Where / OrderBy / Limit / Start / WithId still work) but its
    /// terminal <c>ExecuteAsync</c> compiles to <c>SELECT field1, field2 FROM …</c> via
    /// <see cref="QueryCompiler.CompileProjection"/> and materialises each row through the
    /// projection's lambda — no entity hydration, no session.
    /// <para>
    /// Projections are flat by definition; chaining <see cref="WithInclude"/> after
    /// <c>.Select(...)</c> is rejected at compile time (the projection chain doesn't expose
    /// the include surface).
    /// </para>
    /// </summary>
    public ProjectionQuery<T, TRow> Select<TRow>(ISurfaceProjection<TRow> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (Includes.Count > 0)
        {
            throw new InvalidOperationException(
                "Cannot bind a projection to a query with Include* calls. Projections are flat; use ExecuteAsync if you need traversal, or drop the includes before .Select(...).");
        }
        return new ProjectionQuery<T, TRow>(this, projection);
    }

    /// <summary>
    /// Compile this query to SurrealQL + typed-CBOR bindings without executing. Used by
    /// <see cref="SurrealSession.FetchAsync{T}"/> and any other caller that wants to
    /// inspect or splice the rendered query before sending.
    /// </summary>
    public (string Sql, global::Disruptor.Surreal.Values.SurrealObject Bindings) Compile()
        => QueryCompiler.Compile(Table, Filter, PinnedId, Includes, OrderClauses, LimitCount, StartAt);

    /// <summary>
    /// Compile this query as an id-only selection: <c>SELECT id FROM table …</c>. The
    /// runtime entry point for the generator-emitted <c>{Table}QueryIds.IdsAsync</c>
    /// extension; exposed here so the consumer-side generator output can render the SQL
    /// without taking a dependency on the internal <see cref="QueryCompiler"/>. Throws
    /// when <see cref="Includes"/> are present — id-only selection is flat by definition.
    /// </summary>
    public (string Sql, global::Disruptor.Surreal.Values.SurrealObject Bindings) CompileIdsOnly()
    {
        if (Includes.Count > 0)
        {
            throw new InvalidOperationException(
                "CompileIdsOnly does not support Include* calls. Drop the includes, or use Compile if you need traversal.");
        }
        return QueryCompiler.CompileIdsOnly(Table, Filter, PinnedId, OrderClauses, LimitCount, StartAt);
    }

    /// <summary>
    /// Compile, execute, and hydrate the query against a caller-supplied
    /// <see cref="SurrealSession"/>. The session receives every traversed slice
    /// (root rows, inline-ref expansions, nested children) through
    /// <see cref="IHydrationSink"/>. Returns the root-level entities; the rest of the
    /// graph is reachable through them.
    /// <para>
    /// LoadAsync (write-mode) calls this with a session newed up against the model's
    /// <see cref="IReferenceRegistry"/>; <see cref="ExecuteAsync"/> (read-mode) calls it
    /// with a throw-away session. Direct callers can hydrate multiple queries into one
    /// session — useful for batched read-then-mutate flows — though the typical path is
    /// the generated <c>LoadAsync</c> extension.
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<T>> ExecuteIntoSessionAsync(
        SurrealSession session,
        Disruptor.Surreal.SurrealClient db,
        CancellationToken ct = default)
        => ExecuteIntoSessionAsync(session, (sql, bindings, c) => db.QueryAsync(sql, bindings, c), ct);

    /// <inheritdoc cref="ExecuteIntoSessionAsync(SurrealSession, Disruptor.Surreal.SurrealClient, CancellationToken)"/>
    public Task<IReadOnlyList<T>> ExecuteIntoSessionAsync(
        SurrealSession session,
        Disruptor.Surreal.SurrealTransaction tx,
        CancellationToken ct = default)
        => ExecuteIntoSessionAsync(session, (sql, bindings, c) => tx.QueryAsync(sql, bindings, c), ct);

    private async Task<IReadOnlyList<T>> ExecuteIntoSessionAsync(
        SurrealSession session,
        Func<string, global::Disruptor.Surreal.Values.SurrealObject?, CancellationToken, Task<Disruptor.Surreal.SurrealQueryResponse>> queryFn,
        CancellationToken ct)
    {
        var (sql, bindings) = QueryCompiler.Compile(Table, Filter, PinnedId, Includes, OrderClauses, LimitCount, StartAt);
        var response = await queryFn(sql, bindings, ct);
        var rows = ExtractRows(response);

        IHydrationSink sink = session;

        var list = new List<T>();
        if (rows is SurrealListValue arr)
        {
            foreach (var row in arr.List)
            {
                if (row is SurrealObjectValue obj) list.Add(HydrateOne(obj, sink));
            }
        }
        else if (rows is SurrealObjectValue single)
        {
            list.Add(HydrateOne(single, sink));
        }

        // Walk each root row's nested arrays and feed them through Hydrate as well —
        // children / inline-ref records emitted by the compiler land in the same session
        // and reads of [Children] / [Reference] resolve correctly.
        for (var i = 0; i < list.Count; i++)
        {
            var rowVal = rows is SurrealListValue rowArr ? rowArr.List[i] : rows!;
            if (rowVal is SurrealObjectValue rowObj) HydrateNested(rowObj, Includes, sink);
        }

        return list;

        static T HydrateOne(SurrealObjectValue row, IHydrationSink sink)
        {
            var entity = new T();
            entity.Hydrate(row, sink);
            return entity;
        }
    }

    /// <summary>Pull the rows portion out of a SurrealQueryResponse — the first statement's result.</summary>
    internal static SurrealValue? ExtractRows(Disruptor.Surreal.SurrealQueryResponse response)
    {
        if (response.Count == 0) return null;
        return response.Statements[0].Result;
    }

    /// <summary>
    /// Recursively hydrate the included slices on a single row and mark each visited
    /// slice as loaded on the row's owner.
    /// <see cref="IncludeChildrenNode"/> expands to a JSON array under the child-table
    /// alias; each element gets a fresh entity instance via the node's own
    /// <see cref="IncludeChildrenNode.Hydrator"/> callback (generator-emitted at codegen
    /// time, captures the right concrete <c>new T()</c> + <c>Hydrate</c>).
    /// <see cref="IncludeInlineRefNode"/> is already projected into the row by
    /// <c>field.*</c> and is picked up by the owning entity's own <c>Hydrate</c> via
    /// <see cref="HydrationValue.HydrateReference{T}"/>; we still mark the slice loaded
    /// so the read path knows the user asked for it.
    /// </summary>
    private static void HydrateNested(SurrealObjectValue row, IReadOnlyList<IIncludeNode> nodes, IHydrationSink sink)
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
                    if (children.Hydrator is null) continue;
                    if (!row.Object.TryGetValue(children.ChildTable, out var arrVal)) continue;
                    if (arrVal is not SurrealListValue arr) continue;

                    foreach (var childRow in arr.List)
                    {
                        if (childRow is not SurrealObjectValue childObj) continue;
                        children.Hydrator(childRow, sink);
                        HydrateNested(childObj, children.Nested, sink);
                    }
                    break;

                case IncludeRelationNode relation:
                    if (hasOwnerId)
                    {
                        sink.MarkSliceLoaded(ownerId, relation.ParentSliceKey);
                    }
                    if (!row.Object.TryGetValue(relation.ParentSliceKey, out var relVal)) continue;
                    if (relVal is not SurrealListValue relArr) continue;

                    if (relation.IdsOnly)
                    {
                        // Cross-aggregate: each item is an edge row { id, in, out }. Feed
                        // the session's edges dict directly — no entity hydration.
                        foreach (var edgeRow in relArr.List)
                        {
                            if (edgeRow is not SurrealObjectValue edgeObj) continue;
                            if (!HydrationValue.TryReadRecordId(edgeObj, "in", out var src)) continue;
                            if (!HydrationValue.TryReadRecordId(edgeObj, "out", out var dst)) continue;
                            sink.Edge(src, relation.EdgeName, dst);
                        }
                    }
                    else
                    {
                        // Within-aggregate: each item is a target row. Hydrate the entity
                        // and synthesize the edge from (parentRowId, edgeName, targetId).
                        foreach (var targetRow in relArr.List)
                        {
                            if (targetRow is not SurrealObjectValue targetObj) continue;
                            relation.Hydrator?.Invoke(targetRow, sink);
                            if (!hasOwnerId) continue;
                            if (!HydrationValue.TryReadRecordId(targetObj, "id", out var targetId)) continue;
                            var src = relation.IsOutgoing ? ownerId : targetId;
                            var dst = relation.IsOutgoing ? targetId : ownerId;
                            sink.Edge(src, relation.EdgeName, dst);
                            HydrateNested(targetObj, relation.Nested, sink);
                        }
                    }
                    break;
            }
        }
    }
}
