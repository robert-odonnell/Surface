using System.Text.Json;

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

    /// <summary>Generator entry point. <paramref name="table"/> is the snake-cased SurrealDB table name.</summary>
    public Query(string table) : this(table, filter: null, pinnedId: null, includes: Array.Empty<IIncludeNode>()) { }

    private Query(string table, IPredicate? filter, RecordId? pinnedId, IReadOnlyList<IIncludeNode> includes)
    {
        Table = table;
        Filter = filter;
        PinnedId = pinnedId;
        Includes = includes;
    }

    /// <summary>
    /// Adds <paramref name="predicate"/> to the WHERE clause. Multiple <see cref="Where"/>
    /// calls AND-merge — wrap with <see cref="Predicate.Or"/> in the predicate factory if
    /// you want disjunction.
    /// </summary>
    public Query<T> Where(IPredicate predicate)
    {
        var combined = Filter is null ? predicate : Predicate.And(Filter, predicate);
        return new Query<T>(Table, combined, PinnedId, Includes);
    }

    /// <summary>
    /// Pins the query to a single record id. Subsequent <see cref="Where"/> calls still
    /// apply, AND-merged with the id pin.
    /// </summary>
    public Query<T> WithId(IRecordId id)
        => new(Table, Filter, RecordId.From(id), Includes);

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
        return new Query<T>(Table, Filter, PinnedId, next);
    }

    /// <summary>
    /// Compiles the AST to SurrealQL, executes via <paramref name="transport"/>, and
    /// hydrates each row into a fresh entity. Returns the root-level entities; nested
    /// rows from <see cref="WithInclude"/> are tracked alongside in an internal session
    /// reachable via the entities' navigation properties.
    /// </summary>
    public Task<IReadOnlyList<T>> ExecuteAsync(ISurrealTransport transport, CancellationToken ct = default)
        => ExecuteIntoSessionAsync(new SurrealSession(), transport, ct);

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
    public async Task<IReadOnlyList<T>> ExecuteIntoSessionAsync(
        SurrealSession session,
        ISurrealTransport transport,
        CancellationToken ct = default)
    {
        var (sql, bindings) = QueryCompiler.Compile(Table, Filter, PinnedId, Includes);
        using var doc = await transport.ExecuteAsync(sql, bindings, ct);
        var rs = new SurrealResultSet(doc.RootElement);
        var rows = rs.ResultAt(0);

        IHydrationSink sink = session;

        var list = new List<T>();
        switch (rows.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var row in rows.EnumerateArray())
                {
                    list.Add(HydrateOne(row, sink));
                }
                break;
            case JsonValueKind.Object:
                list.Add(HydrateOne(rows, sink));
                break;
            // Null / Undefined / scalar — no rows.
        }

        // Walk each root row's nested arrays and feed them through Hydrate as well — so
        // children / inline-ref records emitted by the compiler land in the same session
        // and reads of [Children] / [Reference] resolve correctly. The compiler's chosen
        // alias names match what the user asked for via WithInclude, so we descend the
        // include AST to know exactly which keys to look up.
        for (var i = 0; i < list.Count; i++)
        {
            var rowJson = rows.ValueKind == JsonValueKind.Array
                ? GetRowAt(rows, i)
                : rows;
            HydrateNested(rowJson, Includes, sink);
        }

        return list;

        static T HydrateOne(JsonElement row, IHydrationSink sink)
        {
            var entity = new T();
            ((IEntity)entity).Hydrate(row, sink);
            return entity;
        }
    }

    private static JsonElement GetRowAt(JsonElement array, int index)
    {
        var i = 0;
        foreach (var row in array.EnumerateArray())
        {
            if (i == index) return row;
            i++;
        }
        throw new InvalidOperationException($"Row index {index} out of range.");
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
    /// <see cref="HydrationJson.HydrateReference{T}"/>; we still mark the slice loaded
    /// so the read path knows the user asked for it.
    /// </summary>
    private static void HydrateNested(JsonElement row, IReadOnlyList<IIncludeNode> nodes, IHydrationSink sink)
    {
        var hasOwnerId = HydrationJson.TryReadRecordId(row, "id", out var ownerId);

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
                    if (children.Hydrator is null) continue; // ad-hoc node; tests skip nested hydration
                    if (!row.TryGetProperty(children.ChildTable, out var arr)) continue;
                    if (arr.ValueKind != JsonValueKind.Array) continue;

                    foreach (var childRow in arr.EnumerateArray())
                    {
                        if (childRow.ValueKind != JsonValueKind.Object) continue;
                        children.Hydrator(childRow, sink);
                        HydrateNested(childRow, children.Nested, sink);
                    }
                    break;
            }
        }
    }
}
