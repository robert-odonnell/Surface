using System.Text;
using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// A single hydrated edge row. Both endpoints are exposed as canonical <see cref="RecordId"/>
/// — the typed id-side union markers (<c>IRestrictedById</c>, etc.) are useful at the
/// query call site for input validation, but downstream code that consumes results is
/// usually pivoting on the canonical id pair.
/// </summary>
public readonly record struct EdgeRow(RecordId Source, RecordId Target);

/// <summary>
/// Read-mode query against a SurrealDB <c>RELATION</c> table (one per forward relation
/// kind). Constructed by the generator's <c>Workspace.Query.Edges.{Kind}</c> partial
/// fragment; chained via <see cref="WhereIn"/>, <see cref="WhereOut"/>, <see cref="Where"/>;
/// terminated by <see cref="ExecuteAsync"/> which returns an <see cref="EdgeRow"/> per row.
/// <para>
/// <typeparamref name="TIn"/> and <typeparamref name="TOut"/> are id-side types — the
/// concrete <c>{Name}Id</c> when a side has a single member, or the generated id-side
/// union marker (<c>IRestrictedById</c>, etc.) when there are 2+. The runtime treats both
/// the same way: every <see cref="IRecordId"/> collapses to <see cref="RecordId"/> at
/// parameter-binding time.
/// </para>
/// </summary>
public sealed class SurfaceEdgeQuery<TIn, TOut>
    where TIn : IRecordId
    where TOut : IRecordId
{
    private readonly string edgeTable;
    private readonly IReadOnlyList<IRecordId>? inFilter;
    private readonly IReadOnlyList<IRecordId>? outFilter;
    private readonly IPredicate? extra;
    private readonly IReadOnlyList<OrderClause> orderClauses;
    private readonly int? limitCount;
    private readonly int? startAt;

    /// <summary>Generator entry point. <paramref name="edgeTable"/> is the snake-cased SurrealDB edge table name.</summary>
    public SurfaceEdgeQuery(string edgeTable)
        : this(edgeTable, inFilter: null, outFilter: null, extra: null, orderClauses: [], limitCount: null, startAt: null) { }

    private SurfaceEdgeQuery(
        string edgeTable,
        IReadOnlyList<IRecordId>? inFilter,
        IReadOnlyList<IRecordId>? outFilter,
        IPredicate? extra,
        IReadOnlyList<OrderClause> orderClauses,
        int? limitCount,
        int? startAt)
    {
        this.edgeTable = edgeTable;
        this.inFilter = inFilter;
        this.outFilter = outFilter;
        this.extra = extra;
        this.orderClauses = orderClauses;
        this.limitCount = limitCount;
        this.startAt = startAt;
    }

    /// <summary>
    /// Restricts the source side (<c>in</c>) to a list of ids — emits
    /// <c>in IN $param</c>. Multiple <see cref="WhereIn"/> calls overwrite, not merge.
    /// </summary>
    public SurfaceEdgeQuery<TIn, TOut> WhereIn(IEnumerable<TIn> ids)
    {
        var snap = SnapshotIds(ids);
        return new(edgeTable, snap, outFilter, extra, orderClauses, limitCount, startAt);
    }

    /// <summary>
    /// Restricts the target side (<c>out</c>) to a list of ids — emits
    /// <c>out IN $param</c>. Multiple <see cref="WhereOut"/> calls overwrite, not merge.
    /// </summary>
    public SurfaceEdgeQuery<TIn, TOut> WhereOut(IEnumerable<TOut> ids)
    {
        var snap = SnapshotIds(ids);
        return new(edgeTable, inFilter, snap, extra, orderClauses, limitCount, startAt);
    }

    /// <summary>
    /// Adds an arbitrary predicate to the WHERE clause (AND-merged with any side
    /// restrictions). Pair with the generator-emitted <c>{ForwardKind}EdgeQ</c> factory
    /// to filter on edge payload fields — e.g.
    /// <c>edges.OutgoingFrom([id]).Where(UsesEdgeQ.Kind.Eq("call"))</c>.
    /// </summary>
    public SurfaceEdgeQuery<TIn, TOut> Where(IPredicate predicate)
    {
        var combined = extra is null ? predicate : Predicate.And(extra, predicate);
        return new(edgeTable, inFilter, outFilter, combined, orderClauses, limitCount, startAt);
    }

    /// <summary>Order edge rows by a payload field. Same shape as <see cref="SurfaceQuery{T}.OrderBy"/>; renders as <c>ORDER BY field ASC|DESC</c>.</summary>
    public SurfaceEdgeQuery<TIn, TOut> OrderBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending)
        => AppendOrder(new OrderClause(property.Field, direction));

    /// <summary>Tie-break on a secondary payload field. Equivalent to chaining another <see cref="OrderBy"/>.</summary>
    public SurfaceEdgeQuery<TIn, TOut> ThenBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending)
        => AppendOrder(new OrderClause(property.Field, direction));

    private SurfaceEdgeQuery<TIn, TOut> AppendOrder(OrderClause clause)
    {
        var next = new OrderClause[orderClauses.Count + 1];
        for (var i = 0; i < orderClauses.Count; i++)
        {
            next[i] = orderClauses[i];
        }
        next[orderClauses.Count] = clause;
        return new(edgeTable, inFilter, outFilter, extra, next, limitCount, startAt);
    }

    /// <summary>Caps the result set at <paramref name="count"/> rows server-side. Multiple calls overwrite.</summary>
    public SurfaceEdgeQuery<TIn, TOut> Limit(int count)
        => new(edgeTable, inFilter, outFilter, extra, orderClauses, count > 0 ? count : null, startAt);

    /// <summary>Skips the first <paramref name="count"/> rows server-side. Pair with <see cref="Limit"/> for paged reads.</summary>
    public SurfaceEdgeQuery<TIn, TOut> Start(int count)
        => new(edgeTable, inFilter, outFilter, extra, orderClauses, limitCount, count > 0 ? count : null);

    // ─── Direction-clarifying aliases ───
    // WhereIn / WhereOut name the SurrealDB column directly, which is concise but easy
    // to misread as "incoming/outgoing." The aliases below name the *role* on each
    // side instead — call sites become unambiguous: "edges originating from X" picks
    // OutgoingFrom; "edges landing on X" picks IncomingTo. Same wire SQL either way.

    /// <summary>Source-side filter — alias for <see cref="WhereIn"/>. Restricts the edge's <c>in</c> column.</summary>
    public SurfaceEdgeQuery<TIn, TOut> WhereSource(IEnumerable<TIn> ids) => WhereIn(ids);

    /// <summary>Target-side filter — alias for <see cref="WhereOut"/>. Restricts the edge's <c>out</c> column.</summary>
    public SurfaceEdgeQuery<TIn, TOut> WhereTarget(IEnumerable<TOut> ids) => WhereOut(ids);

    /// <summary>Edges originating from the given sources — filters <c>in</c>. Reads more naturally than <see cref="WhereIn"/> at the call site for outgoing-edge queries.</summary>
    public SurfaceEdgeQuery<TIn, TOut> OutgoingFrom(IEnumerable<TIn> sources) => WhereIn(sources);

    /// <summary>Edges landing on the given targets — filters <c>out</c>. Reads more naturally than <see cref="WhereOut"/> at the call site for incoming-edge queries.</summary>
    public SurfaceEdgeQuery<TIn, TOut> IncomingTo(IEnumerable<TOut> targets) => WhereOut(targets);

    /// <summary>
    /// Compiles the AST to SurrealQL, executes via <paramref name="transport"/>, and
    /// projects each row to an <see cref="EdgeRow"/>. Returns an empty list on null /
    /// undefined results.
    /// </summary>
    public Task<IReadOnlyList<EdgeRow>> ExecuteAsync(Surreal.SurrealClient db, CancellationToken ct = default)
        => ExecuteAsync(db.QueryAsync, ct);

    /// <inheritdoc cref="ExecuteAsync(Disruptor.Surreal.SurrealClient, CancellationToken)"/>
    public Task<IReadOnlyList<EdgeRow>> ExecuteAsync(Surreal.SurrealTransaction tx, CancellationToken ct = default)
        => ExecuteAsync(tx.QueryAsync, ct);

    private async Task<IReadOnlyList<EdgeRow>> ExecuteAsync(
        Func<string, SurrealObject?, CancellationToken, Task<Surreal.SurrealQueryResponse>> queryFn,
        CancellationToken ct)
    {
        var (sql, bindings) = SurfaceEdgeQueryCompiler.Compile(edgeTable, inFilter, outFilter, extra, orderClauses, limitCount, startAt);
        var response = await queryFn(sql, bindings, ct);
        var rows = response.Count > 0 ? response.Take(0) : null;

        var list = new List<EdgeRow>();
        if (rows is SurrealListValue arr)
        {
            foreach (var row in arr.List)
            {
                if (row is SurrealObjectValue obj)
                {
                    list.Add(ReadEdgeRow(obj));
                }
            }
        }
        else if (rows is SurrealObjectValue single)
        {
            list.Add(ReadEdgeRow(single));
        }
        return list;
    }

    private static EdgeRow ReadEdgeRow(SurrealObjectValue row)
    {
        var src = HydrationValue.ReadRecordId(row.Object["in"]);
        var tgt = HydrationValue.ReadRecordId(row.Object["out"]);
        return new EdgeRow(src, tgt);
    }

    private static IReadOnlyList<IRecordId> SnapshotIds<TId>(IEnumerable<TId> ids)
        where TId : IRecordId
    {
        if (ids is ICollection<TId> col)
        {
            var arr = new IRecordId[col.Count];
            var i = 0;
            foreach (var id in col)
            {
                arr[i++] = id;
            }
            return arr;
        }

        var list = new List<IRecordId>();
        foreach (var id in ids)
        {
            list.Add(id);
        }
        return list;
    }
}

/// <summary>
/// Edge-table flavour of <see cref="SurfaceQueryCompiler"/> — separate because the WHERE shape
/// is built around the fixed <c>in</c> / <c>out</c> columns rather than a free-form
/// predicate AST. Falls through to <see cref="SurfaceQueryCompiler"/>'s normalisation hooks for
/// id collapsing (typed <c>{Name}Id</c> → canonical <see cref="RecordId"/>) so the
/// transport's parameter renderer formats record literals correctly.
/// </summary>
internal static class SurfaceEdgeQueryCompiler
{
    public static (string Sql, SurrealObject Bindings) Compile(
        string edgeTable,
        IReadOnlyList<IRecordId>? inFilter,
        IReadOnlyList<IRecordId>? outFilter,
        IPredicate? extra,
        IReadOnlyList<OrderClause>? orderClauses = null,
        int? limit = null,
        int? start = null)
    {
        var b = new SurfaceQueryCompiler.Builder();
        var sb = new StringBuilder();
        // SurrealDB requires every ORDER BY field to appear in the SELECT projection
        // ("Missing order idiom <field> in statement selection"). The hydration path
        // only reads id/in/out — extra columns ride along but are dropped on the way
        // back, so the wire payload only widens by what the user actually orders on.
        sb.Append("SELECT id, in, out");
        AppendOrderProjection(sb, orderClauses);
        sb.Append(" FROM ").Append(edgeTable.Identifier());

        // Build a small synthetic AST so we can reuse QueryCompiler's predicate visitor for
        // the side filters + the user-supplied extra predicate. Side filters take canonical
        // RecordId already (via the public WhereIn/WhereOut surface accepting IRecordId).
        IPredicate? combined = null;
        if (inFilter is not null)
        {
            combined = AddClause(combined, BuildIn("in", inFilter));
        }
        if (outFilter is not null)
        {
            combined = AddClause(combined, BuildIn("out", outFilter));
        }
        if (extra is not null)
        {
            combined = AddClause(combined, extra);
        }

        if (combined is not null)
        {
            sb.Append(" WHERE ").Append(b.CompilePredicate(combined));
        }

        AppendOrderBy(sb, orderClauses);
        AppendLimit(sb, limit);
        AppendStart(sb, start);

        sb.Append(';');
        return (sb.ToString(), b.Bindings);
    }

    private static void AppendOrderProjection(StringBuilder sb, IReadOnlyList<OrderClause>? clauses)
    {
        if (clauses is null || clauses.Count == 0)
        {
            return;
        }

        // id/in/out are already projected; only widen for distinct payload fields.
        for (var i = 0; i < clauses.Count; i++)
        {
            var field = clauses[i].Field;
            if (IsBaseColumn(field) || ContainsField(clauses, field, i))
            {
                continue;
            }

            sb.Append(", ").Append(field.Identifier());
        }
    }

    private static bool IsBaseColumn(string field)
        => field is "id" or "in" or "out";

    private static bool ContainsField(IReadOnlyList<OrderClause> clauses, string field, int upTo)
    {
        for (var j = 0; j < upTo; j++)
        {
            if (clauses[j].Field == field)
            {
                return true;
            }
        }
        return false;
    }

    private static void AppendOrderBy(StringBuilder sb, IReadOnlyList<OrderClause>? clauses)
    {
        if (clauses is null || clauses.Count == 0)
        {
            return;
        }

        sb.Append(" ORDER BY ");
        for (var i = 0; i < clauses.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var c = clauses[i];
            sb.Append(c.Field.Identifier()).Append(c.Direction == OrderDirection.Descending ? " DESC" : " ASC");
        }
    }

    private static void AppendLimit(StringBuilder sb, int? limit)
    {
        if (limit is { } n && n > 0)
        {
            sb.Append(" LIMIT ").Append(n);
        }
    }

    private static void AppendStart(StringBuilder sb, int? start)
    {
        if (start is { } n && n > 0)
        {
            sb.Append(" START ").Append(n);
        }
    }

    private static IPredicate AddClause(IPredicate? existing, IPredicate next)
        => existing is null ? next : Predicate.And(existing, next);

    private static InPredicate BuildIn(string field, IReadOnlyList<IRecordId> ids)
    {
        var values = new object?[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            values[i] = ids[i];
        }
        return new InPredicate(field, values);
    }
}
