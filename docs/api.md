# API Reference

This is a user-facing API map for the generated surface and the runtime types most consumers touch.

## Packages And Namespaces

- `Disruptor.Surface.Runtime`: runtime core — `SurrealSession`, `IEntity`, `RecordId`, `WriterLease`, `CommitPlanner`, `HydrationJson`, `ISurrealTransport`, `SurrealException`, … No transport implementation lives here; pick one of the sibling packages or write your own against `ISurrealTransport`.
- `Disruptor.Surface.Transport.Http`: over-the-network transport — `SurrealHttpClient`, `SurrealConfig`. Talks to a remote SurrealDB via `/rpc` + JSON-RPC. The default for multi-host / multi-process deployments.
- `Disruptor.Surface.Transport.Embedded`: in-process transport — `SurrealEmbeddedTransport`. Backed by SurrealDB embedded with a RocksDB file store; side-steps the HTTP body-size ceiling that bites on large commits.
- `Disruptor.Surface.Generator`: Roslyn source generator. Reference as a private analyzer dependency.
- `Disruptor.Surface.Annotations`: namespace for modeling attributes (lives inside the runtime package).

## Modeling Attributes

| Attribute | Target | Purpose |
| --- | --- | --- |
| `[Table]` | partial class | Includes a class in the generated model. |
| `[AggregateRoot]` | `[Table]` class | Marks the root of a loadable aggregate. Members are discovered through `[Children]`. |
| `[CompositionRoot]` | partial class | Receives generated `Load{Root}Async`, `Schema`, `ApplySchemaAsync`, and `ReferenceRegistry` members. Exactly one is allowed per compilation. |
| `[Id]` | partial property | Optional public typed-id accessor. At most one per table. If omitted, the generator still emits an internal id anchor. |
| `[Property]` | partial property | Persisted scalar field or `SurrealArray<T>` inline array field. |
| `[Reference]` | partial property | Record reference. Non-nullable get-only references are mandatory; nullable settable references are optional. |
| `[Inline]` | with `[Reference]` | Hydrates the referenced record inline with its owner. Without `[Inline]`, only the referenced id is hydrated. |
| `[Parent]` | partial property | Parent link for aggregate hierarchy. |
| `[Children]` | partial get-only collection | Reverse lookup over child records. |
| `[Reject]` | with `[Reference]` | Blocks deletion of the target while this reference points at it. Default behavior. |
| `[Unset]` | with nullable `[Reference]` | Clears this reference when the target is deleted. |
| `[Cascade]` | with `[Reference]` | Deletes the referencing record when the target is deleted. |
| `[Ignore]` | with `[Reference]` | Leaves the reference unchanged when the target is deleted. |
| `ForwardRelation` | base class | Base for user-defined forward relation attributes (no edge payload). |
| `ForwardRelation<TPayload>` | base class | Forward relation attribute with a typed edge payload. The generator emits a `DEFINE FIELD` on the relation table for each public scalar property of `TPayload`. |
| `InverseRelation<TForward>` | base class | Base for user-defined inverse relation attributes. |

## Generated Entity API

For each `[Table]` class, the generator emits a partial implementation that:

- Implements `IEntity`.
- Adds backing fields for generated properties.
- Binds each entity instance to a `SurrealSession` when tracked or hydrated.
- Routes property writes into `SurrealSession.SetField(...)`.
- Routes reference clears into `SurrealSession.UnsetField(...)`.
- Routes child and relation reads into session queries.

Generated property behavior:

| Declaration | Generated behavior |
| --- | --- |
| `[Id] public partial DesignId Id { get; set; }` | Lazy typed id. Setter is allowed only before the entity is bound to a session. |
| `[Property] public partial string Title { get; set; }` | Synchronous getter/setter. Setter records a pending field write. |
| `[Property] public partial SurrealArray<T> Items { get; }` | Lazy mutation-aware list wrapper. Mutations record a pending field write. |
| `[Reference] public partial T Ref { get; }` | Mandatory reference. Getter throws if the referenced entity is not available in the session. |
| `[Reference] public partial T? Ref { get; set; }` | Optional reference. Setting `null` clears the field. |
| `[Reference, Inline] public partial T? Ref { get; set; }` | Optional owned-sidecar reference that hydrates with the owner. |
| `[Parent] public partial Parent Parent { get; set; }` | Parent link. Setter records a parent field write. |
| `[Children] public partial IReadOnlyCollection<Child> Children { get; }` | Query over in-session children by parent link. |
| `[ForwardKind] public partial IReadOnlyCollection<T> Targets { get; }` | Forward edge read. Uses `QueryOutgoing` for same-aggregate edges or id reads for cross-aggregate edges. |
| `[InverseKind] public partial IReadOnlyCollection<T> Sources { get; }` | Inverse edge read. Uses `QueryIncoming` for same-aggregate edges or id reads for cross-aggregate edges. |

Each generated entity also has a protected `Session` property. Use it from your own partial members to write small domain verbs:

```csharp
public void Restricts(UserStory story) => Session.Relate<Restricts>(this, story);
```

Optional hooks:

```csharp
partial void OnCreateDetails(Details details)
{
    details.Summary = "Created with owner";
}

partial void OnDeleting()
{
    foreach (var child in Constraints)
    {
        Session.Delete(child);
    }
}
```

`OnCreate{Name}` is emitted for mandatory get-only references. `OnDeleting` runs before the entity's own delete command is queued.

## Generated Id API

Each table gets a typed id:

```csharp
public readonly record struct DesignId(string Value) : IRecordId
{
    public string Value { get; } = RecordIdFormat.Validate(Value);

    public string Table => "designs";
    public string ToLiteral() => Value;
    public static DesignId New() => new(Ulid.NewUlid().ToString());
    public override string ToString() => Table + ":" + Value;
    public static implicit operator RecordId(DesignId id) => new(id.Table, id.Value);
}
```

`Value` is a `string` validated at construction by `RecordIdFormat.Validate`. Two and only two forms are accepted; anything else throws `FormatException`:

- **Ulid stringification** — exactly 26 characters of `[A-Z0-9]` (Crockford Base32). What `New()` mints. The default for fresh records.
- **Short lower_snake_case slug** — starts with `[a-z]`, followed by `[a-z0-9_]*`, max 32 characters. Opt-in for stable-named records (singletons, config rows, well-known references). Short on purpose: if you're reaching for a 30-char slug, you probably want a Ulid.

```csharp
var fresh   = DesignId.New();                        // 26-char Ulid
var primary = new DesignId("primary");               // slug, OK
var bad     = new DesignId("Some Mixed Case");       // throws FormatException
```

There is no assembly-level override — Ulid is the only mint type, and quoted-string ids are explicitly unsupported.

## Generated Composition Root API

For a `[CompositionRoot]` named `Workspace`, the generator emits:

```csharp
public static IReadOnlyList<string> Schema { get; }

public static Task ApplySchemaAsync(
    ISurrealTransport transport,
    CancellationToken ct = default);

public static IReferenceRegistry ReferenceRegistry { get; }

public static GeneratedQueryRoot Query { get; }

public Task<SurrealSession> LoadDesignAsync(
    ISurrealTransport transport,
    DesignId rootId,
    CancellationToken ct = default);
```

There is one `Load{Root}Async` method per `[AggregateRoot]`. The legacy load path remains alongside the unified `Workspace.Query.{Root}.WithId(...).LoadAsync(...)` shape — both produce the same hydrated session for a no-`Include*` call.

#### Schema migrations are out of scope

`ApplySchemaAsync` is forward-only and additive: every chunk uses `DEFINE … IF NOT EXISTS`, so re-applying is safe and idempotent for adds, but the library does **not** emit `REMOVE FIELD`, `REMOVE TABLE`, or any kind of diff between the model and the live database. Renaming a field, narrowing a type, dropping a table, and similar destructive shape changes are out of scope — too many context-dependent decisions (data preservation, online-vs-offline, downstream consumers) to wrap in a one-size helper without burning the caller. If you need migration semantics, run your own DDL alongside `ApplySchemaAsync`: iterate `Workspace.Schema` directly, splice your `REMOVE`/`UPDATE` statements where appropriate, and version the migration steps externally.

## Query API

Two terminal verbs share one query AST:

- `ExecuteAsync(transport, ct)` — read mode. Returns hydrated entities; the supporting session is internal and never exposed.
- `LoadAsync(transport, lease, ct)` — write mode. Returns a `SurrealSession` you mutate and commit. Only emitted on `Query<TRoot>` where `TRoot.IsAggregateRoot`.

### `Workspace.Query`

The user's `[CompositionRoot]` partial gains a static `Query` accessor returning `GeneratedQueryRoot`, with one property per `[Table]` and an `Edges` sub-root:

```csharp
Workspace.Query.Designs       // Query<Design>
Workspace.Query.Constraints   // Query<Constraint>
Workspace.Query.UserStories   // Query<UserStory>     (pluralised PascalCase)
Workspace.Query.Edges.Restricts  // EdgeQuery<ConstraintId, IRestrictedById>
```

### Predicate factories — `{Name}Q`

Per `[Table]` the generator emits a sibling static class with one `PropertyExpr<T>` per `[Id]`/`[Property]`:

```csharp
public static class ConstraintQ
{
    public static readonly PropertyExpr<ConstraintId> Id = new("id");
    public static readonly PropertyExpr<string> Description = new("description");
}
```

`PropertyExpr<T>` exposes:

| Method | SurrealQL output |
| --- | --- |
| `Eq(T value)` | `field = $pN` |
| `Lt`/`Le`/`Gt`/`Ge(T value)` | `field {<,<=,>,>=} $pN` |
| `In(params T[] values)` / `In(IEnumerable<T>)` | `field IN $pN` |
| `Contains(string substring)` (extension on `PropertyExpr<string>`) | `string::contains(field, $pN)` |

Compose via `Predicate.And(...)`, `Predicate.Or(...)`, `Predicate.Not(...)`.

### `Query<T>`

```csharp
public sealed class Query<T> where T : class, IEntity, new()
{
    public string Table { get; }
    public IPredicate? Filter { get; }
    public RecordId? PinnedId { get; }
    public IReadOnlyList<IIncludeNode> Includes { get; }
    public IReadOnlyList<OrderClause> OrderClauses { get; }
    public int? LimitCount { get; }
    public int? StartAt { get; }

    public Query<T> Where(IPredicate predicate);
    public Query<T> WithId(IRecordId id);
    public Query<T> WithInclude(IIncludeNode node);

    // Server-side ordering / paging — render as ORDER BY / LIMIT / START at the
    // tail of the SurrealQL. Pair with the generated {Table}Q factory to type-check
    // the field reference at the call site.
    public Query<T> OrderBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending);
    public Query<T> ThenBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending);
    public Query<T> Limit(int count);   // <= 0 clears the cap
    public Query<T> Start(int count);   // <= 0 clears the offset

    public Task<IReadOnlyList<T>> ExecuteAsync(ISurrealTransport transport, CancellationToken ct = default);
    public Task<IReadOnlyList<T>> ExecuteIntoSessionAsync(SurrealSession session, ISurrealTransport transport, CancellationToken ct = default);
    public string Compile();
}
```

`Where` AND-merges across calls. `WithId` overwrites. `WithInclude` is the primitive the generated `Include*` extensions call into. `Compile()` renders the AST to a SurrealQL string with every binding inlined as a literal via `SurrealFormatter` — no separate parameter dictionary.

`Limit` / `Start` / `OrderBy` push search-shape semantics server-side. SurrealQL's clause order is fixed (`WHERE → ORDER BY → LIMIT → START`); the compiler emits in that order regardless of which chain method ran first. Multiple `Limit` / `Start` calls overwrite (last wins); `OrderBy` and `ThenBy` are equivalent — both append a clause — and chain commas in declaration order. Typical search-tool shape:

```csharp
var topMatches = await Workspace.Query.Symbols
    .Where(SymbolQ.Name.Contains(query))
    .OrderBy(SymbolQ.Name)
    .Limit(20)
    .ExecuteAsync(transport);
```

Without these, callers had to pull the full match set and `.Take(N)` / `.OrderBy(...)` in-process — fine for smoke tests, expensive for tool-call latencies on large indexes.

### Traversal — `{Name}TraversalBuilder` and `{Name}QueryIncludes`

For each `[Table]` with traversable members (`[Children]` or `[Reference, Inline]`), the generator emits two siblings:

- A traversal-builder type (`DesignTraversalBuilder`) with `.Where(IPredicate)` plus one `IncludeX(Action<{Child}TraversalBuilder>?)` per member. This is the type passed as the `configure` argument inside a parent's `Include*` lambda.
- A static extensions class (`DesignQueryIncludes`) that exposes the same `Include*` shape as extension methods on `Query<Design>` — the root-level entry point.

```csharp
var rows = await Workspace.Query.Designs
    .WithId(designId)
    .IncludeDetails()                                    // [Reference, Inline] → field.* projection
    .IncludeConstraints(c => c                          // [Children] → nested SELECT
        .Where(ConstraintQ.Description.Contains("security"))
        .IncludeDetails())
    .IncludeEpics(e => e.IncludeFeatures(f =>
        f.Where(FeatureQ.Description.Contains("auth"))))
    .ExecuteAsync(transport);
```

Forward and inverse relation traversals are exposed via `IncludeRelationNode` — see below. Plain (non-`Inline`) `[Reference]` traversals are not exposed; the loader pulls them as id-only and there's no v1 read shape for arbitrary record links beyond the inline form.

### Relation traversal — `IncludeRelationNode`

Per relation property the generator emits one `Include{Name}` method on the entity's traversal builder and a matching extension on `Query<T>`. Three shapes:

| Relation kind | Method shape | SurrealQL emitted |
| --- | --- | --- |
| Within-aggregate, single-target | `IncludeRestrictions(Action<{Target}TraversalBuilder>? configure = null)` — supports a target-side `Where(...)` filter and nested `Include*` calls | `(->edge->target[WHERE filter].*) AS slice` (forward), `(<-edge<-source[WHERE filter].*) AS slice` (inverse) |
| Within-aggregate, multi-target | `IncludeRestrictions()` — no configure lambda; relation includes are leaves when the target is a union | `(->edge->?.*) AS slice` (forward), `(<-edge<-?.*) AS slice` (inverse) |
| Cross-aggregate (any arity) | `IncludeRestrictions()` — no configure lambda; ids only, no entity hydration | `(SELECT id, in, out FROM edge WHERE in = $parent.id) AS slice` (forward), `(SELECT id, in, out FROM edge WHERE out = $parent.id) AS slice` (inverse) |

Hydration:

- **Within-aggregate** — each projected target row is hydrated via a generator-emitted callback. Single-target uses a direct `new {Target}() + Hydrate`; multi-target uses a switch on the row's `id:<table>` prefix to dispatch to the right concrete entity. For each target, an edge is synthesised from `(parentRowId, edgeName, targetId)` (direction-aware) and recorded via `IHydrationSink.Edge`. Subsequent reads of `entity.{RelationProperty}` resolve through `Session.QueryOutgoing` / `Session.QueryIncoming` against the populated edges + entities dicts.
- **Cross-aggregate** — each edge row's `id`/`in`/`out` is unpacked and recorded directly via `IHydrationSink.Edge`. No target entity hydration. Reads of `entity.{RelationProperty}` resolve through `Session.QueryRelatedIds` / `Session.QueryInverseRelatedIds` and return `IReadOnlyCollection<IRecordId>`, matching the entity-side surface.

Multi-target relation includes are leaves by design — the target side is a union of [Table] types with no shared traversal builder, so a `configure` lambda has no single-typed receiver. Future versions may add per-target-table filter routing (`.OnTarget<Feature>(f => f.Where(...))` or similar) if real callsites warrant the extra surface.

### Edge queries — `Workspace.Query.Edges.{Kind}`

Per forward relation kind, `EdgeQuery<TIn, TOut>` returns flat `(Source, Target)` pairs:

```csharp
public sealed class EdgeQuery<TIn, TOut>
    where TIn : IRecordId
    where TOut : IRecordId
{
    // Canonical filters — name the SurrealDB column directly.
    public EdgeQuery<TIn, TOut> WhereIn(IEnumerable<TIn> ids);
    public EdgeQuery<TIn, TOut> WhereOut(IEnumerable<TOut> ids);

    // Direction-clarifying aliases — same wire SQL as WhereIn/WhereOut, named after
    // the role rather than the column. Pick whichever reads better at the call site.
    public EdgeQuery<TIn, TOut> WhereSource(IEnumerable<TIn> ids);     // ≡ WhereIn
    public EdgeQuery<TIn, TOut> WhereTarget(IEnumerable<TOut> ids);    // ≡ WhereOut
    public EdgeQuery<TIn, TOut> OutgoingFrom(IEnumerable<TIn> sources); // edges where in ∈ sources
    public EdgeQuery<TIn, TOut> IncomingTo(IEnumerable<TOut> targets);  // edges where out ∈ targets

    public EdgeQuery<TIn, TOut> Where(IPredicate predicate);

    // Server-side ordering / paging — same shape as Query<T>.
    public EdgeQuery<TIn, TOut> OrderBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending);
    public EdgeQuery<TIn, TOut> ThenBy<TValue>(PropertyExpr<TValue> property, OrderDirection direction = OrderDirection.Ascending);
    public EdgeQuery<TIn, TOut> Limit(int count);
    public EdgeQuery<TIn, TOut> Start(int count);

    public Task<IReadOnlyList<EdgeRow>> ExecuteAsync(ISurrealTransport transport, CancellationToken ct = default);
}

public readonly record struct EdgeRow(RecordId Source, RecordId Target);
```

`TIn` and `TOut` are id-side types — the concrete `{Name}Id` for single-member sides, or the generated id-side union marker (`IRestrictedById`, `IReferencedById`, …) when 2+ tables participate.

The aliases exist because `WhereIn` / `WhereOut` are easy to misread as "incoming/outgoing" when they actually name the SurrealDB columns directly (`in` = source, `out` = target). For code-index-style queries that pivot heavily on edge direction, `OutgoingFrom(symbol)` and `IncomingTo(symbol)` make intent unambiguous at the call site.

### Edge payload predicate factory — `{Kind}EdgeQ`

For each forward relation kind declared via `ForwardRelation<TPayload>`, the generator emits a `{Kind}EdgeQ` static class — same shape as the entity-side `{Table}Q` factory, one `PropertyExpr<T>` per public scalar property of the payload type. Bare `ForwardRelation` (no payload) produces no factory.

```csharp
public sealed class UsesPayload
{
    public string Kind { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public string RunId { get; set; } = "";
}

public sealed class UsesAttribute : ForwardRelation<UsesPayload>;

// Generator emits, alongside `Uses : IRelationKind`:
public static class UsesEdgeQ
{
    public static readonly PropertyExpr<string> Kind     = new("kind");
    public static readonly PropertyExpr<string> FilePath = new("file_path");
    public static readonly PropertyExpr<int>    Line     = new("line");
    public static readonly PropertyExpr<string> RunId    = new("run_id");
}
```

Compose with `EdgeQuery.Where` / `.OrderBy` to push payload-aware filtering and ordering server-side:

```csharp
var calls = await Workspace.Query.Edges.Uses
    .OutgoingFrom([symbol.Id])
    .Where(UsesEdgeQ.Kind.Eq("call"))
    .OrderBy(UsesEdgeQ.Line)
    .Limit(50)
    .ExecuteAsync(transport);
```

Without the factory, edge payload fields stayed effectively write-only — readable from the wire but not a typed first-class member of the query surface.

### `LoadAsync`

For aggregate-root tables, the generator emits:

```csharp
public static Task<SurrealSession> LoadAsync(
    this Query<Design> query,
    ISurrealTransport transport,
    WriterLease lease,
    CancellationToken ct = default);
```

Body:

- Throws `InvalidOperationException` when `WithId` wasn't called — load mode is single-aggregate-rooted.
- With no `Include*` calls, delegates to the legacy `{Root}AggregateLoader.PopulateAsync` for the full-aggregate hydrate.
- With `Include*` calls, runs the compiler-driven nested SELECT and hydrates only the chosen slice. Slices outside the load shape throw `LoadShapeViolationException` on read.

The lease is a write-mode marker — the user holds it and passes the same handle into `session.CommitAsync(transport, lease)`.

### Strict-with-escape: `LoadShapeViolationException` and `Fetch`

A filtered `LoadAsync` only marks the user-Included slices as loaded. Reads against `[Children]`/`[Reference]`/`[Parent]`/relation properties whose slice wasn't loaded throw:

```csharp
public sealed class LoadShapeViolationException : InvalidOperationException
{
    public RecordId Owner { get; }
    public string Field { get; }
    public string FetchHint { get; }
}
```

The exception message points at `session.FetchAsync(...)` — the top-up extension query that hydrates additional slices into the existing session:

```csharp
try
{
    var notes = design.Notes;        // throws — Notes wasn't included
}
catch (LoadShapeViolationException)
{
    await session.FetchAsync(
        Workspace.Query.Designs.WithId(design.Id).IncludeNotes(),
        transport);
    var notes = design.Notes;        // works
}
```

`FetchAsync` runs a partial-merge hydration: existing entities in the session receive `IEntity.HydratePartial` (skips fields the user has already mutated since load — pending writes always win); brand-new entities get the full `IEntity.Hydrate`. Slices listed in the extension query's `Includes` are marked loaded. Closed sessions throw `InvalidOperationException`.

```csharp
public Task FetchAsync<T>(
    Query<T> query,
    ISurrealTransport transport,
    CancellationToken ct = default)
    where T : class, IEntity, new();
```

## Relations

Declare relation attributes:

```csharp
public sealed class RestrictsAttribute : ForwardRelation;
public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;
```

The generator emits a marker class for the forward kind:

```csharp
public sealed class Restricts : IRelationKind
{
    public static string EdgeName => "restricts";
}
```

Runtime calls:

```csharp
session.Relate<Restricts>(source, target);
session.Unrelate<Restricts>(source, target);
session.UnrelateAllFrom<Restricts>(source);
session.UnrelateAllTo<Restricts>(target);

session.QueryOutgoing<Restricts, UserStory>(constraint);
session.QueryIncoming<Restricts, Constraint>(story);
session.QueryRelatedIds<Restricts>(sourceEntity);
session.QueryInverseRelatedIds<Restricts>(targetEntity);
```

### Typed edge payloads — `ForwardRelation<TPayload>`

When the relation itself carries data (confidence scores, run id, source location, …), declare the forward attribute with a payload type. The generator walks the payload's public scalar properties and emits a `DEFINE FIELD` on the relation table for each, mirroring how `[Property]` fields are emitted on entity tables. Same scalar coverage (`SchemaEmitter.MapScalarType`), same `IF NOT EXISTS` idempotency, same `DEFAULT` seeding so untouched fields don't break SCHEMAFULL inserts.

```csharp
public sealed class UsesPayload
{
    public string Kind { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public string RunId { get; set; } = "";
}

public sealed class UsesAttribute : ForwardRelation<UsesPayload>;
public sealed class UsedByAttribute : InverseRelation<UsesAttribute>;
```

Generated schema for the `uses` table:

```sql
DEFINE TABLE IF NOT EXISTS uses SCHEMAFULL
TYPE RELATION
FROM code_symbols
TO code_symbols
ENFORCED;
DEFINE FIELD IF NOT EXISTS kind ON uses TYPE string DEFAULT "";
DEFINE FIELD IF NOT EXISTS file_path ON uses TYPE string DEFAULT "";
DEFINE FIELD IF NOT EXISTS line ON uses TYPE int DEFAULT 0;
DEFINE FIELD IF NOT EXISTS run_id ON uses TYPE string DEFAULT "";
```

The payload class is a plain POCO — no `[Property]` annotations needed; public scalar properties (`string`, `int`/`long`, `bool`, `float`/`double`/`decimal`, `DateTime`/`DateTimeOffset`, `Guid`, `Ulid`) are picked up automatically. Anything else is silently skipped. Static, indexer, write-only, and inherited-already-seen properties are not emitted as fields.

At write time, pass payload data through the dictionary overload of `Relate` / `RelateOnce`:

```csharp
session.RelateOnce<Uses>(source, target, new Dictionary<string, object?>
{
    ["kind"]      = "call",
    ["file_path"] = "src/Foo.cs",
    ["line"]      = 42,
    ["run_id"]    = currentRunId,
});
```

Field names in the dictionary use the snake-cased SurrealDB form (matching what the schema declared), not the C# property name. The bare `ForwardRelation` (no `<TPayload>`) keeps the pre-feature schema unchanged — no extra fields emitted on the relation table.

Within-aggregate relation properties hydrate entity instances. Cross-aggregate relation properties expose `IRecordId` endpoints because the other aggregate is not part of the current snapshot.

## Runtime Types

### `ISurrealTransport`

```csharp
public interface ISurrealTransport : IAsyncDisposable
{
    Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default);
}
```

A SurrealQL string in, a `JsonDocument` out. No vars — bindings are inlined as SurrealQL literals upstream by `QueryCompiler` / `SurrealCommandEmitter` / etc. via `SurrealFormatter`. Implement this for a custom transport (test recorders, alternate connectivity, retry policy); most users use `SurrealHttpClient`.

### `SurrealHttpClient`

Lives in the `Disruptor.Surface.Transport.Http` package (namespace `Disruptor.Surface.Transport.Http`). HTTP transport for SurrealDB. Uses the `/rpc` endpoint with JSON-RPC 2.0 envelopes (`{"method": "query", "params": [<sql>, {}]}`); the `params[1]` slot stays empty because every binding has already been inlined into the SQL. SurrealDB's JSON binder treats record-shaped objects as generic `Object`s rather than `Thing`s, so routing record ids through `params[1]` would silently miss; SurrealQL literal syntax (`table:value`) is parsed at the query level and preserves type. The bypass also lifts the per-query statement-count ceiling that a `LET $pN = …;` prefix used to hit on large commits.

```csharp
await using var transport = new SurrealHttpClient(config, httpClient);

using var doc = await transport.ExecuteAsync("SELECT * FROM designs;");
var resultSet = await transport.SqlAsync("SELECT * FROM designs;");
```

Useful members:

- `Config`: the `SurrealConfig` used by the client.
- `ExecuteAsync(sql, ct)`: transport contract used by generated loaders, commits, and the query layer.
- `SqlAsync(sql, ct)`: returns `SurrealResultSet` for direct SQL use.

#### Reading `SurrealException` messages

`SurrealHttpClient` walks the JSON-RPC response array and throws `SurrealException` on the **first** statement with `status: "ERR"`, propagating that statement's verbatim `result` text. For most failures (field coercion, schema violation, predicate type mismatch) this surfaces the real per-statement error directly in `ex.Message`.

The exception is the transaction-rollup case. When SurrealDB aborts a `BEGIN…COMMIT` script and returns a single envelope like `{"status":"ERR","result":"the query was not executed due to a failed transaction"}` without per-statement detail, the message is generic. Recovery pattern:

```csharp
catch (SurrealException ex) when (ex.Message.Contains("not executed", StringComparison.OrdinalIgnoreCase))
{
    // Re-run the same script statement-by-statement, no BEGIN/COMMIT, against a
    // throwaway transaction or a sandbox namespace. Each statement now reports its
    // own ERR independently — the actual culprit (field type mismatch, missing
    // table, malformed RELATE) appears with its own message. Then fix and retry
    // the original transactional commit.
}
```

This is caller-side; the library doesn't auto-replay because the side effects of partial replay aren't generally safe. `WriterLeaseStolenException` is the one transactional failure the library translates by name — caught and rethrown as a typed exception by `SurrealSession.CommitAsync` so the reload-and-retry loop is unambiguous.

### `SurrealEmbeddedTransport`

In-process `ISurrealTransport` backed by SurrealDB embedded with a RocksDB file store. Lives in the optional `Disruptor.Surface.Transport.Embedded` package — drop-in replacement for `SurrealHttpClient` when consumers want to skip the `/rpc` round-trip. Useful for code-index full rebuilds, bulk imports, single-process workloads where the HTTP body-size ceiling bites.

```csharp
await using var transport = new SurrealEmbeddedTransport(
    filePath: "code-index.db",
    @namespace: "code-index",
    database: "main");

await Workspace.ApplySchemaAsync(transport);
// … rest of the pipeline reads exactly like the HTTP path.
```

Key behaviours:

- **Bundled engine is `surrealdb-core 3.0.5`** (per the `SurrealDb.Embedded.RocksDb` 0.10.x package). The pre-v3 statement-count ceiling that the HTTP path used to hit on large commits doesn't apply.
- **Single-writer in-process** — a `SemaphoreSlim(1, 1)` serialises any script that begins with `BEGIN TRANSACTION` (the WriterLease pre-commit fragment). Reads and non-transactional scripts skip the lock so concurrent reads stay parallel; transactional commits queue, keeping the WriterLease's CAS-on-sequence as the contention point of last resort rather than a permanent hot path.
- **CBOR → JSON projection** — the SDK speaks CBOR; the runtime parsers expect the JSON-RPC envelope shape. `CborJsonProjection` walks the SDK's `SurrealDbResponse` and emits `[{result, status}, …]` with custom converters for record ids (`"table:value"`), datetimes (ISO 8601), decimals (string-quoted), durations, byte arrays. Goes around the SDK's own `RecordIdJsonConverter` which writes just the id portion.
- **Input-side stays inlined** — `SurrealFormatter` already renders every value as a SurrealQL literal (record ids, datetimes, etc.); no `Things` flow through any binding dictionary, so the original `/rpc` Thing-binding problem doesn't recur on the embedded path either.

Two ctors:

```csharp
// Owns a fresh client; disposes it on transport teardown.
new SurrealEmbeddedTransport(filePath, @namespace, database, engineOptions?);

// Wraps a caller-owned ISurrealDbClient (e.g. shared with the SDK's typed read APIs).
// Caller manages the client's lifetime.
new SurrealEmbeddedTransport(existingClient);
```

`Client` exposes the underlying `ISurrealDbClient` so consumer code can layer the SDK's typed APIs alongside the Disruptor.Surface session abstraction.

Caveats:

- **Native binary distribution** — the embedded RocksDB engine ships native libraries via the `SurrealDb.Embedded.RocksDb` package. Consumer deployments need the right `runtimes/<rid>/native/...` payload for their target platform.
- **Cross-process write coordination doesn't apply** — RocksDB is single-process. The WriterLease is still useful for in-process discipline (the in-process semaphore complements it) but the cross-process safety story it provides on the HTTP path doesn't translate.

### `SurrealConfig`

```csharp
var config = SurrealConfig.Default();

var config = SurrealConfig.FromConnectionString(
    "Url=http://127.0.0.1:8000;Namespace=main;Database=main;User=root;Password=root;TimeoutSeconds=30");
```

Recognized keys include `Server`, `Endpoint`, `Url`, `Namespace`, `Ns`, `Database`, `Db`, `Username`, `User`, `Password`, `Pass`, and `TimeoutSeconds`.

### `SurrealSession`

`SurrealSession` is the main runtime unit of work.

Construction:

```csharp
var session = new SurrealSession(Workspace.ReferenceRegistry);
```

Read methods:

| Method | Purpose |
| --- | --- |
| `Get<T>(IRecordId id)` | Lookup a hydrated or tracked entity by id. |
| `GetParent<T>(IEntity owner)` | Resolve a parent link. |
| `GetReference<T>(owner, field)` | Resolve mandatory reference or throw. |
| `GetReferenceOrDefault<T>(owner, field)` | Resolve optional reference or return `null`. |
| `QueryChildren<T>(owner, childTable)` | Read children by parent link. |
| `QueryOutgoing<TKind, T>(owner)` | Read same-aggregate outgoing relation targets. |
| `QueryIncoming<TKind, T>(owner)` | Read same-aggregate incoming relation sources. |
| `QueryRelatedIds<TKind>(owner)` | Read cross-aggregate outgoing ids. |
| `QueryInverseRelatedIds<TKind>(owner)` | Read cross-aggregate incoming ids. |

Write methods:

| Method | Purpose |
| --- | --- |
| `Track(entity)` | Bind and register a fresh entity, queueing a create. |
| `SetField(owner, field, value, kind)` | Low-level field write used by generated setters. |
| `UnsetField(owner, field, kind)` | Low-level field clear used by generated setters. |
| `Relate<TKind>(source, target)` | Queue relation creation. |
| `Relate<TKind>(source, target, payload)` | Queue relation creation with a typed payload — renders as `RELATE source->edge->target CONTENT { … }`. Use for edges that carry data (confidence, run id, resolution method). |
| `RelateOnce<TKind>(source, target)` | Idempotent relation creation. The edge id is derived deterministically from `(source, kind, target)` via SHA-256, so re-issuing the same triple lands on the same edge row instead of stacking duplicates. Renders as `RELATE source -> edge_table:<hash> -> target` — the RELATE-with-explicit-id form so SurrealDB registers the row as a graph edge on `TYPE RELATION ENFORCED` tables. The right verb for re-indexing / re-import workloads. |
| `RelateOnce<TKind>(source, target, payload)` | Idempotent relate with payload — same deterministic id; payload becomes the trailing `CONTENT { … }` clause. `in` / `out` are encoded by the RELATE syntax itself and aren't repeated in the payload. |
| `Unrelate<TKind>(source, target)` | Queue relation deletion. |
| `UnrelateAllFrom<TKind>(source)` | Queue bulk deletion of all outgoing edges of a kind. |
| `UnrelateAllTo<TKind>(target)` | Queue bulk deletion of all incoming edges of a kind. |
| `Delete(entity)` | Queue entity deletion and invoke `OnDeleting`. |
| `Delete(id)` | Queue id-only deletion without `OnDeleting`. |

Boundary methods:

| Method | Purpose |
| --- | --- |
| `RenderBatch()` | Build the current SurrealQL batch without closing the session. Useful for diagnostics. |
| `CommitAsync(transport, lease, ct)` | Build, optionally renew the lease, execute, clear, and close the session. |
| `AbandonAsync(ct)` | Drop pending writes and close the session. |

After `CommitAsync` or `AbandonAsync`, `IsClosed` is `true` and public reads/writes throw.

#### Large commits and batching

A single `CommitAsync` lands the whole pending batch as one SurrealQL script — atomic when a `WriterLease` is supplied, atomic per-statement otherwise. For very large workloads (tens of thousands of records, bulk re-imports, code-index full-rebuilds) you'll eventually hit the practical ceiling: SurrealDB statement limits, HTTP body size, request timeout, or memory pressure on either side.

The library doesn't auto-chunk. The reason is that the right chunking strategy depends on what the data looks like, and **the implementor knows how to slice their data better than the library does**:

- A code-index rebuild can split by *symbol* (each chunk = one source file's worth of nodes + edges) and accept that intermediate states are visible because nothing else is reading mid-rebuild.
- A financial ledger can't split at all — the whole batch must be one transaction.
- A bulk import from a queue can split by *batch id* with a separate marker table tracking which ranges are committed.

The recommended pattern: chunk on the caller side by creating one `SurrealSession` per chunk, committing each in sequence (or in parallel, with separate `WriterLease` instances if isolation matters), and tracking progress externally (a marker record, a log line, a sentinel field on a coordinator entity). The library gives you `session.Pending.Records.Count` and `session.Log.Count` so you can decide when a session has accumulated enough to flush. Don't fight the unit-of-work boundary — if a single logical transaction truly is huge, you have a modelling problem, not a transport problem.

### `WriterLease`

`WriterLease` coordinates cross-process writers through a single `writer_lease:main`
record per workspace, using optimistic concurrency on a monotonic `seq` counter.
**Single-writer paradigm** — one outstanding lease across all aggregates; concurrent
acquirers race for the commit's CAS check. Acquire it via the generated
`Workspace.AcquireWriterAsync(transport)` accessor:

```csharp
await using var lease = await workspace.AcquireWriterAsync(transport);
await session.CommitAsync(transport, lease);
```

Protocol: `AcquireAsync` reads the current `seq` (defaulting to 0 if no row exists) and
captures it on the lease. `CommitAsync` splices a `BEGIN TRANSACTION; IF seq_on_db !=
captured THEN THROW … END; UPSERT seq + 1;` fragment around the data writes — atomic
with the data, throws `WriterLeaseStolenException` if another writer advanced `seq`
first.

No TTL, no holder id, no clock skew, no theft-recovery timer, no aggregate slug. Crashed
writers are forgotten — the next acquirer reads the current `seq` fresh and proceeds.
`DisposeAsync` is a no-op.

Key members:

- `AcquireAsync(transport, ct)` — the underlying primitive. Prefer the generated
  `Workspace.AcquireWriterAsync(transport)` accessor at call sites.
- `ExpectedSequence`: the seq value this lease captured (or last successfully committed).
- `SchemaScript`: DDL for the runtime lease table. Generated schema includes it.

Exception:

- `WriterLeaseStolenException`: another writer's commit advanced `seq` past what this
  lease captured. The caller should abandon the in-flight writes, reload the aggregate,
  and retry from the fresh snapshot.

### `RecordIdFormat`

Single-source-of-truth validator for typed-id `Value` strings. Three forms are accepted:

| Form | Shape | Mint |
| --- | --- | --- |
| **Ulid** | 26 chars uppercase Crockford base32 | `Ulid.NewUlid().ToString()` (default for `RecordId.New`) |
| **Slug** | starts with `[a-z]`, body `[a-z0-9_]`, ≤ 32 chars | hand-written for stable-named records (singletons, config rows) |
| **Content hash** | bare `[0-9a-f]{24}` or prefixed `[a-z]_[0-9a-f]{24}` | `RecordIdFormat.HashText(text, prefix?)` — SHA-256 truncated to 12 bytes |

```csharp
RecordIdFormat.Validate("primary");                           // slug
RecordIdFormat.Validate("01HXY...26chars");                   // ulid
RecordIdFormat.Validate("0123456789abcdef01234567");          // bare hash
RecordIdFormat.Validate("m_0123456789abcdef01234567");        // prefixed hash
RecordIdFormat.Validate("Bad Value");                         // throws FormatException

var hash    = RecordIdFormat.HashText("System.IDisposable");          // bare 24-char hex
var tagged  = RecordIdFormat.HashText("System.IDisposable", 'm');     // "m_<hash>"

RecordIdFormat.IsValid("primary");                             // true
RecordIdFormat.IsValid(null);                                  // false
RecordIdFormat.MaxSlugLength;                                  // 32
RecordIdFormat.HashLength;                                     // 24
RecordIdFormat.PrefixedHashLength;                             // 26
```

The hash form is the deterministic / content-addressed path: same input always yields the same id, so it's the natural pick when the record is keyed by something the caller knows up front (a code symbol's full name, a canonical message, a normalised URL). Birthday-collision-safe up to ~10¹⁴ ids at 96 bits. The optional single-letter prefix is for cheap visual categorisation (e.g. `m_` for module, `t_` for type, `f_` for function); it doesn't change collision behaviour.

The generator routes every `{Name}Id(string Value)` ctor through `Validate`, so user code can't construct an id with a malformed value — the throw happens at construction, not at commit time.

### `RecordId`

Canonical id type:

```csharp
var fresh   = RecordId.New("designs");                                      // ulid value
var pinned  = new RecordId("designs", "primary");                           // slug value
var parsed  = RecordId.Parse("designs:01J...");                             // round-trip parse

// Deterministic content-addressed id — same (table, text) always yields the same id.
var symbol  = RecordId.FromText("symbols", "Disruptor.Surface.Runtime.SurrealSession");
var tagged  = RecordId.FromText("symbols", "ICodeSymbol", prefix: 'i');     // "symbols:i_<hash>"
```

Important members:

- `Table`
- `Value`
- `ToLiteral()`
- `ToString()`
- `From(IRecordId id)` — collapse a typed id (or any `IRecordId`) to a canonical `RecordId`.
- `New(table, value?)` — fresh ulid-backed id.
- `FromText(table, text, prefix?)` — deterministic content-addressed id; convenience over `RecordIdFormat.HashText`.
- `TryParse(...)`
- `IsForTable(table)`

### `SurrealArray<T>`

Use as a `[Property]` for inline ordered arrays:

```csharp
public sealed record Scenario(string Kind, string Description);

[Property]
public partial SurrealArray<Scenario> Scenarios { get; }
```

It implements `IList<T>` and `IReadOnlyList<T>`. Mutations such as `Add`, `Remove`, `Clear`, index assignment, and `Move` notify the owning entity so the field is included in the next commit.

### Result Helpers

For direct SQL calls:

```csharp
var result = await transport.SqlAsync("SELECT * FROM designs;");
var rows = result.DecodeList<MyDto>();
var first = result.DecodeFirst<MyDto>();
var count = result.Count();
var raw = result.ResultAt();
```

Related types:

- `SurrealResultSet`
- `SurrealResultReader`
- `SurrealStatementResponse`
- `SurrealQueryException`
- `SurrealProtocolException`

### Commit Planning And Formatting

Most consumers do not call these directly, but they are public for diagnostics and tests:

- `PendingState`: compact indexed write-intent state.
- `CommandLog`: chronological command history.
- `CommitPlanner.Build(pending, registry)`: produces ordered commands and enforces reference delete behavior.
- `SurrealCommandEmitter.Emit(commands)`: renders SurrealQL and parameters.
- `SurrealFormatter`: formats identifiers, record ids, and string literals.

## Diagnostics

The generator emits diagnostics for invalid model shapes:

| Code | Meaning |
| --- | --- |
| `CG001` | `[Table]` class must be partial. |
| `CG008` | Table declares more than one `[Id]` property. |
| `CG009` | `[Children]` element type cannot be a generic type parameter. |
| `CG010` | `[Reference]` target must be a `[Table]` type. |
| `CG011` | Entity is reachable from multiple aggregate roots. |
| `CG012` | `[Unset]` requires nullable reference storage. |
| `CG013` | More than one reference delete behavior was declared. |
| `CG014` | Cascade-only reference cycle. |
| `CG015` | Delete behavior attribute was placed on `[Parent]`. |
| `CG017` | `[Ignore]` may create a dangling reference. |
| `CG018` | More than one `[CompositionRoot]`. |
| `CG019` | `[CompositionRoot]` class must be partial. |
| `CG020` | `[Children]` member has no `[Parent]` path back to the aggregate root. |
| `CG021` | `[Reference]` crosses aggregate boundaries; use a relation instead. |
| `CG022` | Annotated property (`[Id]/[Property]/[Parent]/[Reference]/[Children]`) must be declared `partial`. |
| `CG024` | Property has multiple role attributes; the five role attributes are mutually exclusive. |
| `CG025` | `[Property]` type has no SurrealDB scalar mapping. |
| `CG026` | `[Children]` element type must be a `[Table]` (concrete-but-not-Table case; `CG009` covers type-parameter element). |
| `CG027` | `[Parent]` target must be a `[Table]`. |
| `CG028` | Annotated property must not be declared `static`. |

Some older relation-method diagnostics remain in the codebase for retired shapes, but normal user-facing relation writes should go through `Session.Relate<TKind>` and related methods.
