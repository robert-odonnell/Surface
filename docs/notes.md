# Notes

This file provides guidance to contributing developers when working with code in this repository.

**Maintain this file.**

## Build & run

- Build the whole solution: `dotnet build Disruptor.Surface.slnx`
- Build just the generator (no consumer): `dotnet build src/Disruptor.Surface.Generator/Disruptor.Surface.Generator.csproj`
- Build just the runtime library: `dotnet build src/Disruptor.Surface.Runtime/Disruptor.Surface.Runtime.csproj`
- Build the consumer (this triggers source generation): `dotnet build src/Disruptor.Surface.Sample/Disruptor.Surface.Sample.csproj`
- Generated files land in `src/Disruptor.Surface.Sample/obj/Debug/net10.0/generated/Disruptor.Surface.Generator/Disruptor.Surface.Generator.ModelGenerator/` — inspect these to see what the generator actually emitted for a given `[Table]` class. `EmitCompilerGeneratedFiles=true` is set in `Disruptor.Surface.Sample.csproj` to make this directory authoritative.
- Force a clean re-run of the generator: `dotnet build … --no-incremental`. The generator caches by record equality, so a stale `.g.cs` from a deleted source class lingers as an orphan in the generated dir until you wipe it manually.
- Run the harness against a live Surreal: `dotnet run --project src/Disruptor.Surface.Sample` (see `## Running the harness` in `README.md`).

## Project layout

Three projects, dependencies fan out from `Disruptor.Surface.Sample`:

- `src/Disruptor.Surface.Generator` (`netstandard2.0`, `IsRoslynComponent=true`) — the incremental Roslyn source generator. Cannot reference `net10.0` types; everything in `Model/` is hand-rolled to be equatable so the incremental pipeline can dedupe. Bundles `Humanizer.Core` as an analyzer dep via the `GetDependencyTargetPaths` MSBuild trick (see `Disruptor.Surface.Generator.csproj`) — without that, the analyzer host can't load `Humanizer.dll`.
- `src/Disruptor.Surface.Runtime` (`net10.0`) — the runtime half: `SurrealSession`, `IEntity`, `IRelationKind`, `RecordId`, `IReferenceRegistry`, `ReferenceFieldInfo`, `HydrationValue`, `ISaveContext`, `CommandLog`. Namespace `Disruptor.Surface.Runtime`. Two package deps: `Disruptor.Surreal` (the SurrealDB SDK — CBOR over WebSocket) and `Ulid`. Consumers add a `ProjectReference` (or `PackageReference` once published).
- `src/Disruptor.Surface.Sample` (`net10.0`) — the test bed: schema modeled in `[Table]` classes, the `[CompositionRoot]`-tagged `Workspace` partial, and a console-app harness in `Program.cs`. `ProjectReference` to `Disruptor.Surface.Runtime`, plus `OutputItemType="Analyzer"` on the generator so it picks up `[Table]`-driven emission without taking a runtime dependency on the generator assembly.

The library has no transport layer of its own — `Disruptor.Surreal` (CBOR over WebSocket; no embedded mode, no HTTP) is the only wire. Consumers connect once via `Disruptor.Surreal.SurrealClient.ConnectAsync(...)` and pass the `Surreal` (read-only) or a `Disruptor.Surreal.SurrealTransaction` (write-mode) into the generated load methods.

## Generator pipeline (read this before touching `Disruptor.Surface.Generator`)

`ModelGenerator.Initialize` wires four `ForAttributeWithMetadataName` providers (tables, forward kinds, inverse kinds, the user's `[CompositionRoot]`) into a single `ModelGraph`. The data flow:

1. **Attribute discovery** — the user-facing attributes (`[Table]`, `[AggregateRoot]`, `[CompositionRoot]`, `[Id]`, `[Property]`, `[Parent]`, `[Children]`, `[Reference]`, `[Inline]`, the four reference-delete behaviors `[Reject]` / `[Unset]` / `[Cascade]` / `[Ignore]`, the relation bases `RelationAttribute` / `ForwardRelation` / `InverseRelation<TForward>`, the relation-variant endpoint markers `[In]` / `[Out]`, and the union-endpoint attribute bases `In<TKind>` / `Out<TKind>`) live as ordinary `.cs` files in `src/Disruptor.Surface.Runtime/Annotations/`, namespace `Disruptor.Surface.Annotations`. The generator binds to them by metadata name through `ForAttributeWithMetadataName`; the FQN constants in `AnnotationsMetadata` must stay in lockstep with the runtime declarations.
2. **Per-symbol extractors** (`Pipeline/`) lower Roslyn symbols into pure-data records under `Model/`. They cannot resolve cross-table references yet — `TypeRef.IsTableType` is seeded from the immediately visible attributes and patched up later.
3. **Linking** (`RelationLinker.Build`) takes the collected tables + relation kinds + composition roots and (a) rewrites every `TypeRef` so `IsTableType` is true wherever the underlying type was discovered to be a `[Table]`, (b) computes per-relation-kind `RelationUnion` sets — for each forward kind, the source set (forward attribute holders) and target set (inverse attribute holders), each becoming a marker interface when ≥2 members, (c) computes per-aggregate `AggregateModel` membership by walking `[Children]` from each `[AggregateRoot]` and detects entities reachable from 2+ roots as conflict descriptors, (d) detects cascade-only reference cycles for CG014, (e) stitches user-declared union-endpoint interfaces (attributed with `In<TKind>` / `Out<TKind>`-derived attributes) with per-table opt-ins (partial `I{Name}RecordId : IFooTarget` declarations) into `UnionEndpointModel`s on `graph.UnionEndpoints`.
4. **Emit** (`Emit/`) — emitters fire per generation:
   - `IdEmitter` — per-table `{Name}Id` `readonly record struct` with id-side union interfaces in its base list.
   - `UnionInterfaceEmitter` — per multi-member union, BOTH the entity-side marker (`IRestrictedBy`) AND the id-side marker (`IRestrictedById`).
   - `CompositionRootEmitter` — emits a partial declaration of the user's `[CompositionRoot]`-tagged class with two `Load{Root}Async` overloads per `[AggregateRoot]`: one taking `Disruptor.Surreal.SurrealClient db` (read-only), one taking `Disruptor.Surreal.SurrealTransaction tx` (write-mode, sees in-txn writes from the same transaction). No ctor, no fields, no base — the user owns construction entirely. Skipped when no `[CompositionRoot]` exists in the compilation.
   - `RelationKindEmitter` — per forward relation attribute (e.g. `RestrictsAttribute`), emits a sibling marker class without the `Attribute` suffix (`Restricts : IRelationKind`) carrying the SurrealDB edge name as a static property. Also emits the per-kind `{KindName}Id` typed-id struct (`RestrictsId`), shared across every variant of the kind. Inverse kinds get no marker — the edge is named after the forward.
   - `RelationVariantEmitter` — per relation-variant class (`[Restricts] partial class ConstraintRestrictsUserStory`), emits the `IEntity` partial implementation: `IRelationVariant` marker, `[In]`/`[Out]` setter dispatch, `IEntity.SaveAsync` body that issues `INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY UPDATE …]`, and per-kind `{KindName}Hydration.HydrateVariant(SurrealValue, IHydrationSink)` dispatcher. For multi-variant kinds, also emits the per-kind variant marker interface `I{KindName}Variant`. Single-variant kinds skip the interface emission for cleanliness.
   - `AggregateLoaderEmitter` — per `[AggregateRoot]`, an internal `{Root}AggregateLoader` static class with two `PopulateAsync` overloads (SurrealClient db / SurrealTransaction tx). Each issues a single nested-`SELECT` query: root row with `*` plus `field.*` inline expansion for each `[Reference, Inline]`, then per-non-root-member subselects scoped via dotted parent paths back to the root (`WHERE feature.epic.design = $parent.id`), then per-relation-kind edge subselects (within-aggregate + cross-aggregate target-side). Hydration is delegated to per-entity `IEntity.Hydrate(SurrealValue, IHydrationSink)`, which writes directly into the entity's backing fields (no `sink.Parent` / `sink.Reference` calls — entities own their state).
   - `PartialEmitter` — partial implementations of every annotated property/method. Setters are pure backing-field writes — no `__WriteField`, no buffer, no Session interaction (the one exception: `[Parent]` setters cascade-track the child into the parent's session via `parent.Session.AdoptIfUnbound(this)`). Per-entity session plumbing: `_session` field, explicit `IEntity.Bind` / `IEntity.Session`, protected `Session` accessor that throws when unbound, `__EnsureSliceLoaded` slice guard. Per-entity hooks: `IEntity.Initialize` (idempotent mandatory-ref seeding via the user's `OnCreate{Name}` hooks), `IEntity.Hydrate` (SurrealValue-consuming row-to-entity population, writes backing fields directly), `IEntity.OnDeleting` dispatch, `IEntity.MarkAllSlicesLoaded`, `IEntity.GetParentId` (when the table has a `[Parent]`), and **`IEntity.SaveAsync`** — per-entity Save dispatch that walks forward-dependency backing fields (Reference / Parent) recursively, dispatches `CREATE/UPDATE record:id CONTENT { ... }`, then walks new children via the `[Children]` property accessor. Edges are user-driven now: relation variants are standalone entities, so the user calls `Session.SaveAsync(new TVariant { Source = …, Target = … }, tx)` explicitly. Relation collection property reads emit `Session.QueryOutgoing<TKind, TElement>(this)` / `QueryIncoming<TKind, TElement>(this)` / `QueryRelatedIds<TKind>(this)` / `QueryInverseRelatedIds<TKind>(this)`.
   - `ReferenceRegistryEmitter` — sealed `IReferenceRegistry` impl in the consumer (`GeneratedReferenceRegistry`) PLUS a partial fragment of the user's `[CompositionRoot]` exposing the singleton via `public static IReferenceRegistry ReferenceRegistry`. Model-scoped so multiple Disruptor.Surface-generated assemblies can coexist in one process.
   - `SchemaEmitter` — emits the chunked DDL via `{CompositionRoot}.Schema` (a partial fragment) backed by an internal `_chunks` array. `IReadOnlyList<string>` of DDL chunks: entity-tables block + per-`[Table]` field block + per-relation-kind table definition. Idempotent via `DEFINE … IF NOT EXISTS`. The generator also emits `Workspace.ApplySchemaAsync(db)` and `ApplySchemaAsync(tx)` for the common boot path.
   - `LoadEntryEmitter` / `IdsAsyncEmitter` / `TraversalBuilderEmitter` — query-side surface (`Query<T>.LoadAsync`, `Query<T>.IdsAsync`, per-table traversal builders).
   - Diagnostics — CG001+ descriptors reported from the linker output.

### Equatability is the contract

Every record under `Model/` is fed to `IncrementalGenerator` providers. **All collections must be `EquatableArray<T>`, never `ImmutableArray<T>` or `List<T>`**, because Roslyn deduplicates pipeline outputs by record equality and the BCL collection types compare by reference. Adding a mutable field, a lazy cache, or a non-equatable collection silently regresses incremental builds. See `ModelGraph`'s `<remarks>` for the canonical statement of this rule.

### User code cannot reference generator-emitted types

When the user writes `partial IReadOnlyCollection<IRestrictedBy> Foo { get; }`, Roslyn tries to resolve `IRestrictedBy` *during the same analysis pass* in which the generator would emit it. The interface doesn't exist yet from Roslyn's POV → it captures an error type, the generated impl's `FullyQualifiedName` doesn't match the user's declaration, and you get `CS9255 (partial member declarations must have the same type)` plus `CS0246 (type not found)` in the generated `.g.cs`. **Generator-emitted code referencing other generator-emitted types is fine** — both files compile in the same later pass — so the workaround is to keep the user's signature pointed at types that already exist (`IEntity` for entity collections, `IRecordId` for id collections) and only use the generated interface inside emitted code. **The same caveat applies to relation-kind / variant types:** declare the entity-side typed read collection with `IRestrictedBy` (the user-side base interface that already exists), not with the emitted `Restricts` marker class as a generic argument inside a `partial` member's declared type. In expression position (as a generic argument to `Session.SaveAsync(new ConstraintRestrictsUserStory { … }, tx)` or `Session.QueryVariantsOutgoingAsync<TVariant>(...)`) the type resolves fine because that resolution happens at body-compile time, after generator emit.

### Emit conventions

- `IdEmitter` emits each `{Name}Id` as a `readonly record struct {Name}Id(string Value)`. The `Value` initializer routes through `RecordIdFormat.Validate`, which only accepts two forms: a 26-char Ulid stringification (what `New()` mints) or a short lower_snake_case slug (max 32 chars, opt-in for stable-named records like config singletons). Anything else throws `FormatException` at construction. Quoted-string ids are explicitly unsupported.
- `PartialEmitter.SessionType`, `EntityInterface` (and the matching constants in the other emitters) pin the target namespace `Disruptor.Surface.Runtime`. If the runtime is renamed or split, every emitter that bakes a `global::Disruptor.Surface.Runtime.*` literal must change in lockstep.
- `RelationKindEmitter` strips the `Attribute` suffix to name the marker class. `RestrictsAttribute` (the user's attribute, used as `[Restricts]`) and `Restricts` (the marker, used as a generic argument like `Session.QueryOutgoingAsync<Restricts, UserStory>(...)`) coexist in the same namespace because attribute-position resolution looks for `*Attribute` first and type-position resolution looks for the bare name.
- `ReferenceRegistryEmitter` keeps the impl class internal to the consumer assembly and exposes the singleton via a partial fragment on `[CompositionRoot]`. Same pattern works for any per-model metadata — emit the impl as an internal class, attach a static accessor to the user's partial. Anything new the runtime needs at load time gets passed into `SurrealSession`'s ctor; nothing reaches into a process-global facade any more.
- **The generator does not look at user methods at all.** Every model annotation (`[Property]`, `[Parent]`, `[Reference]`, `[Children]`, `RelationAttribute`-derived) is `AttributeTargets.Property` only when applied to a member; on the relation-variant path the same `RelationAttribute`-derived attributes target the variant **class** (preview.51). Methods cannot carry them. The `Session` DSL handed to each entity is the entire library contract; domain methods are plain user code calling that DSL. If a user wants a domain verb, they write a one-liner: `public Task RestrictsAsync(UserStory story, SurrealTransaction tx, CancellationToken ct = default) => Session.SaveAsync(new ConstraintRestrictsUserStory { Source = this, Target = story }, tx, ct);`
- `SurrealNaming` (wraps Humanizer) handles `ToFieldName` / `ToTableName` / `ToEdgeName` / `Singularize` / `Pluralize` / `StripAttributeSuffix`. Table names are pluralised + snake-cased at codegen time; field/edge names are snake-cased; relation source-interface names are singularised forward-attribute names (`Restricts` → `IRestrict`).

### Diagnostics

`Pipeline/Diagnostics.cs` defines the `CG001`–`CG028` descriptors. When adding a new validation, add the descriptor here and report it from `ModelGenerator.Emit` (or the appropriate extractor). Selected highlights: CG001 (`[Table]` not partial), CG011 (entity reachable from multiple aggregate roots), CG014 (cascade-only reference cycle), CG018 (multiple `[CompositionRoot]` classes), CG019 (`[CompositionRoot]` class not partial).

## Runtime model (Disruptor.Surface.Runtime)

The generated partials are not standalone — they call into a small runtime that consumers must wire up:

- **`IEntity`** — every `[Table]` class implements this implicitly via the emitted partial. Session-side hooks (all explicit-interface impls, so they don't pollute the user's type):
  - `RecordId Id` — canonical id.
  - `SurrealSession? Session` — null until the entity is bound.
  - `Bind(SurrealSession session)` — one-shot setter for the entity's `_session` field; needed for read-side resolution (children, relations, lazy reference fall-through to identity map).
  - `Initialize(SurrealSession session)` — seeds mandatory `[Reference]` targets via the user's `OnCreate*` hooks. Idempotent — guards each mint with `if (_field is null)` so the SaveAsync auto-bind path can call it without double-minting.
  - `Hydrate(SurrealValue row, IHydrationSink sink)` — loader-driven row-to-entity population. Writes directly into the entity's backing fields (no `sink.Parent` / `sink.Reference` calls — entities own their state). Edges and slice marks still go through the sink (cross-entity, session-scoped).
  - `OnDeleting()` — fires before the entity's own DELETE so user cleanup can queue child clears.
  - `MarkAllSlicesLoaded(IHydrationSink sink)` — fresh-Tracked entities own their full state, so every slice is implicitly loaded. The legacy aggregate loader also calls this after Hydrate.
  - `GetParentId() => RecordId?` — emitted on entities with a `[Parent]`. Default-interface no-op for tables without one. Used by `Session.QueryChildren` to match a candidate child against its parent owner.
  - `SaveAsync(ISaveContext ctx, CancellationToken ct)` — per-entity Save dispatch (see SurrealSession below).
- **`SurrealSession`** — single concrete class. Snapshot-isolated entity store. **No ambient context** — entities hold their session via `_session`. Reads:
  - Sync entity lookups: `Get<T>(id)`, `IsTracked(id)`, `IsSliceLoaded(owner, field)`, `QueryChildren<T>(owner, childTable)` (matches `IEntity.GetParentId` against the owner), `QueryOutgoing<T>` / `QueryIncoming<T>` for within-aggregate edges, `QueryRelatedIds<TKind>` / `QueryInverseRelatedIds<TKind>` for cross-aggregate.
  - `[Parent]` and `[Reference]` resolve directly off the entity's own backing fields (no `state.Parents` / `state.References` mirror dicts) — `Session.Get<T>(id)` is the fall-through when only the id is cached.
  - Writes (sync, in-memory): `Track` (registers a fresh entity, runs Initialize idempotently, marks every slice loaded), `AdoptIfUnbound(child)` (cascade-track called from `[Parent]` setters). Edge writes (Relate / Unrelate) are async-only — see below.
  - Async dispatch through an app-owned `Disruptor.Surreal.SurrealTransaction`:
    - `SaveAsync(IEntity entity, SurrealTransaction tx, ct)` — per-entity Save. Auto-binds + initialises the entity, walks forward dependencies (Reference / Parent backing fields) recursively, dispatches a whole-entity `CREATE/UPDATE record:id CONTENT { ... }`, walks new children via the `[Children]` accessor recursively, marks the entity saved. Per-entity is the canonical write surface; the user picks what to save. **Relation variants are entities too** — passing a `[Restricts]`-tagged variant instance (`new ConstraintRestrictsUserStory { Source = constraint, Target = userStory }`) routes through the variant's emitted `IEntity.SaveAsync` body which dispatches `INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY UPDATE …]` and updates `state.Edges` so subsequent in-session reads see the new edge. `IRelationVariant : IEntity` is the methodless marker the session branches on.
    - `DeleteAsync(IEntity entity, SurrealTransaction tx, ct)` — runs three-phase pre-flight via `PlanDelete` (Cascade + Unset to fixpoint, then steady-state Reject blockers throw `CascadeRejectException` before any wire dispatch), then dispatches a single `tx.DeleteAsync(id)`; the substrate's emitted `REFERENCE ON DELETE` clauses cascade the rest. Library predicts; substrate enforces. `IEntity.EnumerateReferences` + `SetReferenceTo` are emitted by the generator to feed the planner.
    - `UnrelateAsync<TKind>(src?, tgt?, tx, ct)` — direct edge deletion. At least one endpoint non-null; one-side-null is bulk delete.
    - `QueryVariantsOutgoingAsync<TVariant>(srcId, tx, ct)` / `QueryVariantsIncomingAsync<TVariant>(tgtId, tx, ct)` — async traversal returning hydrated variant entities; tracked in the session, edges mirrored in `state.Edges`. `db` overloads + `IEntity` convenience overloads.
    - `QueryOutgoingAsync<TKind, TTarget>(srcId, tx, ct)` / `QueryIncomingAsync<TKind, TTarget>(tgtId, tx, ct)` — async traversal returning target entities directly (skips variant materialisation); not auto-tracked.
    - `QueryVariantsAsync<TVariant>(sql, bindings, tx, ct)` — raw-SQL escape hatch.
- **`Disruptor.Surreal.SurrealTransaction`** — the SDK's transaction handle. The library never owns one; the app calls `db.BeginTransactionAsync()`, passes the handle into Save/Delete/Relate, and calls `tx.CommitAsync()` (or `tx.CancelAsync()`) when its logical unit of work is done. Native `SurrealConflictException` surfaces at COMMIT for concurrent writers.
- **`IRelationKind`** — runtime interface with `static abstract string EdgeName { get; }`. Implemented by every emitted forward-relation marker class (`Restricts`, `References`, …). The static abstract gives `Session.QueryOutgoingAsync<TKind, T>`, `Session.UnrelateAsync<TKind>`, and the reflection-cached `TVariant → kind → edge-name` lookup (used by the variant-typed async query terminals) compile-time access to the edge name without instance construction.
- **`IRelationVariant`** — methodless marker interface (`IRelationVariant : IEntity`) emitted onto every relation-variant class by `RelationVariantEmitter`. `SurrealSession.SaveContext.MarkSaved` / `CleanupLocalState` branch on this to update `state.Edges` for in-session consistency. User code never declares this directly.
- **User's `[CompositionRoot]` partial** — the user declares a partial class tagged with `[CompositionRoot]` (e.g. `public partial class Workspace`); the generator emits two `Load{Root}Async` overloads per `[AggregateRoot]`: one taking `Surreal db` (read-only — no transaction; the load just queries), one taking `Transaction tx` (write-mode — load query runs inside the txn so it sees in-txn writes from the same transaction). No ctor, fields, or base class are emitted; the user owns construction (caches, telemetry, …) entirely. Library promise: minimal intrusion. Hydration is delegated to a generator-emitted `{Root}AggregateLoader.PopulateAsync` static class which issues the single nested SurrealQL query.
- **`RecordId` / `IRecordId`** — canonical `(Table, Value)` pair. Every generated `{Name}Id` implicitly converts to `RecordId` so session internals key off one struct type while the user-facing API stays strongly typed. `{Name}Id` also implements every id-side union marker (`IRestrictedById`, `IReferencedById`, …) it's a member of.
- **`SurrealArray<T>`** — mutable ordered collection that backs SurrealDB's inline `array<object>` columns. Implements `IList<T>` plus `Move(from, to)`. The wrapper takes a writer callback for mutation notifications; under the pure-setter model the generator passes a no-op writer and Save reads the wrapper at dispatch time. Generator emits a lazy-cached wrapper for any `[Property]` whose declared type is `SurrealArray<T>`; the loader pre-populates the wrapper from the `Value` array payload via `HydrationValue.ReadOrDefault<List<T>>` (snake_case property matching).
- **`HydrationValue`** — Value-native helpers used by emitted `IEntity.Hydrate` and the runtime's load/query consumers. `ReadRecordId` / `TryReadRecordId` / `ReadString` / `ReadOrDefault<T>` / `TryReadReferenceId` (id-only path) / `HydrateInlineReference<T>` (returns the hydrated entity for inline expansions). `ReadOrDefault` uses a small reflection-based converter for primitives, arrays / `List<T>`, and POCOs / records (snake_case property matching).
- **`ISaveContext`** — passed to per-entity `IEntity.SaveAsync` bodies. Carries the open `SurrealTransaction`, `IsTracked(IRecordId)` (returns true when the id was loaded-at-start or already saved this pass), `SaveAsync(IEntity, ct)` recursion callback, `MarkSaved(IEntity)` post-dispatch. Implemented privately by `SurrealSession`.
- **`CommandLog`** — append-only diagnostic log of model commands. Under the pure-setter model captures `Track` (Create), `Relate`, `Unrelate`, and `Delete` intents — property setters do not record. Useful for tests asserting "what intent did the session capture?" and for telemetry.

## Authoring conventions for `[Table]` consumers

When working in `Disruptor.Surface.Sample/Models` (or any consumer):

- A `[Table]` class **must** be `partial` (CG001) and declare exactly **one** `[Id]` partial property (CG007/CG008). The id type is the generated `{Name}Id` struct.
- Exactly one class **may** be tagged `[CompositionRoot]` and **must** be `partial` (CG018/CG019). The generator grafts `Load{Root}Async` instance methods onto it; you own the ctor, fields, etc. Without one, the load methods aren't emitted; you can still call `{Root}AggregateLoader.PopulateAsync` directly.
- `[AggregateRoot]` on the root entity of an aggregate (e.g. `Design`, `Review`). Membership is computed by walking `[Children]` from the root; entities reachable from 2+ roots produce CG011.
- **Entity reads are sync; writes split into sync (in-memory) + async (dispatch).** Sync property setters write directly to backing fields. Async dispatch happens through `session.SaveAsync` / `DeleteAsync` / `UnrelateAsync` against an app-owned `SurrealTransaction`. Edges are written by `Save`-ing a relation variant (`session.SaveAsync(new TVariant { Source = …, Target = … }, tx)`).
- `[Property]` / `[Parent]` / `[Reference]` / `[Children]` are **property-only attributes** — Roslyn rejects them on methods.
- `[Property]` — scalar field. Declare as `partial T Name { get; set; }`; generator emits a pure backing-field property: `get => _name; set => _name = value;`. Get-only is also legal. For inline `array<object>` columns, declare as `partial IReadOnlyList<TElem> Items { get; }` (get-only); generator emits a `List<TElem>` backing + `AddItem` / `RemoveItem` / `ClearItems` helpers, walks `TElem`'s public scalar properties at codegen time to emit typed Hydrate / Save (no reflection). `IList<TElem>` and `List<TElem>` are also accepted shapes — same backing, helpers still emitted.
- `[Reference]` — pointer to another `[Table]`. Declare as `partial T Name { get; }` (mandatory, non-nullable; generator emits the `OnCreate{Name}` hook + `Initialize` entry that mints the target via `new T()` and assigns it directly into the backing field), or `partial T? Name { get; set; }` (optional, with pure backing-field setter). Generator emits two backing fields per reference: `_{name}` for the entity ref cache and `_{name}Id` for the record id; the getter falls back to `Session.Get<T>(_{name}Id)` when only the id is cached (covers "loaded as id only" + "user later loaded the other aggregate separately").
- `[Parent]` — pointer to the parent in a hierarchical relationship. Declare as `partial T Name { get; set; }`. Same dual-backing-field shape as `[Reference]`. Setter additionally calls `parent.Session.AdoptIfUnbound(this)` so a freshly constructed `new Constraint { Design = design }` joins design's session and shows up in `design.Constraints` at Save time.
- `[Children]` — sync collection from the parent side, computed via reverse-fk traversal. Declare as `partial IReadOnlyCollection<T> Name { get; }`. No Add/Remove (children are managed via `child.Parent = parent` on the child side).
- **Forward/inverse relations** — declare an attribute pair like `RestrictsAttribute : ForwardRelation` + `RestrictedByAttribute : InverseRelation<RestrictsAttribute>`. The generator emits a sibling `Restricts : IRelationKind` marker class. **Within-aggregate** entity-side read collections: declare as `IReadOnlyCollection<IEntity>`. **Cross-aggregate**: declare as `IReadOnlyCollection<IRecordId>`. There is no sync `Relate` and no buffered intent for `SaveAsync` to drain (preview.45 ripped the relation write buffer out — substrate owns concurrency).
- **Relation variants (preview.51).** Edge mutations go through variant classes — annotate a class with the relation kind (e.g. `[Restricts]`), name endpoints with `[In]` / `[Out]` (entity for within-aggregate, typed id for foreign-aggregate), and optional `[Property]` payload members. The variant **is** an `IEntity`. To create an edge: `await session.SaveAsync(new ConstraintRestrictsUserStory { Source = constraint, Target = userStory }, tx)`. Multi-variant kinds get `SCHEMALESS` edge tables (each variant's payload coexists on the same table); single-variant kinds keep `SCHEMAFULL`. `Session.UnrelateAsync<TKind>(src?, tgt?, tx)` survives for edge deletion (bulk and pair-wise).
- **Union-endpoint variants (preview.54).** A variant's `[In]` / `[Out]` can be a user-declared union interface, accepting any participating table's typed id. Declare the union as a parameterless attribute deriving from `In<TKind>` / `Out<TKind>` (`public sealed class FooTargetAttribute : Out<RestrictsAttribute>`), apply it to a partial interface deriving from `IRecordId` (`[FooTarget] public partial interface IFooTarget : IRecordId`), and enrol each participating `[Table]` via a per-table partial (`partial interface IConstraintRecordId : IFooTarget`). The variant then types the endpoint as the union interface (`[Out] partial IFooTarget Target { get; set; }`). One variant covers every union member — no per-target duplication. The hydration dispatcher's `(in.tb, out.tb)` switch Cartesian-expands over union members; the schema's `FROM` / `TO` clause expands too. Diagnostics: CG031 (kind mismatch — union pinned to one kind applied to a variant of another), CG032 (dead union — interface attributed but no per-table marker enrols any table).
- **Shared-shape relation interfaces (preview.55).** When several relation kinds share the same `(Source, Target, payload)` shape, declare a `partial interface I... : IRelationVariant` capturing it and add it to each variant's base list. The generator emits a static factory onto the interface: `static I Create<TKind>(Action<I> init) where TKind : IRelationKind`, dispatching on `typeof(TKind)` to instantiate the right concrete variant. Construction goes from a hand-maintained switch to one expression: `var edge = IMyContract.Create<Calls>(e => { e.Source = s; e.Target = t; … })`. Polymorphic queries across the contributing kinds are deliberately NOT generated — per-kind dispatch on the read side remains a user concern (the limit is intentional; kinds remain distinct edge tables). Diagnostics: CG033 (interface must be partial), CG035 (no implementing variants — dead interface).
- **Annotated shared-shape lift (preview.56).** When the shared-shape interface itself carries `[In]` / `[Out]` / `[Property]` / `[Id]` on its members, variants can collapse the per-variant `[In]/[Out]/[Property] partial T Source/Target/Payload { get; set; }` boilerplate down to `[Calls] partial class CallsRelation : IMyContract;`. The linker walks every annotated shared-shape interface in the variant's base chain (transitive closure), merging them with the variant's own self-declared members on a per-role/per-name basis. Local self-declared members win wherever they overlap; non-overlapping interface contributions add; overlapping pieces must agree on Role + Name + Type + nullability or the variant fails closed (silent drop, matching the existing malformed-input contract). The interface closure also walks each shared-shape's own base interfaces — payload shape can live on a separate `IPayload : IRelationVariant` and an endpoint shape on `IEdge : IPayload`, and a variant `: IEdge` gets the composed result. The variant emit picks up lifted props as full (non-partial) auto-property declarations satisfying the interface contract via partial-class declaration merging; self-declared members keep their `partial` keyword. Unannotated shared-shape interfaces (the preview.55 default) leave the lift inert.
- **Union interfaces** — emitted automatically per relation kind whose target/source set has 2+ members. Naming: target side uses the inverse attribute name (`I{InverseName}` → `IRestrictedBy`); source side uses the singularised forward attribute name (`I{Singularize(ForwardName)}` → `IRestrict`). Each entity union has a parallel id-side union (`I{InverseName}Id` → `IRestrictedById`).
- The `{Name}Id.Value` is a `string`, validated to be either a Ulid stringification (auto-minted by `New()`) or a short lower_snake_case slug (≤32 chars). Use the slug form sparingly — for stable-named records (singletons, config rows). Anything else should be a Ulid.

## End-to-end usage shape

```csharp
// One-shot SDK connection. CBOR over WebSocket.
await using var db = await Disruptor.Surreal.SurrealClient.ConnectAsync(SurrealOptions.Parse(
    "Url=ws://localhost:8000;Namespace=app;Database=main;User=root;Password=root"));

// User's [CompositionRoot] partial — generator grafts Load*Async overloads onto it.
[CompositionRoot]
public partial class Workspace { }

var workspace = new Workspace();

// Apply schema (idempotent).
await Workspace.ApplySchemaAsync(db);

// Read session — pass the SDK Surreal directly. No transaction.
var read = await workspace.LoadDesignAsync(db, designId);
var design = read.Get<Design>(designId)!;
foreach (var c in design.Constraints)
    Console.WriteLine(c.Description);

// Write session — app opens a Transaction, library dispatches into it,
// app calls tx.CommitAsync (or tx.CancelAsync) when done.
await using var tx = await db.BeginTransactionAsync();
try
{
    var session = await workspace.LoadDesignAsync(tx, designId);
    var design = session.Get<Design>(designId)!;
    design.Description = "edited";

    var constraint = session.Track(new Constraint { Design = design, Description = "no negatives" });

    // Per-entity Save: walks forward refs (Details) and Tracked children (Constraints, Epics, …)
    // for the entity. Relation variants are entities too — Save them directly:
    await session.SaveAsync(design, tx);
    await session.SaveAsync(
        new ConstraintRestrictsUserStory { Source = constraint, Target = someUserStory }, tx);
    await tx.CommitAsync();
}
catch (Disruptor.Surreal.SurrealConflictException)
{
    // Another writer's commit landed first. Reload + retry. tx auto-cancels on dispose.
}
```

The Sample project's classes (`Design`, `Constraint`, `Epic`, `Feature`, `UserStory`, `AcceptanceCriteria`, `Test`, `Review`, `Finding`, `Observation`, `Issue`, `DesignChange`, `Details`) are the canonical worked examples — read them alongside the generated `.g.cs` outputs (especially `{Namespace}.{CompositionRoot}.Schema.g.cs`) to see the input/output mapping. The DDL is no longer hand-maintained: `SchemaEmitter` walks the model graph and emits the chunks behind a static `Schema` accessor on the user's `[CompositionRoot]` partial — the emitted `Workspace.ApplySchemaAsync(db)` is the canonical boot path. The same partial also exposes `Workspace.ReferenceRegistry`, which the emitted `Load*Async` methods pass into `SurrealSession`'s ctor.

## Engineering log

Newest first. One or two lines per preview. "Substantive" means architecture / behaviour / new public surface; polish (renames, doc edits, formatting) is omitted.

### preview.57 — annotated shared-shape lift: closure walk + per-property merge (DONE 2026-05-13)

preview.56 shipped the lift as a strict "exactly one annotated source" rule with all-or-nothing variant bodies. preview.57 relaxes both axes so the same machinery composes:

- **Closure walk on the interface side.** `SharedShapeExtractor.TryExtract` now lifts model attributes from `iface.AllInterfaces` (the transitive base closure) plus the interface itself, sorted by FQN for deterministic order. An endpoint shape can live on `IEdge : IRelationVariant`, payload on `IPayload : IRelationVariant`, and a third `ICombined : IEdge, IPayload` composes both. `SamePropertyIdentity` (Role + Name + Type FQN) dedupes a property declared at multiple levels of the chain so the merge doesn't double-add.
- **Per-property merge on the variant side.** `RelationLinker.LiftVariantsFromSharedShape` walks every annotated shared-shape candidate the variant implements (sorted by FQN), threading each through `TryMergeLift` → `TryMergeSingular` / `CompatibleProperty`. Local self-declared members win for overlapping roles; non-overlapping interface contributions accumulate; overlapping pieces must agree on Role + Name + Type FQN + `IsNullable` or the variant fails closed (silent drop, same fail-soft contract as malformed input).
- **Extractor relaxation.** `RelationVariantExtractor` only drops a variant on multiplicity violation (`>1` of a role) now — half-populated variants (only `[In]`, no `[Out]`, or vice versa) pass through with one endpoint null for the linker to fill from an interface contribution.

Three patterns that didn't compose in preview.56 now do:

```csharp
// 1. Payload-only base contract (doesn't need to derive from IRelationVariant
//    — the closure walk picks up its [Property] declarations regardless):
public interface IEdgePayload {
    [Property] string Confidence { get; set; }
}
public partial interface ICodeSymbolEdge : IEdgePayload, IRelationVariant {
    [In]  CodeSymbolId Source { get; set; }
    [Out] CodeSymbolId Target { get; set; }
}
[Calls] public partial class CallsRelation : ICodeSymbolEdge;

// 2. Layered IRelationVariant interfaces — payload contract on the base, endpoints
//    on the derived:
public partial interface IEdgePayload : IRelationVariant {
    [Property] string Confidence { get; set; }
}
public partial interface ICodeSymbolEdge : IEdgePayload {
    [In]  CodeSymbolId Source { get; set; }
    [Out] CodeSymbolId Target { get; set; }
}
[Calls] public partial class CallsRelation : ICodeSymbolEdge;

// 3. Variant adds its own per-variant payload while lifting endpoints + shared
//    payload from the interface:
[Calls] public partial class CallsRelation : ICodeSymbolEdge {
    [Property] public partial string Notes { get; set; }
}
```

Fail-closed semantics preserved: two annotated interfaces with truly incompatible Source types (`CodeSymbolId` vs `OtherSymbolId`) still drop the variant — the test that exercised "ambiguity drops" in preview.56 was repurposed as "conflict drops" with genuinely conflicting endpoint contracts.

Tests: **283/283 green** (280 prior + 3 net new for the merge cases). The preview.56 "multi-interface ambiguity drops" test became preview.57's "conflicting endpoint types drop" test; the original "missing-In/Out returns null" extractor tests were renamed to "passes through for linker lift" and assert the partial-model shape that now reaches the linker.

Known rough edge / future work: when the merge fails (incompatible Role / Name / Type / IsNullable across two contributions), the variant is currently dropped silently — matching the existing malformed-input contract but offering no diagnostic. The natural follow-up is a `CG036`-style diagnostic ("shared-shape lift conflict") naming the variant, the conflicting interfaces, and the role/property where they disagree, so users hitting the failure mode get a clear pointer to the offending pair instead of a missing-emit symptom.

### preview.56 — annotated shared-shape lift onto empty-body variants (DONE 2026-05-12)

The shared-shape interface introduced in preview.55 now doubles as the source of `[In]` / `[Out]` / `[Property]` / `[Id]` model attributes. When the user puts model annotations on the interface members, any variant whose body collapses to `;` inherits the shape from the interface — saving 3–4 lines of per-variant boilerplate. preview.55's behaviour is fully preserved: variants that self-declare attributed `partial` members continue to drive their own emit, and unannotated shared-shape interfaces stay inert.

```csharp
public partial interface ICodeSymbolInheritsRelation : IRelationVariant {
    [In]       CodeSymbolId Source { get; set; }
    [Out]      CodeSymbolId Target { get; set; }
    [Property] string Confidence { get; set; }
}

// preview.55 (still works):
[Inherits] public partial class A : ICodeSymbolInheritsRelation {
    [In]       public partial CodeSymbolId Source { get; set; }
    [Out]      public partial CodeSymbolId Target { get; set; }
    [Property] public partial string Confidence { get; set; }
}

// preview.56 — collapsed:
[Inherits] public partial class B : ICodeSymbolInheritsRelation;
```

Pipeline changes:
- `SharedShapeInterfaceCandidate` gains `LiftedIn` / `LiftedOut` / `LiftedId` / `LiftedPayload` fields. `SharedShapeExtractor.TryExtract` walks the interface's `IPropertySymbol` members through `RelationVariantExtractor.ResolveRole` / `BuildProperty` (now `internal static` so both extractors share the same classification + shape).
- `RelationVariantModel.In` / `Out` go nullable. `RelationVariantExtractor` no longer returns null when a variant declares zero own annotated members; it produces a placeholder model with null endpoints awaiting an interface lift. Half-populated variants (some `[In]` but no `[Out]`) still return null — the malformed-input fail-soft is unchanged.
- `RelationLinker.LiftVariantsFromSharedShape` runs after the per-variant rewrite. For each variant with null endpoints, it finds matching annotated shared-shape candidates from the variant's `ImplementedInterfaceFullNames`; exactly one match wins and contributes In/Out/Id/Payload, multiple matches drop the variant (ambiguity), zero matches drop it (no source). The lifted props go through the same `RewriteType` as table-rewritten props so `IsTableType` is correctly populated.
- `RelationVariantEmitter` and `SchemaEmitter` defensively skip variants whose endpoints remain null (should never happen post-linker; defensive guards keep tests bypassing the linker safe). All four property-emit paths (entity-typed, typed-id, union-endpoint, payload) emit `partial T Name` only when `IsPartial=true` on the property model. Lifted-from-interface props carry `IsPartial=false` (interface members aren't `partial`), so the generator emits full auto-property declarations on the variant class.

What stayed:
- preview.55 self-describing shape — variants can still declare their own attributed partial members; the lift only ever fills *null* endpoints.
- `IRelationVariant` marker, `Create<TKind>` factory emit, CG033 (interface must be partial), CG035 (no implementing variants).
- Polymorphic query surface across kinds is still NOT generated; per-kind dispatch on the read side remains a user concern.

Sample: `src/Disruptor.Surface.Sample/Spike/InheritsRelation.cs` declares an annotated `ICodeSymbolInheritsRelation` and a single empty-body `[Inherits] partial class CodeSymbolInheritsCodeSymbol : ICodeSymbolInheritsRelation;`. The emitted `.RelationVariant.g.cs` carries full IEntity scaffolding (Hydrate, SaveAsync, EnumerateReferences) and full property declarations — no partial keyword in sight. Schema picks up the lifted payload (`DEFINE FIELD confidence ON inherits TYPE string`) and FROM/TO endpoints (`FROM code_symbols TO code_symbols ENFORCED`).

Tests: **280/280 green** (274 prior + 6 net new). New coverage: empty-body variant compiles + round-trips via reflection; emitted props don't carry the `partial` keyword; schema picks up lifted payload/endpoints; own annotated members win over the interface; unannotated interface leaves empty-body variant inert; multiple annotated interfaces on one variant drop it (ambiguity).

### preview.55 — shared-shape relation interfaces with kind-keyed Create&lt;TKind&gt; factory (DONE 2026-05-12)

User-declared interfaces deriving from `IRelationVariant` are recognised as "shared-shape contracts" over relation variants whose endpoint+payload shape match. The generator emits a static factory onto each such (partial) interface, removing the hand-maintained switch from variant construction call sites:

```csharp
public partial interface ICodeSymbolEdge : IRelationVariant {
    CodeSymbolId Source { get; set; }
    CodeSymbolId Target { get; set; }
    string Confidence { get; set; }
}

[Calls]      public partial class CallsRelation      : ICodeSymbolEdge { /* [In], [Out], [Property] */ }
[References] public partial class ReferencesRelation : ICodeSymbolEdge { /* same shape */ }

// Call site:
var edge = ICodeSymbolEdge.Create<Calls>(e => { e.Source = s; e.Target = t; e.Confidence = "high"; });
```

What the generator does:
- `SharedShapeExtractor` discovers partial interfaces whose transitive base chain includes `Disruptor.Surface.Runtime.IRelationVariant`. Excludes the runtime interface itself and union-endpoint interfaces (those derive from `IRecordId`).
- `RelationVariantExtractor` now captures the variant's `ImplementedInterfaceFullNames` (filtering out the runtime markers) so the linker can match variants to shared-shape contracts.
- `RelationLinker.ComputeSharedShapes` builds one `SharedShapeModel` per candidate, listing every implementing variant with `(VariantFqn, KindMarkerFqn, EdgeName)`. Common source/target endpoint types across variants are computed but not consumed by the emitter; they're held for future use.
- `SharedShapeEmitter` emits a partial fragment of the interface carrying `static {I} Create<TKind>(System.Action<{I}> init) where TKind : IRelationKind` — an if-chain dispatching on `typeof(TKind)` to `new ConcreteVariant()`, then running the user's initialiser before returning the instance.

Scope (deliberately narrow per design conversation): generator help is **kind-keyed construction only**. The polymorphic query surface across kinds stays a user-side concern — relation kinds remain distinct edge tables, and querying still requires per-kind dispatch (`session.QueryVariantsOutgoingAsync<TVariant>` per kind, concatenate). The shared-shape interface gives `IEnumerable<I>` uniform handling on the read side, but the generator doesn't synthesise "all variants of these kinds" reads.

Diagnostics:
- **CG033** — shared-shape interface not declared `partial`. Error; the generator can't graft the static method otherwise.
- **CG035** — partial shared-shape interface with zero implementing variants. Warning; the interface still functions as a marker type, but the factory has nothing to dispatch to.

Sample: the existing `Spike/` subdirectory (added when scoping this feature) drove the design and now ships as the working example — `ICodeSymbolEdge` over `CallsRelation` + `ReferencesRelation`, both on the new `CodeSymbol` aggregate. 5 new `EmissionShapeTests` cover the interface satisfaction property, factory dispatch, unknown-kind throw, CG033, CG035. **274/274 green** (270 prior + 4 net new; one existing spike test got refocused into the partial-property-satisfaction check).

### preview.54 — union endpoints on relation variants (DONE 2026-05-12)

Variants can now type an `[In]` / `[Out]` endpoint as a user-declared union interface deriving from `IRecordId`, accepting any participating table's typed id. A single variant covers what would otherwise need N variant classes — one per concrete target type.

Shape:
- New attribute bases `In<TKind>` / `Out<TKind>` in `Disruptor.Surface.Annotations`. Users derive `public sealed class FooAttribute : Out<RestrictsAttribute>` and apply `[Foo]` to a partial interface that derives from `IRecordId`. The kind binding lives in the generic argument; the interface attribute is parameterless at use site (consistent with the rest of the annotations).
- Per-table marker interface `I{Name}RecordId : IRecordId` emitted by `IdEmitter` alongside each `{Name}Id` struct (preview.54 phase 1, commit `efa31da`). The user enrols a table in a union by extending its marker in a partial: `partial interface IConstraintRecordId : IFooTarget { }`. Combined with the user-declared `IFooTarget : IRecordId`, the typed id transitively satisfies the union and becomes assignable to a `[Out] partial IFooTarget Target` property.
- Two-pass syntax discovery in `UnionEndpointExtractor` (Phase 2, commit `3782c3f`): pass (a) finds interfaces attributed with anything deriving from `In<TKind>` / `Out<TKind>` and lifts `TKind` from the attribute's base chain; pass (b) finds partial `I{Name}RecordId` interface decls with a non-empty base list. `RelationLinker.ComputeUnionEndpoints` stitches the two candidate sets into `UnionEndpointModel`s on `ModelGraph.UnionEndpoints`.

Variant emitter (Phase 3):
- `RelationVariantEmitter` branches on `graph.FindUnionEndpoint(...)` for each `[In]` / `[Out]` property. Union endpoints emit a single backing field of the union interface (no cached entity ref, no separate id backing — id-only, like cross-aggregate typed-id endpoints).
- Hydrate body switches on the loaded row's table name (`in.tb` / `out.tb`) to construct the matching `{Name}Id` and casts back to the union interface; unknown tables throw via the default arm so a schema-vs-row drift fails fast.
- `EnumerateReferences` uses `RecordId.From(IRecordId)` (no implicit operator on the interface). `SaveAsync` skips the forward-dep walk for union endpoints (caller is responsible for endpoint existence).
- Per-kind hydration dispatcher Cartesian-expands union endpoints into `(in.tb, out.tb)` pairs so a row with any participating endpoint table dispatches to the union variant. Pair collisions still surface as CG030.

Schema emitter (Phase 4):
- `SchemaEmitter.CollectEndpointTables` consults `graph.FindUnionEndpoint(...)` before falling back to `ResolveEndpointTable`; a union-typed endpoint contributes every `UnionEndpointModel.MemberTableFullNames` entry to the edge table's `FROM` / `TO` clause. The `TYPE RELATION ENFORCED ... FROM source TO a|b|c` shape covers every union member at the substrate level.

Diagnostics (Phase 5):
- **CG031** — union endpoint pinned to a kind different from the variant's class-level kind attribute. Reported from `RelationVariantEmitter` per `[In]` / `[Out]` endpoint.
- **CG032** — union interface with zero per-table marker partial opting any table in (unreachable union). Warning, not error — the union still resolves but variants can't satisfy any `FROM`/`TO` clause.

Sample:
- New `[Pertains]` kind in `src/Disruptor.Surface.Sample/Relations/PertainsAttribute.cs` with a `[PertainsTarget]` union (`IPertainsTarget`). Constraint and UserStory opt in via per-table partials in `src/Disruptor.Surface.Sample/Models/PertainsTargetMembers.cs`. The single variant `IssuePertainsTarget` in `src/Disruptor.Surface.Sample/Relations/Variants/` accepts either typed id at the `[Out]` site.

Tests: 269/269 green (261 prior + 8 new union-endpoint shape tests covering property emission, hydrate switch, RecordId.From use, save-skip-walk, dispatcher Cartesian, schema FROM/TO expansion, CG031, CG032).

### preview.51 — relation-as-class redesign (DONE 2026-05-12)

Replaces the `ForwardRelation<TPayload>` generic-payload pattern with relation declarations that look like `[Table]` classes — annotated with the relation kind (e.g. `[Restricts]` on the class), with `[In]` / `[Out]` properties naming the endpoints, optional `[Id]`, and zero-or-more `[Property]` payload members. Multiple variants per kind discriminated at hydration via `(in.tb, out.tb)`. Phases 1, 1b, 2, 3, 4, 5, 6a, 6b, 6c shipped end-to-end; **231 tests passing, 0 skipped.** Sample re-runs end-to-end against a live SurrealDB.

Shape:
- `RelationAttribute` allows `AttributeTargets.Property | AttributeTargets.Class`. On a property → "typed read collection on this entity"; on a class → "variant declaration of the kind".
- `[In]` / `[Out]` mark the endpoint properties on a variant class. Property type is the entity (within-aggregate) or the typed id (foreign-aggregate); the generator picks the read-side resolution based on which form was declared.
- `[Property]` on a variant class carries payload columns; the generator walks them at codegen for typed Hydrate / Save.
- A variant **is** an `IEntity` — the runtime treats it the same as table entities, with `IRelationVariant : IEntity` as a methodless marker so `SurrealSession.SaveContext.MarkSaved` / `CleanupLocalState` can branch and update `state.Edges` for in-session consistency.
- Per-kind `{KindName}Id` typed-id struct (e.g. `RestrictsId`) emitted by `RelationKindEmitter`/`IdEmitter.WriteIdType`. Single id type shared across all variants of the kind.
- Per-kind hydration dispatcher `{KindName}Hydration.HydrateVariant(SurrealValue, IHydrationSink)` emitted for every kind (single-variant gets one too for call-site uniformity); branches on `(in.tb, out.tb)` for multi-variant kinds.
- Per-kind variant marker interface `I{KindName}Variant` emitted only for kinds with 2+ variants (single-variant kinds skip it).
- Schema emission: multi-variant kinds get `SCHEMALESS` (each variant's payload coexists on one edge table); single-variant kinds keep `SCHEMAFULL`. Both still get `TYPE RELATION ENFORCED` + `DEFINE INDEX … COLUMNS in, out UNIQUE`.

Write path:
- **`Session.SaveAsync(variantInstance, tx)` is the relation write path.** Construct the variant with its endpoints (`new ConstraintRestrictsUserStory { Source = constraint, Target = userStory }`), pass it to `SaveAsync`. The variant's emitted `IEntity.SaveAsync` dispatches `INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY UPDATE …]` (or `INSERT RELATION IGNORE INTO …` for payloadless variants). No more `RelateAsync` overloads, no `RelateAsyncReplace`, no per-kind `{Marker}RelateExtensions` static class.
- `Session.UnrelateAsync<TKind>(src?, tgt?, tx, ct)` survives unchanged for bulk and pair-wise edge deletion.

New async query terminals on `SurrealSession`:
- `QueryVariantsOutgoingAsync<TVariant>(srcId, tx, ct)` / `QueryVariantsIncomingAsync<TVariant>(tgtId, tx, ct)` — returns hydrated variant entities, tracked in the session, edges mirrored in `state.Edges`.
- `QueryOutgoingAsync<TKind, TTarget>(srcId, tx, ct)` / `QueryIncomingAsync<TKind, TTarget>(tgtId, tx, ct)` — returns target entities directly (skips variant hydration); not auto-tracked.
- `QueryVariantsAsync<TVariant>(sql, bindings, tx, ct)` — raw-SQL escape hatch.
- All have `db` (read-only) overloads + `IEntity` convenience overloads.

Sync read affordances (entity-side `[Restricts] partial IReadOnlyCollection<...>` properties on the entity classes) are unchanged — they still read from the in-memory snapshot via `Session.QueryOutgoing` / `QueryIncoming`. Variant-typed async queries are orthogonal (fresh substrate read).

**Variant query endpoint-resolution gotcha.** `QueryVariantsOutgoingAsync<TVariant>` returns hydrated variants whose entity-typed `[In]` / `[Out]` properties resolve through the session's identity map. **If the endpoint entities aren't in the session, the property getter throws `Endpoint 'X' is not set.`** This is intentional: variant queries fetch only the edge rows, not their endpoints. Three ways to handle it: (a) reuse the same session that `Load{Root}Async` populated so within-aggregate endpoints resolve cleanly; (b) for cross-aggregate edges or any case where endpoints aren't loaded, use `((IEntity)v).EnumerateReferences()` to read raw `(fieldName, RecordId?)` tuples for `"in"` / `"out"` directly from the variant row's hydrated state — always works, no resolution; (c) for variants with typed-id endpoints (cross-aggregate — `[In] ConstraintId Source`), `Source` and `Target` return the typed id directly without entity resolution.

Deleted:
- `Session.RelateAsync<TKind>(...)` — all 8 overloads gone. Use `SaveAsync(new TVariant { … }, tx)` instead.
- `Session.RelateAsyncReplace<TKind>(...)` — gone. The variant `SaveAsync` does the equivalent dispatch via `INSERT RELATION INTO … ON DUPLICATE KEY UPDATE`.
- `{Marker}RelateExtensions` static class emission (was generated for typed-payload kinds in preview.50) — gone.
- `EdgePredicateFactoryEmitter` — entire emitter file deleted. Edge payload predicates are reachable through the per-variant `{Variant}Q` factory now (variant **is** an entity).
- `EdgePayloadFieldModel` model record — gone.
- `RelationKindModel.PayloadFields` and `PayloadTypeFqn` — fields removed.
- 3 previously-skipped `EmissionShapeTests` (the legacy `TypedEdgePayload_*` set) — replaced by Phase 2 + Phase 3 tests on the new shape.

What stayed (intentionally):
- `ForwardRelation` and `InverseRelation<TForward>` abstract bases — user attribute classes still derive from these. Only `ForwardRelation<TPayload>` (the typed-payload generic) is gone.
- `IRelationKind.EdgeName` static-virtual — used by Phase 4's reflection cache for `TVariant → kind → edge-name` lookup.
- Entity-side `[Restricts] partial IReadOnlyCollection<...>` properties — sync read affordance, unchanged.

Sample changes:
- 11 new variant classes in `src/Disruptor.Surface.Sample/Relations/Variants/` (one per relation kind, except `Restricts` which has 3 — UserStory / AcceptanceCriteria / Test targets).
- `Program.cs` migrated: every `session.RelateAsync<TKind>(src, tgt, tx)` → `session.SaveAsync(new TVariant { Source = src, Target = tgt }, tx)` (10 call sites).
- New `DemoAsyncQueryTerminals(designId, reviewId, db)` helper exercising all five Phase 4 patterns: (a) `QueryVariantsOutgoingAsync<ConstraintRestrictsUserStory>` (option A typed variant, uses `EnumerateReferences()` to print endpoint ids robustly across cross-aggregate target rows), (b) `QueryOutgoingAsync<Restricts, UserStory>` (option B target traversal), (c) `QueryIncomingAsync<Validates, Test>` (option B incoming), (d) multi-variant kind dispatch — same `Constraint` source, querying the AC and Test variants of `Restricts`, (e) cross-aggregate variant — `IssueConcernsConstraint` with typed-id endpoints (`Source` / `Target` are typed ids, no resolution risk).

Two runtime bugs surfaced during the Sample-run validation and were fixed in-flight:
- **Schema syntax fix**: Phase 2's `SchemaEmitter` initially produced `SCHEMAFLEXIBLE` which is not a valid SurrealDB keyword. Corrected to `SCHEMALESS` (`SchemaEmitter.cs:462`). Locked by Sample run against a live SurrealDB v3.
- **Cross-`SaveContext` entity tracking**: `SaveContext.MarkSaved` now promotes saved entity ids into the session's `loadedAtStart` set, so a subsequent top-level `Session.SaveAsync(...)` whose forward-dep walk reaches a previously-saved entity sees `IsTracked=true` and dispatches UPDATE rather than CREATE. Without the promotion, CREATE was firing twice and the substrate rejected the duplicate. Locked by `SaveAsync_TwoSequentialCalls_PromoteSavedEntityToTrackedAcrossSaveContexts` test.

### preview.50 — typed RelateAsync per ForwardRelation\<TPayload\> (deleted in preview.51)

Generator emitted a `{Marker}RelateExtensions` static class with four typed `RelateAsync` overloads per `ForwardRelation<TPayload>` kind, each going through `SurrealSession.RelateAsyncReplace<TKind>` (`INSERT RELATION INTO … ON DUPLICATE KEY UPDATE`). Unblocked the code-indexer use case (per-edge replay-replace semantics with a typed payload). Superseded by preview.51's relation-as-class redesign — `ForwardRelation<TPayload>`, the `{Marker}RelateExtensions` emitter, and `RelateAsyncReplace` are all deleted; the `INSERT RELATION INTO … ON DUPLICATE KEY UPDATE` dispatch shape lives on inside the per-variant `IEntity.SaveAsync` body.

### preview.49 — INSERT RELATION IGNORE replaces UPSERT for idempotent edges

`SurrealSession.RelateAsyncCore` now dispatches `INSERT RELATION IGNORE INTO {edge} $_content;` with id/in/out built into a `SurrealObject`. Reverses preview.46's UPSERT path: `TYPE RELATION ENFORCED` schemas reject UPSERT (it creates a regular row that the substrate then refuses to recognise as an edge). `IGNORE` absorbs both duplicate-id and `UNIQUE INDEX (in, out)` violations as silent no-ops, satisfying the substrate's edge-typing constraint while keeping idempotence on the deterministic `RecordId.Idempotent` edge id.

### preview.48 — nullable round-trip + cross-session Save guards

Two real fixes: (1) nullable scalar round-trip via `ReadOrDefault<T?>` in `EmitHydrateValueProperty` (loaded `null` was previously hitting non-nullable backing fields and erroring); (2) `Track<T>` now binds before identity-map insert so cross-session attempts fail cleanly without leaving dangling entries; `EnsureBoundForSave` rejects `entity.Session != this` so `SaveAsync` can't silently split reads (entity's bound session) from writes (the tx-providing session).

### preview.47 — cascade re-anchor (Step 5 of unpainting plan)

`DeleteAsync` runs three-phase pre-flight via `PlanDelete` (Cascade + Unset to fixpoint, then steady-state Reject blockers throw `CascadeRejectException` before any wire dispatch). Library predicts; substrate enforces via `REFERENCE ON DELETE` clauses. `IEntity.EnumerateReferences` + `SetReferenceTo` are emitted by the generator. Closes the "currently parked" cascade gap left by preview.34's strip.

### preview.46 — RELATE → UPSERT for idempotence (REVERSED in preview.49)

Switched `RelateAsync` to `UPSERT` against the deterministic `RecordId.Idempotent` edge id. Looked clean — substrate-native idempotence via primary key — but `TYPE RELATION ENFORCED` schemas reject `UPSERT` (it doesn't satisfy the edge-typing path). Reversed in preview.49 by `INSERT RELATION IGNORE`.

### preview.45 — ripped out the relation write buffer

`RelateAsync` is now a sync-on-tx call against the substrate; no more snapshot-diff dispatch via `SaveAsync`. Sync `Relate`/`Unrelate` removed from the public API. Closes the "library shadows the substrate" framing: with PendingState (preview.35) and CommitPlanner (preview.34) already gone, the relation buffer was the last write-buffer holdover. Substrate owns concurrency.

### preview.44 — two real bug fixes around dispatch correctness

(1) Silent `SurrealQueryResponse` statement errors now propagate via `EnsureSuccess()` / `Take(int)` — previously a failing statement was silently dropped on the floor. (2) Buffered relation payloads / edge ids were getting lost through `SaveAsync` — the snapshot-diff dispatch wasn't carrying them. (Moot after preview.45's removal of the relation buffer, but both findings stood on their own merits.)

### preview.43 — Surface\* prefix rename + style sweep

Query/projection types now carry the `Surface*` prefix (`SurfaceQuery<T>`, `SurfaceProjection`, `SurfaceQueryCompiler`, …) to keep the public API namespaced and avoid collisions with the SDK's `Surreal*` types. C# 14 extension blocks adopted across the runtime. Largely cosmetic but every public read API moved.

### preview.42 — typed-CBOR end-to-end + SurrealFormatter trimmed

User values flow as typed `SurrealValue` bindings; `SurrealFormatter` trimmed to the regex-validated `Identifier()` extension only (no more `RecordId` / `StringLiteral` / `RenderSurrealLiteral`). Closes the SQL-formatting attack surface from the days of inlined record-id literals.

### preview.40, .37, .36, .35, .34, .33 — see git log

Earlier preview entries (down to preview.33's relations-dispatch-in-SaveAsync) remain accurately summarised in commit messages. They predate this engineering log and the architecture sections above already reflect their net effect (PendingState / CommitPlanner / `flushAll` / `RenderBatch` / JSON bridge all gone).
