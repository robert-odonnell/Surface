# Architecture

Disruptor.Surface has two halves:

- A Roslyn source generator that reads attributed C# model types and emits model-specific code.
- A small runtime that provides sessions, hydration helpers, ids, schema execution, and per-entity Save dispatch over the `Disruptor.Surreal` SDK.

The design goal is model-specific code at compile time and explicit runtime boundaries at execution time. The generator knows your aggregate graph and can produce strongly typed code. The runtime stays generic, has no transport layer of its own, and does not use process-global model state.

## High-Level Flow

```text
C# model declarations
        |
        v
Roslyn generator
        |
        +--> typed ids
        +--> entity partial implementations  (Hydrate, SaveAsync, Bind, …)
        +--> relation kind markers
        +--> aggregate loaders
        +--> schema chunks
        +--> reference registry
        |
        v
Consumer application
        |
        +--> SurrealClient.ConnectAsync(...)
        +--> Workspace.ApplySchemaAsync(db)
        +--> Workspace.Load{Root}Async(db | tx, id)
        +--> mutate generated entities (sync, in-memory)
        +--> tx = await db.BeginTransactionAsync()
        +--> session.SaveAsync(entity, tx)
        +--> tx.CommitAsync()
        |
        v
SurrealDB
```

## Generator Pipeline

The generator scans the compilation for:

- `[Table]` classes.
- `[AggregateRoot]` markers.
- `[CompositionRoot]` class.
- Properties with `[Id]`, `[Property]`, `[Reference]`, `[Parent]`, `[Children]`, and relation attributes (the latter on entity properties that expose a typed read collection).
- Relation attribute classes deriving from `ForwardRelation` or `InverseRelation<TForward>`.
- **Relation variant classes** (preview.51) — classes annotated with a relation kind attribute (e.g. `[Restricts]`) carrying `[In]` / `[Out]` endpoint properties and zero-or-more `[Property]` payload members. Multiple variants per kind give a `SCHEMALESS` edge table; one variant gives `SCHEMAFULL` with the variant's payload columns.

It builds a model graph containing tables, properties, aggregate membership, relation kinds, relation unions, and reference metadata. Diagnostics are reported before emission when the model shape cannot be generated safely.

Main emitter responsibilities:

| Emitter | Output |
| --- | --- |
| `IdEmitter` | `{Table}Id` typed record structs implementing `IRecordId`. |
| `PartialEmitter` | Entity partial implementations: pure-backing-field property setters, session binding, hydration (`Hydrate(SurrealValue, IHydrationSink)` writes directly into backing fields), `SaveAsync(ISaveContext, ct)` dispatch, `GetParentId()`, delete hooks, and slice-guard read paths. |
| `RelationKindEmitter` | `IRelationKind` marker class per forward relation attribute, plus the per-kind `{KindName}Id` typed-id struct (e.g. `RestrictsId`) shared across every variant of the kind. For multi-variant kinds, also emits the per-kind variant marker interface `I{KindName}Variant`. |
| `RelationVariantEmitter` | `IEntity` partial implementation per relation variant class (`[Restricts] partial class ConstraintRestrictsUserStory`): `IRelationVariant` marker, `[In]`/`[Out]` setter dispatch, `IEntity.SaveAsync` body that issues `INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY UPDATE …]`, and per-kind `{KindName}Hydration.HydrateVariant(SurrealValue, IHydrationSink)` dispatcher (single-variant gets one too for call-site uniformity; multi-variant branches on `(in.tb, out.tb)`). |
| `UnionInterfaceEmitter` | Shared interfaces for relation target/source unions when one relation can point at multiple table types. |
| `AggregateLoaderEmitter` | Internal loader per aggregate root, with two `PopulateAsync` overloads (Surreal db / Transaction tx). |
| `CompositionRootEmitter` | `Load{Root}Async` methods on the user's composition root — two overloads per aggregate root (db / tx). |
| `SchemaEmitter` | Ordered idempotent SurrealDB DDL chunks plus `ApplySchemaAsync(db)` / `ApplySchemaAsync(tx)`. Multi-variant relation kinds get `SCHEMALESS` edge tables; single-variant kinds keep `SCHEMAFULL`. |
| `ReferenceRegistryEmitter` | Model-scoped reference metadata. |
| `QueryRootEmitter` | `Workspace.Query` accessor + per-table `Query<T>` roots. |
| `EdgeQueryRootEmitter` | `Workspace.Query.Edges.{Kind}` accessors with id-side type parameters. |
| `PredicateFactoryEmitter` | Per-table `{Name}Q` static class — typed `PropertyExpr<T>` per scalar column. (Variant classes are entities, so they get a `{Variant}Q` factory the same way table classes do — payload columns are addressable as ordinary entity properties.) |
| `IdsAsyncEmitter` | Per-table `{Name}QueryIds` static class with the `IdsAsync` extension on `Query<{Table}>` — typed id-only selection terminal. Two overloads (db / tx). |
| `TraversalBuilderEmitter` | Per-table `{Name}TraversalBuilder` plus the `{Name}QueryIncludes` extensions on `Query<T>`. |
| `LoadEntryEmitter` | `LoadAsync` extension on `Query<TRoot>` for aggregate roots — two overloads (db / tx); delegates to the legacy loader for empty-`Includes`, runs the compiler-driven path otherwise. |
| `HydrateRootEmitter` | `Workspace.Hydrate` accessor + per-table `{Table}(ids)` factories returning `HydrationQuery<T>` (typed and raw `IRecordId` overloads). |

## Model Mapping

Entity tables use lower snake case plural names. For example, `DesignChange` maps to `design_changes`. Property fields use lower snake case names. For example, `RepositoryRoot` maps to `repository_root`.

Each `[Table]` maps to a SurrealDB `SCHEMAFULL` table. The generated schema includes:

- Table declarations.
- Scalar field declarations for `[Property]`.
- `array<object>` fields plus per-member `field.*.member` sub-field DDL for inline element collections (`[Property] partial IReadOnlyList<T>` etc.).
- `record<target>` fields for `[Reference]`.
- `option<record<target>>` for nullable references.
- Parent fields with `REFERENCE ON DELETE REJECT`.
- Computed reverse fields for `[Children]`.
- Relation tables for forward relation kinds, with `DEFINE INDEX … COLUMNS in, out UNIQUE`.

`[Reference, Inline]` is the owned-sidecar carve-out. The aggregate loader expands that reference with `field.*` and hydrates the linked row in the same session. Plain `[Reference]` stores and hydrates the id only.

## Aggregate Boundaries

An aggregate is a `[AggregateRoot]` plus every entity reachable by walking `[Children]`. The generator enforces that a table belongs to at most one aggregate.

Aggregate membership affects:

- Which generated `Load{Root}Async` methods exist.
- Which tables a nested loader query includes.
- Whether a relation property returns entities or ids (within-aggregate vs cross-aggregate).
- The natural recursion shape of `IEntity.SaveAsync` — children dispatched by Save are the aggregate's `[Children]` graph.

Aggregates are *not* concurrency boundaries. Concurrent writers across all aggregates collide at COMMIT as native `SurrealConflictException` from the SDK; there is no per-aggregate or workspace-wide lease.

Cross-aggregate links should be modeled as relation kinds, not `[Reference]` fields. A `[Reference]` from one aggregate to another produces `CG021`.

## Loading

For every aggregate root, the generator emits an internal loader such as `DesignAggregateLoader` with two `PopulateAsync` overloads — one taking `Surreal db` (read-only), one taking `Transaction tx` (write-mode load that runs the query inside the txn so it sees in-txn writes).

The generated composition-root method is thin:

```csharp
public async Task<SurrealSession> LoadDesignAsync(
    Disruptor.Surreal.SurrealClient db,
    DesignId rootId,
    CancellationToken ct = default)
{
    var ws = new SurrealSession(ReferenceRegistry);
    await DesignAggregateLoader.PopulateAsync(ws, db, rootId, ct);
    return ws;
}

public async Task<SurrealSession> LoadDesignAsync(
    Disruptor.Surreal.SurrealTransaction tx,
    DesignId rootId,
    CancellationToken ct = default)
{
    var ws = new SurrealSession(ReferenceRegistry);
    await DesignAggregateLoader.PopulateAsync(ws, tx, rootId, ct);
    return ws;
}
```

The loader performs one nested SurrealQL query rooted at the aggregate id:

- The root row selects `*`.
- Inline references are projected with `field.*`.
- Child tables are loaded with subselects scoped back to the root through parent paths.
- Edge tables touching the aggregate are loaded into edge arrays.

Hydration is routed through `IHydrationSink`, an explicit interface on `SurrealSession`, and consumes the SDK's `Disruptor.Surreal.Values.SurrealValue` directly — no JSON intermediary. `HydrationValue` provides the Value-native helpers (`ReadRecordId`, `ReadString`, `ReadOrDefault<T>`, `HydrateReference<T>`) that emitted `IEntity.Hydrate` bodies call into.

## Query+Load Surface

Five terminal verbs share one query AST. Each picks a different materialisation contract; the AST (predicates, ordering, paging, includes, pinned id) doesn't change between them. Every terminal accepts either `Surreal db` (read-only) or `Transaction tx` (in-txn read with full visibility into pending writes from the same transaction).

- `ExecuteAsync(db | tx)` — read mode. Compiles the predicate + traversal AST to SurrealQL, executes it, hydrates root entities (and any traversed children) into an internal `SurrealSession`. Returns the root list; the session is opaque.
- `IdsAsync(db | tx)` — id-only selection. Compiles to `SELECT id FROM table …` and projects each returned `RecordId` into the typed `{Table}Id`. Includes are rejected — id-only selection is flat by definition.
- `Select(projection).ExecuteAsync(db | tx)` — projection mode. Compiles to `SELECT field1, field2 FROM table …` (only the columns the projection's lambda touches) and runs the lambda once per row to materialise immutable user-defined `TRow` instances. The library does NOT generate projection types; the user owns `TRow` and the materialise lambda. Discovery runs the lambda once at construction time with a probe row that captures the SELECT field list.
- `LoadAsync(db | tx)` — write-mode session, only on aggregate roots. Same compile-and-hydrate path against a session built with `Workspace.ReferenceRegistry`. Returns the session for mutation + dispatch via `SaveAsync`. With no `Include*` calls, delegates to the legacy `{Root}AggregateLoader` so the full-aggregate path keeps its single-query shape.
- `Workspace.Hydrate.{Table}(ids).WithInclude(...).ExecuteAsync(db | tx)` — hydration terminal. Pairs with `IdsAsync`: takes a list of ids and materialises each into a tracked session along with included slices. Reuses `Query<T>`'s compiler+sink pipeline by composing an `InPredicate` over the id column — single round-trip, identical wire SQL to `Query<T>.Where(IdIn).WithInclude(...).ExecuteAsync`.

Compilation:

```text
Predicate AST + Includes AST + PinnedId
        |
        v
QueryCompiler.Compile
        |
        +--> (sql, bindings) — SurrealQL with $_pN placeholders + a typed-CBOR
             SurrealObject with each leaf value wrapped as the right SurrealValue
             variant (records become SurrealRecordIdValue, IN lists become
             SurrealListValue, etc.):
             SELECT *, field.*, (SELECT … FROM child WHERE parent = $parent.id) AS …
             WHERE description = $_p0 AND id = $_p1
             bindings = { _p0: "x", _p1: SurrealRecordIdValue(constraints:01HX…) }
        |
        v
Disruptor.Surreal.SurrealClient.QueryAsync(sql, bindings)
(or SurrealTransaction.QueryAsync — same shape inside a transaction)
        |
        +--> CBOR over WebSocket → SurrealQueryResponse → SurrealValue tree
```

Identifiers (table names, field names, edge names, slice keys) stay inlined in the SQL
— they're trusted, regex-validated by `SurrealFormatter.Identifier`. `LIMIT` / `START`
integers also stay inlined (no escape concern). User-supplied values always go via the
typed bindings. Same goes for the per-entity Save dispatch and Delete / Unrelate paths —
they call `tx.CreateAsync(id, content)` / `tx.UpsertAsync` / `tx.DeleteAsync(id)`
directly with typed arguments, and the relation-variant `SaveAsync` body issues
`INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY UPDATE …]` via `tx.QueryAsync`
with a typed `SurrealObject` binding. No SurrealQL string formatting, no JSON anywhere on
the wire path.

Generator-emitted hydrator delegates on each `IncludeChildrenNode` capture the right concrete `new TChild()` + `Hydrate(SurrealValue, IHydrationSink)` at codegen time, so the runtime walks the `SurrealValue` tree without reflection. `Fetch` reuses the same compile-and-hydrate path: existing tracked entities re-Hydrate (slice-widening; scalar fields get clobbered with the DB row, no merge guard); brand-new entities get the include's full Hydrate.

Strict-with-escape: filtered loads only mark the user-`Include`d slices loaded; reads outside the slice throw `LoadShapeViolationException` with a hint at `session.FetchAsync(...)`. The legacy aggregate-loader path marks every slice on every loaded entity, so existing code stays unaffected.

## Session Lifecycle

`SurrealSession` is a snapshot-isolated entity store. It has no ambient static current session. Generated entities hold a reference to the session they are bound to.

Lifecycle for fresh entities:

```text
session.Track(entity)
        |
        +--> bind entity to session
        +--> Initialize mandatory references (OnCreate{Name} hooks; idempotent)
        +--> mark every slice loaded (fresh entity owns its full state)
```

Object-initializer writes land directly in the entity's backing fields — no buffer, no
flush phase. The one cascade in a sync setter is `[Parent]`'s
`parent.Session.AdoptIfUnbound(this)`, which pulls a freshly constructed
`new Constraint { Design = design }` into design's session.

Lifecycle for loaded entities:

```text
Load{Root}Async(db | tx, id)
        |
        +--> create entity instance
        +--> hydrate fields from Value tree (via IEntity.Hydrate)
        +--> bind through IHydrationSink
        +--> mark as loaded-at-start in the identity map
```

Reads resolve directly off the entity's backing fields (`[Property]`, `[Reference]`, `[Parent]` — the latter two with a `Session.Get<T>(id)` fall-through when only the id is cached) or via the session for cross-entity navigation (`[Children]`, relations). Sync writes are property setters and `Track`. The `CommandLog` records `Track`, `Unrelate`, and `Delete` intents for diagnostics — property setters do **not** record (relation creates land as ordinary entity Save records, since variants are `IEntity`). **No database call happens until you call an async dispatch method:**

- `session.SaveAsync(entity, tx, ct)` — per-entity Save. Auto-binds the entity, walks its forward dependencies (Reference / Parent) recursively, dispatches a whole-entity `CREATE/UPDATE record:id CONTENT { ... }`, then walks new children recursively. **Relation variants are entities too** — passing a variant instance routes through the variant's emitted `IEntity.SaveAsync` body, which dispatches `INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY UPDATE …]`.
- `session.DeleteAsync(entity, tx, ct)` — runs `OnDeleting`, dispatches a single `DELETE`, removes from the in-memory snapshot.
- `session.UnrelateAsync<TKind>(src?, tgt?, tx, ct)` — direct edge deletion (one or both endpoints, the bulk forms hit every matching edge).
- `session.QueryVariantsOutgoingAsync<TVariant>` / `QueryVariantsIncomingAsync<TVariant>` — async traversal returning hydrated variant entities; tracked in the session, edges mirrored in `state.Edges`. `db` overloads + `IEntity` convenience overloads.
- `session.QueryOutgoingAsync<TKind, TTarget>` / `QueryIncomingAsync<TKind, TTarget>` — async traversal returning target entities directly (skips variant materialisation); not auto-tracked. `QueryVariantsAsync<TVariant>(sql, bindings, tx)` is the raw-SQL escape hatch.

A session is reusable: the snapshot stays valid after a SaveAsync and you can keep reading or dispatch more writes (against the same or a different transaction). The transaction lifecycle is the app's responsibility — open with `db.BeginTransactionAsync()`, commit with `tx.CommitAsync()`, or cancel with `tx.CancelAsync()` (auto-cancels on `await using` dispose).

## Save Dispatch

Per-entity `SaveAsync` is generator-emitted, not reflective. `PartialEmitter` walks each `[Table]`'s model and emits a typed body roughly shaped as:

```text
session.SaveAsync(entity, tx)                (the public API)
        |
        v
((IEntity)entity).SaveAsync(ctx, ct)         (emitted per entity)
        |
        +--> for each forward dep (Reference / Inline / Parent):
        |       if (!ctx.IsTracked(dep.Id)) await ctx.SaveAsync(dep, ct);
        |
        +--> dispatch the entity itself via tx.QueryAsync:
        |       CREATE record:id CONTENT { ...full state... }   (when new)
        |       UPDATE record:id CONTENT { ...full state... }   (when loaded-at-start)
        |
        +--> ctx.MarkSaved(entity)
        |
        +--> for each tracked child (new only):
                await ctx.SaveAsync(child, ct);
```

Relation variants are entities too — passing a variant instance to the same `SaveAsync` (e.g. `await session.SaveAsync(new ConstraintRestrictsUserStory { Source = constraint, Target = userStory }, tx)`) routes through the variant's emitted `IEntity.SaveAsync` body, which dispatches `INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY UPDATE …]` and updates `state.Edges` so subsequent in-session reads see the new edge. `IRelationVariant : IEntity` is the methodless marker the session branches on at `MarkSaved` / `CleanupLocalState` time.

`ISaveContext` is the per-Save orchestration interface (Transaction handle, `IsTracked` query, recursion callback, `MarkSaved`). `SurrealSession` implements it privately. The dispatch is whole-entity: every save sends a complete `CONTENT { ... }` payload, no per-field SET/UNSET. Concurrent writers may both touch the same row; SurrealDB's MVCC catches the collision at COMMIT and surfaces `Disruptor.Surreal.SurrealConflictException`.

The library does not change-track. The user picks which roots / variants to call `SaveAsync` on, and the recursion walks from there. New entities are dispatched exactly once per Save pass; existing ones (loaded from the DB) only dispatch when explicitly passed.

## Concurrency Model

There is no application-level lock, lease, or sequence counter. Concurrency is delegated to SurrealDB v3's transaction MVCC:

- App opens a `Transaction` via `db.BeginTransactionAsync()`.
- App reads, mutates, dispatches whatever it likes inside the txn.
- App calls `tx.CommitAsync()`. If another writer's commit landed first inside the same MVCC window, the COMMIT throws `Disruptor.Surreal.SurrealConflictException`.
- The caller catches, reloads the affected aggregate, reapplies its intent, and retries.

This is optimistic concurrency at the substrate level. Two writers can both load and both mutate locally — only the first to commit wins; the second sees the conflict at commit time. Suits the library's snapshot character: load → mutate → save → commit happens fast enough that races are rare and retries are cheap.

`LoadAsync(tx, …)` runs the load query inside the txn so cross-session in-txn writes are visible. Multiple sessions, multiple aggregates, and raw `Disruptor.Surreal` SDK calls can all participate in the same `Transaction` for cross-aggregate atomicity — the boundary is whatever the app decides.

## Wire Boundary

The library does not own a transport. `Disruptor.Surreal` is the SDK — CBOR over WebSocket — and is the only wire layer:

```csharp
using Disruptor.Surreal;
using Disruptor.Surreal.Connection;

await using var db = await SurrealClient.ConnectAsync(SurrealOptions.Parse(
    "Url=ws://localhost:8000;Namespace=app;Database=main;User=root;Password=root"));
```

Generated query terminals call `tx.QueryAsync(sql, bindings)` with typed-CBOR `SurrealObject` bindings — `QueryCompiler` allocates a `$_pN` placeholder per leaf value and pushes it into bindings as the right `SurrealValue` variant (records become `SurrealRecordIdValue`, IN lists become `SurrealListValue`, etc.). Per-entity Save / Delete / Unrelate dispatch goes through the SDK's typed CRUD methods directly: `tx.CreateAsync(id, content)` / `tx.UpsertAsync(id, content)` / `tx.DeleteAsync(id)`. Relation-variant `SaveAsync` issues `INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY UPDATE …]` via `tx.QueryAsync` with a typed `SurrealObject` binding. The wire path is end-to-end CBOR — no SurrealQL formatting for user values, no escape rules, no JSON. Schema DDL stays as text (`tx.QueryAsync(chunk)` per chunk) since DDL is fundamentally text. The aggregate loader's nested SELECT also stays text but the root id flows in via a typed `$_rootId` binding. SurrealDB's wire-binding of record-shaped objects through CBOR preserves `Thing` types correctly — the older JSON-RPC binding limitation that the inlining-via-`SurrealFormatter` workaround addressed doesn't apply on the CBOR transport.

The SDK's `SurrealQueryResponse` and `Disruptor.Surreal.Values.SurrealValue` are the Value-tree return shape; `HydrationValue` walks them in emitted hydration bodies.

## Reference Delete Behavior

Reference delete behavior is split between substrate enforcement (schema) and library prediction (runtime pre-flight). The library predicts; the substrate enforces.

Generated schema emits SurrealDB `REFERENCE ON DELETE` clauses based on the user's attributes:

- `[Reject]`: SurrealDB rejects the delete when a surviving record still references the target. Default behavior.
- `[Unset]`: SurrealDB clears nullable references to the deleted target.
- `[Cascade]`: SurrealDB deletes records that reference the deleted target.
- `[Ignore]`: leave references unchanged.

Cascade-only cycles are rejected at compile time (`CG014`).

`DeleteAsync` runs a three-phase pre-flight via `PlanDelete` (preview.47): Cascade + Unset propagate to fixpoint against the loaded snapshot's reference state, then any steady-state Reject blockers throw `CascadeRejectException` before any wire dispatch happens. Once the plan is clean, the library dispatches a single `DELETE` for the targeted entity and the substrate's `REFERENCE ON DELETE` clauses drive the dependent cleanup. The generator emits `IEntity.EnumerateReferences` and `SetReferenceTo` to feed the planner.

## Concurrency And Consistency Boundaries

The library provides snapshot-style application sessions, not long-lived live objects. A loaded session does not automatically observe database changes after load — re-load (or `FetchAsync` for a slice) when you need fresh data.

The generated loader hydrates one aggregate snapshot per `Load{Root}Async` call. Cross-aggregate relation properties expose ids, so the caller explicitly decides when to load another aggregate.

For atomicity across aggregates, share an app-owned `Transaction` between sessions: open it, pass it into multiple `LoadAsync(tx, …)` calls, dispatch with `SaveAsync(entity, tx)` against each session, then commit once. Conflicts at COMMIT surface as `SurrealConflictException` regardless of how many sessions or aggregates participated.

## Extension Points

Useful extension points for consumers:

- Add constructors, services, caches, telemetry, and application methods to the `[CompositionRoot]` partial.
- Add domain methods to entity partials and call protected `Session` (sync, in-memory) or take a `Transaction` parameter for async dispatch.
- Implement `OnCreate{Name}` hooks for mandatory-reference seeding.
- Implement `OnDeleting` hooks for custom dependent cleanup.
- Use `Workspace.Schema` directly for migration tooling — iterate the chunk list, splice your own DDL where appropriate, run inside your own transaction.
- Use `session.Log` (a `CommandLog`) to inspect captured intent for diagnostics or telemetry.

Useful extension points for maintainers:

- Add scalar mappings in `SchemaEmitter`.
- Add accepted id value forms by extending `RecordIdFormat.Validate` (today: 26-char Ulid stringifications, ≤32-char lower_snake_case slugs, and 24-char content-hash forms).
- Extend `RelationVariantEmitter` / `RelationKindEmitter` when edge payload support evolves.
- Extend diagnostics before adding new emitted shapes.
