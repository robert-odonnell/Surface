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

1. **Attribute discovery** — the user-facing attributes (`[Table]`, `[AggregateRoot]`, `[CompositionRoot]`, `[Id]`, `[Property]`, `[Parent]`, `[Children]`, `[Reference]`, `[Inline]`, the four reference-delete behaviors `[Reject]` / `[Unset]` / `[Cascade]` / `[Ignore]`, the relation bases `RelationAttribute` / `ForwardRelation` / `InverseRelation<TForward>`, and `[RecordIdValue<T>]`) live as ordinary `.cs` files in `src/Disruptor.Surface.Runtime/Annotations/`, namespace `Disruptor.Surface.Annotations`. The generator binds to them by metadata name through `ForAttributeWithMetadataName`; the FQN constants in `AnnotationsMetadata` must stay in lockstep with the runtime declarations.
2. **Per-symbol extractors** (`Pipeline/`) lower Roslyn symbols into pure-data records under `Model/`. They cannot resolve cross-table references yet — `TypeRef.IsTableType` is seeded from the immediately visible attributes and patched up later.
3. **Linking** (`RelationLinker.Build`) takes the collected tables + relation kinds + composition roots and (a) rewrites every `TypeRef` so `IsTableType` is true wherever the underlying type was discovered to be a `[Table]`, (b) computes per-relation-kind `RelationUnion` sets — for each forward kind, the source set (forward attribute holders) and target set (inverse attribute holders), each becoming a marker interface when ≥2 members, (c) computes per-aggregate `AggregateModel` membership by walking `[Children]` from each `[AggregateRoot]` and detects entities reachable from 2+ roots as conflict descriptors, (d) detects cascade-only reference cycles for CG014.
4. **Emit** (`Emit/`) — emitters fire per generation:
   - `IdEmitter` — per-table `{Name}Id` `readonly record struct` with id-side union interfaces in its base list.
   - `UnionInterfaceEmitter` — per multi-member union, BOTH the entity-side marker (`IRestrictedBy`) AND the id-side marker (`IRestrictedById`).
   - `CompositionRootEmitter` — emits a partial declaration of the user's `[CompositionRoot]`-tagged class with two `Load{Root}Async` overloads per `[AggregateRoot]`: one taking `Disruptor.Surreal.SurrealClient db` (read-only), one taking `Disruptor.Surreal.SurrealTransaction tx` (write-mode, sees in-txn writes from the same transaction). No ctor, no fields, no base — the user owns construction entirely. Skipped when no `[CompositionRoot]` exists in the compilation.
   - `RelationKindEmitter` — per forward relation attribute (e.g. `RestrictsAttribute`), emits a sibling marker class without the `Attribute` suffix (`Restricts : IRelationKind`) carrying the SurrealDB edge name as a static property. The class is the type witness used by `SurrealSession.RelateAsync<Restricts>(src, tgt, tx)` and friends. Inverse kinds get no marker — the edge is named after the forward.
   - `AggregateLoaderEmitter` — per `[AggregateRoot]`, an internal `{Root}AggregateLoader` static class with two `PopulateAsync` overloads (SurrealClient db / SurrealTransaction tx). Each issues a single nested-`SELECT` query: root row with `*` plus `field.*` inline expansion for each `[Reference, Inline]`, then per-non-root-member subselects scoped via dotted parent paths back to the root (`WHERE feature.epic.design = $parent.id`), then per-relation-kind edge subselects (within-aggregate + cross-aggregate target-side). Hydration is delegated to per-entity `IEntity.Hydrate(SurrealValue, IHydrationSink)`, which writes directly into the entity's backing fields (no `sink.Parent` / `sink.Reference` calls — entities own their state).
   - `PartialEmitter` — partial implementations of every annotated property/method. Setters are pure backing-field writes — no `__WriteField`, no buffer, no Session interaction (the one exception: `[Parent]` setters cascade-track the child into the parent's session via `parent.Session.AdoptIfUnbound(this)`). Per-entity session plumbing: `_session` field, explicit `IEntity.Bind` / `IEntity.Session`, protected `Session` accessor that throws when unbound, `__EnsureSliceLoaded` slice guard. Per-entity hooks: `IEntity.Initialize` (idempotent mandatory-ref seeding via the user's `OnCreate{Name}` hooks), `IEntity.Hydrate` (SurrealValue-consuming row-to-entity population, writes backing fields directly), `IEntity.OnDeleting` dispatch, `IEntity.MarkAllSlicesLoaded`, `IEntity.GetParentId` (when the table has a `[Parent]`), and **`IEntity.SaveAsync`** — per-entity Save dispatch that walks forward-dependency backing fields (Reference / Parent) recursively, dispatches `CREATE/UPDATE record:id CONTENT { ... }`, walks new children via the `[Children]` property accessor, and dispatches new outgoing relations via the snapshot diff (`Session.GetNewOutgoingEdges<TKind>`). Relation collection property reads emit `Session.QueryOutgoing<TKind, TElement>(this)` / `QueryIncoming<TKind, TElement>(this)` / `QueryRelatedIds<TKind>(this)` / `QueryInverseRelatedIds<TKind>(this)`.
   - `ReferenceRegistryEmitter` — sealed `IReferenceRegistry` impl in the consumer (`GeneratedReferenceRegistry`) PLUS a partial fragment of the user's `[CompositionRoot]` exposing the singleton via `public static IReferenceRegistry ReferenceRegistry`. Model-scoped so multiple Disruptor.Surface-generated assemblies can coexist in one process.
   - `SchemaEmitter` — emits the chunked DDL via `{CompositionRoot}.Schema` (a partial fragment) backed by an internal `_chunks` array. `IReadOnlyList<string>` of DDL chunks: entity-tables block + per-`[Table]` field block + per-relation-kind table definition. Idempotent via `DEFINE … IF NOT EXISTS`. The generator also emits `Workspace.ApplySchemaAsync(db)` and `ApplySchemaAsync(tx)` for the common boot path.
   - `LoadEntryEmitter` / `IdsAsyncEmitter` / `TraversalBuilderEmitter` — query-side surface (`Query<T>.LoadAsync`, `Query<T>.IdsAsync`, per-table traversal builders).
   - Diagnostics — CG001+ descriptors reported from the linker output.

### Equatability is the contract

Every record under `Model/` is fed to `IncrementalGenerator` providers. **All collections must be `EquatableArray<T>`, never `ImmutableArray<T>` or `List<T>`**, because Roslyn deduplicates pipeline outputs by record equality and the BCL collection types compare by reference. Adding a mutable field, a lazy cache, or a non-equatable collection silently regresses incremental builds. See `ModelGraph`'s `<remarks>` for the canonical statement of this rule.

### User code cannot reference generator-emitted types

When the user writes `partial IReadOnlyCollection<IRestrictedBy> Foo { get; }`, Roslyn tries to resolve `IRestrictedBy` *during the same analysis pass* in which the generator would emit it. The interface doesn't exist yet from Roslyn's POV → it captures an error type, the generated impl's `FullyQualifiedName` doesn't match the user's declaration, and you get `CS9255 (partial member declarations must have the same type)` plus `CS0246 (type not found)` in the generated `.g.cs`. **Generator-emitted code referencing other generator-emitted types is fine** — both files compile in the same later pass — so the workaround is to keep the user's signature pointed at types that already exist (`IEntity` for entity collections, `IRecordId` for id collections) and only use the generated interface inside emitted code. **The same caveat applies to typed relation kinds:** declare your domain-verb passthrough with `IRestrictedBy` (the user-side base interface that already exists), not with the emitted `Restricts` marker class as a generic argument inside a `partial` member's declared type — but in expression position (as `Session.RelateAsync<Restricts>(...)`) the marker resolves fine because that resolution happens at body-compile time, after generator emit.

### Emit conventions

- `IdEmitter` emits each `{Name}Id` as a `readonly record struct {Name}Id(string Value)`. The `Value` initializer routes through `RecordIdFormat.Validate`, which only accepts two forms: a 26-char Ulid stringification (what `New()` mints) or a short lower_snake_case slug (max 32 chars, opt-in for stable-named records like config singletons). Anything else throws `FormatException` at construction. Quoted-string ids are explicitly unsupported.
- `PartialEmitter.SessionType`, `EntityInterface` (and the matching constants in the other emitters) pin the target namespace `Disruptor.Surface.Runtime`. If the runtime is renamed or split, every emitter that bakes a `global::Disruptor.Surface.Runtime.*` literal must change in lockstep.
- `RelationKindEmitter` strips the `Attribute` suffix to name the marker class. `RestrictsAttribute` (the user's attribute, used as `[Restricts]`) and `Restricts` (the marker, used as `Session.RelateAsync<Restricts>(...)`) coexist in the same namespace because attribute-position resolution looks for `*Attribute` first and type-position resolution looks for the bare name.
- `ReferenceRegistryEmitter` keeps the impl class internal to the consumer assembly and exposes the singleton via a partial fragment on `[CompositionRoot]`. Same pattern works for any per-model metadata — emit the impl as an internal class, attach a static accessor to the user's partial. Anything new the runtime needs at load time gets passed into `SurrealSession`'s ctor; nothing reaches into a process-global facade any more.
- **The generator does not look at user methods at all.** Every model annotation (`[Property]`, `[Parent]`, `[Reference]`, `[Children]`, `RelationAttribute`-derived) is `AttributeTargets.Property` only — methods cannot carry them. The `Session` DSL handed to each entity is the entire library contract; domain methods are plain user code calling that DSL. If a user wants a domain verb, they write a one-liner: `public void Restricts(IRestrictedBy x) => Session.Relate<Restricts>(this, x);` (sync, in-memory) or `public Task RestrictsAsync(IRestrictedBy x, Transaction tx) => Session.RelateAsync<Restricts>(this, x, tx);` (async dispatch).
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
    - `SaveAsync(IEntity entity, SurrealTransaction tx, ct)` — per-entity Save. Auto-binds + initialises the entity, walks forward dependencies (Reference / Parent backing fields) recursively, dispatches a whole-entity `CREATE/UPDATE record:id CONTENT { ... }`, walks new children via the `[Children]` accessor recursively, marks the entity saved. **Edges are not in scope** — dispatch them with `RelateAsync` against the same transaction. Per-entity is the canonical write surface; the user picks what to save.
    - `DeleteAsync(IEntity entity, SurrealTransaction tx, ct)` — runs `OnDeleting`, dispatches a single `DELETE`, removes from the in-memory snapshot. Cascade ([Reject]/[Cascade]/[Unset] semantics) currently not re-anchored after the preview.34 strip; user is responsible for dependent cleanup or accepts the schema's `REFERENCE ON DELETE` behavior.
    - `RelateAsync<TKind>(src, tgt, tx, ct)` — direct edge creation with `RecordId.Idempotent` default. Optional overloads accept an explicit edge id (`RecordId edge`) and/or a payload (`IReadOnlyDictionary<string, object?>` → `RELATE … CONTENT { … }`). Updates the in-memory edge index so subsequent `QueryOutgoing`/`QueryIncoming` see the new edge within the session.
    - `UnrelateAsync<TKind>(src?, tgt?, tx, ct)` — direct edge deletion. At least one endpoint non-null; one-side-null is bulk delete.
- **`Disruptor.Surreal.SurrealTransaction`** — the SDK's transaction handle. The library never owns one; the app calls `db.BeginTransactionAsync()`, passes the handle into Save/Delete/Relate, and calls `tx.CommitAsync()` (or `tx.CancelAsync()`) when its logical unit of work is done. Native `SurrealConflictException` surfaces at COMMIT for concurrent writers.
- **`IRelationKind`** — runtime interface with `static abstract string EdgeName { get; }`. Implemented by every emitted forward-relation marker class (`Restricts`, `References`, …). The static abstract gives `Session.RelateAsync<TKind>` etc. compile-time access to the edge name without instance construction.
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
- **Entity reads are sync; writes split into sync (in-memory) + async (dispatch).** Sync property setters write directly to backing fields. Async dispatch happens through `session.SaveAsync` / `DeleteAsync` / `RelateAsync` / `UnrelateAsync` against an app-owned `SurrealTransaction`.
- `[Property]` / `[Parent]` / `[Reference]` / `[Children]` are **property-only attributes** — Roslyn rejects them on methods.
- `[Property]` — scalar field. Declare as `partial T Name { get; set; }`; generator emits a pure backing-field property: `get => _name; set => _name = value;`. Get-only is also legal. For inline `array<object>` columns, declare as `partial IReadOnlyList<TElem> Items { get; }` (get-only); generator emits a `List<TElem>` backing + `AddItem` / `RemoveItem` / `ClearItems` helpers, walks `TElem`'s public scalar properties at codegen time to emit typed Hydrate / Save (no reflection). `IList<TElem>` and `List<TElem>` are also accepted shapes — same backing, helpers still emitted.
- `[Reference]` — pointer to another `[Table]`. Declare as `partial T Name { get; }` (mandatory, non-nullable; generator emits the `OnCreate{Name}` hook + `Initialize` entry that mints the target via `new T()` and assigns it directly into the backing field), or `partial T? Name { get; set; }` (optional, with pure backing-field setter). Generator emits two backing fields per reference: `_{name}` for the entity ref cache and `_{name}Id` for the record id; the getter falls back to `Session.Get<T>(_{name}Id)` when only the id is cached (covers "loaded as id only" + "user later loaded the other aggregate separately").
- `[Parent]` — pointer to the parent in a hierarchical relationship. Declare as `partial T Name { get; set; }`. Same dual-backing-field shape as `[Reference]`. Setter additionally calls `parent.Session.AdoptIfUnbound(this)` so a freshly constructed `new Constraint { Design = design }` joins design's session and shows up in `design.Constraints` at Save time.
- `[Children]` — sync collection from the parent side, computed via reverse-fk traversal. Declare as `partial IReadOnlyCollection<T> Name { get; }`. No Add/Remove (children are managed via `child.Parent = parent` on the child side).
- **Forward/inverse relations** — declare an attribute pair like `RestrictsAttribute : ForwardRelation` + `RestrictedByAttribute : InverseRelation<RestrictsAttribute>`. The generator emits a sibling `Restricts : IRelationKind` marker class. **Within-aggregate** read collections: declare as `IReadOnlyCollection<IEntity>`. **Cross-aggregate**: declare as `IReadOnlyCollection<IRecordId>`. Mutations go through `Session.Relate<TKind>(src, tgt)` (sync, in-memory) and dispatch via `SaveAsync`'s snapshot diff, or directly via `Session.RelateAsync<TKind>(src, tgt, tx)`.
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
    constraint.Restricts(someUserStory);   // sync user passthrough — Session.Relate<Restricts>(this, x)

    // Per-entity Save: walks forward refs (Details), Tracked children (Constraints, Epics, …),
    // and new outgoing relations (Restricts) — single call dispatches the whole subgraph.
    await session.SaveAsync(design, tx);
    await tx.CommitAsync();
}
catch (Disruptor.Surreal.SurrealConflictException)
{
    // Another writer's commit landed first. Reload + retry. tx auto-cancels on dispose.
}
```

The Sample project's classes (`Design`, `Constraint`, `Epic`, `Feature`, `UserStory`, `AcceptanceCriteria`, `Test`, `Review`, `Finding`, `Observation`, `Issue`, `DesignChange`, `Details`) are the canonical worked examples — read them alongside the generated `.g.cs` outputs (especially `{Namespace}.{CompositionRoot}.Schema.g.cs`) to see the input/output mapping. The DDL is no longer hand-maintained: `SchemaEmitter` walks the model graph and emits the chunks behind a static `Schema` accessor on the user's `[CompositionRoot]` partial — the emitted `Workspace.ApplySchemaAsync(db)` is the canonical boot path. The same partial also exposes `Workspace.ReferenceRegistry`, which the emitted `Load*Async` methods pass into `SurrealSession`'s ctor.
