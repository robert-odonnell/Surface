# Architecture

> This document is for contributors to Disruptor.Surface itself — folks adding emitters, diagnostics, attributes, or runtime types. Library *users* should read [`intro.md`](intro.md) for the value proposition, [`quickstart.md`](quickstart.md) for a guided model walkthrough, and [`api.md`](api.md) for the user-facing surface.
>
> **Before changing anything under `src/Disruptor.Surface.Generator/Model/` or `src/Disruptor.Surface.Generator/Pipeline/`, read the [Incremental Generator Contract](#incremental-generator-contract) section.** The pipeline relies on record value-equality for cache hits, and the rules aren't enforced by anything but discipline. The engineering log in [`notes.md`](notes.md) is the authoritative running history of what's shipped under which preview tag.

Disruptor.Surface has two halves:

- A Roslyn source generator that reads attributed C# model types and emits model-specific code.
- A small runtime that provides sessions, hydration helpers, ids, schema execution, and per-entity Save dispatch over the `Disruptor.Surreal` SDK.

The design goal is model-specific code at compile time and explicit runtime boundaries at execution time. The generator knows your aggregate graph and can produce strongly typed code. The runtime stays generic, has no transport layer of its own, and does not use process-global model state.

## High-Level Flow

`Workspace` in the diagrams and examples below is the *user's* `[CompositionRoot]` class — whatever name the consumer gave their partial composition root. It's not a library type. Every static accessor the generator grafts onto it (`Workspace.Query`, `Workspace.Schema`, `Workspace.ApplySchemaAsync`, `Workspace.Load{Root}Async`, `Workspace.ReferenceRegistry`, `Workspace.Hydrate`) lives on the user's class.

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

Runtime types referenced throughout this doc (`IEntity`, `IRecordId`, `IRelationKind`, `IRelationVariant`, `IHydrationSink`, `ISaveContext`) are defined in `src/Disruptor.Surface.Runtime/`. [`api.md`](api.md#runtime-types) covers each in detail from the user's perspective; for contributor work, the source files themselves are the canonical reference.

## Generator Pipeline

`ModelGenerator.Initialize` (in `src/Disruptor.Surface.Generator/ModelGenerator.cs`) wires eight `IncrementalValueProvider` streams into one `Combine` cascade, hands the combined input to `RelationLinker.Build` to produce a `ModelGraph`, and registers the emit pass against `RegisterSourceOutput`:

| Stream | How it discovers | Discovered shape | Lives in |
| --- | --- | --- | --- |
| Tables | `ForAttributeWithMetadataName(TableAttribute)` | `TableModel` (name, properties, aggregate-root flag, partial flag) | `TableExtractor` |
| Forward relation kinds | `CreateSyntaxProvider` over class-with-base-list, semantic-model confirm | `RelationKindModel(Direction = Forward)` | `RelationKindExtractor` |
| Inverse relation kinds | Same as forward, filtered by base derivation from `InverseRelation<>` | `RelationKindModel(Direction = Inverse)` | `RelationKindExtractor` |
| Composition root | `ForAttributeWithMetadataName(CompositionRootAttribute)` | `CompositionRootModel` | `CompositionRootExtractor` |
| Relation variants | `CreateSyntaxProvider` over class-with-attribute-list | `RelationVariantModel` (one per `[Restricts]`/`[Calls]`/… on a class) | `RelationVariantExtractor` |
| Union-interface candidates | `CreateSyntaxProvider` over interface-with-attribute-list | `UnionInterfaceCandidate` — interfaces attributed with anything deriving from `In<TKind>` / `Out<TKind>` | `UnionEndpointExtractor` |
| Union-membership candidates | `CreateSyntaxProvider` over per-table marker interfaces with non-empty base lists | `UnionMembershipCandidate` — the user's `partial interface I{Name}RecordId : IFooTarget` opt-ins | `UnionEndpointExtractor` |
| Shared-shape candidates | `CreateSyntaxProvider` over interface-with-base-list | `SharedShapeInterfaceCandidate` — partial interfaces deriving from `IRelationVariant`, plus `Lifted{In,Out,Id,Payload}` lifted from `[In]/[Out]/[Property]/[Id]` on the interface members and (preview.57) every reachable base interface | `SharedShapeExtractor` |

The streams' predicates are syntax-only so the incremental cache keys hash cheaply; the transforms then use the semantic model. Each transform is `static` (no captures) and returns either a value record or `null` (filtered out by `.Where`).

`RelationLinker.Build` is the single point of fan-in. It:

1. **Rewrites `TypeRef`s** so `TypeRef.IsTableType` is true for every type that turned out to be a `[Table]` after the full table set is known. Per-symbol extractors can't see this; they seed `IsTableType` from immediately-visible attributes and the linker patches up forward references.
2. **Computes per-kind union sets** (`Unions`) — the source-side and target-side `[Table]` members for each forward kind. 2+ members → a marker interface emitted by `UnionInterfaceEmitter` (with both an entity-side and an id-side variant).
3. **Computes union endpoints** (`UnionEndpoints`) — stitches the union-interface candidates and per-table membership opt-ins into `UnionEndpointModel`s keyed by interface FQN.
4. **Lifts variant shapes from annotated shared-shape interfaces** (`LiftVariantsFromSharedShape`, preview.56/.57) — each variant's `In/Out/Id/Payload` is merged with every annotated shared-shape candidate it implements (transitive base closure, sorted by FQN). Local self-declared members win for overlapping roles; non-overlapping interface contributions accumulate; hard conflicts (incompatible `Role + Name + Type + IsNullable` for the same property) drop the variant silently.
5. **Computes shared-shape models** (`SharedShapes`) — for each candidate interface, lists every variant whose `ImplementedInterfaceFullNames` includes it.
6. **Computes aggregates and aggregate conflicts** (CG011) by walking `[Children]` from each `[AggregateRoot]`.
7. **Detects cascade-only reference cycles** (CG014) on the `[Reference, Cascade]` subgraph.

The output is a `ModelGraph` record (see [Model graph shape](#model-graph-shape) below).

**Diagnostics fire from three layers.** The split matters when you're adding a new one:

- **Inside an extractor.** Cheap structural checks that don't need cross-table information (e.g. "is this property partial?"). Rare; most extractors stay pure and let the linker decide.
- **Inside `RelationLinker`.** Cross-table checks that need the full model — currently used for CG014 (cascade-only cycles) and the aggregate-conflict computation feeding CG011. The shared-shape lift conflict path (preview.57) also lives here but currently fail-soft drops without a CGxxx; a `CG036` ("shared-shape lift conflict") is the planned follow-up — if you're adding it, thread conflict descriptors out of `TryMergeLift` / `TryMergeSingular` into a new `ModelGraph` array (mirroring `AggregateConflicts` / `CascadeCycles`) and have `ModelGenerator.Emit` walk it.
- **Inside `ModelGenerator.Emit`.** Everything else. The bulk of the per-table validation (CG001, CG008, CG022, CG024–CG028) lives here in long sequential loops; cross-aggregate reference checks (CG021), composition-root presence (CG018/CG019), and per-aggregate parent-reachability (CG020) also fire here. Shared-shape diagnostics (CG033, CG035) fire late, after `SharedShapeEmitter` has run.

Most contributors adding a diagnostic want the `ModelGenerator.Emit` layer — the model is fully linked, the source-production-context is in hand, and the convention is established. Add a descriptor in `Pipeline/Diagnostics.cs` and a `spc.ReportDiagnostic(...)` call at the appropriate loop body.

Main emitter responsibilities:

| Emitter | Output |
| --- | --- |
| `IdEmitter` | `{Table}Id` typed record structs implementing `IRecordId`, plus the per-table `I{Name}RecordId` marker interface alongside (the surface user partials extend to enrol a table in a union endpoint). |
| `PartialEmitter` | Entity partial implementations: pure-backing-field property setters, session binding, hydration (`Hydrate(SurrealValue, IHydrationSink)` writes directly into backing fields), `SaveAsync(ISaveContext, ct)` dispatch, `GetParentId()`, delete hooks, and slice-guard read paths. |
| `RelationKindEmitter` | `IRelationKind` marker class per forward relation attribute, plus the per-kind `{KindName}Id` typed-id struct (e.g. `RestrictsId`) shared across every variant of the kind. For multi-variant kinds, also emits the per-kind variant marker interface `I{KindName}Variant`. |
| `RelationVariantEmitter` | `IEntity` partial implementation per relation variant class (`[Restricts] partial class ConstraintRestrictsUserStory`): `IRelationVariant` marker, `[In]`/`[Out]` setter dispatch, `IEntity.SaveAsync` body that issues `INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY UPDATE …]`, and per-kind `{KindName}Hydration.HydrateVariant(SurrealValue, IHydrationSink)` dispatcher (single-variant gets one too for call-site uniformity; multi-variant branches on `(in.tb, out.tb)`; union endpoints Cartesian-expand across member tables). Property emission gates on `RelationVariantPropertyModel.IsPartial`: self-declared variant props emit `partial T Name`, lifted-from-interface props emit full auto-property declarations satisfying the interface contract via partial-class declaration merging (preview.56+). |
| `UnionInterfaceEmitter` | Auto-emitted shared interfaces for relation target/source unions when one relation kind can point at multiple table types (2+ members). Emits BOTH the entity-side marker (`IRestrictedBy`) AND the id-side marker (`IRestrictedById`). Distinct from *user-declared* union-endpoint interfaces (those interfaces are user-owned; the generator stitches per-table opt-ins onto them via `UnionEndpoints` in `ModelGraph`). |
| `SharedShapeEmitter` | Partial fragment per user-declared `partial interface I... : IRelationVariant` carrying a `static {I} Create<TKind>(Action<{I}> init) where TKind : IRelationKind` factory. Body is an if-chain dispatching on `typeof(TKind)` to instantiate the matching variant. |
| `AggregateLoaderEmitter` | Internal loader per aggregate root, with two `PopulateAsync` overloads (Surreal db / Transaction tx). |
| `CompositionRootEmitter` | `Load{Root}Async` methods on the user's composition root — two overloads per aggregate root (db / tx). |
| `SchemaEmitter` | Ordered idempotent SurrealDB DDL chunks plus `ApplySchemaAsync(db)` / `ApplySchemaAsync(tx)`. Multi-variant relation kinds get `SCHEMALESS` edge tables; single-variant kinds keep `SCHEMAFULL`. Union-endpoint `FROM` / `TO` clauses expand to every member table. |
| `ReferenceRegistryEmitter` | Model-scoped reference metadata + a `Workspace.ReferenceRegistry` static accessor on the user's composition root. |
| `QueryRootEmitter` | `Workspace.Query` accessor + per-table `SurfaceQuery<T>` roots. |
| `EdgeQueryRootEmitter` | `Workspace.Query.Edges.{Kind}` accessors with id-side type parameters (per-table id for single-target sides, generated id-side union marker for multi-target sides). |
| `PredicateFactoryEmitter` | Per-table `{Name}Q` static class — typed `PropertyExpr<T>` per scalar column. Variant classes are entities, so they get a `{Variant}Q` factory the same way table classes do — payload columns are addressable as ordinary entity properties. |
| `IdsAsyncEmitter` | Per-table `{Name}QueryIds` static class with the `IdsAsync` extension on `SurfaceQuery<{Table}>` — typed id-only selection terminal. Two overloads (db / tx). |
| `TraversalBuilderEmitter` | Per-table `{Name}TraversalBuilder` plus the `{Name}QueryIncludes` extensions on `SurfaceQuery<T>`. |
| `LoadEntryEmitter` | `LoadAsync` extension on `SurfaceQuery<TRoot>` for aggregate roots — two overloads (db / tx); delegates to the legacy loader for empty-`Includes`, runs the compiler-driven path otherwise. |
| `HydrateRootEmitter` | `Workspace.Hydrate` accessor + per-table `{Table}(ids)` factories returning `HydrationQuery<T>` (typed and raw `IRecordId` overloads). |

## Incremental Generator Contract

Roslyn's incremental generator pipeline caches the output of every stage by **value-equality on the stage's record type**. If a downstream stage's input is `Equals`-identical to the previous run's input, Roslyn reuses the cached output and skips the work. Get this wrong and you silently regress to a non-incremental build: every keystroke in a consumer project re-runs every emitter.

**The contract, in three rules:**

1. **Every collection on every model record must be `EquatableArray<T>`.** Never `ImmutableArray<T>`, never `List<T>`, never `IEnumerable<T>`. The BCL collection types compare by *reference* — two structurally-identical immutable arrays test as unequal — which busts the cache on every keystroke. `EquatableArray<T>` (in `src/Disruptor.Surface.Generator/Model/EquatableArray.cs`) is a value-equal wrapper. The constraint is `where T : IEquatable<T>`, so the element type also has to opt in.
2. **Model records are frozen.** No mutable fields, no `set;`-able properties, no lazy caches, no `[NonSerialized]`-style escape hatches. C# record equality synthesises over *every* field; a lazy cache participates whether you want it to or not. If you need a lookup helper, expose it as a method on the record body that recomputes (see `ModelGraph.BuildTableIndex` for the pattern).
3. **Predicates on `CreateSyntaxProvider` stay syntax-only; transforms stay `static`.** The predicate runs against *every* syntax change in the consumer's compilation, so allocating, touching the semantic model, or capturing instance state inflates per-keystroke cost. The transform runs less often (only when the predicate hits) but should still avoid captures so Roslyn can interleave runs cheaply.

When you add a new model record, copy the shape of an existing one (e.g. `TableModel`, `RelationVariantModel`) and use `EquatableArray<T>` for every collection field. The `ModelGraph` `<remarks>` block in `Model/ModelGraph.cs` is the canonical statement of the cache-equality rule — read it if you find yourself wanting to add a lookup field.

### Model graph shape

The fully-linked output of `RelationLinker.Build` is a `ModelGraph` record with these fields (all `EquatableArray<T>`):

| Field | Contents |
| --- | --- |
| `Tables` | Every `[Table]` class, with `TypeRef.IsTableType` patched up against the full table set. |
| `RelationKinds` | Forward + inverse kinds concatenated. Direction is on `RelationKindModel.Direction`. |
| `RelationVariants` | One per `[Restricts]`-on-class (etc.) — the variants that drive edge emission. |
| `Unions` | Auto-computed source/target unions per forward kind with 2+ members. Feeds `UnionInterfaceEmitter`. |
| `UnionEndpoints` | User-declared union-endpoint interfaces, each with its member table list. |
| `SharedShapes` | User-declared shared-shape interfaces, each with its implementing variants. (preview.56+: candidates also carry `Lifted{In,Out,Id,Payload}` extracted from interface members across the transitive base closure; `RelationLinker.LiftVariantsFromSharedShape` consumes these to fill in / merge variant shapes before `SharedShapes` is computed.) |
| `Aggregates` | Per-`[AggregateRoot]` membership, computed by `[Children]` BFS from each root. |
| `AggregateConflicts` | `"Member\|Root1,Root2,…"` strings for CG011 (member reachable from 2+ roots). |
| `CascadeCycles` | Cycle path strings for CG014. |
| `CompositionRoots` | The user's `[CompositionRoot]` classes — usually 0 or 1; CG018 fires when >1. |

`ModelGraph` also exposes helper methods (`FindUnionEndpoint`, `BuildTableIndex`, `BuildKindIndex`, `FindKind`, `PairedInverse`, `UnionsForTable`, `TargetTypeFor`) that emitters call. None of these mutate the graph; they all recompute on the fly.

## AnnotationsMetadata lockstep rule

Modeling attribute classes (`[Table]`, `[Property]`, `[Restricts]`-style bases, …) live in `src/Disruptor.Surface.Runtime/Annotations/Annotations.cs` as ordinary C# types in the `Disruptor.Surface.Annotations` namespace. The **generator binds to them by fully-qualified metadata-name string** in `src/Disruptor.Surface.Generator/Annotations/AnnotationsMetadata.cs`:

```csharp
public const string Table          = "Disruptor.Surface.Annotations.TableAttribute";
public const string InverseRelation = "Disruptor.Surface.Annotations.InverseRelation`1";
public const string InUnionBase    = "Disruptor.Surface.Annotations.In`1";
// …
```

The generator can't reference the runtime assembly (it's `netstandard2.0`, the runtime is `net10.0`), so these strings are the only contract between the two halves. **If you add or rename an attribute, you must touch both files in the same change** — otherwise `ForAttributeWithMetadataName` silently matches zero syntax nodes and the feature stops working with no diagnostic.

Generic-arity suffix: `` `1 `` for one type parameter, etc. — match what the runtime declaration emits as its metadata name. Test by adding an `EmissionShapeTests` case that uses the attribute on a fixture and asserts the expected emission.

## User code cannot reference generator-emitted types in declarations

A subtle but recurring trap. When a user writes:

```csharp
[RestrictedBy]
public partial IReadOnlyCollection<IRestrictedBy> Sources { get; }   // ← will NOT compile
```

…Roslyn tries to resolve `IRestrictedBy` *during the same analysis pass* in which the generator would emit it. From Roslyn's POV the interface doesn't exist yet → it captures an error type → the generated impl's `FullyQualifiedName` doesn't match the user's declaration → `CS9255` ("partial member declarations must have the same type") and `CS0246` ("type not found") fire in the emitted `.g.cs`.

**Generator-emitted code referencing other generator-emitted types is fine** — both files compile in the same later pass — so the workaround is to keep the user's signature pointed at types that already exist (`IEntity` for entity collections, `IRecordId` for id collections) and only use the generated interface inside emitted code. Same caveat applies to relation-kind / variant types: declare the read-side collection with `IEntity`, not with the emitted `Restricts` marker class as a generic argument inside a partial member's declared type.

In *expression* position the resolution rule flips — `Session.SaveAsync(new ConstraintRestrictsUserStory { … }, tx)` resolves fine because body compilation happens after generator emit. The trap is declaration-site partial-member type references, not call-site usages.

If you add a new generator-emitted interface, surface this constraint to users in the doc comment on the matching modeling attribute.

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

"Slice" terminology, used throughout this section and Session Lifecycle: a slice is one navigable region of an entity — a `[Children]` collection, a `[Reference]` link, a `[Parent]` link, or a forward/inverse relation collection. The session tracks per-(owner, slice-name) loaded flags, and reads against an unloaded slice throw `LoadShapeViolationException`. Fresh entities (created via `Track`) are marked as having every slice loaded automatically; loader-hydrated entities are marked per-slice based on what the user `Include*`'d.

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

## Test Architecture

Tests live in `tests/Disruptor.Surface.Tests/`. The split:

- **`Generator/EmissionShapeTests.cs`** — fixture-driven shape assertions. Each test feeds a model source string through `GeneratorHarness.Run` and asserts substrings against the concatenated emitted source. Use for "the generator emits X for this input." Not full snapshot matching — just enough to pin contracts the consumer relies on.
- **`Generator/DiagnosticsTests.cs`** — fixtures designed to trip a specific `CGxxx`. Assert that exactly that diagnostic fires (and ideally that no others do).
- **`Generator/CodeWriterTests.cs`** — unit tests for the `CodeWriter` helper used by emitters.
- **`Pipeline/RelationVariantExtractorTests.cs`** (and any other per-extractor file) — pipeline-stage unit tests that probe extractor behaviour without running the full generator.
- **`Runtime/…`** — runtime unit tests for `SurrealSession`, `RecordId`, `RecordIdFormat`, `SurfaceFormatter`, `HydrationValue` family, query compiler, fake-Surreal end-to-end.

`Generator/GeneratorHarness.cs` is the entry point everyone uses. Key methods:

- `Run(source)` — compiles `source` against the runtime + SDK references, runs the generator, returns `(GeneratorDriverRunResult, OutputCompilation, RunDiagnostics, CompileDiagnostics)`.
- `AllGeneratedSource(result)` — concatenates every emitted tree with `// === filename ===` separators, plus emitter exceptions / diagnostics when no trees were emitted. Most assertions are `Assert.Contains` against this string.
- `FindGeneratedFile(result, fileNameContains)` — pick out one specific emitted tree by filename substring.
- `CompileAndLoad(source)` — compiles fixture + generated output into an in-memory assembly and loads it, throwing on any compile error. Used by end-to-end tests that need to actually invoke the generated entity API at runtime — catches "looks right but doesn't run right" bugs that shape assertions miss.

Conventions:

- Every new emitter gets at least one `EmissionShapeTests` case covering the canonical output shape.
- Every new diagnostic gets a `DiagnosticsTests` case asserting it fires on the bad input *and* one asserting it does not fire on the good input.
- Fixtures are inline `const string` blocks with `using Disruptor.Surface.Annotations; using Disruptor.Surface.Runtime;` and a `namespace M;`. `MinimalModel` in `EmissionShapeTests.cs` is the canonical starting point — copy and tweak.

The harness uses `LanguageVersion.Preview` so partial properties (C# 13+) compile. Don't downgrade this without checking the fixture set; most tests use partial members.

## Build & Inspection

Build, test, and harness commands live in [`notes.md`](notes.md#build--run). Two things to know that aren't obvious from the commands themselves:

- **Generated `.g.cs` files for the sample project land in `src/Disruptor.Surface.Sample/obj/Debug/net10.0/generated/Disruptor.Surface.Generator/Disruptor.Surface.Generator.ModelGenerator/`.** `EmitCompilerGeneratedFiles=true` is set in the sample csproj so this directory is authoritative for "what did the generator actually produce" inspection. When you add a new emitter, eyeball its output here before writing the test.
- **The generator caches by record equality, so a stale `.g.cs` from a deleted source class lingers as an orphan in the generated dir until you wipe it.** `--no-incremental` doesn't help with orphans (it forces re-emission of valid outputs, not deletion of invalid ones). Delete the file manually if something went weird.

## Recipes

Cookbook for the four most common contributor changes.

### Add a new modeling attribute

1. **`src/Disruptor.Surface.Runtime/Annotations/Annotations.cs`** — declare the attribute class in the `Disruptor.Surface.Annotations` namespace. Pick the right `AttributeUsage` targets. Add a `<summary>` doc comment explaining what it does and what it pairs with.
2. **`src/Disruptor.Surface.Generator/Annotations/AnnotationsMetadata.cs`** — add a `public const string Foo = "Disruptor.Surface.Annotations.FooAttribute";` entry. Lockstep rule applies.
3. **The relevant extractor** (`TableExtractor` for property-level attributes, a new stream in `ModelGenerator.Initialize` for class-level ones) — read the attribute and populate a new field on the model record.
4. **The model record** (`PropertyModel`, `TableModel`, …) — add the field. **Use `EquatableArray<T>` for any collection.** No mutable state.
5. **The emitter(s) that should react** — add the behaviour. Most attributes only affect one emitter; some affect schema, partial emission, and the query layer together.
6. **`Pipeline/Diagnostics.cs`** — add any `CGxxx` descriptors for invalid usages. Report them from `ModelGenerator.Emit` in the same loop that handles related role attributes.
7. **`tests/Disruptor.Surface.Tests/Generator/EmissionShapeTests.cs`** — assert the new emission shape.
8. **`tests/Disruptor.Surface.Tests/Generator/DiagnosticsTests.cs`** — assert the new diagnostic.

### Add a new diagnostic

1. **`Pipeline/Diagnostics.cs`** — add a `public static readonly DiagnosticDescriptor` entry. Pick a stable `CGxxx` id (next free in sequence; see [`api.md`](api.md#diagnostics) for the current map). Severity is usually `Error`; `Warning` for "this works but probably isn't what you meant" cases.
2. **The report site** — usually a `spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.YourDescriptor, Location.None, ...))` call inside the appropriate loop in `ModelGenerator.Emit`. Earlier-stage reports (linker, extractor) are rarer; prefer Emit unless you genuinely need to short-circuit downstream work.
3. **Both a positive and negative test** in `DiagnosticsTests.cs`.
4. **Add the row to the diagnostics table in [`api.md`](api.md#diagnostics).** The api doc is the user-facing index; keep it in sync.

### Add a new emitter

1. **New file in `src/Disruptor.Surface.Generator/Emit/`** — class named `{Thing}Emitter`, single public static method `Emit(SourceProductionContext spc, ModelGraph graph)`. Follow the existing emitters' shape — they all use `CodeWriter` for output building and call `spc.AddSource(hintName, source)` per emitted file.
2. **Wire into `ModelGenerator.Emit`** — add the call in the existing emit sequence. Order matters when one emitter's output references another's (e.g. `IdEmitter` runs before `PartialEmitter` because partial entities reference typed ids).
3. **At least one `EmissionShapeTests` case** asserting the new output shape.
4. **Update the emitters table in this doc.**

### Add a new relation feature

The relation surface is the most complex. A new feature usually means touching:

1. **An attribute** (modelling intent) → `Annotations.cs` + `AnnotationsMetadata.cs`.
2. **A pipeline stream + extractor** — `ModelGenerator.Initialize` to wire it, a new file under `Pipeline/`.
3. **A model record** + integration into `ModelGraph` + linker stitching in `RelationLinker.Build`.
4. **An emitter** — typically a partial fragment onto an existing emitted shape, occasionally a new top-level file.
5. **Schema impact** — `SchemaEmitter` for any DDL changes (`FROM` / `TO` clauses, schemaful vs schemaless decisions, indexes).
6. **Hydration impact** — `RelationVariantEmitter` per-kind hydration dispatcher if the row→variant mapping changes.
7. **Diagnostics** — usually two or three CGxxx codes catching common misuses.
8. **A sample model + a runtime end-to-end test.**

Preview.54 (union endpoints), preview.55 (shared-shape interfaces), and preview.56/.57 (annotated shared-shape lift + per-property merge) are recent end-to-end examples — read the corresponding entries in [`notes.md`](notes.md#engineering-log) for the full change set list. preview.56/.57 in particular shows the cross-extractor join pattern: per-symbol extractors stay self-contained, the linker performs the join (`LiftVariantsFromSharedShape`) so the incremental cache stays correct when either side's syntax changes.

## Extension Points for Consumers

These are user-facing extensibility points. (Consumer-side; replicated from [`api.md`](api.md) in summary form for quick reference.)

- Add constructors, services, caches, telemetry, and application methods to the `[CompositionRoot]` partial.
- Add domain methods to entity partials and call protected `Session` (sync, in-memory) or take a `Transaction` parameter for async dispatch.
- Implement `OnCreate{Name}` hooks for mandatory-reference seeding.
- Implement `OnDeleting` hooks for synchronous pre-delete bookkeeping.
- Use `Workspace.Schema` directly for migration tooling — iterate the chunk list, splice your own DDL where appropriate, run inside your own transaction.
- Use `session.Log` (a `CommandLog`) to inspect captured intent for diagnostics or telemetry.
