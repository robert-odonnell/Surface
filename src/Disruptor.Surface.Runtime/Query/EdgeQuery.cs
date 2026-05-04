using System.Text;
using System.Text.Json;

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
public sealed class EdgeQuery<TIn, TOut>
    where TIn : IRecordId
    where TOut : IRecordId
{
    private readonly string edgeTable;
    private readonly IReadOnlyList<IRecordId>? inFilter;
    private readonly IReadOnlyList<IRecordId>? outFilter;
    private readonly IPredicate? extra;

    /// <summary>Generator entry point. <paramref name="edgeTable"/> is the snake-cased SurrealDB edge table name.</summary>
    public EdgeQuery(string edgeTable)
        : this(edgeTable, inFilter: null, outFilter: null, extra: null) { }

    private EdgeQuery(
        string edgeTable,
        IReadOnlyList<IRecordId>? inFilter,
        IReadOnlyList<IRecordId>? outFilter,
        IPredicate? extra)
    {
        this.edgeTable = edgeTable;
        this.inFilter = inFilter;
        this.outFilter = outFilter;
        this.extra = extra;
    }

    /// <summary>
    /// Restricts the source side (<c>in</c>) to a list of ids — emits
    /// <c>in IN $param</c>. Multiple <see cref="WhereIn"/> calls overwrite, not merge.
    /// </summary>
    public EdgeQuery<TIn, TOut> WhereIn(IEnumerable<TIn> ids)
    {
        var snap = SnapshotIds<TIn>(ids);
        return new(edgeTable, snap, outFilter, extra);
    }

    /// <summary>
    /// Restricts the target side (<c>out</c>) to a list of ids — emits
    /// <c>out IN $param</c>. Multiple <see cref="WhereOut"/> calls overwrite, not merge.
    /// </summary>
    public EdgeQuery<TIn, TOut> WhereOut(IEnumerable<TOut> ids)
    {
        var snap = SnapshotIds<TOut>(ids);
        return new(edgeTable, inFilter, snap, extra);
    }

    /// <summary>
    /// Adds an arbitrary predicate to the WHERE clause (AND-merged with any side
    /// restrictions). Useful for filtering on edge-carried fields once those become a
    /// thing. Today edge rows have only <c>id</c>/<c>in</c>/<c>out</c>, so <c>Where</c>
    /// is mostly a forward-compat hook.
    /// </summary>
    public EdgeQuery<TIn, TOut> Where(IPredicate predicate)
    {
        var combined = extra is null ? predicate : Predicate.And(extra, predicate);
        return new(edgeTable, inFilter, outFilter, combined);
    }

    /// <summary>
    /// Compiles the AST to SurrealQL, executes via <paramref name="transport"/>, and
    /// projects each row to an <see cref="EdgeRow"/>. Returns an empty list on null /
    /// undefined results.
    /// </summary>
    public async Task<IReadOnlyList<EdgeRow>> ExecuteAsync(ISurrealTransport transport, CancellationToken ct = default)
    {
        var (sql, bindings) = EdgeQueryCompiler.Compile(edgeTable, inFilter, outFilter, extra);
        using var doc = await transport.ExecuteAsync(sql, bindings, ct);
        var rs = new SurrealResultSet(doc.RootElement);
        var rows = rs.ResultAt(0);

        var list = new List<EdgeRow>();
        switch (rows.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var row in rows.EnumerateArray())
                {
                    list.Add(ReadEdgeRow(row));
                }
                break;
            case JsonValueKind.Object:
                list.Add(ReadEdgeRow(rows));
                break;
        }
        return list;
    }

    private static EdgeRow ReadEdgeRow(JsonElement row)
    {
        var src = HydrationJson.ReadRecordId(row.GetProperty("in"));
        var tgt = HydrationJson.ReadRecordId(row.GetProperty("out"));
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
/// Edge-table flavour of <see cref="QueryCompiler"/> — separate because the WHERE shape
/// is built around the fixed <c>in</c> / <c>out</c> columns rather than a free-form
/// predicate AST. Falls through to <see cref="QueryCompiler"/>'s normalisation hooks for
/// id collapsing (typed <c>{Name}Id</c> → canonical <see cref="RecordId"/>) so the
/// transport's parameter renderer formats record literals correctly.
/// </summary>
internal static class EdgeQueryCompiler
{
    public static (string Sql, IReadOnlyDictionary<string, object?>? Bindings) Compile(
        string edgeTable,
        IReadOnlyList<IRecordId>? inFilter,
        IReadOnlyList<IRecordId>? outFilter,
        IPredicate? extra)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT id, in, out FROM ").Append(SurrealFormatter.Identifier(edgeTable));

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

        if (combined is null)
        {
            sb.Append(';');
            return (sb.ToString(), null);
        }

        var (whereClause, bindings) = QueryCompiler.CompilePredicate(combined);
        sb.Append(" WHERE ").Append(whereClause).Append(';');
        return (sb.ToString(), bindings);
    }

    private static IPredicate AddClause(IPredicate? existing, IPredicate next)
        => existing is null ? next : Predicate.And(existing, next);

    private static IPredicate BuildIn(string field, IReadOnlyList<IRecordId> ids)
    {
        var values = new object?[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            values[i] = ids[i];
        }
        return new InPredicate(field, values);
    }
}
