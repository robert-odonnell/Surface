# Introduction

Disruptor.Surface is a C# source-generator based persistence layer for SurrealDB. You describe your domain model with partial classes and attributes, and the generator emits the plumbing needed to load aggregates, hold an in-memory snapshot, and dispatch per-entity writes into an app-owned `Disruptor.Surreal.SurrealTransaction`.

The library is aimed at domain models where aggregates matter. A `[AggregateRoot]` plus its `[Children]` graph becomes the unit of loading. A `SurrealSession` represents one loaded snapshot. You read and mutate the generated model synchronously, then call `session.SaveAsync(entity, tx)` to dispatch writes into a transaction the *application* owns — the library never opens or commits transactions on your behalf.

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
- `Workspace.Query` — a typed query surface with predicate factories (`ConstraintQ.Description.Contains("…")`), traversal builders (`IncludeConstraints(c => c.Where(...))`), edge query roots (`Workspace.Query.Edges.Restricts.WhereIn(...)`), and five terminal verbs sharing one AST: `IdsAsync` (typed id list), `Select(projection).ExecuteAsync` (immutable projection rows), `ExecuteAsync` (hydrated entities), `LoadAsync` (write-mode aggregate session), and `Workspace.Hydrate.{Table}(ids)` for non-aggregate-shaped slices into a tracked session. Every terminal accepts either `Surreal db` (read) or `Transaction tx` (in-txn read with full visibility into pending writes).
- `Workspace.Schema` and `Workspace.ApplySchemaAsync(db)` / `ApplySchemaAsync(tx)`.
- `Workspace.ReferenceRegistry`, used by reference metadata at session construction time.
- A `Restricts : IRelationKind` marker class per forward relation attribute, plus a per-entity `IEntity.SaveAsync` body that walks forward refs, dispatches the entity, walks new children, and dispatches new outgoing relations via a snapshot diff.
- Compile-time diagnostics for invalid model shapes.

The generated code does not require your entities to inherit from a base class. It implements the required runtime interfaces in generated partial fragments and leaves your constructors, dependency injection, caching, and application wiring alone.

## Runtime Model

The main runtime concepts are:

- `Disruptor.Surreal.SurrealClient`: the SurrealDB SDK connection — CBOR over WebSocket. The library has no transport layer of its own; you connect once via `SurrealClient.ConnectAsync(...)` and pass the handle into the generated load methods.
- `Disruptor.Surreal.SurrealTransaction`: the SDK's transaction handle. **The library never owns one** — the app calls `db.BeginTransactionAsync()`, passes the handle into Save/Delete/Relate/Unrelate (and into write-mode loads), and calls `tx.CommitAsync()` (or `tx.CancelAsync()`) when its logical unit of work is done.
- `SurrealSession`: snapshot-isolated entity store. Sync reads, sync writes (mutate the in-memory snapshot), async dispatch (`SaveAsync` / `DeleteAsync` / `RelateAsync` / `UnrelateAsync`) against an open `Transaction`.
- `RecordId` and generated `{Entity}Id` types: strongly typed SurrealDB record ids.
- `SurrealArray<T>`: mutation-aware inline array field wrapper.
- `IRelationKind` markers: emitted per forward relation attribute, carry the SurrealDB edge name as a `static abstract` property — no string edge names in user code.

The usual flow is:

1. Connect once via `SurrealClient.ConnectAsync(SurrealOptions.Parse(...))`.
2. Apply the generated schema at startup: `await Workspace.ApplySchemaAsync(db)`.
3. Read or load. Pick the terminal that matches the workload:
   - **Id selection** — `Workspace.Query.{Table}.Where(...).IdsAsync(db)` returns `IReadOnlyList<{Table}Id>`. The "identify, don't materialise" terminal; pairs with `Hydrate` below.
   - **Projection** — `Workspace.Query.{Table}.Where(...).Select(projection).ExecuteAsync(db)` returns immutable user-defined `TRow` rows. The user owns the `TRow` shape and the materialise lambda; the library doesn't generate projection types.
   - **Hydrated entity reads** — `Workspace.Query.{Table}.Where(...).ExecuteAsync(db)` returns hydrated entities tracked in an internal session (opaque, never-saved).
   - **Edge pairs** — `Workspace.Query.Edges.{Kind}.WhereIn(...).ExecuteAsync(db)` returns flat `(source, target)` rows.
   - **Hydration** — `Workspace.Hydrate.{Table}(ids).WithInclude(...).ExecuteAsync(db|tx)` materialises specific rows + slices into a tracked `SurrealSession`. Pass `tx` when downstream mutations should participate in an existing transaction.
   - **Aggregate load** — `Workspace.LoadDesignAsync(db, id)` for read-only or `LoadDesignAsync(tx, id)` for write-mode (the load query runs inside the txn so it sees in-txn writes from the same transaction); also reachable as `Workspace.Query.Designs.WithId(id).Include*(...).LoadAsync(db|tx)` for the filtered shape.
4. Read and mutate entities through normal sync properties and methods.
5. For writes: open a transaction with `await using var tx = await db.BeginTransactionAsync();`, call `await session.SaveAsync(entity, tx)` per aggregate root (the per-entity Save dispatch walks forward refs, then the entity, then new children, then new outgoing relations), then call `await tx.CommitAsync()`. Concurrent writers collide at COMMIT as `Disruptor.Surreal.SurrealConflictException` — catch, reload, retry.

Filtered loads enforce strict-with-escape: reads of slices the user didn't include throw `LoadShapeViolationException`; `session.FetchAsync(...)` is the typed escape hatch that hydrates an additional slice into the existing session, preserving any in-flight user mutations.

## What It Is Not

Disruptor.Surface is not a general ORM. It does not try to hide SurrealDB, provide LINQ, or manage long-lived change tracking — `SaveAsync(thing)` is the user's explicit choice of what to save, not an inferred dirty-flag flush. It favors explicit aggregate loads, app-owned transactions, typed relation kinds, generated SurrealQL, and small runtime surfaces that are easy to reason about.

The current repository is a preview implementation. The sample exercises schema generation, aggregate hydration, relation reads/writes, per-entity Save dispatch into an app-owned transaction, and the full query layer against a live SurrealDB v3 — but you should validate behavior against your SurrealDB version and workload before depending on it in production.
