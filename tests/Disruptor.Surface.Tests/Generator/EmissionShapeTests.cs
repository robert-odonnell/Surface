using Microsoft.CodeAnalysis;
using Xunit;

namespace Disruptor.Surface.Tests.Generator;

/// <summary>
/// Shape assertions on what the generator emits for representative inputs. Not full
/// snapshot matching — just enough to pin down the contracts that consumer code depends
/// on (typed id structs, IRelationKind markers, [CompositionRoot] load methods).
/// </summary>
public sealed class EmissionShapeTests
{
    private const string MinimalModel = """
        using Disruptor.Surface.Annotations;
        using Disruptor.Surface.Runtime;
        using System.Collections.Generic;
        namespace M;

        public sealed class RestrictsAttribute : ForwardRelation;
        public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;

        [Table, AggregateRoot] public partial class Design {
            [Id] public partial DesignId Id { get; set; }
            [Property] public partial string Description { get; set; }
            [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
            [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }
        }

        [Table] public partial class Constraint {
            [Id] public partial ConstraintId Id { get; set; }
            [Parent] public partial Design Design { get; set; }
            [Property] public partial string Description { get; set; }
            [Restricts] public partial IReadOnlyCollection<IEntity> Restrictions { get; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    [Fact]
    public void Emits_TypedIdStruct_PerTable()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        Assert.Contains("readonly record struct DesignId", allSrc);
        Assert.Contains("readonly record struct ConstraintId", allSrc);
        // Each typed id implements IRecordId.
        Assert.Contains(": global::Disruptor.Surface.Runtime.IRecordId", allSrc);
    }

    [Fact]
    public void Emits_RelationKindMarker_PerForwardKind_WithEdgeName()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // Marker class without the Attribute suffix, implementing IRelationKind, with
        // the snake-cased edge name as a static property.
        Assert.Contains("public sealed class Restricts : global::Disruptor.Surface.Runtime.IRelationKind", allSrc);
        Assert.Contains("public static string EdgeName => \"restricts\";", allSrc);
        // No marker for the inverse — the edge is named after the forward.
        Assert.DoesNotContain("public sealed class RestrictedBy : global::Disruptor.Surface.Runtime.IRelationKind", allSrc);
    }

    [Fact]
    public void IdSetter_Throws_When_EntityIsBoundToSession()
    {
        // [Id] is the user's optional public-facing accessor — we own its body, so the
        // setter (when declared) refuses to overwrite the anchor once the entity is bound:
        // the session's identity map is keyed on the current id, so silently mutating it
        // after bind would corrupt every dict. Pre-bind sets are fine; that's the
        // `new Design { Id = knownId }` pattern.
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        Assert.Contains("if (_session is not null)", allSrc);
        Assert.Contains("Cannot mutate Id after the entity is bound to a session.", allSrc);
    }

    [Fact]
    public void Emits_CompositionRoot_LoadAsync_PerAggregateRoot()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var compositionRootFile = GeneratorHarness.FindGeneratedFile(result, "CompositionRoot.g.cs");
        Assert.NotNull(compositionRootFile);

        var src = compositionRootFile.ToString();
        Assert.Contains("public partial class Workspace", src);
        Assert.Contains("LoadDesignAsync", src);
        // Two overloads: one for read-only via SurrealClient db, one for write-mode via SurrealTransaction tx.
        Assert.Contains("global::Disruptor.Surreal.SurrealClient db", src);
        Assert.Contains("global::Disruptor.Surreal.SurrealTransaction tx", src);
        Assert.Contains("global::M.DesignId rootId", src);
        // No ctor, no fields — minimal-intrusion contract.
        Assert.DoesNotContain("public Workspace(", src);
    }

    [Fact]
    public void Loader_InlineExpands_OnlyInlineMarkedReferences()
    {
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Root {
                [Id] public partial RootId Id { get; set; }
                [Reference, Inline] public partial Owned? Owned { get; set; }
                [Reference]         public partial Foreign? Foreign { get; set; }
            }

            [Table] public partial class Owned {
                [Id] public partial OwnedId Id { get; set; }
                [Property] public partial string Header { get; set; }
            }

            [Table] public partial class Foreign {
                [Id] public partial ForeignId Id { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, _, _) = GeneratorHarness.Run(src);
        var loader = GeneratorHarness.FindGeneratedFile(result, "RootAggregateLoader");
        Assert.NotNull(loader);

        var loaderSrc = loader.ToString();

        // [Inline]-marked Owned gets `owned.*` projection in the loader query.
        Assert.Contains("owned.*", loaderSrc);
        // Plain [Reference] Foreign does NOT — caller resolves separately.
        Assert.DoesNotContain("foreign.*", loaderSrc);
    }

    [Fact]
    public void Loader_EmitsEdgeSubselect_ForMultiSourceRelationKind()
    {
        // Bug regression: BuildEdgeWhere previously delegated to a FindSingleSourceTable
        // helper that returned null on the second hit, which meant relation kinds with
        // 2+ source tables (multi-source unions) silently lost their edge subselect in
        // the loader and reads came back empty after reload. Fix enumerates ALL source
        // tables in this aggregate and OR's their path equalities together.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;
            public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
                [Children] public partial IReadOnlyCollection<Rule> Rules { get; }
                [Children] public partial IReadOnlyCollection<UserStory> UserStories { get; }
            }

            // Two source tables for the [Restricts] kind — Constraint AND Rule.
            // The generator emits an IRestrict source-side union for them; the loader
            // must include the edge subselect with both source paths OR'd together.
            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
                [Parent] public partial Design Design { get; set; }
                [Restricts] public partial IReadOnlyCollection<UserStory> Restrictions { get; }
            }

            [Table] public partial class Rule {
                [Id] public partial RuleId Id { get; set; }
                [Parent] public partial Design Design { get; set; }
                [Restricts] public partial IReadOnlyCollection<UserStory> Restrictions { get; }
            }

            [Table] public partial class UserStory {
                [Id] public partial UserStoryId Id { get; set; }
                [Parent] public partial Design Design { get; set; }
                [RestrictedBy] public partial IReadOnlyCollection<global::Disruptor.Surface.Runtime.IEntity> Restrictions { get; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, _, _) = GeneratorHarness.Run(src);
        var loader = GeneratorHarness.FindGeneratedFile(result, "DesignAggregateLoader");
        Assert.NotNull(loader);

        var loaderSrc = loader.ToString();
        // The edge subselect must reference _restricts AND OR over both source paths
        // (constraints' and rules' parent paths back to design — both empty paths here
        // since both are direct children of Design root).
        Assert.Contains("_restricts", loaderSrc);
        Assert.Contains("HydrateEdges(rootRow, \"_restricts\"", loaderSrc);
    }

    [Fact]
    public void EmptyTable_GetsIEntityScaffolding_NoMembers()
    {
        // Lenient stance: a [Table] with no partial annotated members is still
        // legal — the id anchor is unconditional, so the entity has IEntity hooks
        // (Bind/Initialize/Hydrate/Flush/OnDeleting) and a Track-able identity.
        // The previous behavior silently emitted nothing, leaving the type without
        // IEntity at runtime.
        var src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class Marker { }
            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, runDiags, _) = GeneratorHarness.Run(src);
        var partial = GeneratorHarness.FindGeneratedFile(result, "M.Marker.g.cs");
        Assert.NotNull(partial);

        var partialSrc = partial.ToString();
        Assert.Contains("partial class Marker", partialSrc);
        Assert.Contains("global::Disruptor.Surface.Runtime.IEntity", partialSrc);
        Assert.Contains(".Bind(", partialSrc);
        Assert.Contains(".Hydrate(", partialSrc);
        Assert.Empty(runDiags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void Emits_AggregateLoader_PerRoot()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var loader = GeneratorHarness.FindGeneratedFile(result, "DesignAggregateLoader");
        Assert.NotNull(loader);

        var src = loader.ToString();
        Assert.Contains("internal static class DesignAggregateLoader", src);
        Assert.Contains("public static async Task PopulateAsync", src);
    }

    [Fact]
    public void CompositionRoot_HostsModelMetadata_AsStaticMembers()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // Schema chunks live behind a static accessor on the user's [CompositionRoot].
        Assert.Contains("public static System.Collections.Generic.IReadOnlyList<string> Schema", allSrc);
        // Reference registry exposed the same way; the impl class is internal.
        Assert.Contains("public static global::Disruptor.Surface.Runtime.IReferenceRegistry ReferenceRegistry", allSrc);
        Assert.Contains("internal sealed class GeneratedReferenceRegistry", allSrc);

        // No more global static facade or [ModuleInitializer] hookup.
        Assert.DoesNotContain("ModuleInitializer", allSrc);
        Assert.DoesNotContain("Disruptor.Surface.Runtime.ReferenceRegistry.Register", allSrc);

        // Load*Async passes the local registry into the new SurrealSession ctor.
        Assert.Contains("new global::Disruptor.Surface.Runtime.SurrealSession(ReferenceRegistry)", allSrc);

        // Convenience ApplySchemaAsync sits next to Schema for the common boot path.
        Assert.Contains("public static async global::System.Threading.Tasks.Task ApplySchemaAsync", allSrc);
    }

    [Fact]
    public void NoCompositionRoot_SuppressesLoadMethodEmission()
    {
        var src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [Table, AggregateRoot] public partial class Solo {
                [Id] public partial SoloId Id { get; set; }
            }
            // No [CompositionRoot] declared.
            """;
        var (result, _, _, _) = GeneratorHarness.Run(src);
        var compositionRootFile = GeneratorHarness.FindGeneratedFile(result, "CompositionRoot.g.cs");
        Assert.Null(compositionRootFile);
        // The aggregate loader is still emitted — advanced users can call it directly.
        Assert.NotNull(GeneratorHarness.FindGeneratedFile(result, "SoloAggregateLoader"));
    }

    [Fact]
    public void EntityPartial_BindsSession_ViaIEntity_InsteadOfAmbient()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // The ambient `Session.Current` shape is gone; entities hold their own session
        // through IEntity.Bind / explicit-impl Session. Bind is one-shot — re-binding to
        // a different session throws.
        Assert.Contains("void global::Disruptor.Surface.Runtime.IEntity.Bind(global::Disruptor.Surface.Runtime.SurrealSession session)", allSrc);
        Assert.Contains("Entity is already bound to a different session.", allSrc);
        Assert.Contains("private global::Disruptor.Surface.Runtime.SurrealSession? _session;", allSrc);
        Assert.DoesNotContain(".Current", allSrc);
    }

    [Fact]
    public void RelationCollectionRead_UsesTypedKind_AndDirectionalMethod()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // Constraint.Restrictions is the FORWARD side of restricts → QueryOutgoing.
        Assert.Contains("Session.QueryOutgoing<global::M.Restricts, global::Disruptor.Surface.Runtime.IEntity>(this)", allSrc);
        // Design.Restrictions is the INVERSE side of restricts → QueryIncoming.
        Assert.Contains("Session.QueryIncoming<global::M.Restricts, global::M.Constraint>(this)", allSrc);
    }

    [Fact]
    public void NoAutoEmittedAddRemoveClear_OnRelationCollections()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // The protected default mutators were dropped in favour of typed Session.Relate<TKind>.
        Assert.DoesNotContain("protected void AddRestriction", allSrc);
        Assert.DoesNotContain("protected void RemoveRestriction", allSrc);
        Assert.DoesNotContain("protected void ClearRestrictions", allSrc);
    }

    [Fact]
    public void Emits_TraversalBuilder_PerTable_WithIncludeMethodsAndExtensions()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var designBuilder = GeneratorHarness.FindGeneratedFile(result, "DesignTraversalBuilder");
        Assert.NotNull(designBuilder);

        var src = designBuilder.ToString();

        // Builder type is sealed and exposes Where + an Include per [Children].
        Assert.Contains("public sealed class DesignTraversalBuilder", src);
        Assert.Contains("public DesignTraversalBuilder Where(", src);
        Assert.Contains("public DesignTraversalBuilder IncludeConstraints(", src);
        Assert.Contains("global::M.ConstraintTraversalBuilder", src);

        // Generates the AST node with the parent-link field name resolved at codegen time
        // (not via runtime reflection).
        Assert.Contains("new global::Disruptor.Surface.Runtime.Query.IncludeChildrenNode(\"constraints\", \"design\"", src);

        // Sibling extension class anchors on Query<Design> with the same surface so
        // root-level and nested-level Include calls read identically at the call site.
        Assert.Contains("public static class DesignQueryIncludes", src);
        Assert.Contains("this global::Disruptor.Surface.Runtime.Query.SurfaceQuery<global::M.Design> query", src);
        Assert.Contains("query.WithInclude(new global::Disruptor.Surface.Runtime.Query.IncludeChildrenNode(\"constraints\", \"design\"", src);
    }

    [Fact]
    public void TraversalBuilder_Skips_PlainReference_KeepsInlineReferenceAndChildren()
    {
        // [Reference, Inline] becomes IncludeX(); plain [Reference] (foreign pointer) is
        // not exposed for traversal — the loader pulls it as id-only and there's no
        // sensible v1 read shape for arbitrary record links.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Root {
                [Id] public partial RootId Id { get; set; }
                [Reference, Inline] public partial Owned? Owned { get; set; }
                [Reference]         public partial Foreign? Foreign { get; set; }
                [Children]          public partial IReadOnlyCollection<Child> Children { get; }
            }

            [Table] public partial class Owned {
                [Id] public partial OwnedId Id { get; set; }
            }

            [Table] public partial class Foreign {
                [Id] public partial ForeignId Id { get; set; }
            }

            [Table] public partial class Child {
                [Id] public partial ChildId Id { get; set; }
                [Parent] public partial Root Root { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, _, _) = GeneratorHarness.Run(src);
        var rootBuilder = GeneratorHarness.FindGeneratedFile(result, "RootTraversalBuilder");
        Assert.NotNull(rootBuilder);

        var s = rootBuilder.ToString();
        Assert.Contains("public RootTraversalBuilder IncludeOwned(", s);     // [Reference, Inline]
        Assert.Contains("public RootTraversalBuilder IncludeChildren(", s);  // [Children]
        Assert.DoesNotContain("IncludeForeign", s);                          // plain [Reference] skipped
    }

    [Fact]
    public void Emits_LoadAsync_OnlyOnAggregateRoots()
    {
        // Design is [AggregateRoot] → DesignQueryLoad is emitted.
        // Constraint is not → no ConstraintQueryLoad. The query layer's "load mode is
        // single-aggregate-rooted" rule lives in the generator, not in a runtime check;
        // a non-root Query<T> simply has no LoadAsync extension visible.
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        Assert.Contains("public static class DesignQueryLoad", allSrc);
        Assert.Contains("LoadAsync(this global::Disruptor.Surface.Runtime.Query.SurfaceQuery<global::M.Design>", allSrc);
        Assert.DoesNotContain("ConstraintQueryLoad", allSrc);
    }

    [Fact]
    public void LoadAsync_DelegatesTo_AggregateLoader_PopulateAsync()
    {
        // PR5 contract: LoadAsync v1 is a thin shim over the existing aggregate loader.
        // The body constructs a typed root id from the canonical RecordId, news up a
        // SurrealSession with the user's ReferenceRegistry, and forwards. PR6 replaces
        // this shim with a compiler-driven path when Includes are present.
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var loadFile = GeneratorHarness.FindGeneratedFile(result, "DesignQueryLoad");
        Assert.NotNull(loadFile);

        var src = loadFile.ToString();
        Assert.Contains("global::Disruptor.Surface.Runtime.DesignAggregateLoader.PopulateAsync", src);
        Assert.Contains("new global::Disruptor.Surface.Runtime.SurrealSession(global::M.Workspace.ReferenceRegistry)", src);
        Assert.Contains("new global::M.DesignId(query.PinnedId.Value.Value)", src);
    }

    [Fact]
    public void LoadAsync_BranchesOnIncludes_AndRequiresPinnedId()
    {
        // PR6: filtered loads use the compiler-driven path (no NIE throw). Two
        // preconditions remain: a missing WithId still throws InvalidOperationException
        // with a hint at the right call, and the Includes-non-empty branch routes through
        // ExecuteIntoSessionAsync rather than the legacy aggregate loader.
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var loadFile = GeneratorHarness.FindGeneratedFile(result, "DesignQueryLoad");
        Assert.NotNull(loadFile);

        var src = loadFile.ToString();

        Assert.Contains("if (query.PinnedId is null)", src);
        Assert.Contains("throw new global::System.InvalidOperationException", src);
        Assert.Contains(".WithId(DesignId)", src);

        // Two-path body: Includes non-empty → ExecuteIntoSessionAsync; empty → legacy
        // aggregate loader. The NIE throw is gone in PR6.
        Assert.Contains("if (query.Includes.Count > 0)", src);
        Assert.Contains("await query.ExecuteIntoSessionAsync(session, db, ct);", src);
        Assert.Contains("global::Disruptor.Surface.Runtime.DesignAggregateLoader.PopulateAsync", src);
        Assert.DoesNotContain("NotImplementedException", src);
    }

    [Fact]
    public void TraversalBuilder_LeafTable_StillEmitsBuilder_WithJustWhere()
    {
        // A table with no traversable members (no [Children], no [Reference, Inline])
        // still gets a builder so the per-Table emit surface stays uniform — the user can
        // call .Where(...) on it inside a parent's IncludeX configure lambda. The
        // sibling extension class is suppressed (nothing to anchor on Query<T>).
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Root {
                [Id] public partial RootId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Leaf> Leaves { get; }
            }

            [Table] public partial class Leaf {
                [Id] public partial LeafId Id { get; set; }
                [Parent] public partial Root Root { get; set; }
                [Property] public partial string Description { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, _, _) = GeneratorHarness.Run(src);
        var leafBuilder = GeneratorHarness.FindGeneratedFile(result, "LeafTraversalBuilder");
        Assert.NotNull(leafBuilder);

        var s = leafBuilder.ToString();
        Assert.Contains("public sealed class LeafTraversalBuilder", s);
        Assert.Contains("public LeafTraversalBuilder Where(", s);
        // No Include* methods — Leaf has none of [Children] / [Reference, Inline].
        Assert.DoesNotContain("public LeafTraversalBuilder Include", s);
        // No sibling extension class either.
        Assert.DoesNotContain("LeafQueryIncludes", s);
    }

    // Three preview.51 phase 1 skipped tests (TypedEdgePayload_EmitsSchemaFieldsOnRelationTable,
    // TypedEdgePayload_EmitsRelateAsyncExtensionsClass_WithFourOverloads,
    // ForwardRelationWithPayload_Emits_EdgeQ_PredicateFactory) deleted in phase 6a along
    // with the features they exercised — ForwardRelation<TPayload>, the {Marker}RelateExtensions
    // class, and the {Kind}EdgeQ predicate factory. Equivalent assertions on the new
    // [Restricts]-on-class shape live in MultiVariantRelation_*, SingleVariantRelation_*,
    // and Variant_SaveAsync_*.
    //
    // BareForwardRelation_EmitsNoRelateAsyncExtensions also dropped — without
    // ForwardRelation<TPayload> there's nothing for "no extensions emitted" to assert
    // against, and the BareForwardRelation_EmitsNoPayloadFields test below still pins
    // the no-payload schema shape.

    [Fact]
    public void BareForwardRelation_EmitsNoPayloadFields()
    {
        // Regression: forward relations declared via the non-generic ForwardRelation
        // base must NOT pick up phantom payload fields. The pre-payload schema shape
        // is preserved verbatim.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;
            public sealed class CalledByAttribute : InverseRelation<CallsAttribute>;

            [Table, AggregateRoot] public partial class Symbol {
                [Id] public partial SymbolId Id { get; set; }
                [Calls] public partial IReadOnlyCollection<Symbol> CalledSymbols { get; }
                [CalledBy] public partial IReadOnlyCollection<Symbol> CallingSymbols { get; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(src);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        Assert.Contains("DEFINE TABLE IF NOT EXISTS calls SCHEMAFULL", allSrc);
        // No DEFINE FIELD lines for the calls table — payloadless edge keeps the
        // pre-feature schema.
        Assert.DoesNotContain("DEFINE FIELD IF NOT EXISTS kind ON calls", allSrc);
        Assert.DoesNotContain("DEFINE FIELD IF NOT EXISTS run_id ON calls", allSrc);
    }

    [Fact]
    public void Emits_HydrateRoot_PerTable_WithTypedAndRawIdOverloads()
    {
        // Workspace.Hydrate.{Table}(ids) is the entry point for the Hydrate terminal.
        // Two overloads per table — typed {Table}Id (ergonomic) and IRecordId (raw
        // canonical, useful for cross-aggregate edge endpoints).
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        Assert.Contains("public sealed class GeneratedHydrationRoot", allSrc);
        Assert.Contains("public static GeneratedHydrationRoot Hydrate => GeneratedHydrationRoot.Instance;", allSrc);
        // Typed-id overload — `Designs(IEnumerable<DesignId> ids)`.
        Assert.Contains("Designs(global::System.Collections.Generic.IEnumerable<global::M.DesignId> ids)", allSrc);
        Assert.Contains("Constraints(global::System.Collections.Generic.IEnumerable<global::M.ConstraintId> ids)", allSrc);
        // Raw-id overload — `Designs(IEnumerable<IRecordId> ids)`.
        Assert.Contains("Designs(global::System.Collections.Generic.IEnumerable<global::Disruptor.Surface.Runtime.IRecordId> ids)", allSrc);
        // Body wires through Workspace.ReferenceRegistry, not a global.
        Assert.Contains("global::M.Workspace.ReferenceRegistry", allSrc);
    }

    [Fact]
    public void Emits_QueryIds_PerTable_WithIdsAsyncExtension()
    {
        // IdsAsync is the "Load = ID selection" terminal: returns IReadOnlyList<{Table}Id>
        // typed at the consumer site so callers don't pivot through canonical RecordId.
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        Assert.Contains("public static class DesignQueryIds", allSrc);
        Assert.Contains("public static class ConstraintQueryIds", allSrc);
        Assert.Contains("IdsAsync(this global::Disruptor.Surface.Runtime.Query.SurfaceQuery<global::M.Design>", allSrc);
        Assert.Contains("IReadOnlyList<global::M.DesignId>", allSrc);
        Assert.Contains("IReadOnlyList<global::M.ConstraintId>", allSrc);
        Assert.Contains("var (sql, __bindings) = query.CompileIdsOnly();", allSrc);
    }

    [Fact]
    public void BareForwardRelation_Emits_NoEdgeQ_Factory()
    {
        // Regression: a forward kind without a payload should not produce an empty
        // {Kind}EdgeQ class — emitter short-circuits.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;
            public sealed class CalledByAttribute : InverseRelation<CallsAttribute>;

            [Table, AggregateRoot] public partial class Symbol {
                [Id] public partial SymbolId Id { get; set; }
                [Calls] public partial IReadOnlyCollection<Symbol> CalledSymbols { get; }
                [CalledBy] public partial IReadOnlyCollection<Symbol> CallingSymbols { get; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(src);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        Assert.DoesNotContain("CallsEdgeQ", allSrc);
    }

    [Fact]
    public void MultiVariantRelation_EmitsSchemaless_WithUnionedInOut()
    {
        // Two [Restricts]-on-class variants share the kind. Each pins its own (In, Out)
        // pair: EpicRestriction is Constraint→Epic, FeatureRestriction is Constraint→Feature.
        // The schema emitter must (a) recognise the kind has multiple variants and switch
        // to SCHEMALESS, (b) union the In endpoints into FROM and the Out endpoints into
        // TO (Ordinal-sorted), (c) emit the unique (in, out) index unconditionally, and
        // (d) NOT emit per-variant DEFINE FIELDs (variants disagree on payload shape).
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;
            public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;

            [Table, AggregateRoot] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
            }
            [Table] public partial class Epic {
                [Id] public partial EpicId Id { get; set; }
            }
            [Table] public partial class Feature {
                [Id] public partial FeatureId Id { get; set; }
            }

            [Restricts]
            public partial class EpicRestriction {
                [In] public partial Constraint Source { get; set; }
                [Out] public partial Epic Target { get; set; }
                [Property] public partial int Severity { get; set; }
            }

            [Restricts]
            public partial class FeatureRestriction {
                [In] public partial Constraint Source { get; set; }
                [Out] public partial Feature Target { get; set; }
                [Property] public partial string Reason { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(src);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // Multi-variant kind => SCHEMALESS. The header still carries TYPE RELATION /
        // ENFORCED so the substrate enforces edge typing.
        Assert.Contains("DEFINE TABLE IF NOT EXISTS restricts SCHEMALESS", allSrc);
        Assert.Contains("FROM constraints", allSrc);
        // Endpoints from both variants get unioned and Ordinal-sorted (epics < features).
        Assert.Contains("TO epics|features", allSrc);

        // The (in, out) uniqueness index always lands, regardless of single/multi variant.
        Assert.Contains("DEFINE INDEX IF NOT EXISTS unique_relationship ON TABLE restricts COLUMNS in, out UNIQUE;", allSrc);

        // No per-variant DEFINE FIELD — the SCHEMALESS table accepts heterogeneous
        // payloads at write time. Phase 3's variant emitter handles per-write payload dispatch.
        Assert.DoesNotContain("DEFINE FIELD IF NOT EXISTS severity ON restricts", allSrc);
        Assert.DoesNotContain("DEFINE FIELD IF NOT EXISTS reason ON restricts", allSrc);
    }

    [Fact]
    public void SingleVariantRelation_EmitsSchemaFull_WithVariantPayloadFields()
    {
        // One [Owns]-on-class variant: PortfolioOwnership pinning Portfolio→Holding with
        // an Acquired DateTime payload. Single-variant kinds keep SCHEMAFULL and emit
        // DEFINE FIELD per [Property] on the variant.
        var src = """
            using Disruptor.Surface.Annotations;
            using System;
            using System.Collections.Generic;
            namespace M;

            public sealed class OwnsAttribute : ForwardRelation;
            public sealed class OwnedByAttribute : InverseRelation<OwnsAttribute>;

            [Table, AggregateRoot] public partial class Portfolio {
                [Id] public partial PortfolioId Id { get; set; }
            }
            [Table] public partial class Holding {
                [Id] public partial HoldingId Id { get; set; }
            }

            [Owns]
            public partial class PortfolioOwnership {
                [In] public partial Portfolio Source { get; set; }
                [Out] public partial Holding Target { get; set; }
                [Property] public partial DateTime Acquired { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(src);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // Single-variant => SCHEMAFULL.
        Assert.Contains("DEFINE TABLE IF NOT EXISTS owns SCHEMAFULL", allSrc);
        Assert.Contains("FROM portfolios", allSrc);
        Assert.Contains("TO holdings", allSrc);

        // The variant's [Property] members emit as DEFINE FIELD on the relation table.
        // DateTime maps to `datetime` with no DEFAULT (per SchemaEmitter.MapScalarType).
        Assert.Contains("DEFINE FIELD IF NOT EXISTS acquired ON owns TYPE datetime;", allSrc);

        // Defensive: the owns-table line shouldn't accidentally land on SCHEMALESS
        // just because the regex/contains might match the suffix. Pin the negative.
        Assert.DoesNotContain("DEFINE TABLE IF NOT EXISTS owns SCHEMALESS", allSrc);
    }

    [Fact]
    public void EveryRelationTable_GetsUniqueInOutIndex()
    {
        // Single-named regression: every relation table — single-variant, multi-variant,
        // and zero-variant (legacy entity-property-only) — must carry the (in, out)
        // unique index. Three forward kinds in one fixture, asserting the index per kind.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            // (1) Multi-variant kind: two [Restricts]-on-class variants.
            public sealed class RestrictsAttribute : ForwardRelation;
            public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;

            // (2) Single-variant kind: one [Owns]-on-class variant.
            public sealed class OwnsAttribute : ForwardRelation;
            public sealed class OwnedByAttribute : InverseRelation<OwnsAttribute>;

            // (3) Zero-variant kind: declared only as entity-property-side reads.
            public sealed class CallsAttribute : ForwardRelation;
            public sealed class CalledByAttribute : InverseRelation<CallsAttribute>;

            [Table, AggregateRoot] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
                [Calls] public partial IReadOnlyCollection<Constraint> CalledConstraints { get; }
                [CalledBy] public partial IReadOnlyCollection<Constraint> CallingConstraints { get; }
            }
            [Table] public partial class Epic {
                [Id] public partial EpicId Id { get; set; }
            }
            [Table] public partial class Portfolio {
                [Id] public partial PortfolioId Id { get; set; }
            }
            [Table] public partial class Holding {
                [Id] public partial HoldingId Id { get; set; }
            }

            [Restricts]
            public partial class EpicRestriction {
                [In] public partial Constraint Source { get; set; }
                [Out] public partial Epic Target { get; set; }
            }
            [Restricts]
            public partial class ConstraintRestriction {
                [In] public partial Constraint Source { get; set; }
                [Out] public partial Constraint Target { get; set; }
            }

            [Owns]
            public partial class PortfolioOwnership {
                [In] public partial Portfolio Source { get; set; }
                [Out] public partial Holding Target { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(src);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // Multi-variant `restricts`.
        Assert.Contains("DEFINE INDEX IF NOT EXISTS unique_relationship ON TABLE restricts COLUMNS in, out UNIQUE;", allSrc);
        // Single-variant `owns`.
        Assert.Contains("DEFINE INDEX IF NOT EXISTS unique_relationship ON TABLE owns COLUMNS in, out UNIQUE;", allSrc);
        // Zero-variant `calls` (entity-property-side only — legacy fallback emission).
        Assert.Contains("DEFINE INDEX IF NOT EXISTS unique_relationship ON TABLE calls COLUMNS in, out UNIQUE;", allSrc);
    }

    [Fact]
    public void KindWithNoVariantClass_StillEmitsSchemaFull()
    {
        // The legacy entity-property-side declaration path (no [Restricts]-on-class) must
        // continue to emit identically to the pre-variant world: SCHEMAFULL, FROM/TO from
        // the entity scan, the (in, out) index, no DEFINE FIELD because no variant carries
        // a payload. Mirrors the BareForwardRelation_EmitsNoPayloadFields shape but pins
        // the explicit SCHEMAFULL / FROM / TO contract too.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;
            public sealed class CalledByAttribute : InverseRelation<CallsAttribute>;

            [Table, AggregateRoot] public partial class Symbol {
                [Id] public partial SymbolId Id { get; set; }
                [Calls] public partial IReadOnlyCollection<Symbol> CalledSymbols { get; }
                [CalledBy] public partial IReadOnlyCollection<Symbol> CallingSymbols { get; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(src);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // Legacy fallback => SCHEMAFULL header, FROM/TO from the entity-property scan.
        Assert.Contains("DEFINE TABLE IF NOT EXISTS calls SCHEMAFULL", allSrc);
        Assert.Contains("FROM symbols", allSrc);
        Assert.Contains("TO symbols", allSrc);
        // (in, out) unique index lands here too — the index is unconditional.
        Assert.Contains("DEFINE INDEX IF NOT EXISTS unique_relationship ON TABLE calls COLUMNS in, out UNIQUE;", allSrc);
        // No payload fields — bare ForwardRelation kinds carry nothing to emit.
        Assert.DoesNotContain("DEFINE FIELD IF NOT EXISTS", FilterCallsTableFields(allSrc));
    }

    /// <summary>
    /// Helper: strips out lines that don't reference the `calls` table. Lets us assert
    /// "no DEFINE FIELD on the calls table" while leaving DEFINE FIELDs on entity tables
    /// (e.g. `symbols.id`) intact.
    /// </summary>
    private static string FilterCallsTableFields(string allSrc)
    {
        var lines = allSrc.Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (line.Contains(" ON calls "))
            {
                sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    // ──────────────────────── relation variant emission (preview.51 phase 3) ────

    /// <summary>
    /// A canonical multi-variant fixture used by several phase-3 variant tests:
    /// <c>[Restricts]</c> kind with two variants (<c>EpicRestriction</c>:
    /// Constraint→Epic, <c>FeatureRestriction</c>: Constraint→Feature). Both variants
    /// declare entity-typed [In]/[Out] within the Constraint aggregate plus payload
    /// [Property] members.
    /// </summary>
    private const string MultiVariantModel = """
        using Disruptor.Surface.Annotations;
        using System.Collections.Generic;
        namespace M;

        public sealed class RestrictsAttribute : ForwardRelation;
        public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;

        [Table, AggregateRoot] public partial class Constraint {
            [Id] public partial ConstraintId Id { get; set; }
        }
        [Table] public partial class Epic {
            [Id] public partial EpicId Id { get; set; }
            [Parent] public partial Constraint Parent { get; set; }
        }
        [Table] public partial class Feature {
            [Id] public partial FeatureId Id { get; set; }
            [Parent] public partial Constraint Parent { get; set; }
        }

        [Restricts]
        public partial class EpicRestriction {
            [In] public partial Constraint Source { get; set; }
            [Out] public partial Epic Target { get; set; }
            [Property] public partial int Severity { get; set; }
        }

        [Restricts]
        public partial class FeatureRestriction {
            [In] public partial Constraint Source { get; set; }
            [Out] public partial Feature Target { get; set; }
            [Property] public partial string Reason { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    [Fact]
    public void Variant_NotPartial_FiresDiagnostic()
    {
        // CG029 — variant classes must be partial. The emitter writes the implementation
        // half (IEntity scaffolding, Hydrate, SaveAsync), which requires the user-side
        // declaration to be partial. Mirrors CG022 / CG001 shape for table/property partials.
        var src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;
            public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;

            [Table, AggregateRoot] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
            }
            [Table] public partial class Epic {
                [Id] public partial EpicId Id { get; set; }
                [Parent] public partial Constraint Parent { get; set; }
            }

            [Restricts]
            public class EpicRestriction {
                [In] public Constraint Source { get; set; } = null!;
                [Out] public Epic Target { get; set; } = null!;
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(src);

        Assert.Contains(runDiags, d => d.Id == "CG029");
    }

    [Fact]
    public void Variant_EmitsPartialClass_WithIEntity_AndPerKindIdAnchor()
    {
        // Per-variant partial class implements IEntity and uses the per-kind {Marker}Id
        // type for its id anchor — single id type shared across all variants of the kind
        // because every variant lives on the same edge table.
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var src = variant.ToString();
        Assert.Contains("public partial class EpicRestriction", src);
        Assert.Contains("global::Disruptor.Surface.Runtime.IEntity", src);
        // Per-kind id type — RestrictsId, not EpicRestrictionId.
        Assert.Contains("private global::M.RestrictsId? _id;", src);
        Assert.Contains("global::M.RestrictsId.New()", src);
    }

    [Fact]
    public void Variant_EmitsSessionPlumbing_Like_TablePartial()
    {
        // Session plumbing (Bind / Session / __EnsureSliceLoaded) is shared with table
        // partials via EntityEmitterCommon — same shape on both, so domain code can rely
        // on identical session-binding semantics whether the class is a [Table] entity or
        // a relation variant.
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var src = variant.ToString();
        Assert.Contains("private global::Disruptor.Surface.Runtime.SurrealSession? _session;", src);
        Assert.Contains("void global::Disruptor.Surface.Runtime.IEntity.Bind(global::Disruptor.Surface.Runtime.SurrealSession session)", src);
        Assert.Contains("Entity is already bound to a different session.", src);
    }

    [Fact]
    public void PerKindIdType_EmittedOnce_NextToMarkerClass()
    {
        // The per-kind {Marker}Id type lives in the same file as the marker class — one
        // id type per forward kind, shared across all variants. Validates RecordIdFormat,
        // exposes Table => "{edgeName}", and mints via Ulid.New().
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var kindFile = GeneratorHarness.FindGeneratedFile(result, "Restricts.RelationKind.g.cs");
        Assert.NotNull(kindFile);

        var src = kindFile.ToString();
        Assert.Contains("public readonly record struct RestrictsId", src);
        // Edge-table name baked into the Table property — matches what RelationKindEmitter
        // emits for the marker class (kind.Name -> "restricts").
        Assert.Contains("public string Table => \"restricts\";", src);
        // Validation goes through RecordIdFormat.Validate (Ulid or short slug only).
        Assert.Contains("global::Disruptor.Surface.Runtime.RecordIdFormat.Validate", src);
        // Implicit conversion to canonical RecordId — same shape as per-table ids.
        Assert.Contains("public static implicit operator global::Disruptor.Surface.Runtime.RecordId(RestrictsId id)", src);
    }

    [Fact]
    public void Variant_EntityTypedIn_EmitsTwoBackingFields_AndResolvesViaSession()
    {
        // [In] / [Out] with an entity-typed property (within-aggregate): generator emits
        // dual backing fields (entity ref + record id) so the getter can fall back to
        // Session.Get<T>(id) when the user later loads the entity by id only.
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var src = variant.ToString();

        // Constraint Source: dual backing fields — the entity ref + the cached record id.
        Assert.Contains("private global::M.Constraint? _source;", src);
        Assert.Contains("private global::Disruptor.Surface.Runtime.RecordId? _sourceId;", src);
        // Getter falls back to Session.Get<T>(__id) when the entity ref isn't locally cached.
        Assert.Contains("_source ?? (_sourceId is { } __id ? _session?.Get<global::M.Constraint>(__id) : null)", src);
        // Mandatory non-nullable endpoint throws if both backing fields are unset.
        Assert.Contains("Endpoint 'Source' is not set.", src);

        // Same shape on the [Out] side.
        Assert.Contains("private global::M.Epic? _target;", src);
        Assert.Contains("private global::Disruptor.Surface.Runtime.RecordId? _targetId;", src);
    }

    [Fact]
    public void Variant_TypedIdEndpoint_EmitsSingleBackingField_NoSessionResolve()
    {
        // [In] / [Out] with a typed id (cross-aggregate): generator emits a single
        // backing field of the typed id; no Session.Get fall-through (caller is expected
        // to load the foreign entity separately).
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class TouchesAttribute : ForwardRelation;
            public sealed class TouchedByAttribute : InverseRelation<TouchesAttribute>;

            [Table, AggregateRoot] public partial class Owner {
                [Id] public partial OwnerId Id { get; set; }
            }
            [Table, AggregateRoot] public partial class Foreign {
                [Id] public partial ForeignId Id { get; set; }
            }

            [Touches]
            public partial class CrossLink {
                [In] public partial OwnerId Source { get; set; }
                [Out] public partial ForeignId Target { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, _, _) = GeneratorHarness.Run(src);
        var variant = GeneratorHarness.FindGeneratedFile(result, "CrossLink.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var s = variant.ToString();
        // Typed-id endpoint: a single backing field of the id type, no Session cache, no
        // entity ref. Pure pass-through getter/setter. The emitter resolves bare error-
        // type names (Roslyn's same-pass view of a not-yet-emitted `{Name}Id`) to the
        // FQN of the matching table's namespace — so the variant compiles even when the
        // variant `.g.cs` lives in a different namespace from the id struct.
        Assert.Contains("private global::M.OwnerId _source = default!;", s);
        Assert.Contains("private global::M.ForeignId _target = default!;", s);
        // No entity-ref backing field for typed-id endpoints.
        Assert.DoesNotContain("private global::M.Owner? _source;", s);
        Assert.DoesNotContain("Session.Get<OwnerId>", s);
    }

    [Fact]
    public void Variant_PayloadProperty_EmitsScalarBackingField()
    {
        // [Property] payload member on a variant emits a pure backing-field property —
        // same shape as scalar [Property] on a [Table] entity. Save reads the field at
        // dispatch time, no Session interaction.
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var src = variant.ToString();
        Assert.Contains("private int _severity = default!;", src);
        Assert.Contains(" partial int Severity", src);
        // Pure-setter shape — `set => _severity = value;`.
        Assert.Contains("set => _severity = value;", src);
    }

    [Fact]
    public void Variant_HydrateBody_ReadsAllProperties()
    {
        // Hydrate parses the loaded edge row's id (per-kind {Marker}Id), tracks the
        // variant on the sink, then per-property reads the matching field. Entity-typed
        // endpoints land in the cached id backing field (entity ref resolves lazily);
        // typed-id endpoints wrap into the typed id struct; payload [Property] members
        // go through HydrationValue.
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var src = variant.ToString();

        // Hydrate impl signature.
        Assert.Contains(".Hydrate(global::Disruptor.Surreal.Values.SurrealValue row, global::Disruptor.Surface.Runtime.IHydrationSink sink)", src);

        // Id parse uses the per-kind RestrictsId, not a per-variant id type.
        Assert.Contains("_id = new global::M.RestrictsId(global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__idVal).Value);", src);

        // Sink registration.
        Assert.Contains("sink.Track(this);", src);

        // Entity-typed [In] endpoint — write to the cached id backing field via
        // TryReadReferenceId (entity ref stays null until first read falls through to
        // Session.Get<T>(id)).
        Assert.Contains("_sourceId = global::Disruptor.Surface.Runtime.HydrationValue.TryReadReferenceId(__obj, \"in\");", src);
        // Same for [Out].
        Assert.Contains("_targetId = global::Disruptor.Surface.Runtime.HydrationValue.TryReadReferenceId(__obj, \"out\");", src);

        // Payload property — int via ReadOrDefault<int>.
        Assert.Contains("_severity = global::Disruptor.Surface.Runtime.HydrationValue.ReadOrDefault<int>(__obj, \"severity\");", src);
    }

    [Fact]
    public void Variant_EnumerateReferences_YieldsInAndOut()
    {
        // EnumerateReferences yields ("in", inId) and ("out", outId) — one entry per
        // endpoint, snake-cased field name + the cached id backing field. Entity-typed
        // endpoints expose _{name}Id directly; typed-id endpoints cast through RecordId.
        // (Phase 5 follow-up: register variants in IReferenceRegistry so cascade-on-
        // delete sees variant endpoints. The method is emitted here to complete the
        // IEntity surface.)
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var src = variant.ToString();
        Assert.Contains("global::Disruptor.Surface.Runtime.IEntity.EnumerateReferences()", src);
        Assert.Contains("yield return (\"in\", _sourceId);", src);
        Assert.Contains("yield return (\"out\", _targetId);", src);

        // SetReferenceTo emits an empty switch when both endpoints are non-nullable
        // (the typical case — edge endpoints can't be unset).
        Assert.Contains(".SetReferenceTo(string fieldName, global::Disruptor.Surface.Runtime.RecordId? value)", src);
        // No `case "in":` / `case "out":` for non-nullable endpoints.
        Assert.DoesNotContain("case \"in\":", src);
        Assert.DoesNotContain("case \"out\":", src);
    }

    [Fact]
    public void Variant_SaveAsync_PayloadBearing_EmitsInsertRelationOnDuplicateKeyUpdate()
    {
        // Payload-bearing variants dispatch INSERT RELATION INTO {edge} $_content ON
        // DUPLICATE KEY UPDATE field1 = $_p_field1 — full-replace upsert, replacing every
        // payload field on re-call.
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var src = variant.ToString();

        // SaveAsync impl signature.
        Assert.Contains(".SaveAsync(global::Disruptor.Surface.Runtime.ISaveContext ctx, global::System.Threading.CancellationToken ct)", src);
        // SQL emission — INSERT RELATION INTO restricts $_content ON DUPLICATE KEY UPDATE severity = $_p_severity;
        Assert.Contains("INSERT RELATION INTO restricts $_content ON DUPLICATE KEY UPDATE severity = $_p_severity;", src);

        // Bindings carry $_content + $_p_severity. ContentValue.Set picks the right wrap.
        Assert.Contains("[\"_content\"] = new global::Disruptor.Surreal.Values.SurrealObjectValue(__content),", src);
        Assert.Contains("global::Disruptor.Surface.Runtime.ContentValue.Set(__bindings, \"_p_severity\", _severity);", src);

        // Dispatch + EnsureSuccess + MarkSaved are the post-dispatch contract.
        Assert.Contains("await ctx.Transaction.QueryAsync(__sql, __bindings, ct).ConfigureAwait(false);", src);
        Assert.Contains("__response.EnsureSuccess();", src);
        Assert.Contains("ctx.MarkSaved(this);", src);
    }

    [Fact]
    public void Variant_SaveAsync_NoPayload_EmitsInsertRelationIgnore()
    {
        // Payload-less variants dispatch INSERT RELATION IGNORE INTO {edge} $_content;
        // — first-call wins on duplicate (substrate-native idempotence via UNIQUE INDEX
        // (in, out)).
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class TouchesAttribute : ForwardRelation;
            public sealed class TouchedByAttribute : InverseRelation<TouchesAttribute>;

            [Table, AggregateRoot] public partial class Owner {
                [Id] public partial OwnerId Id { get; set; }
            }
            [Table] public partial class Other {
                [Id] public partial OtherId Id { get; set; }
                [Parent] public partial Owner Parent { get; set; }
            }

            [Touches]
            public partial class SimpleLink {
                [In] public partial Owner Source { get; set; }
                [Out] public partial Other Target { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, _, _) = GeneratorHarness.Run(src);
        var variant = GeneratorHarness.FindGeneratedFile(result, "SimpleLink.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var s = variant.ToString();
        // Payload-less SQL — IGNORE-shape no-op on duplicate.
        Assert.Contains("INSERT RELATION IGNORE INTO touches $_content;", s);
        // No ON DUPLICATE KEY UPDATE clause.
        Assert.DoesNotContain("ON DUPLICATE KEY UPDATE", s);
        // No $_p_* bindings.
        Assert.DoesNotContain("_p_", s);
    }

    [Fact]
    public void Variant_SaveAsync_WalksEntityTypedEndpointsForwardDeps()
    {
        // Entity-typed endpoints whose ref is set but not yet tracked must save first
        // (the substrate-side foreign key from the edge to the endpoint is checked on
        // INSERT). Mirrors PartialEmitter's [Reference] forward-dep walk.
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(variant);

        var src = variant.ToString();
        Assert.Contains("if (_source is not null && !ctx.IsTracked(((global::Disruptor.Surface.Runtime.IEntity)_source).Id))", src);
        Assert.Contains("await ctx.SaveAsync(_source, ct);", src);
        Assert.Contains("if (_target is not null && !ctx.IsTracked(((global::Disruptor.Surface.Runtime.IEntity)_target).Id))", src);
        Assert.Contains("await ctx.SaveAsync(_target, ct);", src);
    }

    [Fact]
    public void Variant_ImplementsIRelationVariant_InBaseList()
    {
        // Phase 5: every emitted variant carries the IRelationVariant marker so
        // SurrealSession.MarkSaved + CleanupLocalState can branch on edge-shaped
        // entities without a per-kind type registry. Both single-variant and
        // multi-variant kinds get the marker (it's universal — any variant of any
        // kind needs the in/out edge-tuple mirroring).
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(variant);
        Assert.Contains(": global::Disruptor.Surface.Runtime.IEntity, global::Disruptor.Surface.Runtime.IRelationVariant", variant.ToString());
    }

    [Fact]
    public void MultiVariantKind_EmitsVariantMarkerInterface_AllVariantsImplement()
    {
        // Multi-variant kinds get a per-kind I{KindName}Variant marker — every variant
        // class implements it. Lets session APIs talk about "any variant of this kind"
        // generically. Mirrors UnionInterfaceEmitter's pattern for entity-side union markers.
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);

        // Marker interface emitted in its own file.
        var markerFile = GeneratorHarness.FindGeneratedFile(result, "IRestrictsVariant.g.cs");
        Assert.NotNull(markerFile);
        var markerSrc = markerFile.ToString();
        Assert.Contains("public interface IRestrictsVariant : global::Disruptor.Surface.Runtime.IEntity", markerSrc);

        // Both variants implement it via their base list.
        var epicVariant = GeneratorHarness.FindGeneratedFile(result, "EpicRestriction.RelationVariant.g.cs");
        Assert.NotNull(epicVariant);
        Assert.Contains("global::M.IRestrictsVariant", epicVariant.ToString());

        var featureVariant = GeneratorHarness.FindGeneratedFile(result, "FeatureRestriction.RelationVariant.g.cs");
        Assert.NotNull(featureVariant);
        Assert.Contains("global::M.IRestrictsVariant", featureVariant.ToString());
    }

    [Fact]
    public void SingleVariantKind_DoesNotEmitVariantMarkerInterface()
    {
        // Single-variant kinds skip the marker — the variant class itself is the
        // discriminator. The interface would be a single-member with no value.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class OwnsAttribute : ForwardRelation;
            public sealed class OwnedByAttribute : InverseRelation<OwnsAttribute>;

            [Table, AggregateRoot] public partial class Portfolio {
                [Id] public partial PortfolioId Id { get; set; }
            }
            [Table] public partial class Holding {
                [Id] public partial HoldingId Id { get; set; }
                [Parent] public partial Portfolio Parent { get; set; }
            }

            [Owns]
            public partial class PortfolioOwnership {
                [In] public partial Portfolio Source { get; set; }
                [Out] public partial Holding Target { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, _, _) = GeneratorHarness.Run(src);
        // No IOwnsVariant.g.cs emitted.
        var markerFile = GeneratorHarness.FindGeneratedFile(result, "IOwnsVariant.g.cs");
        Assert.Null(markerFile);

        // The single variant doesn't reference IOwnsVariant in its base list either.
        var variant = GeneratorHarness.FindGeneratedFile(result, "PortfolioOwnership.RelationVariant.g.cs");
        Assert.NotNull(variant);
        Assert.DoesNotContain("IOwnsVariant", variant.ToString());
    }

    [Fact]
    public void MultiVariantKind_EmitsHydrationDispatcher_WithSwitchOnInOutTables()
    {
        // Per-kind {KindName}Hydration.HydrateVariant(SurrealValue, IHydrationSink) reads
        // (in.tb, out.tb) off the loaded edge row and instantiates the matching variant.
        // Pairs are: (constraints, epics) → EpicRestriction, (constraints, features) →
        // FeatureRestriction. Even single-variant kinds get the dispatcher (uniform call
        // site for the loader).
        var (result, _, _, _) = GeneratorHarness.Run(MultiVariantModel);
        var dispatcherFile = GeneratorHarness.FindGeneratedFile(result, "RestrictsHydration.g.cs");
        Assert.NotNull(dispatcherFile);

        var src = dispatcherFile.ToString();
        Assert.Contains("internal static class RestrictsHydration", src);
        Assert.Contains("public static global::Disruptor.Surface.Runtime.IEntity? HydrateVariant(global::Disruptor.Surreal.Values.SurrealValue row, global::Disruptor.Surface.Runtime.IHydrationSink sink)", src);

        // Reads in.tb / out.tb off the row.
        Assert.Contains("var __inTb = global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__inV).Table;", src);
        Assert.Contains("var __outTb = global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__outV).Table;", src);

        // Switch picks the variant by (in.tb, out.tb) — Constraint→Epic / Constraint→Feature.
        Assert.Contains("(\"constraints\", \"epics\") => new global::M.EpicRestriction(),", src);
        Assert.Contains("(\"constraints\", \"features\") => new global::M.FeatureRestriction(),", src);

        // The dispatcher delegates to the variant's IEntity.Hydrate to actually populate
        // the row's fields.
        Assert.Contains("__variant.Hydrate(row, sink);", src);
    }

    [Fact]
    public void SingleVariantKind_EmitsHydrationDispatcher_TooForUniformity()
    {
        // Even single-variant kinds get the dispatcher — the loader has a uniform call
        // site regardless of how many variants the kind has.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class OwnsAttribute : ForwardRelation;
            public sealed class OwnedByAttribute : InverseRelation<OwnsAttribute>;

            [Table, AggregateRoot] public partial class Portfolio {
                [Id] public partial PortfolioId Id { get; set; }
            }
            [Table] public partial class Holding {
                [Id] public partial HoldingId Id { get; set; }
                [Parent] public partial Portfolio Parent { get; set; }
            }

            [Owns]
            public partial class PortfolioOwnership {
                [In] public partial Portfolio Source { get; set; }
                [Out] public partial Holding Target { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (result, _, _, _) = GeneratorHarness.Run(src);
        var dispatcherFile = GeneratorHarness.FindGeneratedFile(result, "OwnsHydration.g.cs");
        Assert.NotNull(dispatcherFile);

        var s = dispatcherFile.ToString();
        Assert.Contains("(\"portfolios\", \"holdings\") => new global::M.PortfolioOwnership(),", s);
    }

    [Fact]
    public void Variant_TypedIdEndpoint_AcrossNamespaces_EmitsFullyQualifiedReference()
    {
        // Same-pass type resolution gotcha: cross-aggregate variants reference typed-id
        // structs (`OwnerId`) that haven't been emitted yet at extraction time. Roslyn
        // hands the extractor an error type with the bare simple name, no namespace —
        // and the variant `.g.cs` lives in a different namespace from the id struct,
        // with no using directives of its own. Without a re-resolution against the
        // table catalog the emitted backing field reads `private OwnerId _source =`
        // and CS0246 fires at body-compile time.
        //
        // Fix: RelationVariantEmitter strips the trailing `Id` off bare endpoint type
        // names, looks the table up in the model graph, and re-emits the FQN. Within-
        // aggregate entity-typed endpoints don't need this — `[Table]` types are real
        // user code the extractor sees as proper INamedTypeSymbols.
        var src = """
            using Disruptor.Surface.Annotations;
            using M.Models;
            using System.Collections.Generic;

            namespace M.Models
            {
                public sealed class AssessesAttribute : ForwardRelation { }
                public sealed class AssessedByAttribute : InverseRelation<AssessesAttribute> { }

                [Table, AggregateRoot] public partial class Owner {
                    [Id] public partial OwnerId Id { get; set; }
                }
                [Table, AggregateRoot] public partial class Subject {
                    [Id] public partial SubjectId Id { get; set; }
                }
            }

            namespace M.Variants
            {
                [M.Models.Assesses]
                public partial class OwnerAssessesSubject {
                    [In] public partial OwnerId Source { get; set; }
                    [Out] public partial SubjectId Target { get; set; }
                }
            }

            namespace M
            {
                [CompositionRoot] public partial class Workspace { }
            }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(src);
        var variantFile = GeneratorHarness.FindGeneratedFile(result, "OwnerAssessesSubject.RelationVariant.g.cs");
        Assert.NotNull(variantFile);

        var s = variantFile.ToString();

        // Backing field uses the FQN — without it, CS0246 fires from M.Variants where
        // `OwnerId` doesn't resolve.
        Assert.Contains("private global::M.Models.OwnerId _source = default!;", s);
        Assert.Contains("private global::M.Models.SubjectId _target = default!;", s);

        // Partial-property declaration also uses the FQN. CS9255 still passes because
        // the user's bare-name decl resolves to the same type symbol at body-compile
        // time (via the `using M.Models;` in the variant file).
        Assert.Contains("public partial global::M.Models.OwnerId Source", s);
        Assert.Contains("public partial global::M.Models.SubjectId Target", s);

        // Hydrate body wraps the read id into the typed-id struct via FQN ctor.
        Assert.Contains("_source = new global::M.Models.OwnerId(", s);
        Assert.Contains("_target = new global::M.Models.SubjectId(", s);

        // The bare-name shape that would have shipped pre-fix must be gone — guards
        // against a regression where some emit path drops the `global::` prefix.
        Assert.DoesNotContain("private OwnerId _source", s);
        Assert.DoesNotContain("public partial OwnerId Source", s);
    }

    [Fact]
    public void Variant_EndpointPairCollision_FiresDiagnostic_AndSuppressesDispatcher()
    {
        // CG030 — two variants in the same kind sharing the same (InType, OutType) pair
        // would make the (in.tb, out.tb) dispatcher ambiguous. Generator reports the
        // diagnostic and suppresses the dispatcher emission for the affected kind so
        // there's no silently-wrong "first match wins" surprise at hydration.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;
            public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;

            [Table, AggregateRoot] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
            }
            [Table] public partial class Epic {
                [Id] public partial EpicId Id { get; set; }
                [Parent] public partial Constraint Parent { get; set; }
            }

            [Restricts]
            public partial class FirstVariant {
                [In] public partial Constraint Source { get; set; }
                [Out] public partial Epic Target { get; set; }
            }
            [Restricts]
            public partial class SecondVariant {
                [In] public partial Constraint Source { get; set; }
                [Out] public partial Epic Target { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, runDiags, _) = GeneratorHarness.Run(src);

        Assert.Contains(runDiags, d => d.Id == "CG030");
        // Dispatcher is suppressed when the collision fires.
        Assert.Null(GeneratorHarness.FindGeneratedFile(result, "RestrictsHydration.g.cs"));
    }
}
