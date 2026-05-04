# Architecture

Disruptor.Surface has two halves:

- A Roslyn source generator that reads attributed C# model types and emits model-specific code.
- A small runtime that provides sessions, transport, ids, schema execution, writer leases, hydration helpers, and commit planning.

The design goal is model-specific code at compile time and explicit runtime boundaries at execution time. The generator knows your aggregate graph and can produce strongly typed code. The runtime stays generic and does not use process-global model state.

## High-Level Flow

```text
C# model declarations
        |
        v
Roslyn generator
        |
        +--> typed ids
        +--> entity partial implementations
        +--> relation kind markers
        +--> aggregate loaders
        +--> schema chunks
        +--> reference registry
        |
        v
Consumer application
        |
        +--> ApplySchemaAsync
        +--> Load{Root}Async
        +--> mutate generated entities
        +--> SurrealSession.CommitAsync
        |
        v
SurrealDB
```

## Generator Pipeline

The generator scans the compilation for:

- `[Table]` classes.
- `[AggregateRoot]` markers.
- `[CompositionRoot]` class.
- Properties with `[Id]`, `[Property]`, `[Reference]`, `[Parent]`, `[Children]`, and relation attributes.
- Relation attribute classes deriving from `ForwardRelation` or `InverseRelation<TForward>`.

It builds a model graph containing tables, properties, aggregate membership, relation kinds, relation unions, and reference metadata. Diagnostics are reported before emission when the model shape cannot be generated safely.

Main emitter responsibilities:

| Emitter | Output |
| --- | --- |
| `IdEmitter` | `{Table}Id` typed record structs implementing `IRecordId`. |
| `PartialEmitter` | Entity partial implementations, generated property bodies, session binding, hydration hooks (`Hydrate` + `HydratePartial`), `MarkAllSlicesLoaded`, delete hooks, and slice-guard read paths. |
| `RelationKindEmitter` | `IRelationKind` marker class per forward relation attribute. |
| `UnionInterfaceEmitter` | Shared interfaces for relation target/source unions when one relation can point at multiple table types. |
| `AggregateLoaderEmitter` | Internal loader per aggregate root (legacy full-aggregate path). |
| `CompositionRootEmitter` | `Load{Root}Async` methods on the user's composition root. |
| `SchemaEmitter` | Ordered idempotent SurrealDB DDL chunks and `ApplySchemaAsync`. |
| `ReferenceRegistryEmitter` | Model-scoped reference metadata for delete planning. |
| `QueryRootEmitter` | `Workspace.Query` accessor + per-table `Query<T>` roots. |
| `EdgeQueryRootEmitter` | `Workspace.Query.Edges.{Kind}` accessors with id-side type parameters. |
| `PredicateFactoryEmitter` | Per-table `{Name}Q` static class — typed `PropertyExpr<T>` per scalar column. |
| `TraversalBuilderEmitter` | Per-table `{Name}TraversalBuilder` plus the `{Name}QueryIncludes` extensions on `Query<T>`. |
| `LoadEntryEmitter` | `LoadAsync` extension on `Query<TRoot>` for aggregate roots — delegates to the legacy loader for empty-`Includes`, runs the compiler-driven path otherwise. |

## Model Mapping

Entity tables use lower snake case plural names. For example, `DesignChange` maps to `design_changes`. Property fields use lower snake case names. For example, `RepositoryRoot` maps to `repository_root`.

Each `[Table]` maps to a SurrealDB `SCHEMAFULL` table. The generated schema includes:

- Table declarations.
- Scalar field declarations for `[Property]`.
- `array<object>` fields for `SurrealArray<T>`.
- `record<target>` fields for `[Reference]`.
- `option<record<target>>` for nullable references.
- Parent fields with `REFERENCE ON DELETE REJECT`.
- Computed reverse fields for `[Children]`.
- Relation tables for forward relation kinds.
- Runtime `writer_lease` table DDL.

`[Reference, Inline]` is the owned-sidecar carve-out. The aggregate loader expands that reference with `field.*` and hydrates the linked row in the same session. Plain `[Reference]` stores and hydrates the id only.

## Aggregate Boundaries

An aggregate is a `[AggregateRoot]` plus every entity reachable by walking `[Children]`. The generator enforces that a table belongs to at most one aggregate.

Aggregate membership affects:

- Which generated `Load{Root}Async` methods exist.
- Which tables a nested loader query includes.
- Whether a relation property returns entities or ids.
- Which writes should normally share a writer lease name.

Cross-aggregate links should be modeled as relation kinds, not `[Reference]` fields. A `[Reference]` from one aggregate to another produces `CG021`.

## Loading

For every aggregate root, the generator emits an internal loader such as `DesignAggregateLoader`.

The generated composition-root method is thin:

```csharp
public async Task<SurrealSession> LoadDesignAsync(
    ISurrealTransport transport,
    DesignId rootId,
    CancellationToken ct = default)
{
    var ws = new SurrealSession(ReferenceRegistry);
    await DesignAggregateLoader.PopulateAsync(ws, transport, rootId, ct);
    return ws;
}
```

The loader performs one nested SurrealQL query rooted at the aggregate id:

- The root row selects `*`.
- Inline references are projected with `field.*`.
- Child tables are loaded with subselects scoped back to the root through parent paths.
- Edge tables touching the aggregate are loaded into edge arrays.

Hydration is routed through `IHydrationSink`, an explicit interface on `SurrealSession`. This keeps loader-only operations out of the normal session surface while letting generated code populate entities, parent links, references, and edges without recording writes.

## Query+Load Surface

Two terminal verbs share one query AST:

- `ExecuteAsync(transport)` — read mode. Compiles the predicate + traversal AST to SurrealQL, executes it, hydrates root entities (and any traversed children) into an internal `SurrealSession`. Returns the root list; the session is opaque.
- `LoadAsync(transport, lease)` — write mode, only on aggregate roots. Same compile-and-hydrate path against a session built with `Workspace.ReferenceRegistry`. Returns the session for mutation + commit. With no `Include*` calls, delegates to the legacy `{Root}AggregateLoader` so the full-aggregate path keeps its single-query shape.

Compilation:

```text
Predicate AST + Includes AST + PinnedId
        |
        v
QueryCompiler.Compile
        |
        +--> SurrealQL: SELECT *, field.*, (SELECT … FROM child WHERE parent = $parent.id) AS …
        +--> Bindings dict: $p0 → "x", $p1 → recordId, …
        |
        v
SurrealHttpClient (inlines bindings, posts JSON-RPC)
```

Generator-emitted hydrator delegates on each `IncludeChildrenNode` capture the right concrete `new TChild()` + `Hydrate` at codegen time, so the runtime walks the JSON without reflection. `Fetch` reuses the same compile-and-hydrate path but dispatches to `IEntity.HydratePartial` when an entity is already tracked — pending writes survive a top-up via the `IHydrationSink.HasPendingWrite` guard.

Strict-with-escape: filtered loads only mark the user-`Include`d slices loaded; reads outside the slice throw `LoadShapeViolationException` with a hint at `session.FetchAsync(...)`. The legacy aggregate-loader path marks every slice on every loaded entity, so existing code stays unaffected.

## Session Lifecycle

`SurrealSession` is an identity map plus a pending write batch. It has no ambient static current session. Generated entities hold a reference to the session they are bound to.

Lifecycle for fresh entities:

```text
session.Track(entity)
        |
        +--> bind entity to session
        +--> record Create
        +--> Initialize mandatory references
        +--> flush object-initializer writes
```

Lifecycle for loaded entities:

```text
Load{Root}Async
        |
        +--> create entity instance
        +--> hydrate fields from JSON
        +--> bind through IHydrationSink
        +--> mark as loaded-at-start
```

Reads are synchronous lookups against in-memory dictionaries. Writes update those dictionaries and record commands. No database call happens until `CommitAsync`.

Sessions are one-shot:

- `CommitAsync` executes pending writes and closes the session.
- `AbandonAsync` drops pending writes and closes the session.
- Any exception during commit closes the session and is rethrown.
- After closure, public reads and writes throw.

## Commit Pipeline

The runtime records both chronological commands and compact state:

```text
Generated property/method write
        |
        v
SurrealSession.Record(Command)
        |
        +--> CommandLog
        +--> PendingState
                |
                v
CommitPlanner.Build(...)
                |
                v
SurrealCommandEmitter.Emit(...)
                |
                v
ISurrealTransport.ExecuteAsync(...)
```

`PendingState` folds repeated changes into final intent. `CommitPlanner` then:

1. Resolves reference delete behavior with the model-specific `IReferenceRegistry`.
2. Applies `[Cascade]` and `[Unset]` to a fixpoint.
3. Throws `CommitPlanRejectException` for remaining `[Reject]` blockers.
4. Orders commands into stable phases.

The planned phases are:

1. Deletes before recreate.
2. Creates and upserts.
3. Field updates and unsets.
4. Relation removals.
5. Relation additions.
6. Final deletes.

`SurrealCommandEmitter` renders the final command list to a single SurrealQL script plus parameters.

## Writer Coordination

`WriterLease` is optional but intended for cross-process write coordination. The protocol is **optimistic concurrency on a monotonic sequence**: each aggregate has one `writer_lease:<slug>` record holding a single `seq: int`. The aggregate name is validated as a lower_snake_case slug via `RecordIdFormat` at acquire time.

The typical write flow is:

```csharp
var lease = await WriterLease.AcquireAsync(transport, "design");
var session = await workspace.LoadDesignAsync(transport, id);

// mutate

await session.CommitAsync(transport, lease);
```

`AcquireAsync` reads the current `seq` for the aggregate (defaulting to 0 if no row exists) and captures it on the lease. `CommitAsync` splices a transactional CAS clause around the data writes — `BEGIN TRANSACTION; IF current_seq != captured THEN THROW … END; UPSERT seq + 1;` — so the commit lands atomically with a sequence advance, or aborts entirely on mismatch and the caller sees `WriterLeaseStolenException`. The session closes either way.

There is no TTL, no holder id, no theft-recovery timer. Crashed writers are forgotten on the spot — they leave their captured seq in memory only, which is invisible to everyone else; the next acquirer just reads the current seq fresh and proceeds. `DisposeAsync` is a no-op.

This is optimistic concurrency, not pessimistic locking. Two writers can both `AcquireAsync` the same aggregate and both mutate locally; only the first to commit wins, the second gets `WriterLeaseStolenException` at commit time and must reload-and-retry. Suits the library's one-shot session character: load → mutate → commit happens fast enough that races are rare and retries are cheap.

## Transport Boundary

Generated code depends only on `ISurrealTransport`:

```csharp
Task<JsonDocument> ExecuteAsync(string sql, object? vars = null, CancellationToken ct = default);
```

`SurrealHttpClient` is the included implementation. It:

- Signs in to SurrealDB via `/signin` and caches the bearer token.
- Posts JSON-RPC 2.0 envelopes (`{"method": "query", "params": [<sql>, {}]}`) to `/rpc`.
- Inlines the supplied `vars` into the SurrealQL via `SurrealFormatter` rather than passing them in `params[1]`. SurrealDB's JSON-RPC binder treats record-shaped objects as generic `Object`s, not `Thing`s, so a query like `WHERE id = $p0` bound through JSON vars never matches; SurrealQL literal syntax (`table:value`) is parsed at the query level and preserves type. The bypass also lifts the per-batch statement ceiling that `LET $p0 = …;` prefixes hit on large commits.
- Applies namespace and database headers.
- Extracts `result` from the RPC response envelope; surfaces typed errors via `SurrealException` / `WriterLeaseStolenException`.
- Retries selected retryable transport failures.
- Provides `SqlAsync` and `SurrealResultSet` for direct SQL use.

Applications can replace it with another transport for tests, logging, retry policy, or alternate SurrealDB connectivity.

## Reference Delete Behavior

Reference behavior is split between schema and commit planning.

Generated schema emits SurrealDB `REFERENCE ON DELETE` clauses. The runtime also plans deletes before sending SQL so application commits fail early and consistently:

- `[Reject]`: block delete when a surviving record still references the target.
- `[Unset]`: clear nullable references to a deleted target.
- `[Cascade]`: delete records that reference the deleted target.
- `[Ignore]`: leave references unchanged.

Cascade-only cycles are rejected at compile time.

## Concurrency And Consistency Boundaries

The library provides snapshot-style application sessions, not long-lived live objects. A loaded session does not automatically observe database changes after load. A committed session cannot be reused. Conflicting writers should coordinate through `WriterLease` or an application-level strategy.

The generated loader hydrates one aggregate snapshot. Cross-aggregate relation properties expose ids, so the caller explicitly decides when to load another aggregate.

## Extension Points

Useful extension points for consumers:

- Add constructors, services, caches, and application methods to the `[CompositionRoot]` partial.
- Add domain methods to entity partials and call protected `Session`.
- Implement `OnCreate{Name}` hooks for mandatory references.
- Implement `OnDeleting` hooks for custom dependent cleanup.
- Implement `ISurrealTransport` for custom database access.
- Use `Workspace.Schema` directly for migration tooling.
- Use `RenderBatch()` for debugging generated commit SQL.

Useful extension points for maintainers:

- Add scalar mappings in `SchemaEmitter`.
- Add accepted id value forms by extending `RecordIdFormat.Validate` (today: 26-char Ulid stringifications and ≤32-char lower_snake_case slugs only).
- Extend relation marker classes when edge payload support is added.
- Extend diagnostics before adding new emitted shapes.
