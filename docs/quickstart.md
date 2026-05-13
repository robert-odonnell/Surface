# Quickstart

This guide builds a small Disruptor.Surface model, applies its generated SurrealDB schema, creates data, dispatches it inside an app-owned transaction, reloads it, and adds a relation. By the end you'll have a working `Design` aggregate persisted to SurrealDB.

> **Just want to see it run?** From a checkout of this repository, with SurrealDB started on `127.0.0.1:8000`, run `dotnet run --project src/Disruptor.Surface.Sample`. The bundled sample applies its schema, seeds data, reloads it, and exercises the full query layer — useful as a reference once you're working on your own model. Steps 1–10 below walk through building your own version.

The doc uses one canonical model — `Design`, `Constraint`, `Details` — across every section, so you can copy-paste through the steps without juggling fixtures.

## 1. Add The Packages

In a consumer project, reference the runtime package normally and the generator as a private analyzer dependency:

```xml
<ItemGroup>
  <PackageReference Include="Disruptor.Surface.Runtime" Version="0.1.0-preview.*" />
  <PackageReference Include="Disruptor.Surface.Generator" Version="0.1.0-preview.*" PrivateAssets="all" />
</ItemGroup>
```

(See [`notes.md`](notes.md) for the latest preview tag; the public API is still moving.)

When working against this repository directly, use project references like the sample project:

```xml
<ItemGroup>
  <ProjectReference Include="..\Disruptor.Surface.Runtime\Disruptor.Surface.Runtime.csproj" />
  <ProjectReference Include="..\Disruptor.Surface.Generator\Disruptor.Surface.Generator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

The runtime depends on `Disruptor.Surreal` (the SurrealDB SDK — CBOR over WebSocket) and `Ulid`; both are pulled in transitively. There is no in-library transport — you connect once via the SDK and pass the `Surreal` (read-only) or `Transaction` (write-mode) handle into the generated load methods.

## 2. Declare A Model

Create partial classes and mark the persistent members:

```csharp
using Disruptor.Surface.Annotations;
using Disruptor.Surface.Runtime;

namespace MyApp.Model;

[Table, AggregateRoot]
public partial class Design
{
    [Id] public partial DesignId Id { get; set; }

    [Property] public partial string Title { get; set; }

    // [Reference, Inline, Cascade]:
    //   [Reference]  — record link to another table.
    //   [Inline]     — hydrate the linked row alongside the owner (one query, not two).
    //   [Cascade]    — when the design is deleted, delete the Details row too.
    [Reference, Inline, Cascade] public partial Details? Details { get; set; }

    [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
}

[Table]
public partial class Constraint
{
    [Id] public partial ConstraintId Id { get; set; }

    [Parent] public partial Design Design { get; set; }
    [Property] public partial string Description { get; set; }
}

[Table]
public partial class Details
{
    [Property] public partial string Summary { get; set; }
}
```

Add one composition root class:

```csharp
using Disruptor.Surface.Annotations;

namespace MyApp.Model;

[CompositionRoot]
public partial class Workspace
{
}
```

Build the project. The generator will emit typed ids, property bodies, schema metadata, and two `Workspace.LoadDesignAsync(...)` overloads (one taking `Surreal db`, one taking `Transaction tx`).

## 3. Connect To SurrealDB

Start SurrealDB. The sample harness expects this shape:

```sh
surreal start --bind 127.0.0.1:8000 \
              --default-namespace project-brain \
              --default-database workspace \
              --username root --password secret \
              rocksdb://path/to/db
```

Open a connection. The library has no transport of its own — the SDK's `Surreal` handle is the wire:

```csharp
using Disruptor.Surreal;
using Disruptor.Surreal.Connection;
using MyApp.Model;

await using var db = await SurrealClient.ConnectAsync(SurrealOptions.Parse(
    "Url=ws://127.0.0.1:8000;Namespace=project-brain;Database=workspace;User=root;Password=secret"));
```

`SurrealOptions.Parse` accepts the standard semicolon-separated key/value form; build one programmatically via the `SurrealOptions` constructor if you'd rather not parse a string.

## 4. Apply The Schema

Generated schema chunks are idempotent, so applying them at startup is safe:

```csharp
await Workspace.ApplySchemaAsync(db);
```

For custom logging, filtering, or bundling under your own transaction, iterate chunks directly:

```csharp
foreach (var chunk in Workspace.Schema)
{
    await db.QueryAsync(chunk);
}
```

A `Transaction`-taking overload (`Workspace.ApplySchemaAsync(tx)`) is also emitted if you want schema application to participate in a larger atomic boot sequence.

## 5. Create And Dispatch Data

Construct a session, track new entities, then dispatch into an app-owned transaction. `Workspace.ReferenceRegistry` is a static accessor the generator grafts onto your `[CompositionRoot]` partial — no need to instantiate `Workspace` to get at it.

```csharp
var session = new SurrealSession(Workspace.ReferenceRegistry);

var design = session.Track(new Design
{
    Title = "First design",
    Details = new Details { Summary = "Initial draft" }
});

var constraint = session.Track(new Constraint
{
    Design = design,
    Description = "Must support one-shot commits"
});

await using var tx = await db.BeginTransactionAsync();
// Per-entity Save: design's emitted SaveAsync auto-recurses through forward refs
// (Details), then through tracked children (Constraints). One call dispatches the
// whole subgraph into the open transaction.
await session.SaveAsync(design, tx);
await tx.CommitAsync();
```

Object-initializer values (`Title`, `Details`) buffer on the entity until `Track` binds it to the session; nested entity values like `Details` are auto-tracked when written through a reference field. The library does no change tracking — `SaveAsync(thing)` is the user's explicit choice of what to save.

**You should see:** the transaction commits without error. In a SurrealDB shell, `SELECT * FROM designs;` returns one row with a `designs:01J…` (ulid) id; `SELECT * FROM constraints;` returns one row whose `design` field points at it; `SELECT * FROM details;` returns the inline `Details` sidecar.

### Common first-time stumbles

- **`SurrealConflictException` at `CommitAsync`.** Another writer's commit landed first inside the same MVCC window. Reload the affected aggregate, reapply your intent, retry — the transaction auto-cancels on dispose.
- **`LoadShapeViolationException` at a property read.** You're using a filtered `LoadAsync` and reading a slice you didn't `Include`. See [step 9](#9-filtered-loads-and-top-up-fetch) for the recovery pattern (`session.FetchAsync(...)`).
- **`CG022: '…' carries [Property] but is not declared partial`.** Every annotated property must be `partial`. The generator emits the implementation half (backing field, getter/setter, hydrate body), so the user-side declaration must use the keyword. Same applies to `[Id]`, `[Reference]`, `[Parent]`, `[Children]`.
- **`Workspace.LoadDesignAsync` doesn't exist.** Make sure `Workspace` is declared `partial` and tagged with `[CompositionRoot]`; the generator grafts the method onto your partial declaration. CG019 fires if it isn't partial.

## 6. Load And Edit Data

Use the generated composition-root load method for aggregate reads. Pass `db` for read-only, `tx` for write-mode (so the load query sees in-txn writes from the same transaction):

```csharp
var workspace = new Workspace();
var readSession = await workspace.LoadDesignAsync(db, design.Id);

var loaded = readSession.Get<Design>(design.Id)
    ?? throw new InvalidOperationException("Design did not hydrate.");

Console.WriteLine(loaded.Title);
foreach (var item in loaded.Constraints)
{
    Console.WriteLine(item.Description);
}
```

**You should see:** the title plus each constraint description, printed in load order. If `readSession.Get<Design>(design.Id)` returns `null`, the id you passed doesn't exist in the database — check the value committed in step 5.

To edit, open a transaction, load inside it, mutate the generated properties, save, commit:

```csharp
await using var tx = await db.BeginTransactionAsync();
var writeSession = await workspace.LoadDesignAsync(tx, design.Id);
var editable = writeSession.Get<Design>(design.Id)!;

editable.Title = "Updated design";

await writeSession.SaveAsync(editable, tx);
await tx.CommitAsync();
```

A session is a snapshot-isolated entity store. The same `SurrealSession` instance can be reused for further reads (its in-memory snapshot stays valid) — but write dispatch always goes through an open `Transaction`. Aggregates from multiple sessions can be saved into the same `tx` for cross-aggregate atomicity.

## 7. Query Without Loading An Aggregate

For workloads that don't fit the "load the whole aggregate" shape — UI lists, search, dashboards, single-field updates — the generated query layer offers terminal verbs on one shared AST. Each `[Table]` gets a sibling predicate factory (`{Name}Q`) and a query root on `Workspace.Query`. Three terminals you'll reach for first:

```csharp
// 1. Hydrated entity reads — full row data, tracked in an opaque internal session.
var matches = await Workspace.Query.Constraints
    .Where(ConstraintQ.Description.Contains("commit"))
    .ExecuteAsync(db);
foreach (var c in matches)
    Console.WriteLine($"{c.Id}: {c.Description}");

// 2. Id-only selection — IReadOnlyList<ConstraintId>, no entity hydration.
var ids = await Workspace.Query.Constraints
    .Where(ConstraintQ.Description.Contains("commit"))
    .OrderBy(ConstraintQ.Description)
    .Limit(50)
    .IdsAsync(db);

// 3. Projection — typed DTOs, only the columns the projection's lambda reads travel
//    back. The library doesn't generate projection types; you own the record + the
//    materialiser lambda. See api.md for the full ISurfaceProjection<T> mechanism.
public sealed record ConstraintRow(ConstraintId Id, string? Description);

var rows = await Workspace.Query.Constraints
    .Where(ConstraintQ.Description.Contains("commit"))
    .Select(SurfaceProjection.For<ConstraintRow>(row => new ConstraintRow(
        Id:          new ConstraintId(row.Read(ConstraintQ.Id).Value),
        Description: row.Read(ConstraintQ.Description))))
    .ExecuteAsync(db);
```

Predicate operators on `PropertyExpr<T>`: `Eq`, `Lt` / `Le` / `Gt` / `Ge`, `In(...)`, plus the string-only `Contains` extension. Compose with `Predicate.And` / `Or` / `Not`. Server-side `OrderBy` / `ThenBy` / `Limit` / `Start` chain on every terminal — they render as `ORDER BY … LIMIT … START …` at the tail of the SurrealQL.

Every terminal accepts either `Surreal db` (read-only) or `Transaction tx` (in-txn read with full visibility into pending writes). Pass `tx` whenever a query needs to see uncommitted state from the same transaction.

Pin a single record by id and pull traversed slices in the same call:

```csharp
// IncludeDetails() expands the [Reference, Inline] sidecar via SurrealQL's field.* projection.
// IncludeConstraints(c => …) runs a nested SELECT scoped to the parent design.
var rows = await Workspace.Query.Designs
    .WithId(design.Id)
    .IncludeDetails()
    .IncludeConstraints(c => c
        .Where(ConstraintQ.Description.Contains("commit")))
    .ExecuteAsync(db);

var loaded = rows.Single();
Console.WriteLine(loaded.Details?.Summary);
foreach (var c in loaded.Constraints)
    Console.WriteLine(c.Description);
```

`Include*` methods exist for every `[Children]` collection, every `[Reference, Inline]` field, and every relation (covered in step 10). Single-target traversals take a `configure` lambda so you can filter the target side; multi-target / cross-aggregate traversals are leaves with no lambda.

Once you've added a relation in step 10, edges get their own query root for flat `(source, target)` pair reads — `Workspace.Query.Edges.{Kind}.WhereIn(...).ExecuteAsync(db)`. Useful when you want the edges without hydrating either endpoint. See [`api.md`](api.md#edge-queries--workspacequeryedgeskind) for the full shape.

## 8. Hydrate Specific Ids Into A Tracked Session

`Workspace.Hydrate.{Table}(ids)` pairs with `IdsAsync` for the `select-ids → hydrate → mutate → save` flow. Use it when the slice you want to mutate isn't aggregate-shaped — search results, paged lists, batch updates over a filtered set:

```csharp
// 1. Select — id-only.
var ids = await Workspace.Query.Constraints
    .Where(ConstraintQ.Description.Contains("commit"))
    .Limit(20)
    .IdsAsync(db);

// 2. Hydrate — materialise into a tracked session. Pass `db` for read-only or
//    `tx` if downstream mutations should participate in an existing transaction.
await using var tx = await db.BeginTransactionAsync();
var session = await Workspace.Hydrate.Constraints(ids).ExecuteAsync(tx);

// 3. Mutate (sync, in-memory).
foreach (var c in session.GetAll<Constraint>())
    c.Description = c.Description + " (reviewed)";

// 4. Dispatch + commit.
foreach (var c in session.GetAll<Constraint>())
    await session.SaveAsync(c, tx);
await tx.CommitAsync();
```

Two overloads per table — typed `{Table}Id` for the ergonomic call site, raw `IRecordId` for cross-aggregate edge endpoints already collapsed to canonical record ids. An empty-id list short-circuits to an empty session with no wire call.

The wire SQL is identical to what `SurfaceQuery<T>.Where(IdIn).WithInclude(…).ExecuteAsync` would emit. See [`api.md`](api.md#hydration--workspacehydratetableids) for the full `WithInclude` mechanism.

## 9. Filtered Loads And Top-Up Fetch

Switch the terminal verb from `ExecuteAsync` to `LoadAsync` to get a session for the same query AST. Aggregate-root tables only — non-roots compile-error if you try.

```csharp
await using var tx = await db.BeginTransactionAsync();
var session = await Workspace.Query.Designs
    .WithId(design.Id)
    .IncludeDetails()
    .LoadAsync(tx);

var d = session.Get<Design>(design.Id)!;
Console.WriteLine(d.Details?.Summary);     // works — IncludeDetails was called
```

Reads against slices that *weren't* included throw `LoadShapeViolationException`. The exception's message points at `session.FetchAsync(...)` — a top-up extension query that hydrates additional slices into the existing session:

```csharp
try
{
    var constraints = d.Constraints;        // throws — IncludeConstraints wasn't called
}
catch (LoadShapeViolationException)
{
    await session.FetchAsync(
        Workspace.Query.Designs.WithId(d.Id).IncludeConstraints(),
        tx);
    var constraints = d.Constraints;        // works — slice now loaded
}
```

`Fetch` is a slice widener: if you've mutated `d.Title` in memory and Fetch re-hydrates the same row, your edit gets overwritten with the DB value. Save first, or accept the clobber. (The per-field "did the user touch this" tracking was deliberately removed under the explicit-Save model.)

## 10. Add Relations

Edges in SurrealDB are first-class — Disruptor.Surface models them as typed *relation kinds* with concrete *variant* classes. The three pieces are: a forward / inverse attribute pair (the kind), a partial class per `(source, target)` shape (the variant), and the read-side properties on the entity types that participate.

**Step 1 — declare the kind.** A forward / inverse attribute pair: the forward is the "writeable side" you'll apply to variants, the inverse names the read side from the target's perspective. You need both even if you only ever read one direction; the inverse is what makes `UserStory.Restrictions` (the target-side collection) a valid declaration.

```csharp
using Disruptor.Surface.Annotations;

namespace MyApp.Model;

public sealed class RestrictsAttribute   : ForwardRelation;
public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;
```

**Step 2 — extend the canonical model.** Add a `UserStory` child, retrofit `Design` and `Constraint` with read-side relation properties, and declare the variant class:

```csharp
using Disruptor.Surface.Annotations;
using Disruptor.Surface.Runtime;

namespace MyApp.Model;

[Table, AggregateRoot]
public partial class Design
{
    [Id] public partial DesignId Id { get; set; }

    [Property] public partial string Title { get; set; }
    [Reference, Inline, Cascade] public partial Details? Details { get; set; }

    [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
    [Children] public partial IReadOnlyCollection<UserStory>  UserStories { get; }   // NEW
}

[Table]
public partial class Constraint
{
    [Id] public partial ConstraintId Id { get; set; }

    [Parent]   public partial Design Design { get; set; }
    [Property] public partial string Description { get; set; }

    // Source-side read property — pulls UserStory targets via the Restricts edge.
    [Restricts] public partial IReadOnlyCollection<UserStory> Restrictions { get; }     // NEW
}

[Table]
public partial class UserStory
{
    [Id] public partial UserStoryId Id { get; set; }

    [Parent]   public partial Design Design { get; set; }
    [Property] public partial string Summary { get; set; }

    // Target-side read property — pulls Constraints that restrict this story.
    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }
}

// Variant class: one per concrete (source-table, target-table) shape.
[Restricts]
public partial class ConstraintRestrictsUserStory
{
    [In]  public partial Constraint Source { get; set; }
    [Out] public partial UserStory  Target { get; set; }
}
```

`Source` and `Target` are the convention used throughout the sample, but the property *names* are free — only the `[In]` / `[Out]` attributes are load-bearing. Pick whatever reads at the call site (e.g. `Restrictor` / `RestrictedStory` if you prefer domain-shaped names).

**Step 3 — at runtime, save the variant.** The variant **is** an entity; `session.SaveAsync` is the write path:

```csharp
await session.SaveAsync(
    new ConstraintRestrictsUserStory { Source = constraint, Target = userStory },
    tx);
```

The generator emits a `Restricts : IRelationKind` marker class (with a `static abstract string EdgeName`), a per-kind `RestrictsId` typed-id struct, and an `INSERT RELATION INTO {edge}` dispatch inside the variant's `IEntity.SaveAsync` body. Re-saving the same `(source, target)` pair refreshes payload or no-ops if payloadless — there's a `UNIQUE` index on `(in, out)` keeping duplicates out at the schema layer.

Delete an edge with `await session.UnrelateAsync<Restricts>(src, tgt, tx)`. Either endpoint can be `null` for bulk delete (every outgoing edge from `src`, every incoming edge to `tgt`). Sync entity-side reads — `constraint.Restrictions`, `userStory.Restrictions` — pull from the in-session edge index populated at load time and refreshed by `SaveAsync`.

**Payload columns.** Add `[Property]` members to a variant class to carry per-edge data:

```csharp
[Calls]
public partial class CodeSymbolCallsCodeSymbol
{
    [In]       public partial CodeSymbolId Source { get; set; }
    [Out]      public partial CodeSymbolId Target { get; set; }
    [Property] public partial string Confidence { get; set; }
}
```

**Advanced relation shapes** are covered in [`api.md`](api.md):

- **Multiple variants per kind** — different `(source, target)` types under the same forward attribute. Schema flips to `SCHEMALESS`, hydration discriminates by `(in.tb, out.tb)`.
- **Union endpoints** — one variant whose endpoint accepts any of N participating tables. See [api.md "Union endpoints"](api.md#union-endpoints--one-variant-for-multiple-target-tables).
- **Shared-shape relation interfaces** — a `Create<TKind>` factory across kinds with the same payload shape, plus per-property merge so empty-body variants (`[Calls] partial class CallsRelation : ICodeSymbolEdge;`) inherit shape from the interface contract. See [api.md "Shared-shape relation interfaces"](api.md#shared-shape-relation-interfaces--kind-keyed-createtkind-factory--per-property-merge).
- **Async variant query terminals** — substrate-fresh reads via `session.QueryVariantsOutgoingAsync<TVariant>(...)` and friends, with the endpoint-resolution caveat. See [api.md "Runtime calls"](api.md#runtime-calls).

## 11. Run The Repository Sample

The bundled sample exercises everything above against a live SurrealDB v3:

```sh
dotnet run --project src/Disruptor.Surface.Sample
```

It applies the schema, seeds ten Design aggregates plus a Review against one of them, reloads them, and runs the full query layer — predicates, traversals, edges, filtered `LoadAsync`, `LoadShapeViolationException` recovery via `FetchAsync`, and all four shapes of relation include. Useful as a reference for "how does the real thing look at scale" once your own model is running.

## Where next?

- [**`api.md`**](api.md) — full reference for every attribute, generated type, and runtime surface. The natural next read once you've built your first model.
- [**`architecture.md`**](architecture.md) — generator pipeline and the incremental-generator contract. Read this if you're going to change the library itself.
- [**`notes.md`**](notes.md) — engineering log and current preview tag.
