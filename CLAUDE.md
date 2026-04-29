# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

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
- `src/Disruptor.Surface.Runtime` (`net10.0`) — the library half: every type generated code targets (`SurrealSession`, `IEntity`, `IRelationKind`, `RecordId`, `SurrealArray`, `WriterLease`, `CommitPlanner`, `PendingState`, `IReferenceRegistry`, `ReferenceFieldInfo`, `HydrationJson`, the Surreal HTTP transport). Namespace `Disruptor.Surface.Runtime`. Single package dep: `Ulid`. Consumers add a `ProjectReference` (or `PackageReference` once published).
- `src/Disruptor.Surface.Sample` (`net10.0`) — the test bed: the schema modeled in `[Table]` classes, the `[CompositionRoot]`-tagged `Workspace` partial, and a console-app harness in `Program.cs`. `ProjectReference` to `Disruptor.Surface.Runtime`, plus `OutputItemType="Analyzer"` on the generator so it picks up `[Table]`-driven emission without taking a runtime dependency on the generator assembly.

## Generator pipeline (read this before touching `Disruptor.Surface.Generator`)

`ModelGenerator.Initialize` wires four `ForAttributeWithMetadataName` providers (tables, forward kinds, inverse kinds, the user's `[CompositionRoot]`) into a single `ModelGraph`. The data flow:

1. **Attribute discovery** — the user-facing attributes (`[Table]`, `[AggregateRoot]`, `[CompositionRoot]`, `[Id]`, `[Property]`, `[Parent]`, `[Children]`, `[Reference]`, `[Inline]`, the four reference-delete behaviors `[Reject]` / `[Unset]` / `[Cascade]` / `[Ignore]`, the relation bases `RelationAttribute` / `ForwardRelation` / `InverseRelation<TForward>`, and `[RecordIdValue<T>]`) live as ordinary `.cs` files in `src/Disruptor.Surface.Runtime/Annotations/`, namespace `Disruptor.Surface.Annotations`. The generator binds to them by metadata name through `ForAttributeWithMetadataName`; the FQN constants in `AnnotationsMetadata` must stay in lockstep with the runtime declarations. (Earlier versions injected the attributes into every consuming compilation via `RegisterPostInitializationOutput`; that's gone — the runtime is a hard dependency for every consumer anyway, so the attributes belong there too with proper XmlDoc and F12-into-source.)
2. **Per-symbol extractors** (`Pipeline/`) lower Roslyn symbols into pure-data records under `Model/`. They cannot resolve cross-table references yet — `TypeRef.IsTableType` is seeded from the immediately visible attributes and patched up later.
3. **Linking** (`RelationLinker.Build`) takes the collected tables + relation kinds + composition roots and (a) rewrites every `TypeRef` so `IsTableType` is true wherever the underlying type was discovered to be a `[Table]`, (b) computes per-relation-kind `RelationUnion` sets — for each forward kind, the source set (forward attribute holders) and target set (inverse attribute holders), each becoming a marker interface when ≥2 members, (c) computes per-aggregate `AggregateModel` membership by walking `[Children]` from each `[AggregateRoot]` and detects entities reachable from 2+ roots as conflict descriptors, (d) detects cascade-only reference cycles for CG014.
4. **Emit** (`Emit/`) — eight emitters fire per generation:
   - `IdEmitter` — per-table `{Name}Id` `readonly record struct` with id-side union interfaces in its base list.
   - `UnionInterfaceEmitter` — per multi-member union, BOTH the entity-side marker (`IRestrictedBy`) AND the id-side marker (`IRestrictedById`).
   - `CompositionRootEmitter` — emits a partial declaration of the user's `[CompositionRoot]`-tagged class with one instance `Load{Root}Async(transport, rootId, ct)` per `[AggregateRoot]`. No ctor, no fields, no base — the user owns construction entirely. Skipped when no `[CompositionRoot]` exists in the compilation.
   - `RelationKindEmitter` — per forward relation attribute (e.g. `RestrictsAttribute`), emits a sibling marker class without the `Attribute` suffix (`Restricts : IRelationKind`) carrying the SurrealDB edge name as a static property. The class is the type witness used by `SurrealSession.Relate<Restricts>(src, tgt)` and friends. Inverse kinds get no marker — the edge is named after the forward.
   - `AggregateLoaderEmitter` — per `[AggregateRoot]`, an internal `{Root}AggregateLoader` static class with `PopulateAsync` that issues a single nested-`SELECT` query: root row with `*` plus `field.*` inline expansion for each `[Reference]`, then per-non-root-member subselects scoped via dotted parent paths back to the root (`WHERE feature.epic.design = $parent.id`), then per-relation-kind edge subselects (within-aggregate + cross-aggregate target-side). Hydration is delegated to the per-entity `IEntity.Hydrate` which also re-hydrates inline-expanded references.
   - `PartialEmitter` — partial implementations of every annotated property/method, plus the per-entity session-binding plumbing (`_session` field, `_pendingWrites` buffer, explicit `IEntity.Bind` / `IEntity.Flush` / `IEntity.Session`, `__WriteField` / `__ClearField` setter helpers, protected `Session` accessor that throws when unbound), plus `IEntity.Initialize` (mandatory-ref seeding via `session.Track(new T())`), `IEntity.Hydrate` (loader-driven row-to-entity population), and `IEntity.OnDeleting` dispatch. Relation collection property reads emit `Session.QueryRelated<TKind, TElement>(this)` / `QueryRelatedIds<TKind>(this)` / `QueryInverseRelatedIds<TKind>(this)`. **No** auto-emitted `Add{X}` / `Remove{X}` / `Clear{X}` — the typed `Session.Relate<TKind>(...)` is the canonical mutation surface, users write a one-line passthrough if they want a domain verb.
   - `ReferenceRegistryEmitter` — sealed `IReferenceRegistry` impl in the consumer (`GeneratedReferenceRegistry`) PLUS a partial fragment of the user's `[CompositionRoot]` exposing the singleton via `public static IReferenceRegistry ReferenceRegistry`. No `[ModuleInitializer]`, no static facade — model-scoped so multiple Disruptor.Surface-generated assemblies can coexist in one process.
   - `SchemaEmitter` — emits the chunked DDL via `{CompositionRoot}.Schema` (a partial fragment) backed by an internal `Disruptor.SurfaceSchema._chunks` array in the same namespace. `IReadOnlyList<string>` of DDL chunks: writer_lease + entity tables block + one chunk per `[Table]`'s fields + one chunk per relation kind. Idempotent via `DEFINE … IF NOT EXISTS`. Apply at boot via `foreach (var chunk in Workspace.Schema) await transport.ExecuteAsync(chunk);`. Skipped when no `[CompositionRoot]` exists.
   - Diagnostics — CG001 through CG019 reported from the linker output.

### Equatability is the contract

Every record under `Model/` is fed to `IncrementalGenerator` providers. **All collections must be `EquatableArray<T>`, never `ImmutableArray<T>` or `List<T>`**, because Roslyn deduplicates pipeline outputs by record equality and the BCL collection types compare by reference. Adding a mutable field, a lazy cache, or a non-equatable collection silently regresses incremental builds. See `ModelGraph`'s `<remarks>` for the canonical statement of this rule.

### User code cannot reference generator-emitted types

When the user writes `partial IReadOnlyCollection<IRestrictedBy> Foo { get; }`, Roslyn tries to resolve `IRestrictedBy` *during the same analysis pass* in which the generator would emit it. The interface doesn't exist yet from Roslyn's POV → it captures an error type, the generated impl's `FullyQualifiedName` doesn't match the user's declaration, and you get `CS9255 (partial member declarations must have the same type)` plus `CS0246 (type not found)` in the generated `.g.cs`. **Generator-emitted code referencing other generator-emitted types is fine** — both files compile in the same later pass — so the workaround is to keep the user's signature pointed at types that already exist (`IEntity` for entity collections, `IRecordId` for id collections) and only use the generated interface inside emitted code. **The same caveat applies to typed relation kinds:** declare your domain-verb passthrough with `IRestrictedBy` (the user-side base interface that already exists), not with the emitted `Restricts` marker class as a generic argument inside a `partial` member's declared type — but in expression position (as `Session.Relate<Restricts>(...)`) the marker resolves fine because that resolution happens at body-compile time, after generator emit.

### Emit conventions

- `IdEmitter` emits each `{Name}Id` as a `readonly record struct {Name}Id(string Value)`. The `Value` initializer routes through `RecordIdFormat.Validate`, which only accepts two forms: a 26-char Ulid stringification (what `New()` mints) or a short lower_snake_case slug (max 32 chars, opt-in for stable-named records like config singletons). Anything else throws `FormatException` at construction. Quoted-string ids are explicitly unsupported.
- `PartialEmitter.SessionType`, `EntityInterface`, `SurrealArrayMetadata` (and the matching constants in the other emitters) pin the target namespace `Disruptor.Surface.Runtime`. If the runtime is renamed or split, every emitter that bakes a `global::Disruptor.Surface.Runtime.*` literal must change in lockstep.
- `RelationKindEmitter` strips the `Attribute` suffix to name the marker class. `RestrictsAttribute` (the user's attribute, used as `[Restricts]`) and `Restricts` (the marker, used as `Session.Relate<Restricts>(...)`) coexist in the same namespace because attribute-position resolution looks for `*Attribute` first and type-position resolution looks for the bare name.
- `ReferenceRegistryEmitter` keeps the impl class internal to the consumer assembly and exposes the singleton via a partial fragment on `[CompositionRoot]`. Same pattern works for any per-model metadata — emit the impl as an internal class, attach a static accessor to the user's partial. Anything new the runtime needs at commit time gets passed into `SurrealSession`'s ctor; nothing reaches into a process-global facade any more.
- **The generator does not look at user methods at all.** Every model annotation (`[Property]`, `[Parent]`, `[Reference]`, `[Children]`, `RelationAttribute`-derived) is `AttributeTargets.Property` only — methods cannot carry them. The `Session` DSL handed to each entity is the entire library contract; domain methods are plain user code calling that DSL. If a user wants a domain verb, they write a one-liner: `public void Restricts(IRestrictedBy x) => Session.Relate<Restricts>(this, x);`.
- `SurrealNaming` (wraps Humanizer) handles `ToFieldName` / `ToTableName` / `ToEdgeName` / `Singularize` / `Pluralize` / `StripAttributeSuffix`. Table names are pluralised + snake-cased at codegen time; field/edge names are snake-cased; relation source-interface names are singularised forward-attribute names (`Restricts` → `IRestrict`).

### Diagnostics

`Pipeline/Diagnostics.cs` defines the `CG001`–`CG019` descriptors. When adding a new validation, add the descriptor here and report it from `ModelGenerator.Emit` (or the appropriate extractor). `CG002` and `CG016` are intentionally unused. Selected highlights: CG001 (`[Table]` not partial), CG011 (entity reachable from multiple aggregate roots), CG014 (cascade-only reference cycle), CG018 (multiple `[CompositionRoot]` classes), CG019 (`[CompositionRoot]` class not partial).

## Runtime model (Disruptor.Surface.Runtime)

The generated partials are not standalone — they call into a small runtime that consumers must wire up:

- **`IEntity`** — every `[Table]` class implements this implicitly via the emitted partial. Five session-side hooks (all explicit-interface impls, so they don't pollute the user's type):
  - `RecordId Id` — canonical id.
  - `SurrealSession? Session` — null until the entity is bound.
  - `Bind(SurrealSession session)` — one-shot setter for the entity's `_session` field.
  - `Initialize(SurrealSession session)` — seeds mandatory `[Reference]` targets via the user's `OnCreate*` hooks.
  - `Flush(SurrealSession session)` — drains buffered object-initializer writes into the session's pending state.
  - `Hydrate(JsonElement json, SurrealSession session)` — loader-driven row-to-entity population, also re-hydrates inline-expanded references via `HydrationJson.HydrateReference<T>`.
  - `OnDeleting()` — fires before the entity's own DELETE so user cleanup can queue child deletes / clears.
- **`SurrealSession`** — single concrete class. Snapshot-isolated entity store + dirty batch. **No ambient context** — entities hold their session via `_session`; `Session.Current` and `AsAmbient` no longer exist. Reads are sync (`GetParent`, `GetReference` / `GetReferenceOrDefault`, `Get<T>(id)`, `QueryChildren`, `QueryRelated<TKind, TElement>` for within-aggregate, `QueryRelatedIds<TKind>` / `QueryInverseRelatedIds<TKind>` for cross-aggregate). Writes are a uniform surface: `Track`, `Delete`, `SetField` / `UnsetField`, and the typed-kind primitives `Relate<TKind> / Unrelate<TKind> / UnrelateAllFrom<TKind> / UnrelateAllTo<TKind>` (with both `IRecordId` and `IEntity` overloads). String-keyed `Relate(src, tgt, edgeKind)` overloads remain underneath as the implementation core. **`Track` lifecycle**: `entities.TryAdd` → `entity.Bind(this)` → `Record(Command.Create)` → `entity.Initialize(this)` → `entity.Flush(this)`. `SetField` cascades into `Track` for any `IEntity` value, so nested object initialisers (`new Design { Details = new Details { ... } }`) auto-track without explicit Track on every fresh ref. **No `Create<T>` method** — explicit `session.Track(new T())` is the contract.
- **`IRelationKind`** — runtime interface with `static abstract string EdgeName { get; }`. Implemented by every emitted forward-relation marker class (`Restricts`, `References`, …). The static abstract gives `Session.Relate<TKind>` etc. compile-time access to the edge name without instance construction.
- **User's `[CompositionRoot]` partial** — the user declares a partial class tagged with `[CompositionRoot]` (e.g. `public partial class Workspace`); the generator emits one `Load{Root}Async(ISurrealTransport transport, {Root}Id rootId, CancellationToken ct = default)` instance method per `[AggregateRoot]`. No ctor, fields, or base class are emitted — the user owns construction (transport plumbing, caches, telemetry, …) entirely. Library promise: minimal intrusion. Hydration is delegated to a generator-emitted `{Root}AggregateLoader.PopulateAsync` static class which issues the single nested SurrealQL query. Callers who plan to commit acquire a `WriterLease` separately and pass it into `SurrealSession.CommitAsync`.
- **`RecordId` / `IRecordId`** — canonical `(Table, Value)` pair. Every generated `{Name}Id` implicitly converts to `RecordId` so session internals key off one struct type while the user-facing API stays strongly typed. `{Name}Id` also implements every id-side union marker (`IRestrictedById`, `IReferencedById`, …) it's a member of.
- **`SurrealArray<T>`** — mutable ordered collection that backs SurrealDB's inline `array<object>` columns. Implements `IList<T>` plus `Move(from, to)`. Mutations call `owner.Session?.SetField(...)` — silent no-op when the owner is unbound (the underlying list still updates; `IEntity.Flush` queues the final state when the owner is tracked). Generator emits a lazy-cached wrapper for any `[Property]` whose declared type is `SurrealArray<T>`; the loader pre-populates the wrapper from the JSON `array<object>` payload via `JsonSerializer.Deserialize` with `SurrealJson.SerializerOptions` (snake_case).
- **`HydrationJson`** — JSON helpers used by the emitted `IEntity.Hydrate`. `HydrateReference<T>(parent, field, ownerId, session)` is the key one — when the loader's projection returns a `field.*` inline-record (rather than just an id), it constructs `new T()` and runs its `Hydrate` so reads can resolve the linked entity.
- **`WriterLease`** — optimistic-concurrency CAS-on-sequence. **Lives outside the session** — callers acquire it independently and pass into `SurrealSession.CommitAsync`. One `writer_lease:<slug>` record per aggregate, holding a single monotonic `seq: int`. `AcquireAsync` reads the current seq (defaults to 0 if no row exists) and captures it on the lease. At commit time the session splices a `BEGIN TRANSACTION; IF current_seq != captured THEN THROW … END; UPSERT seq+1;` fragment around the data writes — atomic with the data, throws `WriterLeaseStolenException` on CAS mismatch. No TTL, no holder id, no clock skew, no theft-recovery timer. `DisposeAsync` is a no-op. Crashed writers are forgotten on the spot — the next acquirer reads the current seq fresh and proceeds. `AcquireAsync` validates the aggregate name as a slug via `RecordIdFormat`. Suits the library's one-shot session character: load → mutate → commit happens fast enough that races are rare and retries are cheap.
- **`SurrealHttpClient`** — implements `ISurrealTransport`. Knows how to sign in, prepend `LET $...;` parameter bindings, retry on transactional conflicts, and normalise SurrealDB's `{status, result}` envelope. `SurrealConfig.FromConnectionString` parses an ADO-style key/value string.

## Authoring conventions for `[Table]` consumers

When working in `Disruptor.Surface.Sample/Models` (or any consumer):

- A `[Table]` class **must** be `partial` (CG001) and declare exactly **one** `[Id]` partial property (CG007/CG008). The id type is the generated `{Name}Id` struct.
- Exactly one class **may** be tagged `[CompositionRoot]` and **must** be `partial` (CG018/CG019). The generator grafts `Load{Root}Async` instance methods onto it; you own the ctor, fields, transport wiring, etc. Without one, the load methods aren't emitted; you can still call `{Root}AggregateLoader.PopulateAsync` directly if you really want.
- `[AggregateRoot]` on the root entity of an aggregate (e.g. `Design`, `Review`). Membership is computed by walking `[Children]` from the root; entities reachable from 2+ roots produce CG011. `[Reference]` targets (like `Details`) are NOT considered owned — they're loaded with the aggregate via inline `field.*` expansion but live outside any specific one.
- **Reads are sync properties; writes are sync property setters or sync `void` methods.** No member inside the model surface returns `Task`. The only async members are the user's `Workspace.Load{Root}Async` and `SurrealSession.CommitAsync` (boundary).
- `[Property]` / `[Parent]` / `[Reference]` / `[Children]` are **property-only attributes** — Roslyn rejects them on methods.
- `[Property]` — scalar field. Declare as `partial T Name { get; set; }`; generator emits a backing field, expression-body getter, and a setter that writes the field plus calls `__WriteField("name", value)` (which buffers when unbound, calls `Session.SetField` when bound). Get-only is also legal. For inline `array<object>` columns, declare as `partial SurrealArray<T> Name { get; }` (get-only — the wrapper itself is mutable).
- `[Reference]` — pointer to another `[Table]`. Declare as `partial T Name { get; }` (mandatory, non-nullable; generator emits the `OnCreate{Name}` hook + `Initialize` entry that mints the target via `new T()` and calls `session.SetField(..., FieldKind.Reference)`, which cascades into `Track`), or `partial T? Name { get; set; }` (optional, with setter that branches on null). The `OnCreate{Name}` hook is a simple-form `partial void` — implement only if you want custom seed logic.
- `[Parent]` — pointer to the parent in a hierarchical relationship. Declare as `partial T Name { get; set; }`; generator emits getter (`Session.GetParent`) and setter (`__WriteField(..., FieldKind.Parent)`).
- `[Children]` — sync collection from the parent side, computed via reverse-fk traversal. Declare as `partial IReadOnlyCollection<T> Name { get; }`. No Add/Remove (children are managed via `child.Parent = parent`).
- **Forward/inverse relations** — declare an attribute pair like `RestrictsAttribute : ForwardRelation` + `RestrictedByAttribute : InverseRelation<RestrictsAttribute>`. The generator emits a sibling `Restricts : IRelationKind` marker class in the same namespace. **Within-aggregate** relations: declare the read collection as `IReadOnlyCollection<IEntity>`. **Cross-aggregate**: declare as `IReadOnlyCollection<IRecordId>`. Mutations go through `Session.Relate<TKind>(src, tgt)` etc. — typed by kind, with `IRecordId` / `IEntity` overloads. **No auto-emitted `Add{X}` / `Remove{X}` / `Clear{X}`**: write a one-line domain-verb passthrough yourself if you want one (`public void Restricts(IRestrictedBy x) => Session.Relate<Restricts>(this, x);`).
- **Union interfaces** — emitted automatically per relation kind whose target/source set has 2+ members. Naming: target side uses the inverse attribute name (`I{InverseName}` → `IRestrictedBy`, `IReferencedBy`, …); source side uses the singularised forward attribute name (`I{Singularize(ForwardName)}` → `IRestrict`, …). Each entity union has a parallel id-side union (`I{InverseName}Id` → `IRestrictedById`) implemented by every `{Name}Id` struct in the union. Generator adds them to the entity / id base list automatically — never reference them in user-side declarations of partial members.
- The `{Name}Id.Value` is a `string`, validated to be either a Ulid stringification (auto-minted by `New()`) or a short lower_snake_case slug (≤32 chars). Quoted-string ids are explicitly unsupported. Use the slug form sparingly — for stable-named records (singletons, config rows). Anything else should be a Ulid.

## End-to-end usage shape

```csharp
// User's [CompositionRoot] partial — generator grafts Load*Async onto it; ctor is yours.
[CompositionRoot]
public partial class Workspace { }

// Read session — no lease, never commit.
var workspace = new Workspace();
var session = await workspace.LoadDesignAsync(transport, designId);
var design = session.Get<Design>(designId)!;
foreach (var c in design.Constraints)
    Console.WriteLine(c.Description);

// Write session — caller composes lease + load + commit themselves.
try
{
    await using var lease = await WriterLease.AcquireAsync(transport, "design");
    var session = await workspace.LoadDesignAsync(transport, designId);

    var design = session.Get<Design>(designId)!;
    design.Description = "edited";

    var constraint = session.Track(new Constraint { Design = design, Description = "no negatives" });
    constraint.Restricts(someUserStory);   // user's one-line passthrough → Session.Relate<Restricts>(this, x)

    await session.CommitAsync(transport, lease);   // renews lease before flush
}
catch (WriterLeaseStolenException ex) { /* another writer beat us — abandon, reload, retry */ }
```

The Sample project's classes (`Design`, `Constraint`, `Epic`, `Feature`, `UserStory`, `AcceptanceCriteria`, `Test`, `Review`, `Finding`, `Observation`, `Issue`, `DesignChange`, `Details`) are the canonical worked examples — read them alongside the generated `.g.cs` outputs (especially `{Namespace}.{CompositionRoot}.Schema.g.cs`) to see the input/output mapping. The DDL is no longer hand-maintained: `SchemaEmitter` walks the model graph and emits the chunks behind a static `Schema` accessor on the user's `[CompositionRoot]` partial — apply with `foreach (var chunk in Workspace.Schema) await transport.ExecuteAsync(chunk);`. The same partial also exposes `Workspace.ReferenceRegistry`, which the emitted `Load*Async` methods pass into `SurrealSession`'s ctor.
