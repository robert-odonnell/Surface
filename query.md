- projections because thats the natural expression of the query 
- tightening up the transports (http and embedded) with embedded mode, i want to leverage the full 
potential of surreal db and not do "database-y" things in the core.

Reclassify them like this:

```text
Core foundation:
- generated typed query shape
- graph/edge query support
- inspectable SurrealQL rendering

Core completion:
- projections
- transport cleanup / embedded execution

Later extras:
- live query ergonomics
- advanced search/index annotations
- edge payload sugar
- query planning hints
```

## Projections are not polish

Projection is the natural end of a query.

A query without projection is basically:

```text
WHERE + ORDER + LIMIT + give me whatever default row shape exists
```

Useful for a demo, but not the full idea.

The proper shape is:

```csharp
var results = await workspace.Search.CodeSymbols
    .Where(CodeSymbolQ.AnyText.Contains(trimmed))
    .OrderBy(CodeSymbolQ.QualifiedName)
    .Limit(20)
    .Select(CodeSymbolProjections.SearchResult)
    .ExecuteAsync(db, ct);
```

And projection should not just be a C# materializer. It should own the **SurrealQL select list**:

```sql id="ep5tf1"
SELECT
    id.id() AS id,
    kind,
    name,
    qualified_name,
    signature,
    file_path,
    line,
    column
FROM code_symbols
...
```

That is the key. Projection is where you stop loading too much and let SurrealDB shape the answer.

So I’d model projection as:

```csharp
public interface ISurfaceProjection<TRow>
{
    QuerySelectList Select { get; }
    TRow Materialize(SurrealRow row);
}
```

Generated projection:

```csharp
public static class CodeSymbolProjections
{
    public static ISurfaceProjection<CodeSymbolSearchResult> SearchResult { get; }
    public static ISurfaceProjection<CodeSymbolNavigationItem> NavigationItem { get; }
    public static ISurfaceProjection<CodeSymbolLocation> Location { get; }
}
```

Later you can add user-defined projections, but generated projections first. Keep the first cut deterministic.

## Transport tightening is also core-aligned

This is the more architectural one.

Our core premise is:

```text
Surface should generate typed SurrealDB intent, not implement database behaviour.
```

So the runtime boundary should not feel like:

```csharp
ISurrealTransport.ExecuteAsync(string sql, object? vars)
```

forever. That was fine as a first seam, but it is too HTTP-shaped and too stringly.

I’d evolve it toward:

```csharp
public interface ISurrealExecutor : IAsyncDisposable
{
    Task<SurrealResultSet> ExecuteAsync(
        SurrealQueryCommand command,
        CancellationToken ct = default);
}
```

Where:

```csharp
public sealed record SurrealQueryCommand(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    SurrealExecutionMode Mode);
```

And implementations are just adapters:

```text
Surface.Transport.Http
Surface.Transport.Embedded
Surface.Transport.JsonRpc
```

The generator/runtime should not care whether the command goes over HTTP, embedded engine, or later WebSocket. It should care about:

```text
SQL
parameters
result shape
materialization
diagnostics
```

## Embedded mode changes the incentive

If embedded mode is viable for your target, then yes: that pushes even harder toward “let SurrealDB do the work.”

Because then SurrealDB is not a remote service you’re trying to avoid calling. It is your local graph/query engine. At that point, doing database-style filtering/traversal in Surface core is actively daft.

Embedded mode wants this kind of design:

```text
Surface generated query
    -> SurrealQL
        -> embedded SurrealDB engine
            -> result set
                -> generated projection materializer
```

Not:

```text
load a pile of rows
    -> hydrate session
        -> walk dictionaries
            -> pretend we queried
```

The second one is just building a worse database out of C# collections. A bold strategy, usually followed by regret and tea.

## Thoughts

This order:

```text
1. Stabilise generated query AST and rendering. (we're getting close with this one)
2. Add projections as the real query endpoint.
3. Refactor transport from HTTP-shaped transport to executor-shaped boundary.
4. Refactor embedded executor. 
5. Then dogfood edge/path queries hard. 
```

We probably should not have done embedded before projections. Without projections, embedded just makes the wrong query shape faster.

We probably should not do projections before the query AST is stable. Otherwise the projection contract is more of a wet-napkin.

We need to target this: 

```csharp
private readonly Dictionary<RecordId, IEntity> entities = [];
private readonly Dictionary<RecordId, RecordId> parents = [];
private readonly Dictionary<(RecordId Owner, string Field), RecordId> references = [];
private readonly Dictionary<(RecordId Source, string Edge, RecordId Target), bool> edges = [];
private readonly Dictionary<RecordId, HashSet<string>> loadedSlices = [];
```

Because we've baked in graph operations and traversal in right at the core... this is necessary for http transport for performance reasons, waste of time for embedded.

So it not really “transport” in the narrow sense. It is **session read strategy**.

The dictionaries are a **materialized graph slice**.

That is useful when:

```text
HTTP transport
known aggregate/session load
sync property reads
edit snapshot
avoid chatty database calls
```

It is not useful when:

```text
embedded engine
projection query
graph traversal
search/list/discovery
database can execute locally
```

For embedded, building and walking `edges` in C# is often just doing SurrealDB’s job badly, but with more furniture.

## The design

Surface should have two read execution modes:

```text
1. Materialized session mode
   - hydrate selected slices into dictionaries
   - sync entity property reads
   - edit support
   - loadedSlices enforces shape safety

2. Database query mode
   - emit SurrealQL
   - execute against HTTP/embedded/RPC
   - return projections/rows
   - no identity map
   - no edge dictionary traversal
```

Same model metadata. Different execution strategy.

## Getters be local

We must **never let generated property getters hit the database**, even embedded.

This should remain true (we already do this):

```csharp
symbol.CalledSymbols
```

means:

```text
read from the loaded session slice
or throw LoadShapeViolationException
```

Not:

```text
surprise async-ish embedded database query hidden inside a getter
```

This keeps our model honest.

If you want graph traversal from the database, use the query surface:

```csharp
await workspace.Query.CodeSymbols
    .For(symbolId)
    .CalledSymbols()
    .SelectDefault()
    .ExecuteAsync(db, ct);
```

If you want entity navigation, explicitly fetch/load the slice:

```csharp
await session.Fetch(symbol, CodeSymbolSlices.CalledSymbols, ct);

foreach (var called in symbol.CalledSymbols)
{
    ...
}
```

For HTTP, `Fetch` hydrates the local dictionaries.

For embedded, you have a choice:

```text
Fetch into dictionaries only when entity navigation is needed.
Query directly when projection/query results are enough.
```

## The abstraction is not `ISurrealTransport`

It's probably:

```csharp
public interface ISurrealExecutor : IAsyncDisposable
{
    ValueTask<SurrealResultSet> ExecuteAsync(
        SurrealCommand command,
        CancellationToken ct = default);

    SurrealExecutionCapabilities Capabilities { get; }
}
```

Possibly with:

```csharp
[Flags]
public enum SurrealExecutionCapabilities
{
    None = 0,
    Remote = 1,
    Embedded = 2,
    SupportsLiveQuery = 4,
    SupportsTransactions = 8,
    CheapGraphTraversal = 16,
}
```

Then query execution can choose sensible paths.

But session property reads should not care. They read session state.

## Mental model

I should stop thinking of those dictionaries as “the graph.”

They are:

```text
SessionGraphCache
MaterializedSliceStore
LoadedGraphSlice
```

So:

```csharp
internal sealed class MaterializedSessionState
{
    private readonly Dictionary<RecordId, IEntity> entities = [];
    private readonly Dictionary<RecordId, RecordId> parents = [];
    private readonly Dictionary<(RecordId Owner, string Field), RecordId> references = [];
    private readonly Dictionary<(RecordId Source, string Edge, RecordId Target), bool> edges = [];
    private readonly Dictionary<RecordId, HashSet<string>> loadedSlices = [];
}
```

That name alone prevents the architectural mistake. It says: “this is a loaded slice, not the database.”

## For HTTP

HTTP session loading should still materialize slices:

```text
Load root
Fetch child slice
Fetch relation slice
Populate entities/parents/references/edges
Mark loadedSlices
Allow sync property reads
```

That is absolutely valid. The network boundary is expensive, and the entity API is synchronous.

## For embedded

Default should be:

```text
Use SurrealQL query execution.
Return projections.
Do not hydrate session dictionaries unless caller explicitly asked for entity/session navigation.
```

Embedded makes this cheap:

```sql
SELECT
    id.id() AS id,
    qualified_name,
    ->calls->code_symbols.{ id, qualified_name } AS called_symbols
FROM code_symbols
WHERE id = type::record("code_symbols", $id);
```

No reason to turn that into:

```text
load relation edges
stuff dictionary
walk dictionary
shape result in C#
```

unless you specifically need a mutable session snapshot.

## The rule

I’d make this an explicit rule in the design:

```text
Queries execute in SurrealDB.
Sessions read loaded slices.
Fetch bridges database results into session slices.
```

That gives you the best of both worlds.

HTTP gets the performance benefit of local slice traversal.

Embedded gets the performance/simplicity benefit of letting SurrealDB traverse the graph.

And Surface avoids becoming a tiny confused database wearing a C# hat.

```text
One query language.
Two terminal intents.

1. Load a mutable session slice.
2. Return immutable projection results.
```

That is a much better mental model than “queries over here, loads over there” as totally separate things.

## The important distinction is the terminal operation

The fluent query defines:

```text
what records?
which graph path?
which edges?
which filters?
which order?
which limit?
which slices?
```

Then the terminal decides what happens:

```csharp
.LoadSliceAsync(...)      // hydrates session state for future mutation
.Select(...).ExecuteAsync(...) // returns projection rows only
```

Example:

```csharp
var query = workspace.Query.CodeSymbols
    .Where(CodeSymbolQ.Repository.Eq(repositoryId))
    .Where(CodeSymbolQ.Name.Contains("Parser"))
    .Include(CodeSymbolSlices.CalledSymbols)
    .Include(CodeSymbolSlices.UsedSymbols);
```

Mutable slice:

```csharp
var session = await query.LoadSliceAsync(transport, ct);

var symbol = session.Get<CodeSymbol>(symbolId)!;
foreach (var called in symbol.CalledSymbols)
{
    // legal only because CalledSymbols slice was loaded
}
```

Projection:

```csharp
var rows = await query
    .Select(CodeSymbolProjections.NavigationItem)
    .ExecuteAsync(transport, ct);
```

Same query shape. Different materialization contract.

## I would make this distinction explicit in the type system

Something like:

```csharp
public interface ISurfaceQuery<TNode>
{
    ISurfaceProjectionQuery<TProjection> Select<TProjection>(
        ISurfaceProjection<TNode, TProjection> projection);

    ISurfaceSliceQuery<TNode> Include(ISurfaceSlice<TNode> slice);

    Task<SurrealSession> LoadSliceAsync(
        ISurrealExecutor executor,
        CancellationToken ct = default);
}
```

But I’d probably keep the public call shape simpler:

```csharp
workspace.Query.CodeSymbols
    .Where(...)
    .Include(CodeSymbolSlices.Calls)
    .LoadSliceAsync(...)
```

versus:

```csharp
workspace.Query.CodeSymbols
    .Where(...)
    .Select(CodeSymbolProjections.SearchResult)
    .ExecuteAsync(...)
```

That is readable, and it makes intent hard to miss.

## Naming matters here

We should stop naming the mutation-oriented terminal like this:

```csharp
.ExecuteAsync()
```

because then people will confuse it with projection execution.

```csharp
.LoadSliceAsync()
.LoadSessionSliceAsync()
.HydrateSliceAsync()
.LoadForMutationAsync()
```

My pick: **`LoadSliceAsync`**.

It matches your `loadedSlices` concept and doesn’t overpromise “aggregate load”.

For projections, keep:

```csharp
.ExecuteAsync()
```

because that is normal query execution.

## A tale of two materializers

It's probably tempting to let projection materialization and session hydration share lots of their implementation. But they have different invariants.

Projection materializer:

```text
- creates immutable row/result objects
- no identity map
- no session
- no mutation
- no loadedSlices
- can over/under-select freely according to projection
```

Slice hydrator:

```text
- creates/binds entities
- fills entities / parents / references / edges
- marks loadedSlices
- prepares future mutation
- must preserve identity-map rules
- must respect reference/delete planning needs
```

Same query AST. Different emitter/materializer.

## The `loadedSlices` map becomes the enforcement layer

This is the right place for the rule:

```csharp
private readonly Dictionary<RecordId, HashSet<string>> loadedSlices = [];
```

Generated getters become strict:

```csharp
public partial IReadOnlyCollection<CodeSymbol> CalledSymbols
{
    get
    {
        Session.RequireSlice(Id, CodeSymbolSlices.CalledSymbols);
        return Session.QueryOutgoing<Calls, CodeSymbol>(this);
    }
}
```

So partial loading is safe:

```text
loaded slice -> readable
unloaded slice -> throws
```

That gives you selective hydration without lying.

## Mutation rules need to be explicit

Partial slice mutation has traps.

I’d define rules like:

```text
Loaded fields/slices may be read.
Any scalar field may be written if the entity is tracked.
Reference changes require the reference field slice to be known when old-value-sensitive planning is needed.
Deletes require either a full delete-safe slice or database-backed delete validation.
```

Especially deletes. Delete planning can become dangerous if the session doesn’t know all incoming references/children/edges. If Surface cannot prove the slice is delete-safe, throw.

Something like:

```csharp
session.Delete(symbol);
```

may require:

```csharp
session.RequireDeleteSafe(symbol);
```

or the generated query must have loaded:

```csharp
.Include(CodeSymbolSlices.DeleteClosure)
```

Otherwise you’ll get “worked in test, ate production” behavior. Not ideal unless your hobby is forensic archaeology.

## Embedded mode fits neatly

For HTTP:

```text
LoadSliceAsync -> SELECT hydration payload -> populate dictionaries -> future sync reads/mutations
```

For embedded:

```text
Projection ExecuteAsync -> direct SurrealDB query -> projection rows
LoadSliceAsync -> only hydrate dictionaries if future entity mutation/navigation is requested
```

So embedded does not remove `LoadSliceAsync`; it just makes projection queries the obvious default for read APIs.

## The final shape I’d aim for

```csharp
// Read-only public API path
var results = await workspace.Query.CodeSymbols
    .Where(CodeSymbolQ.AnyText.Contains(text))
    .OrderBy(CodeSymbolQ.QualifiedName)
    .Limit(20)
    .Select(CodeSymbolProjections.SearchResult)
    .ExecuteAsync(db, ct);
```

```csharp
// Mutation/session path
var session = await workspace.Query.CodeSymbols
    .Where(CodeSymbolQ.Id.Eq(symbolId))
    .Include(CodeSymbolSlices.Properties)
    .Include(CodeSymbolSlices.CalledSymbols)
    .LoadSliceAsync(db, ct);

var symbol = session.Get<CodeSymbol>(symbolId)!;
symbol.Name = "NewName";

await session.CommitAsync(db, lease, ct);
```

That is the right architecture.

The query defines the slice. The terminal defines whether that slice becomes **mutable session state** or **immutable projection output**. That’s clean, powerful, and very aligned with the premise.

## Load*Async is the terminal

And better still if we force that to only produce record ids for later hydration - that is cleaner still.

Make **`Load` a selection terminal**, not a hydration terminal.

```text
Query -> Load IDs
IDs   -> Hydrate session slice
Query -> Select projection
```

That gives you a hard safety boundary:

```text
Load does not create mutable entities.
Load does not populate a session.
Load only identifies records.
```

Then future mutation requires an explicit second step.

## Like this

```csharp
var symbolIds = await workspace.Query.CodeSymbols
    .Where(CodeSymbolQ.Name.Contains("Parser"))
    .OrderBy(CodeSymbolQ.QualifiedName)
    .Limit(50)
    .LoadAsync(transport, ct);
```

Where:

```csharp
Task<IReadOnlyList<CodeSymbolId>>
```

Then hydration is deliberately separate:

```csharp id="1lydy5"
var session = await workspace.Hydrate.CodeSymbols
    .ByIds(symbolIds)
    .Include(CodeSymbolSlices.Properties)
    .Include(CodeSymbolSlices.CalledSymbols)
    .ExecuteAsync(transport, ct);
```

Or:

```csharp
var session = await workspace.LoadSession
    .CodeSymbols(symbolIds)
    .With(CodeSymbolSlices.Properties)
    .With(CodeSymbolSlices.CalledSymbols)
    .ExecuteAsync(transport, ct);
```

I slightly prefer **`Hydrate`** for the second step because it says what actually happens.

## Projection path stays separate

```csharp
var rows = await workspace.Query.CodeSymbols
    .Where(CodeSymbolQ.Name.Contains("Parser"))
    .OrderBy(CodeSymbolQ.QualifiedName)
    .Limit(50)
    .Select(CodeSymbolProjections.SearchResult)
    .ExecuteAsync(transport, ct);
```

So the terminals become:

```text
.LoadAsync(...)              -> IReadOnlyList<TId>
.Select(...).ExecuteAsync()  -> IReadOnlyList<TProjection>
```

No accidental mutation. No ambiguous “query returned entities.” Good.

## For edge subsets

This gets even better for graph queries.

```csharp
var calledIds = await workspace.Query.CodeSymbols
    .For(symbolId)
    .CalledSymbols()
    .LoadAsync(transport, ct);
```

Returns:

```csharp
IReadOnlyList<CodeSymbolId>
```

For actual edge subsets:

```csharp
var callPairs = await workspace.Query.Edges.Calls
    .From(symbolIds)
    .LoadAsync(transport, ct);
```

Returns something like:

```csharp
IReadOnlyList<EdgePair<CodeSymbolId, CodeSymbolId>>
```

or generated:

```csharp
IReadOnlyList<CallsEdgeIdPair>
```

Then hydration remains explicit:

```csharp
var session = await workspace.Hydrate.CodeSymbols
    .ByIds(callPairs.SelectMany(x => [x.SourceId, x.TargetId]))
    .Include(CodeSymbolSlices.Navigation)
    .ExecuteAsync(transport, ct);
```

## This solves the dangerous bit

The dangerous version is:

```csharp
var session = await query.LoadSliceAsync(...);
```

because users will think the query result and mutable session are the same concept.

This version says:

```text
Query finds.
Hydrate loads.
Session mutates.
Projection consumes.
```

Clean. Clear. Correct.

## I’d name the second step carefully

Avoid:

```csharp
Load(...)
```

for both ID selection and session hydration.

Maybe:

```text
Query.LoadAsync          -> IDs
Hydrate.ByIds(...).ExecuteAsync -> session
```

Or if `Load` feels too hydration-flavoured already:

```text
Query.IdsAsync
Query.LoadIdsAsync
Query.FindIdsAsync
```

Load is still good `Load`, just be brutally clear:

```csharp
Task<IReadOnlyList<TId>> LoadAsync(...)
```

The return type does a lot of the talking.

## Do it exactly this way

```text
Load = ID selection only.
Select = projection materialization only.
Hydrate = session/entity materialization only.
```

That gives Surface a clean public story and keeps mutation on a short leash. 

Good architecture.
