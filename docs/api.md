# API Reference

This is a user-facing API map for the generated surface and the runtime types most consumers touch.

## Packages And Namespaces

- `Disruptor.Surface.Runtime`: runtime package with annotations, transport, sessions, ids, leases, and commit pipeline.
- `Disruptor.Surface.Generator`: Roslyn source generator. Reference as a private analyzer dependency.
- `Disruptor.Surface.Annotations`: namespace for modeling attributes.
- `Disruptor.Surface.Runtime`: namespace for runtime contracts and helpers.

## Modeling Attributes

| Attribute | Target | Purpose |
| --- | --- | --- |
| `[Table]` | partial class | Includes a class in the generated model. |
| `[AggregateRoot]` | `[Table]` class | Marks the root of a loadable aggregate. Members are discovered through `[Children]`. |
| `[CompositionRoot]` | partial class | Receives generated `Load{Root}Async`, `Schema`, `ApplySchemaAsync`, and `ReferenceRegistry` members. Exactly one is allowed per compilation. |
| `[Id]` | partial property | Optional public typed-id accessor. At most one per table. If omitted, the generator still emits an internal id anchor. |
| `[Property]` | partial property | Persisted scalar field or `SurrealArray<T>` inline array field. |
| `[Reference]` | partial property | Record reference. Non-nullable get-only references are mandatory; nullable settable references are optional. |
| `[Inline]` | with `[Reference]` | Hydrates the referenced record inline with its owner. Without `[Inline]`, only the referenced id is hydrated. |
| `[Parent]` | partial property | Parent link for aggregate hierarchy. |
| `[Children]` | partial get-only collection | Reverse lookup over child records. |
| `[Reject]` | with `[Reference]` | Blocks deletion of the target while this reference points at it. Default behavior. |
| `[Unset]` | with nullable `[Reference]` | Clears this reference when the target is deleted. |
| `[Cascade]` | with `[Reference]` | Deletes the referencing record when the target is deleted. |
| `[Ignore]` | with `[Reference]` | Leaves the reference unchanged when the target is deleted. |
| `ForwardRelation` | base class | Base for user-defined forward relation attributes. |
| `InverseRelation<TForward>` | base class | Base for user-defined inverse relation attributes. |

## Generated Entity API

For each `[Table]` class, the generator emits a partial implementation that:

- Implements `IEntity`.
- Adds backing fields for generated properties.
- Binds each entity instance to a `SurrealSession` when tracked or hydrated.
- Routes property writes into `SurrealSession.SetField(...)`.
- Routes reference clears into `SurrealSession.UnsetField(...)`.
- Routes child and relation reads into session queries.

Generated property behavior:

| Declaration | Generated behavior |
| --- | --- |
| `[Id] public partial DesignId Id { get; set; }` | Lazy typed id. Setter is allowed only before the entity is bound to a session. |
| `[Property] public partial string Title { get; set; }` | Synchronous getter/setter. Setter records a pending field write. |
| `[Property] public partial SurrealArray<T> Items { get; }` | Lazy mutation-aware list wrapper. Mutations record a pending field write. |
| `[Reference] public partial T Ref { get; }` | Mandatory reference. Getter throws if the referenced entity is not available in the session. |
| `[Reference] public partial T? Ref { get; set; }` | Optional reference. Setting `null` clears the field. |
| `[Reference, Inline] public partial T? Ref { get; set; }` | Optional owned-sidecar reference that hydrates with the owner. |
| `[Parent] public partial Parent Parent { get; set; }` | Parent link. Setter records a parent field write. |
| `[Children] public partial IReadOnlyCollection<Child> Children { get; }` | Query over in-session children by parent link. |
| `[ForwardKind] public partial IReadOnlyCollection<T> Targets { get; }` | Forward edge read. Uses `QueryOutgoing` for same-aggregate edges or id reads for cross-aggregate edges. |
| `[InverseKind] public partial IReadOnlyCollection<T> Sources { get; }` | Inverse edge read. Uses `QueryIncoming` for same-aggregate edges or id reads for cross-aggregate edges. |

Each generated entity also has a protected `Session` property. Use it from your own partial members to write small domain verbs:

```csharp
public void Restricts(UserStory story) => Session.Relate<Restricts>(this, story);
```

Optional hooks:

```csharp
partial void OnCreateDetails(Details details)
{
    details.Summary = "Created with owner";
}

partial void OnDeleting()
{
    foreach (var child in Constraints)
    {
        Session.Delete(child);
    }
}
```

`OnCreate{Name}` is emitted for mandatory get-only references. `OnDeleting` runs before the entity's own delete command is queued.

## Generated Id API

Each table gets a typed id:

```csharp
public readonly record struct DesignId(string Value) : IRecordId
{
    public string Value { get; } = RecordIdFormat.Validate(Value);

    public string Table => "designs";
    public string ToLiteral() => Value;
    public static DesignId New() => new(Ulid.NewUlid().ToString());
    public override string ToString() => Table + ":" + Value;
    public static implicit operator RecordId(DesignId id) => new(id.Table, id.Value);
}
```

`Value` is a `string` validated at construction by `RecordIdFormat.Validate`. Two and only two forms are accepted; anything else throws `FormatException`:

- **Ulid stringification** — exactly 26 characters of `[A-Z0-9]` (Crockford Base32). What `New()` mints. The default for fresh records.
- **Short lower_snake_case slug** — starts with `[a-z]`, followed by `[a-z0-9_]*`, max 32 characters. Opt-in for stable-named records (singletons, config rows, well-known references). Short on purpose: if you're reaching for a 30-char slug, you probably want a Ulid.

```csharp
var fresh   = DesignId.New();                        // 26-char Ulid
var primary = new DesignId("primary");               // slug, OK
var bad     = new DesignId("Some Mixed Case");       // throws FormatException
```

There is no assembly-level override — Ulid is the only mint type, and quoted-string ids are explicitly unsupported.

## Generated Composition Root API

For a `[CompositionRoot]` named `Workspace`, the generator emits:

```csharp
public static IReadOnlyList<string> Schema { get; }

public static Task ApplySchemaAsync(
    ISurrealTransport transport,
    CancellationToken ct = default);

public static IReferenceRegistry ReferenceRegistry { get; }

public Task<SurrealSession> LoadDesignAsync(
    ISurrealTransport transport,
    DesignId rootId,
    CancellationToken ct = default);
```

There is one `Load{Root}Async` method per `[AggregateRoot]`.

## Relations

Declare relation attributes:

```csharp
public sealed class RestrictsAttribute : ForwardRelation;
public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;
```

The generator emits a marker class for the forward kind:

```csharp
public sealed class Restricts : IRelationKind
{
    public static string EdgeName => "restricts";
}
```

Runtime calls:

```csharp
session.Relate<Restricts>(source, target);
session.Unrelate<Restricts>(source, target);
session.UnrelateAllFrom<Restricts>(source);
session.UnrelateAllTo<Restricts>(target);

session.QueryOutgoing<Restricts, UserStory>(constraint);
session.QueryIncoming<Restricts, Constraint>(story);
session.QueryRelatedIds<Restricts>(sourceEntity);
session.QueryInverseRelatedIds<Restricts>(targetEntity);
```

Within-aggregate relation properties hydrate entity instances. Cross-aggregate relation properties expose `IRecordId` endpoints because the other aggregate is not part of the current snapshot.

## Runtime Types

### `ISurrealTransport`

```csharp
public interface ISurrealTransport : IAsyncDisposable
{
    Task<JsonDocument> ExecuteAsync(string sql, object? vars = null, CancellationToken ct = default);
}
```

Implement this if you want a custom transport. Most users use `SurrealHttpClient`.

### `SurrealHttpClient`

HTTP transport for SurrealDB:

```csharp
await using var transport = new SurrealHttpClient(config, httpClient);

using var doc = await transport.ExecuteAsync("SELECT * FROM designs;");
var resultSet = await transport.SqlAsync("SELECT * FROM designs;");
```

Useful members:

- `Config`: the `SurrealConfig` used by the client.
- `ExecuteAsync(sql, vars, ct)`: transport contract used by generated loaders and commits.
- `SqlAsync(sql, vars, ct)`: returns `SurrealResultSet` for direct SQL use.
- `BuildLetPrefix(vars)`: renders bindings as SurrealQL `LET` statements.

### `SurrealConfig`

```csharp
var config = SurrealConfig.Default();

var config = SurrealConfig.FromConnectionString(
    "Url=http://127.0.0.1:8000;Namespace=main;Database=main;User=root;Password=root;TimeoutSeconds=30");
```

Recognized keys include `Server`, `Endpoint`, `Url`, `Namespace`, `Ns`, `Database`, `Db`, `Username`, `User`, `Password`, `Pass`, and `TimeoutSeconds`.

### `SurrealSession`

`SurrealSession` is the main runtime unit of work.

Construction:

```csharp
var session = new SurrealSession(Workspace.ReferenceRegistry);
```

Read methods:

| Method | Purpose |
| --- | --- |
| `Get<T>(IRecordId id)` | Lookup a hydrated or tracked entity by id. |
| `GetParent<T>(IEntity owner)` | Resolve a parent link. |
| `GetReference<T>(owner, field)` | Resolve mandatory reference or throw. |
| `GetReferenceOrDefault<T>(owner, field)` | Resolve optional reference or return `null`. |
| `QueryChildren<T>(owner, childTable)` | Read children by parent link. |
| `QueryOutgoing<TKind, T>(owner)` | Read same-aggregate outgoing relation targets. |
| `QueryIncoming<TKind, T>(owner)` | Read same-aggregate incoming relation sources. |
| `QueryRelatedIds<TKind>(owner)` | Read cross-aggregate outgoing ids. |
| `QueryInverseRelatedIds<TKind>(owner)` | Read cross-aggregate incoming ids. |

Write methods:

| Method | Purpose |
| --- | --- |
| `Track(entity)` | Bind and register a fresh entity, queueing a create. |
| `SetField(owner, field, value, kind)` | Low-level field write used by generated setters. |
| `UnsetField(owner, field, kind)` | Low-level field clear used by generated setters. |
| `Relate<TKind>(source, target)` | Queue relation creation. |
| `Unrelate<TKind>(source, target)` | Queue relation deletion. |
| `UnrelateAllFrom<TKind>(source)` | Queue bulk deletion of all outgoing edges of a kind. |
| `UnrelateAllTo<TKind>(target)` | Queue bulk deletion of all incoming edges of a kind. |
| `Delete(entity)` | Queue entity deletion and invoke `OnDeleting`. |
| `Delete(id)` | Queue id-only deletion without `OnDeleting`. |

Boundary methods:

| Method | Purpose |
| --- | --- |
| `RenderBatch()` | Build the current SurrealQL batch without closing the session. Useful for diagnostics. |
| `CommitAsync(transport, lease, ct)` | Build, optionally renew the lease, execute, clear, and close the session. |
| `AbandonAsync(ct)` | Drop pending writes and close the session. |

After `CommitAsync` or `AbandonAsync`, `IsClosed` is `true` and public reads/writes throw.

### `WriterLease`

`WriterLease` coordinates cross-process writers through a `writer_lease` table:

```csharp
await using var lease = await WriterLease.AcquireAsync(transport, "design");
await session.CommitAsync(transport, lease);
```

Key members:

- `AcquireAsync(transport, aggregateName, ttl, ct)`.
- `RenewAsync(ct)`.
- `ReleaseAsync(ct)`.
- `DefaultTtl`: five minutes.
- `SchemaScript`: DDL for the runtime lease table. Generated schema includes it.

Exceptions:

- `WriterLeaseUnavailableException`: another non-expired holder owns the lease.
- `WriterLeaseStolenException`: renewal found that the lease was stolen or removed.

### `RecordIdFormat`

Single-source-of-truth validator for typed-id `Value` strings:

```csharp
RecordIdFormat.Validate("primary");        // "primary"
RecordIdFormat.Validate("01HXY...26chars"); // returns the Ulid string
RecordIdFormat.Validate("Bad Value");      // throws FormatException

RecordIdFormat.IsValid("primary");          // true
RecordIdFormat.IsValid(null);               // false
RecordIdFormat.MaxSlugLength;               // 32
```

The generator routes every `{Name}Id(string Value)` ctor through `Validate`, so user code can't construct an id with a malformed value — the throw happens at construction, not at commit time.

### `RecordId`

Canonical id type:

```csharp
var id = RecordId.New("designs");
var root = RecordId.Root("settings");
var slot = RecordId.Slot("settings", "current");
var parsed = RecordId.Parse("designs:01J...");
```

Important members:

- `Table`
- `Value`
- `ToLiteral()`
- `ToString()`
- `From(IRecordId id)`
- `TryParse(...)`
- `IsForTable(table)`

### `SurrealArray<T>`

Use as a `[Property]` for inline ordered arrays:

```csharp
public sealed record Scenario(string Kind, string Description);

[Property]
public partial SurrealArray<Scenario> Scenarios { get; }
```

It implements `IList<T>` and `IReadOnlyList<T>`. Mutations such as `Add`, `Remove`, `Clear`, index assignment, and `Move` notify the owning entity so the field is included in the next commit.

### Result Helpers

For direct SQL calls:

```csharp
var result = await transport.SqlAsync("SELECT * FROM designs;");
var rows = result.DecodeList<MyDto>();
var first = result.DecodeFirst<MyDto>();
var count = result.Count();
var raw = result.ResultAt();
```

Related types:

- `SurrealResultSet`
- `SurrealResultReader`
- `SurrealStatementResponse`
- `SurrealQueryException`
- `SurrealProtocolException`

### Commit Planning And Formatting

Most consumers do not call these directly, but they are public for diagnostics and tests:

- `PendingState`: compact indexed write-intent state.
- `CommandLog`: chronological command history.
- `CommitPlanner.Build(pending, registry)`: produces ordered commands and enforces reference delete behavior.
- `SurrealCommandEmitter.Emit(commands)`: renders SurrealQL and parameters.
- `SurrealFormatter`: formats identifiers, record ids, and string literals.

## Diagnostics

The generator emits diagnostics for invalid model shapes:

| Code | Meaning |
| --- | --- |
| `CG001` | `[Table]` class must be partial. |
| `CG008` | Table declares more than one `[Id]` property. |
| `CG009` | `[Children]` element type cannot be a generic type parameter. |
| `CG010` | `[Reference]` target must be a `[Table]` type. |
| `CG011` | Entity is reachable from multiple aggregate roots. |
| `CG012` | `[Unset]` requires nullable reference storage. |
| `CG013` | More than one reference delete behavior was declared. |
| `CG014` | Cascade-only reference cycle. |
| `CG015` | Delete behavior attribute was placed on `[Parent]`. |
| `CG017` | `[Ignore]` may create a dangling reference. |
| `CG018` | More than one `[CompositionRoot]`. |
| `CG019` | `[CompositionRoot]` class must be partial. |
| `CG020` | `[Children]` member has no `[Parent]` path back to the aggregate root. |
| `CG021` | `[Reference]` crosses aggregate boundaries; use a relation instead. |
| `CG022` | Annotated property (`[Id]/[Property]/[Parent]/[Reference]/[Children]`) must be declared `partial`. |
| `CG024` | Property has multiple role attributes; the five role attributes are mutually exclusive. |

Some older relation-method diagnostics remain in the codebase for retired shapes, but normal user-facing relation writes should go through `Session.Relate<TKind>` and related methods.
