using System.Text.Json;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Read-mode query against a single SurrealDB table. Constructed by the generator's
/// <c>Workspace.Query.{Table}</c> partial fragment; chained via <see cref="Where"/> /
/// <see cref="WithId"/>; terminated by <see cref="ExecuteAsync"/> which returns a list of
/// detached entity instances populated through <c>IEntity.Hydrate</c> with a no-op sink.
/// <para>
/// <b>Detached entities</b>: <c>ExecuteAsync</c> never binds the returned entities to a
/// session, so reads of <c>[Property]</c> / <c>[Id]</c> resolve from in-memory backing
/// fields and reads of <c>[Reference]</c> / <c>[Parent]</c> / <c>[Children]</c> /
/// relations throw via the generated <c>Session</c> accessor. Read mode is for projection
/// only — to navigate a graph, load the aggregate (<c>Workspace.LoadXAsync</c>).
/// </para>
/// </summary>
public sealed class Query<T>
    where T : class, IEntity, new()
{
    private readonly string table;
    private readonly IPredicate? filter;
    private readonly RecordId? pinnedId;

    /// <summary>Generator entry point. <paramref name="table"/> is the snake-cased SurrealDB table name.</summary>
    public Query(string table) : this(table, filter: null, pinnedId: null) { }

    private Query(string table, IPredicate? filter, RecordId? pinnedId)
    {
        this.table = table;
        this.filter = filter;
        this.pinnedId = pinnedId;
    }

    /// <summary>
    /// Adds <paramref name="predicate"/> to the WHERE clause. Multiple <see cref="Where"/>
    /// calls AND-merge — wrap with <see cref="Predicate.Or"/> in the predicate factory if
    /// you want disjunction.
    /// </summary>
    public Query<T> Where(IPredicate predicate)
    {
        var combined = filter is null ? predicate : Predicate.And(filter, predicate);
        return new Query<T>(table, combined, pinnedId);
    }

    /// <summary>
    /// Pins the query to a single record id. Subsequent <see cref="Where"/> calls still
    /// apply, AND-merged with the id pin.
    /// </summary>
    public Query<T> WithId(IRecordId id)
        => new(table, filter, RecordId.From(id));

    /// <summary>
    /// Compiles the AST to SurrealQL, executes via <paramref name="transport"/>, and
    /// hydrates each row into a fresh detached entity. Returns an empty list if the
    /// statement returns no rows.
    /// </summary>
    public async Task<IReadOnlyList<T>> ExecuteAsync(ISurrealTransport transport, CancellationToken ct = default)
    {
        var (sql, bindings) = QueryCompiler.Compile(table, filter, pinnedId);
        using var doc = await transport.ExecuteAsync(sql, bindings, ct);
        var rs = new SurrealResultSet(doc.RootElement);
        var rows = rs.ResultAt(0);

        var list = new List<T>();
        switch (rows.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var row in rows.EnumerateArray())
                {
                    list.Add(HydrateOne(row));
                }
                break;
            case JsonValueKind.Object:
                list.Add(HydrateOne(rows));
                break;
            // Null / Undefined / scalar — no rows.
        }
        return list;

        static T HydrateOne(JsonElement row)
        {
            var entity = new T();
            ((IEntity)entity).Hydrate(row, NoOpHydrationSink.Instance);
            return entity;
        }
    }
}

/// <summary>
/// <see cref="IHydrationSink"/> that swallows every call. Lets <c>IEntity.Hydrate</c>
/// run on a freshly-constructed entity to populate its <c>[Property]</c> backing fields
/// without binding it to a session or recording any parent/reference/edge state. Used by
/// <see cref="Query{T}.ExecuteAsync"/> for read-mode result hydration.
/// </summary>
internal sealed class NoOpHydrationSink : IHydrationSink
{
    public static readonly NoOpHydrationSink Instance = new();

    private NoOpHydrationSink() { }

    public void Track(IEntity entity) { }
    public void Parent(RecordId childId, RecordId parentId) { }
    public void Reference(RecordId ownerId, string fieldName, RecordId refId) { }
    public void Edge(RecordId source, string edgeKind, RecordId target) { }
    public bool IsTracked(IRecordId id) => false;
}
