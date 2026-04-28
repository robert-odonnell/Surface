# Quickstart

This guide builds a small Disruptor.Surface model, applies its generated SurrealDB schema, creates data, commits it, and reloads it.

## 1. Add The Packages

In a consumer project, reference the runtime package normally and the generator as a private analyzer dependency:

```xml
<ItemGroup>
  <PackageReference Include="Disruptor.Surface.Runtime" Version="0.1.0-preview.9" />
  <PackageReference Include="Disruptor.Surface.Generator" Version="0.1.0-preview.9" PrivateAssets="all" />
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

Build the project. The generator will emit typed ids, property bodies, schema metadata, and `Workspace.LoadDesignAsync(...)`.

## 3. Configure SurrealDB

Start SurrealDB. The sample harness expects this shape:

```sh
surreal start --bind 127.0.0.1:8000 \
              --default-namespace project-brain \
              --default-database workspace \
              --username root --password secret \
              rocksdb://path/to/db
```

Create a transport:

```csharp
using Disruptor.Surface.Runtime;
using MyApp.Model;

var config = new SurrealConfig(
    Url: new Uri("http://127.0.0.1:8000"),
    Namespace: "project-brain",
    Database: "workspace",
    User: "root",
    Password: "secret",
    Timeout: TimeSpan.FromSeconds(30));

using var http = new HttpClient();
await using var transport = new SurrealHttpClient(config, http);
```

You can also use `SurrealConfig.Default()` or `SurrealConfig.FromConnectionString(...)`.

## 4. Apply The Schema

Generated schema chunks are idempotent, so applying them at startup is safe:

```csharp
await Workspace.ApplySchemaAsync(transport);
```

For custom logging, filtering, or migration control, iterate chunks directly:

```csharp
foreach (var chunk in Workspace.Schema)
{
    await transport.ExecuteAsync(chunk);
}
```

## 5. Create And Commit Data

For a new aggregate, create a session with the generated reference registry, track entities, then commit:

```csharp
await using var lease = await WriterLease.AcquireAsync(transport, "design");

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

await session.CommitAsync(transport, lease);
```

Object-initializer values are buffered until `Track` binds the entity to the session. Nested entity values, such as `Details`, are automatically tracked when written through a reference field.

## 6. Load And Edit Data

Use the generated composition-root load method for aggregate reads:

```csharp
var workspace = new Workspace();
var readSession = await workspace.LoadDesignAsync(transport, design.Id);

var loaded = readSession.Get<Design>(design.Id)
    ?? throw new InvalidOperationException("Design did not hydrate.");

Console.WriteLine(loaded.Title);
foreach (var item in loaded.Constraints)
{
    Console.WriteLine(item.Text);
}
```

To edit, load a fresh session, mutate the generated properties, and commit:

```csharp
await using var lease = await WriterLease.AcquireAsync(transport, "design");

var writeSession = await workspace.LoadDesignAsync(transport, design.Id);
var editable = writeSession.Get<Design>(design.Id)!;

editable.Title = "Updated design";

await writeSession.CommitAsync(transport, lease);
```

A `SurrealSession` is one-shot. After `CommitAsync` or `AbandonAsync`, the session closes and further reads or writes throw. Load a new session for the next unit of work.

## 7. Add Relations

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

    public void Restricts(UserStory story) => Session.Relate<Restricts>(this, story);
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

The generator emits `Restricts : IRelationKind`. Use `Session.Relate<Restricts>(...)`, `Session.Unrelate<Restricts>(...)`, `Session.QueryOutgoing<Restricts, T>(...)`, and `Session.QueryIncoming<Restricts, T>(...)` instead of string edge names.

## 8. Run The Repository Sample

From this repository:

```sh
dotnet run --project src/Disruptor.Surface.Sample
```

The sample applies schema, seeds `Design` and `Review` aggregates, commits them, reloads them, prints loaded data, and demonstrates writer lease theft detection.
