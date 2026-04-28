# Introduction

Disruptor.Surface is a C# source-generator based persistence layer for SurrealDB. You describe your domain model with partial classes and attributes, and the generator emits the plumbing needed to load aggregates, track in-memory changes, and commit those changes as SurrealQL.

The library is aimed at domain models where aggregates matter. A `[AggregateRoot]` plus its `[Children]` graph becomes the unit of loading. A `SurrealSession` represents one loaded snapshot plus one pending mutation batch. You read and mutate the generated model synchronously, then explicitly commit the batch through an `ISurrealTransport`.

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
- `Workspace.LoadDesignAsync(transport, designId, ct)`, which hydrates the aggregate into a `SurrealSession`.
- `Workspace.Schema` and `Workspace.ApplySchemaAsync(transport)`.
- `Workspace.ReferenceRegistry`, used by commit planning.
- Relation marker types for user-defined relation attributes.
- Compile-time diagnostics for invalid model shapes.

The generated code does not require your entities to inherit from a base class. It implements the required runtime interfaces in generated partial fragments and leaves your constructors, dependency injection, caching, and application wiring alone.

## Runtime Model

The main runtime concepts are:

- `ISurrealTransport`: abstraction for executing SurrealQL.
- `SurrealHttpClient`: HTTP implementation of `ISurrealTransport`.
- `SurrealSession`: identity map, synchronous read surface, and pending write batch.
- `WriterLease`: optional cross-process writer coordination for an aggregate.
- `RecordId` and generated `{Entity}Id` types: strongly typed SurrealDB record ids.
- `SurrealArray<T>`: mutation-aware inline array field wrapper.

The usual flow is:

1. Apply generated schema at startup.
2. Load an aggregate with the generated `Load{Root}Async` method, or create a fresh `SurrealSession` for new aggregates.
3. Read and mutate entities through normal properties and methods.
4. Acquire a `WriterLease` for writes when cross-process coordination matters.
5. Call `session.CommitAsync(transport, lease, ct)`.

## What It Is Not

Disruptor.Surface is not a general ORM. It does not try to hide SurrealDB, provide LINQ, or manage long-lived change tracking. It favors explicit aggregate loads, one-shot sessions, typed relation kinds, generated SurrealQL, and small runtime surfaces that are easy to reason about.

The current repository is a preview implementation. The sample and test suite exercise schema generation, aggregate hydration, relation reads/writes, writer leases, and commit planning, but you should validate behavior against your SurrealDB version and workload before depending on it in production.
