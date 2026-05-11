# API Reference

This is a user-facing API map for the generated surface and the runtime types most consumers touch.

## Packages And Namespaces

- `Disruptor.Surface.Runtime`: runtime core — `SurrealSession`, `IEntity`, `IRelationKind`, `RecordId`, `IReferenceRegistry`, `HydrationValue`, `ISaveContext`, `CommandLog`, `LoadShapeViolationException`. Two package dependencies: `Disruptor.Surreal` (the SurrealDB SDK — CBOR over WebSocket) and `Ulid`. There is no in-library transport — connect via the SDK and pass the `Surreal` (read-only) or `Transaction` (write-mode) handle into the generated load methods.
- `Disruptor.Surface.Generator`: Roslyn source generator. Reference as a private analyzer dependency.
- `Disruptor.Surface.Annotations`: namespace for modeling attributes (lives inside the runtime package).
- `Disruptor.Surreal`: the SDK — sibling project at `../surrealdb-dotnet`. Provides `SurrealClient`, `SurrealTransaction`, `SurrealOptions`, `SurrealQueryResponse`, `Disruptor.Surreal.Values.SurrealValue`, and the typed exception hierarchy (`SurrealException`, `SurrealConflictException`, …).

## Modeling Attributes

| Attribute | Target | Purpose |
| --- | --- | --- |
| `[Table]` | partial class | Includes a class in the generated model. |
| `[AggregateRoot]` | `[Table]` class | Marks the root of a loadable aggregate. Members are discovered through `[Children]`. |
| `[CompositionRoot]` | partial class | Receives generated `Load{Root}Async`, `Schema`, `ApplySchemaAsync`, `ReferenceRegistry`, `Query`, and `Hydrate` members. Exactly one is allowed per compilation. |
| `[Id]` | partial property | Optional public typed-id accessor. At most one per table. If omitted, the generator still emits an internal id anchor. |
| `[Property]` | partial property | Persisted scalar field, or inline element collection (`IReadOnlyList<T>` / `IList<T>` / `List<T>` of records). |
| `[Reference]` | partial property | Record reference. Non-nullable get-only references are mandatory; nullable settable references are optional. |
| `[Inline]` | with `[Reference]` | Hydrates the referenced record inline with its owner. Without `[Inline]`, only the referenced id is hydrated. |
| `[Parent]` | partial property | Parent link for aggregate hierarchy. |
| `[Children]` | partial get-only collection | Reverse lookup over child records. |
| `[Reject]` | with `[Reference]` | Schema-level: SurrealDB blocks deletion of the target while this reference points at it. Default behavior. |
| `[Unset]` | with nullable `[Reference]` | Schema-level: SurrealDB clears this reference when the target is deleted. |
| `[Cascade]` | with `[Reference]` | Schema-level: SurrealDB deletes the referencing record when the target is deleted. |
| `[Ignore]` | with `[Reference]` | Leaves the reference unchanged when the target is deleted. |
| `ForwardRelation` | base class | Base for user-defined forward relation attributes (no edge payload). |
| `ForwardRelation<TPayload>` | base class | Forward relation attribute with a typed edge payload. The generator emits a `DEFINE FIELD` on the relation table for each public scalar property of `TPayload`. |
| `InverseRelation<TForward>` | base class | Base for user-defined inverse relation attributes. |

## Generated Entity API

For each `[Table]` class, the generator emits a partial implementation that:

- Implements `IEntity`.
- Adds backing fields for generated properties.
- Binds each entity instance to a `SurrealSession` when tracked or hydrated.
- Property setters are pure backing-field writes (no Session call, no buffer, no command log entry).
- `[Parent]` setters additionally cascade-track the child into the parent's session via `parent.Session.AdoptIfUnbound(this)` so the parent's `[Children]` sees the new child at Save time.
- Routes child and relation reads into session queries.
- Implements `IEntity.Hydrate(SurrealValue, IHydrationSink)` (Value-consuming row → entity population, writes directly into backing fields).
- Implements `IEntity.SaveAsync(ISaveContext, ct)` — per-entity Save dispatch (forward-deps → entity content → new children → new outgoing relations).

Generated property behavior:

| Declaration | Generated behavior |
| --- | --- |
| `[Id] public partial DesignId Id { get; set; }` | Lazy typed id. Setter is allowed only before the entity is bound to a session. |
| `[Property] public partial string Title { get; set; }` | Synchronous getter/setter. Setter mutates the in-memory snapshot and appends to `CommandLog`. |
| `[Property] public partial IReadOnlyList<T> Items { get; }` | Inline element collection. Generator emits `List<T>` backing + `AddItem` / `RemoveItem` / `ClearItems` helpers; walks `T`'s public scalar properties at codegen for typed Hydrate / Save (no reflection). `IList<T>` / `List<T>` are also accepted shapes. |
| `[Reference] public partial T Ref { get; }` | Mandatory reference. Getter throws if the referenced entity is not available in the session. |
| `[Reference] public partial T? Ref { get; set; }` | Optional reference. Setting `null` clears the field. |
| `[Reference, Inline] public partial T? Ref { get; set; }` | Optional owned-sidecar reference that hydrates with the owner. |
| `[Parent] public partial Parent Parent { get; set; }` | Parent link. Setter records a parent field write. |
| `[Children] public partial IReadOnlyCollection<Child> Children { get; }` | Query over in-session children by parent link. |
| `[ForwardKind] public partial IReadOnlyCollection<T> Targets { get; }` | Forward edge read. Within-aggregate uses `Session.QueryOutgoing<TKind, T>(this)` and returns entities; cross-aggregate uses `Session.QueryRelatedIds<TKind>(this)` and returns `IReadOnlyCollection<IRecordId>`. |
| `[InverseKind] public partial IReadOnlyCollection<T> Sources { get; }` | Inverse edge read. Within-aggregate uses `Session.QueryIncoming<TKind, T>(this)`; cross-aggregate uses `Session.QueryInverseRelatedIds<TKind>(this)`. |

Each generated entity also has a protected `Session` property. Use it from your own partial members to write small domain verbs:

```csharp
// Domain verb dispatched against the user's open Transaction. The RELATE lands
// immediately — there is no buffered intent for SaveAsync to drain.
public Task RestrictsAsync(UserStory story, SurrealTransaction tx, CancellationToken ct = default)
    => Session.RelateAsync<Restricts>(this, story, tx, ct);
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
        await Session.DeleteAsync(child, currentTx);
    }
}
```

`OnCreate{Name}` is emitted for mandatory get-only references and runs during `Track`/Save auto-binding. `OnDeleting` runs before the entity's own `DELETE` is dispatched in `DeleteAsync`.

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

`Value` is a `string` validated at construction by `RecordIdFormat.Validate`. Three forms are accepted; anything else throws `FormatException`:

- **Ulid stringification** — exactly 26 characters of `[A-Z0-9]` (Crockford Base32). What `New()` mints. The default for fresh records.
- **Short lower_snake_case slug** — starts with `[a-z]`, followed by `[a-z0-9_]*`, max 32 characters. Opt-in for stable-named records (singletons, config rows, well-known references).
- **Content hash** — `[0-9a-f]{24}` (bare) or `[a-z]_[0-9a-f]{24}` (single-letter prefix). Produced by `RecordIdFormat.HashText(text, prefix?)` — SHA-256 truncated to 12 bytes. Use when the record is keyed by a piece of canonical text the caller knows up front (a code symbol's full name, a normalised URL).

```csharp
var fresh   = DesignId.New();                        // 26-char Ulid
var primary = new DesignId("primary");               // slug, OK
var hashed  = new DesignId(RecordIdFormat.HashText("System.IDisposable"));  // 24-char hex
var bad     = new DesignId("Some Mixed Case");       // throws FormatException
```

There is no assembly-level override — Ulid is the only mint type, and quoted-string ids are explicitly unsupported.

## Generated Composition Root API

For a `[CompositionRoot]` named `Workspace`, the generator emits:

```csharp
// Schema metadata (static partial fragments).
public static IReadOnlyList<string> Schema { get; }

public static Task ApplySchemaAsync(
    Disruptor.Surreal.SurrealClient db,
    CancellationToken ct = default);

public static Task ApplySchemaAsync(
    Disruptor.Surreal.SurrealTransaction tx,
    CancellationToken ct = default);

// Per-model reference metadata for IHydrationSink wiring at session construction.
public static IReferenceRegistry ReferenceRegistry { get; }

// Query AST roots (flat read terminals).
public static GeneratedQueryRoot Query { get; }

// Hydration entry — pairs with IdsAsync to materialise specific ids into a session.
public static GeneratedHydrationRoot Hydrate { get; }

// Per-aggregate-root load methods (instance, two overloads each).
public Task<SurrealSession> LoadDesignAsync(
    Disruptor.Surreal.SurrealClient db,
    DesignId rootId,
    CancellationToken ct = default);

public Task<SurrealSession> LoadDesignAsync(
    Disruptor.Surreal.SurrealTransaction tx,
    DesignId rootId,
    CancellationToken ct = default);
```

There are two `Load{Root}Async` overloads per `[AggregateRoot]` — one taking `Surreal db` (read-only — no transaction; the load just queries), one taking `Transaction tx` (write-mode — load query runs inside the txn so it sees in-txn writes from the same transaction). Both produce a `SurrealSession` rooted at the requested aggregate; both delegate to `{Root}AggregateLoader.PopulateAsync`. The unified query terminal `Workspace.Query.{Root}.WithId(...).LoadAsync(db | tx)` is the filtered-load equivalent — same hydrated session for a no-`Include*` call, narrower hydration when `Include*` is chained.

#### Schema migrations are out of scope

`ApplySchemaAsync` is forward-only and additive: every chunk uses `DEFINE … IF NOT EXISTS`, so re-applying is safe and idempotent for adds, but the library does **not** emit `REMOVE FIELD`, `REMOVE TABLE`, or any kind of diff between the model and the live database. Renaming a field, narrowing a type, dropping a table, and similar destructive shape changes are out of scope — too many context-dependent decisions (data preservation, online-vs-offline, downstream consumers) to wrap in a one-size helper without burning the caller. If you need migration semantics, run your own DDL alongside `ApplySchemaAsync`: iterate `Workspace.Schema` directly, splice your `REMOVE`/`UPDATE` statements where appropriate, and version the migration steps externally.

## Query API

Five terminal verbs share one query AST. Every terminal accepts either `Surreal db` (read-only) or `Transaction tx` (in-txn read with full visibility into pending writes from the same transaction):

- `IdsAsync(db | tx, ct)` — id-only selection. Returns `IReadOnlyList<{Table}Id>`; no entity hydration, no session. Generator-emitted per `[Table]`.
- `Select(projection).ExecuteAsync(db | tx, ct)` — projection mode. Returns immutable `IReadOnlyList<TRow>`; no entity hydration, no session. The projection owns the SELECT list and the per-row materialiser.
- `ExecuteAsync(db | tx, ct)` — read mode. Returns hydrated entities; the supporting session is internal and never exposed.
- `LoadAsync(db | tx, ct)` — write-mode session. Returns a `SurrealSession` you mutate and dispatch via `SaveAsync`. Only emitted on `Query<TRoot>` where `TRoot.IsAggregateRoot`.
- `Workspace.Hydrate.{Table}(ids).WithInclude(…).ExecuteAsync(db | tx, ct)` — hydration terminal. Takes a list of ids (typically from `IdsAsync`), materialises each into a tracked `SurrealSession` along with any included slices.

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

    public Task<IReadOnlyList<T>> ExecuteAsync(Disruptor.Surreal.SurrealClient db, CancellationToken ct = default);
    public Task<IReadOnlyList<T>> ExecuteAsync(Disruptor.Surreal.SurrealTransaction tx, CancellationToken ct = default);
    public string Compile();
    public string CompileIdsOnly();   // SELECT id FROM table …; throws if Includes present
}
```

`Where` AND-merges across calls. `WithId` overwrites. `WithInclude` is the primitive the generated `Include*` extensions call into. `Compile()` renders the AST to a SurrealQL string with every binding inlined as a literal via `SurrealFormatter` — no separate parameter dictionary.

### Projections — `Select(projection).ExecuteAsync`

Projections compile to `SELECT field1, field2 FROM table …` — only the columns the projection touches travel back, and each row materialises straight into a user-defined record (typically a positional `record`):

```csharp
public sealed record SymbolSearchResult(string Name, string QualifiedName, int Line);

public static class SymbolProjections
{
    public static readonly ISurfaceProjection<SymbolSearchResult> SearchResult =
        SurfaceProjection.For<SymbolSearchResult>(row => new SymbolSearchResult(
            Name:          row.Read(CodeSymbolQ.Name),
            QualifiedName: row.Read(CodeSymbolQ.QualifiedName),
            Line:          row.Read(CodeSymbolQ.Line)));
}

var results = await Workspace.Query.CodeSymbols
    .Where(CodeSymbolQ.Name.Contains("Parser"))
    .OrderBy(CodeSymbolQ.QualifiedName)
    .Limit(20)
    .Select(SymbolProjections.SearchResult)
    .ExecuteAsync(db);
// IReadOnlyList<SymbolSearchResult> — no session, no tracking.
```

The library does **not** generate projection types; the user owns the `TRow` shape and the materialise lambda. At construction time the lambda runs once with a probe row that captures each `Read(PropertyExpr<T>)` call into the SELECT field list (in first-read order). At query time the lambda runs once per row with a `Value`-backed row that decodes each value through `HydrationValue`.

Constraints:
- The lambda must call `row.Read(...)` at least once.
- The target type's constructor must accept default values during the probe — typically a positional record with no inline validation.
- `Include*` calls before `.Select(...)` are rejected — projections are flat by definition. Use `ExecuteAsync` directly if you need traversal.
- Field reads must be unconditional (no branches that skip `row.Read(...)`); discovery captures only the fields the lambda touches on the probe pass.
- v1 supports primitive scalars + nullables. Typed ids and complex types defer until a real callsite needs them.

### Hydration — `Workspace.Hydrate.{Table}(ids)`

The hydration terminal pairs with `IdsAsync` for the `Load → Hydrate → Mutate → Save` flow:

```csharp
// 1. Select — pure id selection.
var symbolIds = await Workspace.Query.CodeSymbols
    .Where(CodeSymbolQ.Name.Contains("Parser"))
    .Limit(20)
    .IdsAsync(db);

// 2. Hydrate — materialise the chosen rows + slices into a tracked session. Pass `db`
//    for read-only or `tx` if downstream mutations should participate in an existing txn.
await using var tx = await db.BeginTransactionAsync();
var session = await Workspace.Hydrate.CodeSymbols(symbolIds)
    .WithInclude(/* IIncludeNode tree describing the slice shape */)
    .ExecuteAsync(tx);

// 3. Mutate (sync, in-memory).
foreach (var symbol in session.GetAll<CodeSymbol>()) { … }

// 4. Dispatch + commit.
foreach (var symbol in session.GetAll<CodeSymbol>())
    await session.SaveAsync(symbol, tx);
await tx.CommitAsync();
```

Two overloads per table — typed `{Table}Id` for the ergonomic call site, raw `IRecordId` for cross-aggregate edge endpoints already collapsed to canonical record ids:

```csharp
public HydrationQuery<CodeSymbol> CodeSymbols(IEnumerable<CodeSymbolId> ids);
public HydrationQuery<CodeSymbol> CodeSymbols(IEnumerable<IRecordId> ids);
```

Empty-id list short-circuits: no wire call, empty session returned. `WithInclude` accepts the same `IIncludeNode` AST that read-mode queries use; the wire SQL is identical to `Query<T>.Where(IdIn).WithInclude(…).ExecuteAsync`.

`Workspace.Load{Root}Async(db | tx, rootId)` is the aggregate-load convenience — same compile-and-hydrate pipeline, scoped to a single aggregate root and its `[Children]` graph. Use it for full-aggregate mutate-and-commit flows; use the hydration terminal for everything that doesn't fit the "load the whole aggregate" shape.

### Id-only selection — `IdsAsync`

The id-only terminal is a generator-emitted extension on `Query<{Table}>`:

```csharp
IReadOnlyList<CodeSymbolId> ids = await Workspace.Query.CodeSymbols
    .Where(CodeSymbolQ.Name.Contains("Parser"))
    .OrderBy(CodeSymbolQ.QualifiedName)
    .Limit(50)
    .IdsAsync(db, ct);
```

Compiles to `SELECT id FROM code_symbols WHERE … ORDER BY … LIMIT … START …` and projects each returned `RecordId` into the typed `{Table}Id`. Useful when you want to identify the records first, then decide whether to hydrate (via `Hydrate.{Table}(ids)`) or pass the ids around for any other purpose.

`Include*` calls are rejected — id-only selection is flat by definition. Use `ExecuteAsync` if you need traversal.

`Limit` / `Start` / `OrderBy` push search-shape semantics server-side. SurrealQL's clause order is fixed (`WHERE → ORDER BY → LIMIT → START`); the compiler emits in that order regardless of which chain method ran first. Multiple `Limit` / `Start` calls overwrite (last wins); `OrderBy` and `ThenBy` are equivalent — both append a clause — and chain commas in declaration order. Typical search-tool shape:

```csharp
var topMatches = await Workspace.Query.Symbols
    .Where(SymbolQ.Name.Contains(query))
    .OrderBy(SymbolQ.Name)
    .Limit(20)
    .ExecuteAsync(db);
```

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
    .ExecuteAsync(db);
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

- **Within-aggregate** — each projected target row is hydrated via a generator-emitted callback. Single-target uses a direct `new {Target}() + Hydrate(Value, IHydrationSink)`; multi-target uses a switch on the row's `id:<table>` prefix to dispatch to the right concrete entity. For each target, an edge is synthesised from `(parentRowId, edgeName, targetId)` (direction-aware) and recorded via `IHydrationSink.Edge`. Subsequent reads of `entity.{RelationProperty}` resolve through `Session.QueryOutgoing` / `Session.QueryIncoming` against the populated edges + entities dicts.
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

    public Task<IReadOnlyList<EdgeRow>> ExecuteAsync(Disruptor.Surreal.SurrealClient db, CancellationToken ct = default);
    public Task<IReadOnlyList<EdgeRow>> ExecuteAsync(Disruptor.Surreal.SurrealTransaction tx, CancellationToken ct = default);
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
    .ExecuteAsync(db);
```

The compiler renders this as `SELECT id, in, out, line FROM uses WHERE … ORDER BY line ASC LIMIT 50;` — SurrealDB requires every `ORDER BY` field to be projected, so the ordered payload columns are spliced into `SELECT` alongside `id`/`in`/`out`. The hydration path still only reads the endpoint pair into `EdgeRow`, so the extra columns ride the wire but don't leak into the result type.

### `LoadAsync`

For aggregate-root tables, the generator emits two overloads:

```csharp
public static Task<SurrealSession> LoadAsync(
    this Query<Design> query,
    Disruptor.Surreal.SurrealClient db,
    CancellationToken ct = default);

public static Task<SurrealSession> LoadAsync(
    this Query<Design> query,
    Disruptor.Surreal.SurrealTransaction tx,
    CancellationToken ct = default);
```

Body:

- Throws `InvalidOperationException` when `WithId` wasn't called — load mode is single-aggregate-rooted.
- With no `Include*` calls, delegates to the legacy `{Root}AggregateLoader.PopulateAsync` for the full-aggregate hydrate.
- With `Include*` calls, runs the compiler-driven nested SELECT and hydrates only the chosen slice. Slices outside the load shape throw `LoadShapeViolationException` on read.

Pass `tx` for write-mode (the load query runs inside the txn so it sees in-txn writes from the same transaction); pass `db` when the loaded session won't dispatch any writes through `tx`.

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
        db);
    var notes = design.Notes;        // works
}
```

`FetchAsync` is a slice widener: existing entities re-Hydrate (overwriting scalar fields with the DB row); brand-new entities go through the include's full Hydrate. Slices listed in the extension query's `Includes` are marked loaded. **Caveat:** if you've mutated an entity in memory and Fetch re-hydrates it, your edits get clobbered — Save first or accept the clobber. Per-field "did the user touch this" tracking was deliberately removed under the explicit-Save model. Closed sessions throw `InvalidOperationException`.

```csharp
public Task FetchAsync<T>(
    Query<T> query,
    Disruptor.Surreal.SurrealClient db,
    CancellationToken ct = default)
    where T : class, IEntity, new();

public Task FetchAsync<T>(
    Query<T> query,
    Disruptor.Surreal.SurrealTransaction tx,
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

Runtime calls — every edge mutation goes through async dispatch against the user's `SurrealTransaction`. Reads stay sync against the in-memory edge index that the loader populates and `RelateAsync` keeps current.

```csharp
// Mutations — each call dispatches RELATE / DELETE-edge inside the transaction.
await session.RelateAsync<Restricts>(source, target, tx);
await session.UnrelateAsync<Restricts>(source, target, tx);
await session.UnrelateAsync<Restricts>(source, target: null, tx);  // bulk: every outgoing edge from source
await session.UnrelateAsync<Restricts>(source: null, target, tx);  // bulk: every incoming edge into target

// Reads (sync, against the in-memory snapshot).
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
DEFINE INDEX IF NOT EXISTS unique_relationship ON TABLE uses COLUMNS in, out UNIQUE;
DEFINE FIELD IF NOT EXISTS kind ON uses TYPE string DEFAULT "";
DEFINE FIELD IF NOT EXISTS file_path ON uses TYPE string DEFAULT "";
DEFINE FIELD IF NOT EXISTS line ON uses TYPE int DEFAULT 0;
DEFINE FIELD IF NOT EXISTS run_id ON uses TYPE string DEFAULT "";
```

Every relation table gets a `DEFINE INDEX … UNIQUE` on `(in, out)` and is declared `TYPE RELATION ENFORCED`. `RelateAsync` dispatches `INSERT RELATION IGNORE INTO {edge} { id, in, out, …payload }` — with the default `RecordId.Idempotent` edge id strategy, the id is a deterministic hash of `{source}|{table}|{target}` so re-running the same triple is a substrate-side no-op (IGNORE absorbs both the duplicate id and the UNIQUE INDEX violation). Caller-minted edge ids (Random Ulid via `RecordId.New(...)`, slug via `new RecordId(...)`) get the same IGNORE protection — a different id for the same `(in, out)` pair triggers the unique index, IGNORE absorbs it; if you genuinely need duplicate edges, the schema would have to drop the unique index.

**Payload semantics:** with IGNORE, the FIRST `RelateAsync` call wins. A second call with a different payload is a no-op — payload is not updated. This matches the "Relate is idempotent" contract: re-calling is safe and doesn't surprise-write. If you need to mutate an existing edge's payload, run `UPDATE edge:<id> CONTENT {…}` directly.

The payload class is a plain POCO — no `[Property]` annotations needed; public scalar properties (`string`, `int`/`long`, `bool`, `float`/`double`/`decimal`, `DateTime`/`DateTimeOffset`, `Guid`, `Ulid`) are picked up automatically. Anything else is silently skipped. Static, indexer, write-only, and inherited-already-seen properties are not emitted as fields.

At write time, pass payload data through the dictionary overload of `RelateAsync`. The edge id strategy is carried by an optional `RecordId edge` parameter; when omitted it defaults to `RecordId.Idempotent(TKind.EdgeName)`:

```csharp
// Idempotent (default) — hash from (source, table, target)
await session.RelateAsync<Uses>(source, target, new Dictionary<string, object?>
{
    ["kind"]      = "call",
    ["file_path"] = "src/Foo.cs",
    ["line"]      = 42,
    ["run_id"]    = currentRunId,
}, tx);

// Random Ulid — caller mints the edge id up front
await session.RelateAsync<Uses>(source, target, RecordId.New(Uses.EdgeName), payload, tx);

// Slug — caller picks a stable name for the edge row
await session.RelateAsync<Uses>(source, target, new RecordId(Uses.EdgeName, "primary_call"), payload, tx);
```

Field names in the dictionary use the snake-cased SurrealDB form (matching what the schema declared), not the C# property name. The bare `ForwardRelation` (no `<TPayload>`) keeps the pre-feature schema unchanged — no extra fields emitted on the relation table.

Within-aggregate relation properties hydrate entity instances. Cross-aggregate relation properties expose `IRecordId` endpoints because the other aggregate is not part of the current snapshot.

## Runtime Types

### `Disruptor.Surreal` — the wire layer

The library has no transport of its own. `Disruptor.Surreal` (sibling project at `../surrealdb-dotnet`) provides the SDK — CBOR over WebSocket — and is the only wire layer:

```csharp
using Disruptor.Surreal;
using Disruptor.Surreal.Connection;

await using var db = await SurrealClient.ConnectAsync(SurrealOptions.Parse(
    "Url=ws://localhost:8000;Namespace=app;Database=main;User=root;Password=root"));
```

The relevant SDK surface for Disruptor.Surface consumers:

| Type | Purpose |
| --- | --- |
| `SurrealClient` | Connection handle. `ConnectAsync(SurrealOptions)`, `QueryAsync(sql, bindings, ct)`, `BeginTransactionAsync()`. |
| `SurrealTransaction` | Transaction handle. `QueryAsync`, `CommitAsync()`, `CancelAsync()`. `IAsyncDisposable` — auto-cancels on dispose. |
| `SurrealOptions` | Connection parameters. `Parse(connectionString)` accepts the standard semicolon-separated key/value form. |
| `SurrealQueryResponse` | Multi-statement result envelope; `Statements[i].Result` is a `SurrealValue`. |
| `Disruptor.Surreal.Values.SurrealValue` | Tagged union of CBOR-typed values. Walked via `SurrealObjectValue` / `SurrealListValue` / scalar variants. |
| `SurrealException` | Base of the SDK's typed exception hierarchy. |
| `SurrealConflictException` | Raised at COMMIT (or earlier on a conflicting write) when another writer's commit landed first inside the same MVCC window. Catch, reload, retry. |

### `SurrealSession`

`SurrealSession` is the main runtime unit of work — a snapshot-isolated entity store with sync reads, sync writes (in-memory), and async dispatch.

Construction:

```csharp
var session = new SurrealSession(Workspace.ReferenceRegistry);
```

Read methods (sync):

| Method | Purpose |
| --- | --- |
| `Get<T>(IRecordId id)` | Lookup a hydrated or tracked entity by id. |
| `QueryChildren<T>(owner, childTable)` | Read children by parent link. Matches each candidate's `IEntity.GetParentId()` against `owner.Id`. |
| `QueryOutgoing<TKind, T>(owner)` | Read same-aggregate outgoing relation targets. |
| `QueryIncoming<TKind, T>(owner)` | Read same-aggregate incoming relation sources. |
| `QueryRelatedIds<TKind>(owner)` | Read cross-aggregate outgoing ids. |
| `QueryInverseRelatedIds<TKind>(owner)` | Read cross-aggregate incoming ids. |
| `IsTracked(IRecordId id)` | True iff currently in the identity map. |
| `IsSliceLoaded(IRecordId owner, string field)` | True iff the slice was hydrated (or the entity was freshly Tracked, which marks every slice). |

Sync write methods (mutate the in-memory snapshot, append to `CommandLog`):

| Method | Purpose |
| --- | --- |
| `Track<T>(T entity)` | Bind a fresh entity to the session, run `Initialize` (idempotent mandatory-ref seeding via `OnCreate*` hooks), mark every slice loaded. |
| `AdoptIfUnbound(IEntity child)` | Cascade-track called from emitted `[Parent]` setters: pulls an unbound child into this session via `Track`. No-op when the child is already bound. |

Async dispatch methods (talk to SurrealDB through an app-owned `Transaction`):

| Method | Purpose |
| --- | --- |
| `SaveAsync(IEntity entity, Transaction tx, ct)` | Per-entity Save. Auto-binds the entity, walks forward dependencies (Reference / Parent), dispatches a whole-entity `CREATE/UPDATE record:id CONTENT { ... }`, walks new children. **Edges are not in scope** — dispatch them with `RelateAsync` against the same `tx`. The user picks what to save; the library does no change tracking. |
| `DeleteAsync(IEntity entity, Transaction tx, ct)` | Run `OnDeleting`, dispatch a single `DELETE`, remove from the in-memory snapshot. |
| `RelateAsync<TKind>(source, target, tx, ct)` | Direct `RELATE` dispatch (also updates the in-memory edge index so subsequent `QueryOutgoing` / `QueryIncoming` reads see the new edge). Edge id defaults to `RecordId.Idempotent(TKind.EdgeName)`; overloads accept an explicit `RecordId edge` and/or a payload `IReadOnlyDictionary<string, object?>` for `RELATE … CONTENT { … }`. |
| `UnrelateAsync<TKind>(source?, target?, tx, ct)` | Direct `DELETE`-edge dispatch. At least one endpoint non-null; one-side-null is the bulk form (every matching edge of the kind, persisted-or-not). |
| `FetchAsync<T>(Query<T> query, db | tx, ct)` | Top-up extension query — partial-merge hydrate into the existing session, mark Included slices loaded. Pending writes always win. |

Lifecycle:

- The session stays open across multiple Save/Delete/Relate dispatches — feel free to dispatch into one `tx`, commit it, dispatch into another `tx`, etc.
- Any exception during a dispatch closes the session (`IsClosed` becomes `true`); subsequent operations throw `InvalidOperationException`.
- `Abandon()` closes the session explicitly.
- The transaction lifecycle is the app's responsibility — the library never opens or commits transactions on your behalf.

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

The generator routes every `{Name}Id(string Value)` ctor through `Validate`, so user code can't construct an id with a malformed value — the throw happens at construction, not at dispatch time.

### `RecordId`

Canonical id type:

```csharp
var fresh   = RecordId.New("designs");                                      // ulid value
var pinned  = new RecordId("designs", "primary");                           // slug value
var parsed  = RecordId.Parse("designs:01J...");                             // round-trip parse

// Deterministic content-addressed id — same (table, text) always yields the same id.
var symbol  = RecordId.FromText("symbols", "Disruptor.Surface.Runtime.SurrealSession");
var tagged  = RecordId.FromText("symbols", "ICodeSymbol", prefix: 'i');     // "symbols:i_<hash>"

// Idempotent edge id sentinel — resolves to a deterministic hash of the linkage
// triple at emit time. Used by Session.Relate<TKind> as the default edge id.
var edge    = RecordId.Idempotent("uses");
```

Important members:

- `Table`
- `Value`
- `ToLiteral()`
- `ToString()`
- `From(IRecordId id)` — collapse a typed id (or any `IRecordId`) to a canonical `RecordId`.
- `New(table, value?)` — fresh ulid-backed id.
- `FromText(table, text, prefix?)` — deterministic content-addressed id; convenience over `RecordIdFormat.HashText`.
- `Idempotent(table)` — deferred sentinel that resolves to `HashText("{src}|{table}|{tgt}")` at emit time.
- `TryParse(...)`
- `IsForTable(table)`

### Inline element collections

Declare an `IReadOnlyList<TElem>`-typed `[Property]` for an inline-record column:

```csharp
public sealed record Scenario(string Kind, string Description);

[Property]
public partial IReadOnlyList<Scenario> Scenarios { get; }
```

The generator walks `Scenario`'s public scalar properties at codegen time (mirrors how `ForwardRelation<TPayload>` walks payload props — no `[Element]`/`[Field]` annotations needed) and emits:

- `private readonly List<Scenario> _scenarios = new();` backing field
- `public partial IReadOnlyList<Scenario> Scenarios => _scenarios;`
- Generator-emitted helpers: `AddScenario(Scenario)`, `RemoveScenario(Scenario)`, `ClearScenarios()`
- Typed `Hydrate` body (per-element `new Scenario(...)` from `SurrealObjectValue`, no reflection)
- Typed `SaveAsync` content (per-element `Dictionary<string, object?>` → `SurrealFormatter` renders as `{ kind: ..., description: ... }`)

`IList<TElem>` and `List<TElem>` are also accepted shapes — same backing field, helpers still emitted (the IList/List shapes also let user code mutate the property directly).

Per-element schema lands as `array<object>` plus per-member `field.*.member` sub-field DDL.

### `HydrationValue`

Value-native helpers used by emitted `IEntity.Hydrate` and the runtime's load/query consumers. All inputs are `Disruptor.Surreal.Values.SurrealValue` — no JSON intermediary.

| Method | Purpose |
| --- | --- |
| `ReadRecordId(SurrealValue v)` | Read any of the wire forms (record-id value / string `"table:value"` / object with `id` field) into a `RecordId`; throws on unrecognised shape. |
| `TryReadRecordId(SurrealObjectValue parent, string field, out RecordId)` | Try-variant on a named field of a row. |
| `ReadString(SurrealObjectValue parent, string field, string fallback = "")` | Read a string field with a fallback when missing or not-a-string. |
| `ReadOrDefault<T>(SurrealObjectValue parent, string field)` | Read a typed field with a default fallback. Handles primitives, nullable wrappers, and arrays / `List<T>` of primitives. Records / POCOs are no longer routed through here — generator-emitted Hydrate bodies build records typed-and-direct. |
| `TryReadReferenceId(SurrealObjectValue parent, string field)` | Read a non-Inline `[Reference]` or `[Parent]` id; returns `null` when the field is missing / null / unrecognisable. |
| `HydrateInlineReference<T>(SurrealObjectValue parent, string field, IHydrationSink sink)` | Construct a `T` from an inline-expanded `[Reference, Inline]` payload and run its `Hydrate`; returns the entity (or null if the field is missing / id-only / already tracked). |

### `ISaveContext`

Per-entity Save orchestration interface. Passed to generator-emitted `IEntity.SaveAsync` bodies; implemented privately by `SurrealSession`.

```csharp
public interface ISaveContext
{
    Disruptor.Surreal.SurrealTransaction Transaction { get; }

    // True iff `id` is known to exist in the DB — either loaded from a prior Hydrate
    // (loadedAtStart) or already CREATEd in this Save pass. NOT a check on the
    // in-memory identity map: a freshly-constructed entity that's been bound for Save
    // sits in state.Entities too, and the entity body needs to distinguish "in
    // session" from "in DB" so it chooses CREATE vs UPDATE correctly.
    bool IsTracked(IRecordId id);

    // Recursion callback — emitted bodies use this to dispatch forward dependencies
    // and new children. Cycle-broken at the session level.
    Task SaveAsync(IEntity entity, CancellationToken ct);

    // Post-dispatch — emitted bodies call this after their CREATE/UPDATE has gone
    // through. Adds the entity to the identity map and flips IsTracked for it to
    // true for subsequent calls in the same Save pass.
    void MarkSaved(IEntity entity);
}
```

### `CommandLog`

Append-only diagnostic log of model commands recorded by sync write methods. Useful for tests asserting "what intent did the session capture?" and for telemetry.

```csharp
public sealed class CommandLog
{
    public IReadOnlyList<Command> Entries { get; }
    public int Count { get; }
    public void Append(Command command);
    public void Clear();
}
```

Not consumed by the per-entity Save dispatch path — that reads the entity's current state directly via the generator-emitted body. Available on `SurrealSession.Log`.

### Internal emit / format helpers

These are public for diagnostics and tests but most consumers do not call them directly:

- `SurrealFormatter.Identifier(string)`: regex-validates a SurrealQL identifier (table / field / edge name) and returns it. The chokepoint that defends against malformed identifiers reaching emitted SQL.
- `Command` / `CommandOp` / `CommandLog`: typed records for the diagnostic intent log. Sync `Track` / `Relate` / `Unrelate` and async `DeleteAsync` / `RelateAsync` / `UnrelateAsync` append `Command` entries; tests + telemetry inspect `session.Log.Entries`.

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
