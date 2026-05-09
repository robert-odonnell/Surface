# Disruptor.Surface

A C# Roslyn source generator that turns `[Table]`-annotated partial classes into a working
SurrealDB persistence layer — typed ids, snapshot-isolated sessions, generated SurrealQL,
typed relation kinds, cross-process writer coordination.

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
- A `Workspace.LoadDesignAsync(transport, id, ct)` instance method (grafted onto your `[CompositionRoot]` partial) that hydrates the whole aggregate from SurrealDB in a single nested-`SELECT`
- A unified query surface with five terminal verbs sharing one AST — `…IdsAsync(transport)` for id-only selection (`IReadOnlyList<{Table}Id>`), `…Select(projection).ExecuteAsync(transport)` for projection rows (immutable user-defined `TRow`), `…ExecuteAsync(transport)` for hydrated entities, `…WithId(id).IncludeConstraints(c => c.Where(...)).LoadAsync(transport, lease)` for filtered write-mode loads of an aggregate root, and `Workspace.Hydrate.{Table}(ids).WithInclude(...).ExecuteAsync(transport, [lease])` to materialise a non-aggregate-shaped slice into a tracked session. Plus `Workspace.Query.Edges.Restricts.WhereIn(...).ExecuteAsync(transport)` for flat edge pairs. Strict-with-escape on filtered loads — unloaded slices throw `LoadShapeViolationException`, `session.FetchAsync(...)` extends the slice in place
- A future-direction `ISurrealExecutor` + `SurrealCommand` boundary alongside the legacy `ISurrealTransport` — both production transports (`SurrealHttpClient`, `SurrealEmbeddedTransport`) implement both
- A `SurrealSession` surface with sync reads (`design.Description`, `design.Constraints`) and mutations queued to a dirty batch
- `SurrealSession.CommitAsync(transport, lease?)` rendering the dirty batch as a single SurrealQL script
- A `Restricts : IRelationKind` marker class per forward relation attribute, so `session.Relate<Restricts>(constraint, userStory)` is type-checked at compile time
- Directional read primitives (`Session.QueryOutgoing<TKind, T>(this)`, `Session.QueryIncoming<TKind, T>(this)`) — no ambiguity on self-referential edges
- `Workspace.Schema` (`IReadOnlyList<string>` of idempotent DDL chunks) and `Workspace.ApplySchemaAsync(transport)` — model-scoped, no process globals
- `Workspace.ReferenceRegistry` carrying the per-model `[Reference]` field metadata (delete behaviour) the commit planner reads through
- Diagnostics (CG001-CG028) that catch model-level mistakes at compile time

The library promise is **minimal intrusion**: the generator never forces a base class, ctor, or
inherited member on you. The `[CompositionRoot]` partial is yours — wire transport, caches,
telemetry, and DI however you want. The library only contributes the load methods.

## Documentation

- [Introduction](docs/intro.md)
- [Quickstart](docs/quickstart.md)
- [API reference](docs/api.md)
- [Architecture](docs/architecture.md)

## Quick start

```csharp
using var http = new HttpClient();
await using var transport = new SurrealHttpClient(SurrealConfig.Default(), http);

// Bootstrap (idempotent — every run is fine). One transport call per chunk so a
// failure pinpoints which one broke. Iterate `Workspace.Schema` directly when you
// want to filter, run in custom transactions, or log per-chunk progress.
await Workspace.ApplySchemaAsync(transport);

// Construct your composition root once at startup. The generator grafts the Load*Async
// methods onto your partial — ctor + transport plumbing are entirely yours.
var workspace = new Workspace();

// Read session — snapshot, no commit.
var ro = await workspace.LoadDesignAsync(transport, designId);
foreach (var c in ro.Get<Design>(designId)!.Constraints)
    Console.WriteLine($"{c.Id}: {c.Description}");

// Surgical read — no aggregate hydration, no session, runs the predicate
// straight on SurrealDB. Use `.IdsAsync(transport)` for an `IReadOnlyList<ConstraintId>`
// when you only need the ids, or `.Select(projection).ExecuteAsync(transport)` for
// typed row DTOs.
var matches = await Workspace.Query.Constraints
    .Where(ConstraintQ.Description.Contains("security"))
    .ExecuteAsync(transport);

// Write session — caller composes lease + session + commit.
await using var lease = await workspace.AcquireWriterAsync(transport);
var rw = await workspace.LoadDesignAsync(transport, designId);

var design = rw.Get<Design>(designId)!;
design.Description = "edited";

// New entities are tracked explicitly. Object-initializer values (Description, Details)
// buffer on the entity until Track binds it, then flush into the session's dirty batch.
var constraint = rw.Track(new Constraint
{
    Design = design,
    Description = "no negatives",
    Details = new Details { Header = "rule" }
});
constraint.Restricts(someUserStory); // user-defined one-liner: => Session.Relate<Restricts>(this, x)

await rw.CommitAsync(transport, lease);
```

## Project layout

```
src/
  Disruptor.Surface.Generator/             — Roslyn source generator (netstandard2.0, analyzer)
  Disruptor.Surface.Runtime/               — runtime core: SurrealSession, IEntity, IRelationKind,
                                             RecordId, WriterLease, CommitPlanner, HydrationJson,
                                             ISurrealTransport, SurrealException, …
                                             No transport implementation lives here — pick one of
                                             the sibling packages below (or implement your own).
  Disruptor.Surface.Transport.Http/        — over-the-network transport. Talks to a remote
                                             SurrealDB via /rpc + JSON-RPC. The default for
                                             multi-host / multi-process deployments.
  Disruptor.Surface.Transport.Embedded/    — in-process transport backed by SurrealDB embedded
                                             with a RocksDB file store. Side-steps the HTTP body-
                                             size ceiling that bites large commits (code-index
                                             full rebuilds, bulk imports). Single-process only.
  Disruptor.Surface.Sample/                — console-app harness that exercises the full pipeline
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
`LoadShapeViolationException`, `FetchAsync` top-up, the four flavors of relation
traversal include, and lease-theft recovery. Useful both as a smoke test and as a
worked end-to-end example.

```sh
surreal start --bind 127.0.0.1:8000 \
              --default-namespace project-brain \
              --default-database workspace \
              --username root --password secret \
              rocksdb://path/to/db

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
| `[Property]`          | `partial T Name { get; set; }`                            | Backing field + setter that buffers/forwards via `__WriteField`. Get-only also legal.       |
| `[Property]`          | `partial SurrealArray<T> Name { get; }`                   | Inline `array<object>` column with mutation-aware wrapper                                   |
| `[Reference]`         | `partial T Name { get; }` (mandatory)                     | Foreign pointer; `OnCreate{Name}` hook auto-mints a fresh target via `new`. Hydrates as id only. |
| `[Reference]`         | `partial T? Name { get; set; }` (optional)                | Foreign pointer; setter routes through `__WriteField`/`__ClearField` with `FieldKind.Reference`. |
| `[Reference, Inline]` | either shape above, plus `[Inline]`                       | Owned-sidecar — loader emits `field.*` projection, hydrates the linked record alongside its owner. |
| `[Parent]`            | `partial T Name { get; set; }`                            | `Session.GetParent<T>(this)` + `__WriteField(..., FieldKind.Parent)`                        |
| `[Children]`          | `partial IReadOnlyCollection<T> Name { get; }`            | Reverse-fk traversal via `Session.QueryChildren`                                            |
| `[CompositionRoot]`   | tag on a partial class                                    | Generator grafts per-`[AggregateRoot]` `Load{Root}Async` plus `Schema` / `ApplySchemaAsync` / `ReferenceRegistry` |

Plus aggregate / relation marker attributes:

- `[AggregateRoot]` — marks the root entity. Aggregate membership is computed by
  walking `[Children]` reachability; entities reached from two roots produce CG011.
- Forward / inverse relations are user-declared attribute pairs deriving from
  `ForwardRelation` / `InverseRelation<TForward>` (see `Disruptor.Surface.Sample.Relations`).
  The generator emits a sibling marker class without the `Attribute` suffix
  (`Restricts : IRelationKind`) per forward kind. Within-aggregate relations expose
  `IReadOnlyCollection<IEntity>` reads; cross-aggregate relations expose
  `IReadOnlyCollection<IRecordId>`. Mutations go through the typed
  `Session.Relate<TKind>(src, tgt)` etc. — no auto-emitted `Add{X}` / `Remove{X}` /
  `Clear{X}` methods; write a one-line domain-verb passthrough if you want one.
- For relations that carry edge data, derive from `ForwardRelation<TPayload>` —
  the generator walks `TPayload`'s public scalar properties and emits a
  `DEFINE FIELD` on the relation table for each. Same scalar mapping as `[Property]`
  fields on entity tables; pass payload data through `session.Relate<TKind>(src,
  tgt, payload)`. Every relation table also gets a
  `DEFINE INDEX … COLUMNS in, out UNIQUE` so duplicate edges are rejected at the
  schema layer.
- `[Reject]`, `[Unset]`, `[Cascade]`, `[Ignore]` on `[Reference]` properties drive
  the commit-time delete planner. `[Reject]` is the default.

## Key concepts

- **Aggregate.** A `[AggregateRoot]` and everything reachable through `[Children]`.
  The unit of load (`Workspace.Load{Root}Async`). Write coordination is workspace-wide
  — a single `WriterLease` gates commits across all aggregates, not per-aggregate.
- **Composition root.** A user-declared partial class tagged `[CompositionRoot]` that
  the generator grafts the per-aggregate `Load*Async` instance methods onto. The class
  is yours — ctor, transport, caches, telemetry. The library only adds load methods.
- **`SurrealSession`.** A snapshot-isolated entity store + dirty batch. Reads are sync;
  mutations queue commands; nothing touches Surreal until `CommitAsync`. Same class
  covers read and write sessions — it knows nothing about persistence permissions.
- **Track lifecycle.** `session.Track(new T { … })` does `Bind` (wires the entity's
  `_session`) → records `Create` → `Initialize` (mandatory-ref seeding) → `Flush`
  (drains object-initializer writes that were buffered while the entity was unbound).
  `SetField` cascades into `Track` for any `IEntity` value, so nested object
  initialisers auto-track without an explicit `Track` per fresh ref.
- **Typed relation kinds + directional reads.** Every forward relation attribute
  (e.g. `RestrictsAttribute`) gets a sibling marker class (`Restricts : IRelationKind`)
  emitted alongside it. `session.Relate<Restricts>(constraint, userStory)` is the
  canonical typed mutation; `Session.QueryOutgoing<Restricts, T>(this)` /
  `Session.QueryIncoming<Restricts, T>(this)` are the explicit-direction reads.
  The marker carries the SurrealDB edge name as a static property; no string literals in user code.
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
- **Writer lease.** Optimistic CAS on a monotonic `seq` counter. **Single-writer
  paradigm** — one `writer_lease:main` row per workspace gates every commit, regardless
  of which aggregate is being touched; concurrent acquirers race for the same lease.
  `workspace.AcquireWriterAsync(transport)` reads the current `seq` and captures it on
  the lease; `CommitAsync` splices a transactional CAS check + `seq + 1` upsert around
  the data writes — atomic with the data, throws `WriterLeaseStolenException` if
  another writer advanced `seq` first. No TTL, no holder id, no theft-recovery timer,
  no aggregate slug; crashed writers leave their captured seq in memory only and the
  next acquirer reads the current seq fresh. Suits the library's one-shot session
  character: load → mutate → commit happens fast enough that races are rare and
  retries are cheap.
- **Commit pipeline.** Recorded commands → `PendingState` (compacted indexed view, plus
  per-field `ReferenceTransition` snapshots) → `CommitPlanner` (three-phase reference-
  delete resolve: cascade/unset to fixpoint, then collect Reject blockers) →
  `SurrealCommandEmitter` (single SurrealQL script). One transport call per commit.

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

Functional first cut. The `Disruptor.Surface.Sample` harness round-trips Design + Review
aggregates against a live SurrealDB instance — schema bootstrap, lease acquisition,
commit, reload, in-memory reads, query-layer projections, filtered loads with Fetch
top-up, lease-theft detection — and is the validation harness for the SurrealQL paths.

What's exercised end-to-end: scalar properties, `SurrealArray<T>` round-trip, `[Reference]`
mandatory + optional (with inline `field.*` re-hydration), `[Parent]`/`[Children]`,
`OnCreate` hooks, writer-lease acquisition + theft detection, full aggregate hydration,
typed relation kinds (forward + inverse, within-aggregate + cross-aggregate), the
unified query surface (predicates, traversals, edges, `IdsAsync`, `Select(projection)`,
`Hydrate.{Table}(ids)`, `Fetch` strict-with-escape).

What's defined but not yet exercised in the harness: `[Cascade]` reference-delete
chains end-to-end, `WriterLeaseStolenException` reload-and-retry flow.

Not production-tested. The Surreal HTTP client, SurrealQL emission, and writer lease
are validated against the harness and one Surreal version, not against a load test
or a wide range of edge cases.

## Further reading

- `CLAUDE.md` — generator pipeline internals, equatability invariants, emit
  conventions, the same-pass type-resolution gotcha. Required reading before
  changing anything in `src/Disruptor.Surface.Generator`.
- Per-class XML doc on the runtime types (`SurrealSession`, `WriterLease`,
  `CommitPlanner`, `PendingState`, `IRelationKind`, `HydrationJson`).
