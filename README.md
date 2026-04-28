# Surface

A C# Roslyn source generator that turns `[Table]`-annotated partial classes into a working
SurrealDB persistence layer — typed ids, snapshot-isolated sessions, generated SurrealQL,
typed relation kinds, cross-process writer coordination.

## At a glance

You write the model:

```csharp
using Surface.Annotations;
using Surface.Runtime;

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
- A `SurrealSession` surface with sync reads (`design.Description`, `design.Constraints`) and mutations queued to a dirty batch
- `SurrealSession.CommitAsync(transport, lease?)` rendering the dirty batch as a single SurrealQL script
- A `Restricts : IRelationKind` marker class per forward relation attribute, so `session.Relate<Restricts>(constraint, userStory)` is type-checked at compile time
- Directional read primitives (`Session.QueryOutgoing<TKind, T>(this)`, `Session.QueryIncoming<TKind, T>(this)`) — no ambiguity on self-referential edges
- `Workspace.Schema` (`IReadOnlyList<string>` of idempotent DDL chunks) and `Workspace.ApplySchemaAsync(transport)` — model-scoped, no process globals
- `Workspace.ReferenceRegistry` carrying the per-model `[Reference]` field metadata (delete behaviour) the commit planner reads through
- Diagnostics (CG001–CG020) that catch model-level mistakes at compile time

The library promise is **minimal intrusion**: the generator never forces a base class, ctor, or
inherited member on you. The `[CompositionRoot]` partial is yours — wire transport, caches,
telemetry, and DI however you want. The library only contributes the load methods.

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

// Write session — caller composes lease + session + commit.
await using var lease = await WriterLease.AcquireAsync(transport, "design");
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
constraint.Restricts(someUserStory);  // user-defined one-liner: => Session.Relate<Restricts>(this, x)

await rw.CommitAsync(transport, lease);
```

## Project layout

```
src/
  Surface.Generator/  — Roslyn source generator (netstandard2.0, analyzer)
  Surface.Runtime/    — runtime library: SurrealSession, IEntity, IRelationKind, RecordId,
                        WriterLease, SurrealHttpClient, CommitPlanner, HydrationJson, …
  Surface.Sample/     — console-app harness that exercises the full pipeline against
                        a live SurrealDB; also the canonical worked example schema
```

`Surface.Sample` references both the generator (as an analyzer, no runtime dep) and the
runtime library. Real consumer projects do the same.

## Building

```sh
dotnet build Surface.sln
```

Generated files land in `src/Surface.Sample/obj/Debug/net9.0/generated/Surface.Generator/Surface.Generator.ModelGenerator/`
— inspect them to see what the generator emitted for a given `[Table]` class.

## Running the harness

The sample is a console app that connects to a live SurrealDB, applies the schema,
seeds three Design aggregates, commits them, then reloads one and prints what came
back. Useful both as a smoke test and as a worked end-to-end example.

```sh
surreal start --bind 127.0.0.1:8000 \
              --default-namespace project-brain \
              --default-database workspace \
              --username root --password secret \
              rocksdb://path/to/db

dotnet run --project src/Surface.Sample
```

Connection parameters are hard-coded in `Program.cs` — adjust them if your Surreal
instance lives elsewhere.

## Authoring conventions

A `[Table]` class must be `partial` and declare exactly one `[Id]` partial property.
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
  `ForwardRelation` / `InverseRelation<TForward>` (see `Surface.Sample.Relations`).
  The generator emits a sibling marker class without the `Attribute` suffix
  (`Restricts : IRelationKind`) per forward kind. Within-aggregate relations expose
  `IReadOnlyCollection<IEntity>` reads; cross-aggregate relations expose
  `IReadOnlyCollection<IRecordId>`. Mutations go through the typed
  `Session.Relate<TKind>(src, tgt)` etc. — no auto-emitted `Add{X}` / `Remove{X}` /
  `Clear{X}` methods; write a one-line domain-verb passthrough if you want one.
- `[Reject]`, `[Unset]`, `[Cascade]`, `[Ignore]` on `[Reference]` properties drive
  the commit-time delete planner. `[Reject]` is the default.

## Key concepts

- **Aggregate.** A `[AggregateRoot]` and everything reachable through `[Children]`.
  The unit of load (`Workspace.Load{Root}Async`) and write coordination (`WriterLease`).
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
  and `Load*Async` constructs sessions with that registry. Multiple Surface-generated
  consumers can coexist in one process without trampling each other.
- **Safe SQL formatting.** All record ids, identifiers, and string literals route through
  `SurrealFormatter` — bare `table:value` when safe, Surreal's `table:⟨value⟩` escape
  when not. Especially relevant if you set `[assembly: RecordIdValue<string>]`.
- **Writer lease.** A per-aggregate row in `writer_lease` with a 5-minute TTL. The
  caller acquires it before committing and passes it into `CommitAsync` for renewal;
  theft surfaces as `WriterLeaseStolenException` so the commit aborts cleanly. Stale
  leases expire and become stealable, capping crash recovery to one TTL window.
- **Commit pipeline.** Recorded commands → `PendingState` (compacted indexed view, plus
  per-field `ReferenceTransition` snapshots) → `CommitPlanner` (three-phase reference-
  delete resolve: cascade/unset to fixpoint, then collect Reject blockers) →
  `SurrealCommandEmitter` (single SurrealQL script). One transport call per commit.

## Diagnostics

The generator emits CG001–CG020 covering: missing `partial` modifier, missing /
duplicate `[Id]`, invalid relation method shape, `[Children]` element-type mistakes,
multiple-aggregate ownership, reference-delete behavior validation, cascade cycles,
dangling-`Ignore` warnings, multiple `[CompositionRoot]` declarations, and
non-partial `[CompositionRoot]`, and `[Children]` member without a `[Parent]` path
back to the aggregate root. Full list in
`src/Surface.Generator/Pipeline/Diagnostics.cs`.

## Status

Functional first cut. The `Surface.Sample` harness round-trips Design aggregates
against a live SurrealDB instance — schema bootstrap, lease acquisition, commit,
reload, in-memory reads — and is the validation harness for the SurrealQL paths.

What's exercised end-to-end: scalar properties, `[Reference]` mandatory + optional
(with inline `field.*` re-hydration), `[Parent]`/`[Children]`, OnCreate hooks,
writer lease, full aggregate hydration, typed relation kinds.

What's defined but not yet exercised in the harness: `SurrealArray<T>` round-trip,
within-aggregate and cross-aggregate edge mutations beyond `Restricts`, `[Cascade]`
reference-delete chains, Review aggregate, `WriterLeaseStolenException` recovery flow.

Not production-tested. The Surreal HTTP client, SurrealQL emission, and writer lease
are validated against the harness and one Surreal version, not against a load test
or a wide range of edge cases.

## Further reading

- `CLAUDE.md` — generator pipeline internals, equatability invariants, emit
  conventions, the same-pass type-resolution gotcha. Required reading before
  changing anything in `src/Surface.Generator`.
- Per-class XML doc on the runtime types (`SurrealSession`, `WriterLease`,
  `CommitPlanner`, `PendingState`, `IRelationKind`, `HydrationJson`).
