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
- Relation attribute classes deriving from `ForwardRelation`, `ForwardRelation<TPayload>`, or `InverseRelation<TForward>`. Generic forwards carry a payload type whose public scalar properties are harvested at extraction time and emitted as `DEFINE FIELD` lines on the relation table.

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
| `EdgePredicateFactoryEmitter` | Per forward kind with a typed payload — `{Kind}EdgeQ` static class with `PropertyExpr<T>` per payload field. |
| `PredicateFactoryEmitter` | Per-table `{Name}Q` static class — typed `PropertyExpr<T>` per scalar column. |
| `IdsAsyncEmitter` | Per-table `{Name}QueryIds` static class with the `IdsAsync` extension on `Query<{Table}>` — typed id-only selection terminal. |
| `TraversalBuilderEmitter` | Per-table `{Name}TraversalBuilder` plus the `{Name}QueryIncludes` extensions on `Query<T>`. |
| `LoadEntryEmitter` | `LoadAsync` extension on `Query<TRoot>` for aggregate roots — delegates to the legacy loader for empty-`Includes`, runs the compiler-driven path otherwise. |
| `HydrateRootEmitter` | `Workspace.Hydrate` accessor + per-table `{Table}(ids)` factories returning `HydrationQuery<T>` (typed and raw `IRecordId` overloads). |

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

Five terminal verbs share one query AST. Each picks a different materialisation contract; the AST (predicates, ordering, paging, includes, pinned id) doesn't change between them.

- `ExecuteAsync(transport)` — read mode. Compiles the predicate + traversal AST to SurrealQL, executes it, hydrates root entities (and any traversed children) into an internal `SurrealSession`. Returns the root list; the session is opaque.
- `IdsAsync(transport)` — id-only selection. Compiles to `SELECT id FROM table …` and projects each returned `RecordId` into the typed `{Table}Id`. Includes are rejected — id-only selection is flat by definition. Generator-emitted per `[Table]` via `IdsAsyncEmitter`; runtime body lives on `Query<T>.CompileIdsOnly()` plus the per-table extension.
- `Select(projection).ExecuteAsync(transport)` — projection mode. Compiles to `SELECT field1, field2 FROM table …` (only the columns the projection's lambda touches) and runs the lambda once per row to materialise immutable user-defined `TRow` instances. The library does NOT generate projection types; the user owns `TRow` and the materialise lambda. Discovery runs the lambda once at construction time with a probe row that captures the SELECT field list.
- `LoadAsync(transport, lease)` — write mode, only on aggregate roots. Same compile-and-hydrate path against a session built with `Workspace.ReferenceRegistry`. Returns the session for mutation + commit. With no `Include*` calls, delegates to the legacy `{Root}AggregateLoader` so the full-aggregate path keeps its single-query shape.
- `Workspace.Hydrate.{Table}(ids).WithInclude(...).ExecuteAsync(transport, [lease])` — hydration terminal. Pairs with `IdsAsync`: takes a list of ids and materialises each into a tracked session along with included slices. Reuses `Query<T>`'s compiler+sink pipeline by composing an `InPredicate` over the id column — single round-trip, identical wire SQL to `Query<T>.Where(IdIn).WithInclude(...).ExecuteAsync`. Two ExecuteAsync overloads: read-mode (no lease) and write-mode (lease required at the call site).

Compilation:

```text
Predicate AST + Includes AST + PinnedId
        |
        v
QueryCompiler.Compile
        |
        +--> SurrealQL with all values inlined as literals via SurrealFormatter:
             SELECT *, field.*, (SELECT … FROM child WHERE parent = $parent.id) AS …
             WHERE description = "x" AND id = constraints:01HX…
        |
        v
SurrealHttpClient (posts JSON-RPC, params[1] = {})
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
ISurrealTransport.ExecuteAsync(string sql)
        (or ISurrealExecutor.ExecuteAsync(SurrealCommand) — both
        production transports implement both interfaces)
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

Fresh records (Create intent + no prior existence) fold their pending sets into a single `CREATE record:id CONTENT { … }` statement. SurrealDB's `TYPE RELATION ENFORCED` validates relation endpoints against the in-progress transactional state at the moment of `RELATE`; the single-statement form lands the endpoint fully populated before any relation-creating statement runs, where a bare `CREATE id;` followed by per-field `UPDATE id SET …` (or `UPSERT`) can leave the record in a state the enforcer rejects.

Edge uniqueness is enforced at the schema layer: every relation table is emitted with a `DEFINE INDEX … COLUMNS in, out UNIQUE`. The runtime always emits an explicit edge id; the strategy is carried by the `RecordId edge` argument to `Relate`:

- **Random** — `RecordId.New(table)` mints a Ulid client-side; renders as `RELATE src->edge_table:<ulid>->tgt`. Each fresh Ulid is distinct, so re-running on the same triple after commit collides with the UNIQUE INDEX.
- **Slug** — `new RecordId(table, slug)` carries a user-chosen stable name; renders as `RELATE src->edge_table:<slug>->tgt`. Re-running with the same slug is idempotent.
- **Idempotent** — `RecordId.Idempotent(table)` defers the value to emit time, where it resolves to `HashText("{src}|{table}|{tgt}")`. Same triple always lands on the same row; no reload required.

The typed-kind shorthand `session.Relate<TKind>(src, tgt)` defaults to the Idempotent strategy. The commit planner's `ExistedAtStart` check still skips `RELATE` for edges loaded as part of the aggregate, avoiding the round-trip even when the strategy would have been a no-op.

`SurrealCommandEmitter` renders the final command list to a single SurrealQL script plus parameters.

## Writer Coordination

`WriterLease` is optional but intended for cross-process write coordination. **Single-writer paradigm** — one `writer_lease:main` record per workspace gates every commit, regardless of which aggregate the commit touches. There is no per-aggregate slug; concurrent writers across all aggregates race for the same lease.

The protocol is **optimistic concurrency on a monotonic sequence**: the lease row holds a single `seq: int`. The workspace's generated `[CompositionRoot]` exposes `AcquireWriterAsync(transport)` as the canonical acquisition surface.

The typical write flow is:

```csharp
await using var lease = await workspace.AcquireWriterAsync(transport);
var session = await workspace.LoadDesignAsync(transport, id);

// mutate

await session.CommitAsync(transport, lease);
```

`AcquireAsync` reads the current `seq` (defaulting to 0 if no row exists) and captures it on the lease. `CommitAsync` splices a transactional CAS clause around the data writes — `BEGIN TRANSACTION; IF current_seq != captured THEN THROW … END; UPSERT seq + 1;` — so the commit lands atomically with a sequence advance, or aborts entirely on mismatch and the caller sees `WriterLeaseStolenException`. The session closes either way.

There is no TTL, no holder id, no theft-recovery timer, no aggregate slug. Crashed writers are forgotten on the spot — they leave their captured seq in memory only, which is invisible to everyone else; the next acquirer just reads the current seq fresh and proceeds. `DisposeAsync` is a no-op.

This is optimistic concurrency, not pessimistic locking. Two writers can both acquire and both mutate locally; only the first to commit wins, the second gets `WriterLeaseStolenException` at commit time and must reload-and-retry. Suits the library's one-shot session character: load → mutate → commit happens fast enough that races are rare and retries are cheap.

## Transport Boundary

Two parallel interfaces — the legacy stringly one and the next-generation parameter-aware one. Generated code talks through the legacy interface today; both production transports implement both.

```csharp
public interface ISurrealTransport : IAsyncDisposable
{
    Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default);
}

public sealed record SurrealCommand(
    string Sql,
    IReadOnlyDictionary<string, object?>? Parameters = null);

public interface ISurrealExecutor : IAsyncDisposable
{
    Task<JsonDocument> ExecuteAsync(SurrealCommand command, CancellationToken ct = default);
}
```

`ISurrealTransport` was the v1 boundary: a SurrealQL string in, a `JsonDocument` out, no separate parameter dictionary. Every value (record ids, strings, numbers) is rendered as a SurrealQL literal by `SurrealFormatter` at the call site (`QueryCompiler`, `SurrealCommandEmitter`) before reaching the transport. SurrealDB's JSON-RPC binder treats record-shaped objects as generic `Object`s rather than `Thing`s, so a query like `WHERE id = $p0` bound through JSON vars never matches; SurrealQL literal syntax (`table:value`) is parsed at the query level and preserves type. Inlining at the codegen layer also lifts the per-batch statement ceiling that `LET $p0 = …;` prefixes hit on large commits.

`ISurrealExecutor` is the future-direction boundary that takes `SurrealCommand` instead. Same return shape, richer input shape — opens the door for parameter-aware execution paths (embedded engines that bind natively, future HTTP/JSON-RPC paths that don't lose `Thing` types) without touching today's inlined-literal callers. The runtime keeps using `ISurrealTransport.ExecuteAsync(string sql)` for now; per-callsite migration lands as use cases need parameter-aware execution. Callers holding an `ISurrealTransport` can up-cast via `transport.AsExecutor()` — returns the same instance when the transport already implements both, otherwise wraps it in `SurrealTransportExecutorAdapter`.

`SurrealHttpClient` (in `Disruptor.Surface.Transport.Http`) is the over-the-network implementation. It:

- Signs in to SurrealDB via `/signin` and caches the bearer token.
- Posts JSON-RPC 2.0 envelopes (`{"method": "query", "params": [<sql>, {}]}`) to `/rpc` — `params[1]` is always the empty bindings object.
- Applies namespace and database headers.
- Extracts `result` from the RPC response envelope; surfaces typed errors via `SurrealException` / `WriterLeaseStolenException`.
- Re-auths once on a 401 (the request never reached the server's SQL execution stage). Does not retry arbitrary failures — automatically retrying mutating commits is unsafe under unknown-outcome failure modes.
- Provides `SqlAsync` and `SurrealResultSet` for direct SQL use.

`SurrealEmbeddedTransport` (in `Disruptor.Surface.Transport.Embedded`) is the in-process implementation backed by SurrealDB embedded with a RocksDB file store. Side-steps the HTTP body-size ceiling that bites large commits (code-index full rebuilds, bulk imports). Single-process only; uses a `SemaphoreSlim` to gate `BEGIN TRANSACTION` scripts so concurrent writers serialise (single-writer paradigm, same as the cross-process `WriterLease`).

Applications can replace either with a custom transport for tests, logging, alternate SurrealDB connectivity, or layered policy. Implementing both `ISurrealTransport` and `ISurrealExecutor` directly avoids the wrapping adapter for callers that hold one or the other.

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
