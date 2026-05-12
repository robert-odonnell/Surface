# Disruptor.Surface

A C# Roslyn source generator that turns `[Table]`-annotated partial classes into a working [SurrealDB](https://surrealdb.com) persistence layer — typed ids, snapshot-isolated sessions, generated SurrealQL, typed relation kinds, and per-entity Save dispatch over the `Disruptor.Surreal` SDK.

The library is aimed at domain models where aggregates matter. You describe the model with attributes; the generator emits the plumbing needed to load an aggregate into a `SurrealSession`, mutate the resulting C# objects synchronously, and dispatch the changes through a transaction your application owns. The library never opens or commits transactions on your behalf — cross-aggregate atomicity is whatever your code decides.

**Requirements:** .NET 10. **License:** [MIT](LICENSE). **Package status:** not yet published to NuGet — for now, reference the projects directly from a checkout (see [Building](#building)).

> **Status: preview.** Functional end-to-end against a live SurrealDB v3, but the public API is still moving. See [`docs/notes.md`](docs/notes.md) for the engineering log and current preview tag. Not production-tested.

## Documentation

| Read this | If you want to |
| --- | --- |
| [`docs/intro.md`](docs/intro.md) | Decide whether the library fits your use case. Comparison vs. EF Core / Dapper / the raw SDK. |
| [`docs/quickstart.md`](docs/quickstart.md) | Build your first model end-to-end. Connect, apply schema, save, load, query, add a relation. |
| [`docs/api.md`](docs/api.md) | Look up the full surface: every modeling attribute, generated type, runtime API, and diagnostic. |
| [`docs/architecture.md`](docs/architecture.md) | Contribute changes. Generator pipeline, emitters table, incremental-generator contract, recipes for adding attributes / diagnostics / emitters. |
| [`docs/notes.md`](docs/notes.md) | Track what's shipped under which preview tag. Build commands, engineering log, equatability invariants. |

## At a glance

You write the model:

```csharp
using Disruptor.Surface.Annotations;
using Disruptor.Surface.Runtime;

namespace MyApp.Model;

[Table, AggregateRoot]
public partial class Design
{
    [Id]                         public partial DesignId Id { get; set; }
    [Property]                   public partial string Title { get; set; }
    [Reference, Inline, Cascade] public partial Details? Details { get; set; }
    [Children]                   public partial IReadOnlyCollection<Constraint> Constraints { get; }
}

[Table]
public partial class Constraint
{
    [Id]       public partial ConstraintId Id { get; set; }
    [Parent]   public partial Design Design { get; set; }
    [Property] public partial string Description { get; set; }
}

[CompositionRoot]
public partial class Workspace { }
```

…and use the generated surface at runtime:

```csharp
// Connect once. CBOR over WebSocket — the library has no transport of its own.
await using var db = await SurrealClient.ConnectAsync(SurrealOptions.Parse(
    "Url=ws://localhost:8000;Namespace=app;Database=main;User=root;Password=root"));

// Apply schema (idempotent).
await Workspace.ApplySchemaAsync(db);

// Create, save, load.
var workspace = new Workspace();
var session = new SurrealSession(Workspace.ReferenceRegistry);
var design = session.Track(new Design { Title = "First design" });

await using (var tx = await db.BeginTransactionAsync())
{
    await session.SaveAsync(design, tx);   // walks Details + Constraints automatically
    await tx.CommitAsync();
}

var loaded = await workspace.LoadDesignAsync(db, design.Id);
Console.WriteLine(loaded.Get<Design>(design.Id)!.Title);
```

## Project layout

```
src/
  Disruptor.Surface.Generator/   — Roslyn source generator (netstandard2.0, analyzer).
  Disruptor.Surface.Runtime/     — runtime: SurrealSession, IEntity, IRelationKind,
                                   IRelationVariant, RecordId, IReferenceRegistry,
                                   HydrationValue, ISaveContext, CommandLog. Two package
                                   deps: Disruptor.Surreal (SurrealDB SDK — CBOR over
                                   WebSocket) and Ulid.
  Disruptor.Surface.Sample/      — console-app harness exercising the full pipeline
                                   against a live SurrealDB. Canonical worked example.
tests/
  Disruptor.Surface.Tests/       — generator emission tests, diagnostic tests, runtime
                                   unit tests, end-to-end fixture compile-and-load.
docs/                            — see the Documentation table above.
```

`Disruptor.Surface.Sample` references both the generator (as an analyzer, no runtime dep) and the runtime library. Real consumer projects do the same.

## Building

```sh
dotnet build Disruptor.Surface.slnx
```

Generated files for the sample land in `src/Disruptor.Surface.Sample/obj/Debug/net10.0/generated/Disruptor.Surface.Generator/Disruptor.Surface.Generator.ModelGenerator/` — inspect them to see what the generator emitted for a given `[Table]` class. Full build/test/inspection commands in [`docs/notes.md`](docs/notes.md#build--run).

## Running the harness

With SurrealDB running on `127.0.0.1:8000`, run `dotnet run --project src/Disruptor.Surface.Sample`. The sample applies the generated schema, seeds aggregates, reloads them, and exercises the full query layer — useful as both smoke test and worked end-to-end example. Connection parameters are hard-coded in `Program.cs`; [`docs/quickstart.md`](docs/quickstart.md) has the `surreal start` command and connection-string details.

## Contributing

If you're going to modify the library itself, start with [`docs/architecture.md`](docs/architecture.md) — it covers the generator pipeline, the [Incremental Generator Contract](docs/architecture.md#incremental-generator-contract) (the one non-obvious rule that breaks generators silently if violated), and recipes for the most common changes. [`docs/notes.md`](docs/notes.md) is the running engineering log; read the most recent entry before touching anything that's been moving lately.
