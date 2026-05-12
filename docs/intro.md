# Introduction

> **Status: preview.** The library exercises end-to-end against a live SurrealDB v3, but the public API is still moving — preview-tagged releases are described in [`notes.md`](notes.md). Validate against your workload before depending on it in production.

Disruptor.Surface is a C# source-generator-based persistence layer for SurrealDB. You describe your domain model with partial classes and attributes; the generator emits typed ids, a query layer, schema, and per-entity Save dispatch. At runtime, you load a clustered group of related records (an *aggregate*) into a `SurrealSession`, mutate the resulting C# objects synchronously, then dispatch the changes through a transaction your application owns.

The unit of loading is the aggregate — a `[AggregateRoot]`-tagged entity plus everything reachable through `[Children]` (think `Order` and its `OrderLines`, or `Design` and its `Constraints`). One aggregate maps to one `SurrealSession`. Because the transaction handle is yours, not the library's, you can save changes from any number of sessions into the same `tx` and commit them atomically — cross-aggregate consistency is whatever the caller decides.

## Is this for you?

**You'll probably like Disruptor.Surface if:**

- You're already on SurrealDB (or planning to be) and want a typed C# surface that knows your schema.
- Your domain has clear aggregate boundaries — entities cluster naturally into "load these together" groups.
- You prefer explicit `SaveAsync(thing)` over inferred dirty-flag tracking. The library does no change detection; you pick what to save.
- You want compile-time errors when your model is malformed (cycles, mismatched ids, cross-aggregate references that should be relations).

**You probably won't like it if:**

- You want a general ORM that abstracts the database away. This is a typed surface over SurrealDB specifically — SurrealQL (SurrealDB's SQL dialect), edge tables (SurrealDB's first-class relation tables), and record links show through.
- You want LINQ. The query API is hand-built around SurrealDB's shape; no `IQueryable`.
- You want long-lived attached entities with EF Core-style change tracking. Sessions are snapshots: load → mutate → save → done.
- You want a generated schema-migration tool. Schema generation is forward-only and additive (`DEFINE … IF NOT EXISTS`); renaming, dropping, or narrowing types is out of scope and you'll need to manage that DDL yourself.
- You're not on SurrealDB. There's no abstract storage layer.

**How it compares**, briefly:

- **vs. EF Core / NHibernate** — no change tracking, no LINQ, no lazy-load-by-default, no DbContext-per-request lifecycle. The compile-time guarantees that ORMs leave for runtime (CG011: entity in two aggregates, CG014: cascade cycle, CG021: cross-aggregate reference, CG025: unmappable property type, …) fire as build errors instead.
- **vs. Dapper / raw SQL** — you don't write the SurrealQL or the materialisation code. The generator emits both from your model.
- **vs. the raw `Disruptor.Surreal` SDK** — adds aggregate loading, typed ids, a query layer, schema generation, and per-entity Save dispatch on top of the SDK's transport. The SDK is still the wire (CBOR-encoded messages over a WebSocket — SurrealDB's native binary protocol); the library never replaces it.

## What You Write

You write ordinary partial classes:

```csharp
using Disruptor.Surface.Annotations;
using Disruptor.Surface.Runtime;

namespace MyApp.Model;

[Table, AggregateRoot]
public partial class Design
{
    [Id] public partial DesignId Id { get; set; }

    [Property] public partial string Title { get; set; }
    [Reference, Cascade, Inline] public partial Details? Details { get; set; }

    [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
}

[Table]
public partial class Constraint
{
    [Id] public partial ConstraintId Id { get; set; }

    [Parent] public partial Design Design { get; set; }
    [Property] public partial string Text { get; set; }
}

[Table]
public partial class Details
{
    [Property] public partial string Summary { get; set; }
}

[CompositionRoot]
public partial class Workspace
{
}
```

## What Gets Generated

For that model, the generator contributes:

- Typed ids such as `DesignId` and `ConstraintId`.
- Implementations for the partial properties.
- Two `Workspace.LoadDesignAsync` overloads — one taking `Disruptor.Surreal.SurrealClient db` (read-only), one taking `Disruptor.Surreal.SurrealTransaction tx` (write-mode load that sees in-txn writes from the same transaction). Both hydrate the aggregate into a `SurrealSession`.
- `Workspace.Schema` and `Workspace.ApplySchemaAsync(db)` / `ApplySchemaAsync(tx)` for applying the generated SurrealDB schema at startup.
- `Workspace.ReferenceRegistry`, used by reference metadata at session construction time.
- A typed query surface rooted at `Workspace.Query`. The pieces:

  | Piece | Shape |
  | --- | --- |
  | Predicate factories | `ConstraintQ.Description.Contains("…")` — one `{Table}Q` per table with typed accessors per scalar. |
  | Traversal builders | `IncludeConstraints(c => c.Where(...))` — chain `Include*` calls to pull related rows in one query. |
  | Edge query roots | `Workspace.Query.Edges.Restricts.WhereIn(...)` — flat `(source, target)` reads per relation kind. |
  | Five terminal verbs | `IdsAsync` (typed id list), `Select(projection).ExecuteAsync` (immutable projection rows), `ExecuteAsync` (hydrated entities), `LoadAsync` (write-mode aggregate session), and `Workspace.Hydrate.{Table}(ids)` (materialise specific ids into a session). |

  Every terminal accepts either `Surreal db` (read-only) or `Transaction tx` (in-txn read with full visibility into pending writes from the same transaction).
- A `Restricts : IRelationKind` marker class per forward relation attribute (plus a per-kind `RestrictsId` typed-id struct), and a per-entity `IEntity.SaveAsync` body that walks forward refs, dispatches the entity, and walks new children. Edges are written by saving a relation variant — `await session.SaveAsync(new ConstraintRestrictsUserStory { Source = constraint, Target = userStory }, tx)` — where the variant class is itself an `IEntity` annotated with the relation kind and `[In]`/`[Out]` endpoint properties.
- Compile-time diagnostics for invalid model shapes.

The generated code does not require your entities to inherit from a base class. It implements the required runtime interfaces in generated partial fragments and leaves your constructors, dependency injection, caching, and application wiring alone.

## Runtime Model

The main runtime concepts are:

- `Disruptor.Surreal.SurrealClient`: the SurrealDB SDK connection — CBOR over WebSocket. The library has no transport layer of its own; you connect once via `SurrealClient.ConnectAsync(...)` and pass the handle into the generated load methods.
- `Disruptor.Surreal.SurrealTransaction`: the SDK's transaction handle. **The library never owns one** — your application calls `db.BeginTransactionAsync()`, passes the handle into `Save` / `Delete` / `Unrelate` (and into write-mode loads), and calls `tx.CommitAsync()` (or `tx.CancelAsync()`) when its logical unit of work is done.
- `SurrealSession`: a snapshot-isolated entity store. "Snapshot-isolated" means the session holds a point-in-time view of the data it loaded — reads against it don't observe other writers' commits until you reload. Sync reads, sync writes (mutate the in-memory snapshot), async dispatch against an open transaction.
- `RecordId` and generated `{Entity}Id` types: strongly typed SurrealDB record ids.
- Inline element collections via `[Property] partial IReadOnlyList<T> { get; }` — generator emits `List<T>` backing + Add/Remove/Clear helpers, walks `T`'s public scalar properties at codegen for typed Hydrate / Save.
- `IRelationKind` markers: emitted per forward relation attribute, carry the SurrealDB edge name as a `static abstract` property — no string edge names in user code.

A minimum-viable end-to-end usage:

```csharp
using Disruptor.Surface.Runtime;
using Disruptor.Surreal;
using Disruptor.Surreal.Connection;

// 1. Connect once. The SDK owns the wire.
await using var db = await SurrealClient.ConnectAsync(SurrealOptions.Parse(
    "Url=ws://localhost:8000;Namespace=app;Database=main;User=root;Password=root"));

// 2. Apply the generated schema. Idempotent.
await Workspace.ApplySchemaAsync(db);

// 3. Create + dispatch. Your code owns the transaction.
var workspace = new Workspace();
var session = new SurrealSession(Workspace.ReferenceRegistry);
var design = session.Track(new Design { Title = "First design" });

await using (var tx = await db.BeginTransactionAsync())
{
    await session.SaveAsync(design, tx);
    await tx.CommitAsync();
}

// 4. Load back. Same overload set for read (`db`) or write (`tx`).
var loaded = await workspace.LoadDesignAsync(db, design.Id);
var d = loaded.Get<Design>(design.Id)!;
Console.WriteLine(d.Title);

// 5. Query without loading an aggregate.
var ids = await Workspace.Query.Designs
    .Where(DesignQ.Title.Contains("First"))
    .Limit(10)
    .IdsAsync(db);
```

Filtered loads enforce strict-with-escape: reads of slices the user didn't include throw `LoadShapeViolationException`; `session.FetchAsync(...)` is the typed escape hatch that hydrates an additional slice into the existing session, preserving any in-flight user mutations. Concurrent writers collide at COMMIT as `Disruptor.Surreal.SurrealConflictException` — catch, reload, retry.

## What It Is Not

Disruptor.Surface is not a general ORM. It doesn't hide SurrealDB, provide LINQ, or manage long-lived change tracking — `SaveAsync(thing)` is the user's explicit choice of what to save, not an inferred dirty-flag flush. It favors explicit aggregate loads, app-owned transactions, typed relation kinds, and generated SurrealQL. The runtime is generic and stateless: no process-global model state, no static current-session, no DI container assumptions.

## Where next?

- [**`quickstart.md`**](quickstart.md) — guided walkthrough: declare a model, apply schema, save and load against a running SurrealDB. Start here if you want to try the library.
- [**`api.md`**](api.md) — reference map of every modeling attribute, generated type, and runtime surface. Start here once you've built your first model and want the full picture.
- [**`architecture.md`**](architecture.md) — generator pipeline, emitters, the incremental-generator contract, and a contributor's cookbook for adding attributes, diagnostics, and emitters. Start here if you're going to make changes to the library itself.
- [**`notes.md`**](notes.md) — engineering log, build commands, and the running history of what's shipped under which preview tag.
