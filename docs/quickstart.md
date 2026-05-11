# Quickstart

This guide builds a small Disruptor.Surface model, applies its generated SurrealDB schema, creates data, dispatches it inside an app-owned transaction, and reloads it.

## 1. Add The Packages

In a consumer project, reference the runtime package normally and the generator as a private analyzer dependency:

```xml
<ItemGroup>
  <PackageReference Include="Disruptor.Surface.Runtime" Version="0.1.0-preview.37" />
  <PackageReference Include="Disruptor.Surface.Generator" Version="0.1.0-preview.37" PrivateAssets="all" />
</ItemGroup>
```

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

Construct a session, track new entities, then dispatch into an app-owned transaction:

```csharp
var workspace = new Workspace();
var session = new SurrealSession(Workspace.ReferenceRegistry);

var design = session.Track(new Design
{
    Title = "First design",
    Details = new Details { Summary = "Initial draft" }
});

var constraint = session.Track(new Constraint
{
    Design = design,
    Text = "Must support one-shot commits"
});

await using var tx = await db.BeginTransactionAsync();
// Per-entity Save: design's emitted SaveAsync auto-recurses through forward refs
// (Details), then through tracked children (Constraints). One call dispatches the
// whole subgraph into the open transaction.
await session.SaveAsync(design, tx);
await tx.CommitAsync();
```

Object-initializer values (`Title`, `Details`) buffer on the entity until `Track` binds it to the session; nested entity values like `Details` are auto-tracked when written through a reference field. The library does no change tracking — `SaveAsync(thing)` is the user's explicit choice of what to save.

If `tx.CommitAsync` throws `Disruptor.Surreal.SurrealConflictException`, another writer's commit landed first inside the same MVCC window. Reload, reapply your intent, and retry — the transaction auto-cancels on dispose.

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
    Console.WriteLine(item.Text);
}
```

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

For workloads that don't fit the "load the whole aggregate" shape — UI lists, search, dashboards, single-field updates — the generated query layer offers terminal verbs on one shared AST. Each `[Table]` gets a sibling predicate factory (`{Name}Q`) and a query root on `Workspace.Query`:

```csharp
// 1. Hydrated entity reads — full row data, tracked in an opaque internal session.
var matches = await Workspace.Query.Constraints
    .Where(ConstraintQ.Description.Contains("security"))
    .ExecuteAsync(db);
foreach (var c in matches)
{
    Console.WriteLine($"{c.Id}: {c.Description}");
}

// 2. Id-only selection — IReadOnlyList<ConstraintId>, no entity hydration.
var ids = await Workspace.Query.Constraints
    .Where(ConstraintQ.Description.Contains("security"))
    .OrderBy(ConstraintQ.Description)
    .Limit(50)
    .IdsAsync(db);

// 3. Projection — typed DTOs, only the columns the projection touches travel back.
public sealed record ConstraintRow(ConstraintId Id, string? Description);

public static readonly ISurfaceProjection<ConstraintRow> ConstraintRowProj =
    SurfaceProjection.For<ConstraintRow>(row => new ConstraintRow(
        Id:          new ConstraintId(row.Read(ConstraintQ.Id).Value),
        Description: row.Read(ConstraintQ.Description)));

var rows = await Workspace.Query.Constraints
    .Where(ConstraintQ.Description.Contains("security"))
    .Limit(20)
    .Select(ConstraintRowProj)
    .ExecuteAsync(db);
```

Predicate operators on `PropertyExpr<T>`: `Eq`, `Lt`/`Le`/`Gt`/`Ge`, `In(...)`, plus the string-only `Contains` extension. Compose multiple predicates with `Predicate.And/Or/Not`. Server-side `OrderBy` / `ThenBy` / `Limit` / `Start` chain on every terminal — render as `ORDER BY … LIMIT … START …` at the tail of the SurrealQL.

Every terminal accepts either `Surreal db` (read) or `Transaction tx` (in-txn read with full visibility into pending writes). Pass `tx` whenever a query needs to see uncommitted state from the same transaction.

Pin a single record by id and pull traversed slices in the same call:

```csharp
var rows = await Workspace.Query.Designs
    .WithId(designId)
    .IncludeDetails()                            // [Reference, Inline] — field.* projection
    .IncludeConstraints(c => c                  // [Children] — nested SELECT
        .Where(ConstraintQ.Description.Contains("security"))
        .IncludeDetails())
    .ExecuteAsync(db);

var design = rows.Single();
foreach (var c in design.Constraints)
    Console.WriteLine(c.Description);
```

Relation traversals follow forward (outgoing) or inverse (incoming) edges and pull the targets in the same query. Single-target relations take a configure lambda for filtering at the target side; multi-target unions and cross-aggregate relations are leaves:

```csharp
// Forward, single-target within-aggregate — drill into target's own slices.
var c = await Workspace.Query.Constraints
    .WithId(constraintId)
    .IncludeRestrictions(r => r
        .Where(StoryQ.Description.Contains("auth"))
        .IncludeDetails())
    .ExecuteAsync(db);
foreach (var story in c.Single().Restrictions)
    Console.WriteLine(story.Description);

// Inverse, multi-target — leaf, no lambda.
var f = await Workspace.Query.Features
    .WithId(featureId)
    .IncludeRestrictions()
    .ExecuteAsync(db);

// Cross-aggregate — id-typed result, just like the entity-side reads.
var design = (await Workspace.Query.Designs
    .WithId(designId)
    .IncludeAssessments()
    .ExecuteAsync(db)).Single();
foreach (var reviewId in design.Assessments)
    Console.WriteLine(reviewId);
```

Edges also have their own root for flat `(source, target)` pair reads, when you want raw edges without hydrating either side as an entity:

```csharp
var pairs = await Workspace.Query.Edges.Restricts
    .WhereIn(constraintIds)
    .ExecuteAsync(db);

foreach (var (src, dst) in pairs.Select(p => (p.Source, p.Target)))
    Console.WriteLine($"{src} → {dst}");
```

## 8. Hydrate Specific Ids Into A Tracked Session

`Workspace.Hydrate.{Table}(ids)` pairs with `IdsAsync` for the `Load → Hydrate → Mutate → Save` flow. Use it when the slice you want to mutate isn't aggregate-shaped (search results, paged scrollers, edge fanout):

```csharp
// 1. Select — id-only.
var symbolIds = await Workspace.Query.CodeSymbols
    .Where(CodeSymbolQ.Name.Contains("Parser"))
    .Limit(20)
    .IdsAsync(db);

// 2. Hydrate — materialise into a tracked session. Pass `db` for read-only or
//    `tx` if downstream mutations should participate in an existing transaction.
await using var tx = await db.BeginTransactionAsync();
var session = await Workspace.Hydrate.CodeSymbols(symbolIds)
    .ExecuteAsync(tx);

// 3. Mutate (sync, in-memory).
foreach (var symbol in session.GetAll<CodeSymbol>())
    symbol.LastSeen = DateTimeOffset.UtcNow;

// 4. Dispatch + commit.
foreach (var symbol in session.GetAll<CodeSymbol>())
    await session.SaveAsync(symbol, tx);
await tx.CommitAsync();
```

Two overloads per table — typed `{Table}Id` for the ergonomic call site, raw `IRecordId` for cross-aggregate edge endpoints already collapsed to canonical record ids. Empty-id list short-circuits to an empty session with no wire call.

`WithInclude(IIncludeNode)` accepts the same node AST as `Query<T>.WithInclude(...)` — wire SQL is identical to `Query<T>.Where(IdIn).WithInclude(...).ExecuteAsync` would emit. Per-relation ergonomic `Include*` helpers for the hydration path are deferred until a real callsite needs them.

## 9. Filtered Loads And Top-Up Fetch

Switch the terminal verb from `ExecuteAsync` to `LoadAsync` to get a session for the same query AST. Aggregate-root tables only — non-roots compile-error if you try.

```csharp
await using var tx = await db.BeginTransactionAsync();
var session = await Workspace.Query.Designs
    .WithId(designId)
    .IncludeDetails()
    .IncludeConstraints(c => c.IncludeDetails())
    .LoadAsync(tx);

var design = session.Get<Design>(designId)!;
foreach (var c in design.Constraints) { /* works — Included */ }
```

Reads against slices that weren't included throw `LoadShapeViolationException`. The exception's message points at `session.FetchAsync(...)` — a top-up extension query that hydrates additional slices into the existing session:

```csharp
try
{
    var epics = design.Epics;
}
catch (LoadShapeViolationException)
{
    await session.FetchAsync(
        Workspace.Query.Designs.WithId(designId).IncludeEpics(e => e.IncludeDetails()),
        tx);
    var epics = design.Epics;            // works — slice now loaded
}
```

`Fetch` is a slice widener: if you've mutated `design.Description` in memory and Fetch re-hydrates the same row, your edit gets overwritten with the DB value. Save first, or accept the clobber. (The per-field "did the user touch this" tracking was deliberately removed under the explicit-Save model.)

## 10. Add Relations

Declare a forward relation attribute and its inverse:

```csharp
using Disruptor.Surface.Annotations;

namespace MyApp.Model;

public sealed class RestrictsAttribute : ForwardRelation;
public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;
```

Use the attributes on relation collection properties:

```csharp
[Table, AggregateRoot]
public partial class Design
{
    [Id] public partial DesignId Id { get; set; }

    [Property] public partial string Title { get; set; }
    [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
    [Children] public partial IReadOnlyCollection<UserStory> UserStories { get; }
}

[Table]
public partial class Constraint
{
    [Id] public partial ConstraintId Id { get; set; }
    [Parent] public partial Design Design { get; set; }
    [Property] public partial string Text { get; set; }

    [Restricts] public partial IReadOnlyCollection<UserStory> RestrictedStories { get; }

    // Domain verb shipped against the user's tx — RelateAsync dispatches the RELATE
    // immediately; no buffered intent for SaveAsync to drain.
    public Task RestrictsAsync(UserStory story, SurrealTransaction tx, CancellationToken ct = default)
        => Session.RelateAsync<Restricts>(this, story, tx, ct);
}

[Table]
public partial class UserStory
{
    [Id] public partial UserStoryId Id { get; set; }
    [Parent] public partial Design Design { get; set; }
    [Property] public partial string Summary { get; set; }

    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }
}
```

The generator emits `Restricts : IRelationKind` (a marker class with a `static abstract string EdgeName`). Use `Session.RelateAsync<Restricts>(src, tgt, tx)` / `Session.UnrelateAsync<Restricts>(src?, tgt?, tx)` for mutations, and the sync `Session.QueryOutgoing<Restricts, T>(...)` / `Session.QueryIncoming<Restricts, T>(...)` for reads off the in-session edge index — no string edge names anywhere in user code. Edge writes always carry a transaction; there's no buffered "set up edges, save later" mode.

## 11. Run The Repository Sample

From this repository:

```sh
dotnet run --project src/Disruptor.Surface.Sample
```

The sample applies the schema, seeds ten Design aggregates and a Review against one of them, reloads them, prints loaded data, and exercises the full query layer (predicates, traversals, edges, filtered `LoadAsync`, `LoadShapeViolationException`, `FetchAsync` top-up, the four flavors of relation include).
