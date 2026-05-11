# Disruptor.Surface

A C# Roslyn source generator that turns `[Table]`-annotated partial classes into a working
SurrealDB persistence layer — typed ids, snapshot-isolated sessions, generated SurrealQL,
typed relation kinds, app-owned transactions over the Disruptor.Surreal SDK.

## At a glance

You write the model:

```csharp
using Disruptor.Surface.Annotations;
using Disruptor.Surface.Runtime;

namespace MyApp.Model;

[Table, AggregateRoot]
public partial class Design
{
    [Id]                public partial DesignId Id { get; set; }
    [Reference, Inline] public partial Details? Details { get; set; }
    [Property]          public partial string Description { get; set; }
    [Children]          public partial IReadOnlyCollection<Constraint> Constraints { get; }
}

[Table]
public partial class Constraint
{
    [Id]                public partial ConstraintId Id { get; set; }
    [Reference, Inline] public partial Details? Details { get; set; }
    [Parent]            public partial Design Design { get; set; }
    [Property]          public partial string Description { get; set; }
}
```

…plus a single-line composition-root declaration:

```csharp
[CompositionRoot]
public partial class Workspace { }
```

`[Inline]` marks owned-sidecar references that hydrate alongside their owner; plain
`[Reference]` is a foreign pointer that stores only the id.

The generator emits everything needed to make this work end-to-end:

- Per-table `{Name}Id` `readonly record struct` with implicit conversion to the canonical `RecordId`
- Two `Workspace.Load{Root}Async` overloads grafted onto your `[CompositionRoot]` partial — one taking `Disruptor.Surreal.SurrealClient db` (read-only), one taking `Disruptor.Surreal.SurrealTransaction tx` (write-mode load that sees in-txn writes)
- A unified query surface with terminal verbs sharing one AST — `…IdsAsync(db|tx)` for id-only selection (`IReadOnlyList<{Table}Id>`), `…Select(projection).ExecuteAsync(db|tx)` for projection rows, `…ExecuteAsync(db|tx)` for hydrated entities, `…WithId(id).IncludeConstraints(c => c.Where(...)).LoadAsync(db|tx)` for filtered write-mode loads of an aggregate root, `Workspace.Hydrate.{Table}(ids).WithInclude(...).ExecuteAsync(db|tx)` to materialise a non-aggregate slice into a tracked session, and `Workspace.Query.Edges.Restricts.WhereIn(...).ExecuteAsync(db|tx)` for flat edge pairs. Strict-with-escape on filtered loads — unloaded slices throw `LoadShapeViolationException`, `session.FetchAsync(...)` extends the slice in place
- A `SurrealSession` surface with sync reads (`design.Description`, `design.Constraints`) and sync mutations into the in-memory snapshot
- Per-entity dispatch via `session.SaveAsync(entity, tx, ct)` — auto-recursive on the entity's reference graph (forward refs → entity → new children → new outgoing relations) as a whole-entity `CREATE/UPDATE … CONTENT { ... }`
- A `Restricts : IRelationKind` marker class per forward relation attribute, so `session.RelateAsync<Restricts>(constraint, userStory, tx)` is type-checked at compile time
- Directional read primitives — within-aggregate (`Session.QueryOutgoing<TKind, T>(this)` / `Session.QueryIncoming<TKind, T>(this)`), cross-aggregate (`Session.QueryRelatedIds<TKind>(this)` / `Session.QueryInverseRelatedIds<TKind>(this)`)
- `Workspace.Schema` (`IReadOnlyList<string>` of idempotent DDL chunks) plus `Workspace.ApplySchemaAsync(db)` / `ApplySchemaAsync(tx)` — model-scoped, no process globals
- `Workspace.ReferenceRegistry` carrying the per-model `[Reference]` field metadata (delete behaviour) the runtime reads through
- Diagnostics (CG001-CG028) that catch model-level mistakes at compile time

The library promise is **minimal intrusion**: the generator never forces a base class, ctor, or
inherited member on you. The `[CompositionRoot]` partial is yours — wire SDK connection, caches,
telemetry, and DI however you want. The library only contributes the load methods.

## Documentation

- [Introduction](docs/intro.md)
- [Quickstart](docs/quickstart.md)
- [API reference](docs/api.md)
- [Architecture](docs/architecture.md)

## Quick start

```csharp
using Disruptor.Surreal;

// One-shot SDK connection. CBOR over WebSocket.
await using var db = await SurrealClient.ConnectAsync(SurrealOptions.Parse(
    "Url=ws://localhost:8000;Namespace=app;Database=main;User=root;Password=root"));

// Bootstrap (idempotent — every run is fine). One RPC per chunk so a failure
// pinpoints which one broke. Iterate `Workspace.Schema` directly when you want
// to filter, run inside a custom txn, or log per-chunk progress.
await Workspace.ApplySchemaAsync(db);

// Construct your composition root once at startup. The generator grafts the
// Load*Async methods onto your partial — ctor + caches + telemetry are yours.
var workspace = new Workspace();

// Read session — snapshot, no transaction.
var ro = await workspace.LoadDesignAsync(db, designId);
foreach (var c in ro.Get<Design>(designId)!.Constraints)
    Console.WriteLine($"{c.Id}: {c.Description}");

// Surgical read — no aggregate hydration, no session, runs the predicate
// straight on SurrealDB. Use `.IdsAsync(db)` for an `IReadOnlyList<ConstraintId>`
// when you only need the ids, or `.Select(projection).ExecuteAsync(db)` for
// typed row DTOs.
var matches = await Workspace.Query.Constraints
    .Where(ConstraintQ.Description.Contains("security"))
    .ExecuteAsync(db);

// Write session — app opens a Transaction, library dispatches into it, app
// commits. Concurrent writers collide at COMMIT as SurrealConflictException.
await using var tx = await db.BeginTransactionAsync();
try
{
    var session = await workspace.LoadDesignAsync(tx, designId);
    var design = session.Get<Design>(designId)!;
    design.Description = "edited";

    var constraint = session.Track(new Constraint
    {
        Design = design,
        Description = "no negatives",
        Details = new Details { Header = "rule" },
    });
    constraint.Restricts(someUserStory); // user-defined one-liner: => Session.Relate<Restricts>(this, x)

    // Auto-recursive: SaveAsync(design) walks forward refs (Details), tracked
    // children (Constraints, Epics, …), and new outgoing relations (Restricts)
    // — one call dispatches the whole subgraph.
    await session.SaveAsync(design, tx);
    await tx.CommitAsync();
}
catch (SurrealConflictException)
{
    // Another writer's commit landed first. Reload + retry. tx auto-cancels on dispose.
}
```

## Project layout

```
src/
  Disruptor.Surface.Generator/   — Roslyn source generator (netstandard2.0, analyzer)
  Disruptor.Surface.Runtime/     — runtime core: SurrealSession, IEntity, IRelationKind,
                                   RecordId, IReferenceRegistry,
                                   HydrationValue, ISaveContext, CommandLog. Two package
                                   deps: Disruptor.Surreal (the SurrealDB SDK — CBOR over
                                   WebSocket) and Ulid. No transport layer of its own.
  Disruptor.Surface.Sample/      — console-app harness that exercises the full pipeline
                                   against a live SurrealDB; also the canonical worked
                                   example schema
```

`Disruptor.Surface.Sample` references both the generator (as an analyzer, no runtime dep) and the
runtime library. Real consumer projects do the same.

## Building

```sh
dotnet build Disruptor.Surface.slnx
```

Generated files land in `src/Disruptor.Surface.Sample/obj/Debug/net10.0/generated/Disruptor.Surface.Generator/Disruptor.Surface.Generator.ModelGenerator/`
— inspect them to see what the generator emitted for a given `[Table]` class.

## Running the harness

The sample is a console app that connects to a live SurrealDB, applies the schema,
seeds ten Design aggregates and a Review against one of them, reloads them, then
exercises the query layer (predicate, traversal, edge), filtered `LoadAsync`,
`LoadShapeViolationException`, `FetchAsync` top-up, and the four flavors of relation
traversal include. Useful both as a smoke test and as a worked end-to-end example.

```sh
surreal start --bind 127.0.0.1:8000 \
              --user root --pass secret \
              rocksdb://path/to/db
```

```sh
dotnet run --project src/Disruptor.Surface.Sample
```

Connection parameters are hard-coded in `Program.cs` — adjust them if your Surreal
instance lives elsewhere.

## Authoring conventions

A `[Table]` class must be `partial` and may declare at most one `[Id]` partial property.
Declaring `[Id]` is recommended for a public typed-id accessor; if omitted, the generator
still emits the internal id anchor required by the runtime.
A single class may be tagged `[CompositionRoot]` and must also be partial — the generator
grafts the per-aggregate load methods onto it.

| Attribute             | Shape                                                     | What you get                                                                                |
| --------------------- | --------------------------------------------------------- | ------------------------------------------------------------------------------------------- |
| `[Id]`                | `partial {Name}Id Id { get; set; }`                       | Lazy-minted typed id; assignable for "construct a handle to a known record"                 |
| `[Property]`          | `partial T Name { get; set; }`                            | Pure backing field — `get => _name; set => _name = value;`. Get-only also legal. |
| `[Property]`          | `partial IReadOnlyList<T> Name { get; }`                  | Inline `array<object>` column. Generator emits `List<T>` backing + `Add{Singular}` / `Remove{Singular}` / `Clear{Plural}` helpers and walks `T`'s public scalar properties at codegen for typed Hydrate / Save (no reflection). |
| `[Reference]`         | `partial T Name { get; }` (mandatory)                     | Foreign pointer; `OnCreate{Name}` hook auto-mints a fresh target via `new`. Hydrates as id only. |
| `[Reference]`         | `partial T? Name { get; set; }` (optional)                | Foreign pointer; pure backing-field setter, getter falls back to `Session.Get<T>(id)` when only the id is cached. |
| `[Reference, Inline]` | either shape above, plus `[Inline]`                       | Owned-sidecar — loader emits `field.*` projection, hydrates the linked record alongside its owner. |
| `[Parent]`            | `partial T Name { get; set; }`                            | Pure backing-field setter; cascade-tracks the child into the parent's session via `parent.Session.AdoptIfUnbound(this)` so `parent.Children` sees it. |
| `[Children]`          | `partial IReadOnlyCollection<T> Name { get; }`            | Reverse-fk traversal via `Session.QueryChildren`                                            |
| `[CompositionRoot]`   | tag on a partial class                                    | Generator grafts per-`[AggregateRoot]` `Load{Root}Async` overloads plus `Schema` / `ApplySchemaAsync` / `ReferenceRegistry` |

Plus aggregate / relation marker attributes:

- `[AggregateRoot]` — marks the root entity. Aggregate membership is computed by
  walking `[Children]` reachability; entities reached from two roots produce CG011.
- Forward / inverse relations are user-declared attribute pairs deriving from
  `ForwardRelation` / `InverseRelation<TForward>` (see `Disruptor.Surface.Sample.Relations`).
  The generator emits a sibling marker class without the `Attribute` suffix
  (`Restricts : IRelationKind`) per forward kind. Within-aggregate relations expose
  `IReadOnlyCollection<IEntity>` reads; cross-aggregate relations expose
  `IReadOnlyCollection<IRecordId>`. Mutations go through `Session.RelateAsync<TKind>(src, tgt, tx)` —
  edges dispatch immediately against the user's transaction; no snapshot diff, no
  buffered intent. No auto-emitted `Add{X}` / `Remove{X}` / `Clear{X}` methods; write
  a one-line `async` domain-verb passthrough if you want one.
- For relations that carry edge data, derive from `ForwardRelation<TPayload>` —
  the generator walks `TPayload`'s public scalar properties and emits a
  `DEFINE FIELD` on the relation table for each. Same scalar mapping as `[Property]`
  fields on entity tables; pass payload data through `session.RelateAsync<TKind>(src,
  tgt, payload, tx)`. Every relation table also gets a
  `DEFINE INDEX … COLUMNS in, out UNIQUE` so duplicate edges are rejected at the
  schema layer.
- `[Reject]`, `[Unset]`, `[Cascade]`, `[Ignore]` on `[Reference]` properties are the
  delete-behavior intent declarations. SurrealDB's schema-level `REFERENCE ON DELETE`
  emission honors them; the in-library cascade planner is currently parked (see Status).

## Key concepts

- **Aggregate.** A `[AggregateRoot]` and everything reachable through `[Children]`.
  The unit of load (`Workspace.Load{Root}Async`). Not a concurrency boundary —
  concurrent writers collide at COMMIT as native `SurrealConflictException` from the
  SDK, regardless of which aggregate they touched.
- **Composition root.** A user-declared partial class tagged `[CompositionRoot]` that
  the generator grafts the per-aggregate `Load*Async` overloads onto. The class is
  yours — ctor, SDK connection, caches, telemetry. The library only adds load methods.
- **`SurrealSession`.** A snapshot-isolated entity store. Reads are sync; sync writes
  (`Track`, property setters, sync `Relate<TKind>`) mutate the in-memory snapshot;
  nothing touches Surreal until you call an async dispatch method (`SaveAsync`,
  `DeleteAsync`, `RelateAsync`, `UnrelateAsync`) against an app-owned `Transaction`.
- **App-owned `Transaction`.** The library never opens or commits transactions. The
  app calls `db.BeginTransactionAsync()` to get a `Disruptor.Surreal.SurrealTransaction`,
  passes the handle into Save/Delete/Relate/Unrelate (and into write-mode loads), and
  calls `tx.CommitAsync()` (or `tx.CancelAsync()`) when its logical unit of work is
  done. Cross-session, cross-aggregate, mixed raw SDK calls — all atomic if they share
  the same txn.
- **Per-entity Save, auto-recursive on the reference graph.**
  `session.SaveAsync(entity, tx)` walks the entity's forward dependencies (`[Reference]`
  / `[Inline]` / `[Parent]`) first, then dispatches the entity itself as a single
  `CREATE/UPDATE record:id CONTENT { ... }` (whole-entity, no per-field SET), then
  walks tracked children. Edges are not part of SaveAsync — they go through
  `RelateAsync<TKind>` against the same transaction, dispatched at the call site so
  there's no buffered intent for the library to drain. The user picks what to save;
  the library does not do change tracking.
- **Track lifecycle.** `session.Track(new T { … })` does `Bind` (wires the entity's
  `_session`) → `Initialize` (mandatory-ref seeding via `OnCreate{Name}` hooks; idempotent
  so a later `SaveAsync` auto-bind doesn't re-mint). Object-initializer writes land
  directly in backing fields — no buffer, no flush. `[Parent]` setters cascade-track via
  `parent.Session.AdoptIfUnbound(this)`, so `new Constraint { Design = design }` joins
  `design`'s session and shows up in `design.Constraints` at Save time.
- **Typed relation kinds + directional reads.** Every forward relation attribute
  (e.g. `RestrictsAttribute`) gets a sibling marker class (`Restricts : IRelationKind`)
  emitted alongside it. `session.RelateAsync<Restricts>(constraint, userStory, tx)` is
  the canonical typed mutation; `Session.QueryOutgoing<Restricts, T>(this)` /
  `Session.QueryIncoming<Restricts, T>(this)` are the explicit-direction reads
  (within-aggregate). Cross-aggregate uses the id-side variants. The marker carries
  the SurrealDB edge name as a `static abstract` property — no string literals in
  user code.
- **Owned-sidecar carve-out.** `[Inline]` paired with `[Reference]` marks a reference as
  owned/compositional — the loader inlines the target's payload via `field.*`. Plain
  `[Reference]` is a foreign pointer, hydrated as an id only. Keeps aggregate boundaries
  honest.
- **Model-scoped runtime.** No process-global state. The user's `[CompositionRoot]`
  partial owns the model metadata (`Workspace.Schema`, `Workspace.ReferenceRegistry`),
  and `Load*Async` constructs sessions with that registry. Multiple Disruptor.Surface-generated
  consumers can coexist in one process without trampling each other.
- **Safe SQL formatting.** All record ids, identifiers, and string literals route through
  `SurrealFormatter` — bare `table:value` when safe, Surreal's `table:⟨value⟩` escape
  when not. Especially relevant if you set `[assembly: RecordIdValue<string>]`.
- **Native concurrency.** SurrealDB v3's transaction MVCC is the concurrency primitive;
  the library does not run a writer lease. Concurrent writers commit independently,
  collide at COMMIT, and surface as `Disruptor.Surreal.SurrealConflictException`. Catch,
  reload, retry.

## Diagnostics

The generator emits CG001-CG028 covering: missing `partial` modifier, duplicate `[Id]`,
`[Children]` element-type mistakes (type-parameter and not-a-`[Table]`),
multiple-aggregate ownership, reference-delete behavior validation, cascade cycles,
dangling-`Ignore` warnings, multiple `[CompositionRoot]` declarations, non-partial
`[CompositionRoot]`, `[Children]` members without a `[Parent]` path back to the
aggregate root, `[Reference]` fields that cross aggregate boundaries, non-partial
annotated members, conflicting role attributes, `[Property]` types without a
SurrealDB scalar mapping, `[Parent]` targets that aren't `[Table]`s, and
`static` annotated members. Full list in
`src/Disruptor.Surface.Generator/Pipeline/Diagnostics.cs` (rendered into the API
reference under `docs/api.md`).

## Status

Functional first cut, post-architectural-pivot to the Disruptor.Surreal SDK. The
`Disruptor.Surface.Sample` harness round-trips Design + Review aggregates against a
live SurrealDB v3 — schema bootstrap, per-entity Save into an app-owned transaction,
reload, in-memory reads, query-layer projections, filtered loads with Fetch top-up.

What's exercised end-to-end: scalar properties, inline element collections (`IReadOnlyList<T>` of records) round-trip, `[Reference]`
mandatory + optional (with inline `field.*` re-hydration), `[Parent]`/`[Children]`,
`OnCreate` hooks, full aggregate hydration, typed relation kinds (forward + inverse,
within-aggregate + cross-aggregate), the unified query surface (predicates, traversals,
edges, `IdsAsync`, `Select(projection)`, `Hydrate.{Table}(ids)`, `Fetch` strict-with-escape),
native `SurrealConflictException` on concurrent commits.

Currently parked: the in-library reference-delete cascade planner (`[Reject]` /
`[Unset]` / `[Cascade]` semantics for `DeleteAsync`) was lost in preview.34's strip;
to be re-anchored against the loaded snapshot. Until then, dependent cleanup is the
caller's responsibility, or you rely on schema-level `REFERENCE ON DELETE` behaviour.

Not production-tested. The Disruptor.Surreal SDK (sibling project at
`../surrealdb-dotnet`), the SurrealQL emission, and the per-entity Save dispatch are
validated against the harness and one Surreal version, not against a load test or a
wide range of edge cases.

## Further reading

- `docs/notes.md` — generator pipeline internals, equatability invariants, emit
  conventions, the same-pass type-resolution gotcha. Required reading before
  changing anything in `src/Disruptor.Surface.Generator`.
- Per-class XML doc on the runtime types (`SurrealSession`, `HydrationValue`,
  `ISaveContext`, `IRelationKind`, `HydrationValue`).
