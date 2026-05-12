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
    public void Emits_PerTableIdSideMarker_AlongsideTypedIdStruct()
    {
        // Per-table I{Name}RecordId interface is emitted alongside each {Name}Id struct
        // as the opt-in surface for record-type-union endpoints. The user extends it via
        // `partial interface I{Name}RecordId : IFooTarget { }` to enrol the table in a
        // union declared elsewhere. The struct includes the marker in its base list so
        // every typed id transitively satisfies any union it's enrolled in.
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        Assert.Contains("public partial interface IDesignRecordId : global::Disruptor.Surface.Runtime.IRecordId { }", allSrc);
        Assert.Contains("public partial interface IConstraintRecordId : global::Disruptor.Surface.Runtime.IRecordId { }", allSrc);
        // Struct base list includes the per-table marker (FQN'd since IdEmitter mixes
        // it with the per-kind id-side markers which always come fully qualified).
        Assert.Contains("global::M.IDesignRecordId", allSrc);
        Assert.Contains("global::M.IConstraintRecordId", allSrc);
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
    public void InlineRecordCollectionHydrate_PreservesNullableMembers()
    {
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            public sealed record Scenario(string? Title, int? Estimate, string Key, int Count);

            [Table] public partial class Root {
                [Id] public partial RootId Id { get; set; }
                [Property] public partial IReadOnlyList<Scenario> Scenarios { get; }
            }
            """;

        var (result, _, _, compileDiags) = GeneratorHarness.Run(src);
        Assert.Empty(compileDiags);

        var rootFile = GeneratorHarness.FindGeneratedFile(result, "M.Root.g.cs");
        Assert.NotNull(rootFile);

        var rootSrc = rootFile.ToString();
        Assert.Contains("Title: global::Disruptor.Surface.Runtime.HydrationValue.ReadOrDefault<string>(__eo_scenarios, \"title\"),", rootSrc);
        Assert.Contains("Estimate: global::Disruptor.Surface.Runtime.HydrationValue.ReadOrDefault<int?>(__eo_scenarios, \"estimate\"),", rootSrc);
        Assert.Contains("Key: global::Disruptor.Surface.Runtime.HydrationValue.ReadString(__eo_scenarios, \"key\"),", rootSrc);
        Assert.Contains("Count: global::Disruptor.Surface.Runtime.HydrationValue.ReadOrDefault<int>(__eo_scenarios, \"count\")", rootSrc);
        Assert.DoesNotContain("Title: global::Disruptor.Surface.Runtime.HydrationValue.ReadString(__eo_scenarios, \"title\")", rootSrc);
        Assert.DoesNotContain("Estimate: global::Disruptor.Surface.Runtime.HydrationValue.ReadOrDefault<int>(__eo_scenarios, \"estimate\")", rootSrc);
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

        // SetReferenceTo: when both endpoints are non-nullable (the typical case — edge
        // endpoints can't be unset), the method body stays empty. The switch itself is
        // not opened, since `switch (x) { }` would trip CS1522 in the generated source.
        Assert.Contains(".SetReferenceTo(string fieldName, global::Disruptor.Surface.Runtime.RecordId? value)", src);
        Assert.DoesNotContain("switch (fieldName)", src);
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
        // Endpoint ids are resolved explicitly before building $_content; missing
        // entity-typed endpoints throw a clear error instead of dereferencing null.
        Assert.Contains("var __sourceId = _sourceId ?? (_source is { } __sourceEntity ? ((global::Disruptor.Surface.Runtime.IEntity)__sourceEntity).Id : throw new global::System.InvalidOperationException(\"Endpoint 'Source' is not set.\"));", src);
        Assert.Contains("var __targetId = _targetId ?? (_target is { } __targetEntity ? ((global::Disruptor.Surface.Runtime.IEntity)__targetEntity).Id : throw new global::System.InvalidOperationException(\"Endpoint 'Target' is not set.\"));", src);
        Assert.Contains("[\"in\"] = new global::Disruptor.Surreal.Values.SurrealRecordIdValue(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk(__sourceId)),", src);
        Assert.Contains("[\"out\"] = new global::Disruptor.Surreal.Values.SurrealRecordIdValue(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk(__targetId)),", src);
        Assert.DoesNotContain("_sourceId ?? ((global::Disruptor.Surface.Runtime.IEntity)_source).Id", src);
        Assert.DoesNotContain("_targetId ?? ((global::Disruptor.Surface.Runtime.IEntity)_target).Id", src);
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

    // ─────────────────── union endpoints (preview.54 Phase 3-5) ────────────────────

    private const string UnionEndpointModel = """
        using Disruptor.Surface.Annotations;
        using Disruptor.Surface.Runtime;
        namespace M;

        public sealed class TagsAttribute : ForwardRelation;
        public sealed class TagsTargetAttribute : Out<TagsAttribute>;

        [TagsTarget] public partial interface ITagsTarget : IRecordId;

        [Table] public partial class A { [Id] public partial AId Id { get; set; } }
        [Table] public partial class B { [Id] public partial BId Id { get; set; } }
        [Table] public partial class C { [Id] public partial CId Id { get; set; } }

        public partial interface IARecordId : ITagsTarget;
        public partial interface IBRecordId : ITagsTarget;

        [Tags] public partial class TagsVariant {
            [In]  public partial AId Source { get; set; }
            [Out] public partial ITagsTarget Target { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    [Fact]
    public void UnionEndpoint_EmitsInterfaceTypedBackingField()
    {
        // The variant's [Out] property is typed to the union interface; the backing
        // field follows suit (no cached entity ref, no separate id backing).
        var (result, _, _, _) = GeneratorHarness.Run(UnionEndpointModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "TagsVariant.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant.ToString();

        Assert.Contains("private global::M.ITagsTarget _target = default!;", src);
        Assert.Contains("public partial global::M.ITagsTarget Target", src);
        Assert.Contains("get => _target;", src);
        Assert.Contains("set => _target = value;", src);
    }

    [Fact]
    public void UnionEndpoint_HydrateSwitchesOnTableName_OverEveryMember()
    {
        // Hydrate reads the row's "out" id and switches on the loaded table name to
        // construct the matching typed id, cast back to the union interface. Default
        // arm throws so an unexpected table fails fast.
        var (result, _, _, _) = GeneratorHarness.Run(UnionEndpointModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "TagsVariant.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant.ToString();

        Assert.Contains("\"as\" => (global::M.ITagsTarget)new global::M.AId", src);
        Assert.Contains("\"bs\" => (global::M.ITagsTarget)new global::M.BId", src);
        Assert.DoesNotContain("\"cs\" =>", src); // C didn't opt in
        Assert.Contains("Unknown table", src);
    }

    [Fact]
    public void UnionEndpoint_EnumerateReferences_UsesRecordIdFromHelper()
    {
        // IRecordId has no implicit operator to RecordId, so the union case routes
        // through RecordId.From(IRecordId). Mandatory unions are guarded with `is { } v`.
        var (result, _, _, _) = GeneratorHarness.Run(UnionEndpointModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "TagsVariant.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant.ToString();

        Assert.Contains("global::Disruptor.Surface.Runtime.RecordId.From(__v_target)", src);
    }

    [Fact]
    public void UnionEndpoint_SaveAsync_SkipsForwardDepWalk()
    {
        // Union endpoints are id-only (no concrete entity), so the [In]/[Out] forward-dep
        // walk doesn't recurse through ctx.SaveAsync for them. The endpoint id is
        // resolved with a null-throw guard.
        var (result, _, _, _) = GeneratorHarness.Run(UnionEndpointModel);
        var variant = GeneratorHarness.FindGeneratedFile(result, "TagsVariant.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant.ToString();

        Assert.Contains("var __targetId = _target ?? throw new global::System.InvalidOperationException", src);
        // No ctx.SaveAsync(_target, ct) — that's only for table-typed within-aggregate endpoints.
        Assert.DoesNotContain("await ctx.SaveAsync(_target", src);
    }

    [Fact]
    public void UnionEndpoint_HydrationDispatcher_CartesianExpandsUnionMembers()
    {
        // The kind-level dispatcher's (in.tb, out.tb) switch enumerates every
        // (source-table, union-member-table) pair so a row pointing at any
        // participating table dispatches to the same variant.
        var (result, _, _, _) = GeneratorHarness.Run(UnionEndpointModel);
        var dispatcher = GeneratorHarness.FindGeneratedFile(result, "TagsHydration.g.cs");
        Assert.NotNull(dispatcher);
        var src = dispatcher.ToString();

        Assert.Contains("(\"as\", \"as\") => new global::M.TagsVariant()", src);
        Assert.Contains("(\"as\", \"bs\") => new global::M.TagsVariant()", src);
        Assert.DoesNotContain("\"cs\")", src);
    }

    [Fact]
    public void UnionEndpoint_Schema_FromToCoversEveryUnionMember()
    {
        // The TYPE RELATION FROM/TO clause includes every union member at the schema
        // level so the substrate accepts any participating table on the union side.
        var (result, _, _, _) = GeneratorHarness.Run(UnionEndpointModel);
        var schema = GeneratorHarness.FindGeneratedFile(result, "Workspace.Schema.g.cs");
        Assert.NotNull(schema);
        var src = schema.ToString();

        Assert.Contains("DEFINE TABLE IF NOT EXISTS tags SCHEMAFULL", src);
        Assert.Contains("FROM as", src);
        Assert.Contains("TO as|bs", src);
    }

    [Fact]
    public void UnionEndpoint_KindMismatch_FiresCG031()
    {
        // A union pinned to one kind applied as the endpoint of a variant declaring a
        // different kind is a schema-vs-runtime inconsistency. The kind mismatch is
        // visible at codegen via the In<TKind> / Out<TKind> generic argument.
        var src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;
            public sealed class ValidatesAttribute : ForwardRelation;
            public sealed class ForRestrictsAttribute : Out<RestrictsAttribute>;

            [ForRestricts] public partial interface IForRestricts : IRecordId;

            [Table] public partial class A { [Id] public partial AId Id { get; set; } }

            public partial interface IARecordId : IForRestricts;

            // The variant is [Validates] but its [Out] uses an [Out<Restricts>] union — mismatch.
            [Validates] public partial class Misaligned {
                [In]  public partial AId Source { get; set; }
                [Out] public partial IForRestricts Target { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG031");
    }

    // ─── shared-shape relation interface (preview.55): user-declared interface +
    //     generated kind-keyed Create<TKind> factory ───────────────────────────────

    private const string SharedShapeSpike = """
        using Disruptor.Surface.Annotations;
        using Disruptor.Surface.Runtime;
        namespace M;

        public sealed class CallsAttribute      : ForwardRelation;
        public sealed class ReferencesAttribute : ForwardRelation;

        [Table, AggregateRoot]
        public partial class CodeSymbol {
            [Id]       public partial CodeSymbolId Id { get; set; }
            [Property] public partial string Fqn { get; set; }
        }

        // User-declared shared contract: `partial` is required so the generator can
        // graft `static Create<TKind>` onto it.
        public partial interface ICodeSymbolEdge : IRelationVariant {
            CodeSymbolId Source { get; set; }
            CodeSymbolId Target { get; set; }
            string Confidence { get; set; }
        }

        [Calls]
        public partial class CallsRelation : ICodeSymbolEdge {
            [In]       public partial CodeSymbolId Source { get; set; }
            [Out]      public partial CodeSymbolId Target { get; set; }
            [Property] public partial string Confidence { get; set; }
        }

        [References]
        public partial class ReferencesRelation : ICodeSymbolEdge {
            [In]       public partial CodeSymbolId Source { get; set; }
            [Out]      public partial CodeSymbolId Target { get; set; }
            [Property] public partial string Confidence { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    [Fact]
    public void SharedShape_PartialPropertiesSatisfyInterfaceContract()
    {
        // Every implementing variant carries Source / Target / Confidence partial
        // properties whose accessor shape matches the interface; partial class
        // declaration merging folds the user's `: ICodeSymbolEdge` with the
        // generator's `: IEntity, IRelationVariant` into one final type.
        var asm = GeneratorHarness.CompileAndLoad(SharedShapeSpike);

        var shared = asm.GetType("M.ICodeSymbolEdge", throwOnError: true)!;
        var callsT = asm.GetType("M.CallsRelation", throwOnError: true)!;
        var refsT  = asm.GetType("M.ReferencesRelation", throwOnError: true)!;
        Assert.True(shared.IsAssignableFrom(callsT));
        Assert.True(shared.IsAssignableFrom(refsT));

        var codeSymbolId = asm.GetType("M.CodeSymbolId", throwOnError: true)!;
        var src = Activator.CreateInstance(codeSymbolId, "01HZ0000000000000000000001")!;
        var tgt = Activator.CreateInstance(codeSymbolId, "01HZ0000000000000000000002")!;

        var calls = Activator.CreateInstance(callsT)!;
        shared.GetProperty("Source")!.SetValue(calls, src);
        shared.GetProperty("Target")!.SetValue(calls, tgt);
        shared.GetProperty("Confidence")!.SetValue(calls, "high");

        Assert.Equal(src, shared.GetProperty("Source")!.GetValue(calls));
        Assert.Equal(tgt, shared.GetProperty("Target")!.GetValue(calls));
        Assert.Equal("high", shared.GetProperty("Confidence")!.GetValue(calls));
    }

    [Fact]
    public void SharedShape_EmitsStaticCreateFactory_DispatchedByKindMarker()
    {
        // Generator-emitted `ICodeSymbolEdge.Create<TKind>(init)` instantiates the
        // right concrete variant based on the typed kind argument, then runs the
        // user's initialiser. This is the kind-keyed dispatch that removes the
        // hand-maintained switch from call sites.
        var asm = GeneratorHarness.CompileAndLoad(SharedShapeSpike);

        var shared = asm.GetType("M.ICodeSymbolEdge", throwOnError: true)!;
        var callsT = asm.GetType("M.CallsRelation", throwOnError: true)!;
        var refsT  = asm.GetType("M.ReferencesRelation", throwOnError: true)!;
        var callsK = asm.GetType("M.Calls", throwOnError: true)!;
        var refsK  = asm.GetType("M.References", throwOnError: true)!;
        var codeSymbolId = asm.GetType("M.CodeSymbolId", throwOnError: true)!;

        var src = Activator.CreateInstance(codeSymbolId, "01HZ0000000000000000000001")!;
        var tgt = Activator.CreateInstance(codeSymbolId, "01HZ0000000000000000000002")!;

        var actionT = typeof(Action<>).MakeGenericType(shared);
        var sourceProp = shared.GetProperty("Source")!;
        var targetProp = shared.GetProperty("Target")!;
        var confidenceProp = shared.GetProperty("Confidence")!;

        Action<object?> init = e =>
        {
            sourceProp.SetValue(e, src);
            targetProp.SetValue(e, tgt);
            confidenceProp.SetValue(e, "high");
        };
        var typedInit = Delegate.CreateDelegate(actionT, init.Target!, init.Method);

        var create = shared.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;

        var callsEdge = create.MakeGenericMethod(callsK).Invoke(null, [typedInit])!;
        Assert.IsType(callsT, callsEdge);
        Assert.Equal(src, sourceProp.GetValue(callsEdge));
        Assert.Equal("high", confidenceProp.GetValue(callsEdge));

        var refsEdge = create.MakeGenericMethod(refsK).Invoke(null, [typedInit])!;
        Assert.IsType(refsT, refsEdge);
    }

    [Fact]
    public void SharedShape_Create_ThrowsForUnregisteredKind()
    {
        // Calling Create<TKind> with a kind whose variant doesn't implement the
        // shared interface must fail loudly — the generated dispatch's final else
        // branch throws ArgumentException with a clear message.
        var source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;

            namespace M {
                public sealed class CallsAttribute : ForwardRelation;
                public sealed class FooAttribute   : ForwardRelation;

                [Table, AggregateRoot]
                public partial class CodeSymbol {
                    [Id]       public partial CodeSymbolId Id { get; set; }
                    [Property] public partial string Fqn { get; set; }
                }

                public partial interface ICodeSymbolEdge : IRelationVariant {
                    CodeSymbolId Source { get; set; }
                    CodeSymbolId Target { get; set; }
                }

                [Calls]
                public partial class CallsRelation : ICodeSymbolEdge {
                    [In]  public partial CodeSymbolId Source { get; set; }
                    [Out] public partial CodeSymbolId Target { get; set; }
                }

                // Standalone variant of an unrelated kind — does NOT implement ICodeSymbolEdge,
                // so passing Foo to Create<TKind> hits the else-arm throw.
                [Foo]
                public partial class StandaloneRelation {
                    [In]  public partial CodeSymbolId Source { get; set; }
                    [Out] public partial CodeSymbolId Target { get; set; }
                }

                [CompositionRoot] public partial class Workspace { }
            }
            """;
        var asm = GeneratorHarness.CompileAndLoad(source);

        var shared = asm.GetType("M.ICodeSymbolEdge", throwOnError: true)!;
        var fooK   = asm.GetType("M.Foo", throwOnError: true)!;

        var actionT = typeof(Action<>).MakeGenericType(shared);
        var noop = Delegate.CreateDelegate(actionT, ((Action<object?>)(_ => { })).Target!, ((Action<object?>)(_ => { })).Method);

        var create = shared.GetMethod("Create", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => create.MakeGenericMethod(fooK).Invoke(null, [noop]));
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public void SharedShape_NonPartialInterface_FiresCG033()
    {
        // Without `partial`, the generator can't graft the static factory fragment;
        // emission is skipped and CG033 surfaces the contract requirement.
        var source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;

            [Table, AggregateRoot] public partial class A { [Id] public partial AId Id { get; set; } }

            public interface IBareContract : IRelationVariant {
                AId Source { get; set; }
                AId Target { get; set; }
            }

            [Calls] public partial class CallsRelation : IBareContract {
                [In]  public partial AId Source { get; set; }
                [Out] public partial AId Target { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(source);
        Assert.Contains(runDiags, d => d.Id == "CG033");
    }

    [Fact]
    public void SharedShape_NoImplementingVariants_FiresCG035()
    {
        // A partial interface deriving from IRelationVariant with no implementing
        // variant is dead. Warning, not error — the interface still resolves as a
        // type and could be filled in later.
        var source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public partial interface IDeadContract : IRelationVariant { }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(source);
        Assert.Contains(runDiags, d => d.Id == "CG035");
    }

    // ─── preview.56 — annotated shared-shape interface lifts In/Out/Property onto
    //     empty-body variants ─────────────────────────────────────────────────────────

    private const string LiftSpike = """
        using Disruptor.Surface.Annotations;
        using Disruptor.Surface.Runtime;
        namespace M;

        public sealed class CallsAttribute : ForwardRelation;

        [Table, AggregateRoot]
        public partial class CodeSymbol {
            [Id]       public partial CodeSymbolId Id { get; set; }
            [Property] public partial string Fqn { get; set; }
        }

        // Annotated shared-shape interface — [In]/[Out]/[Property] target the interface
        // members. preview.56 lifts these onto any variant with an empty body.
        public partial interface ICodeSymbolEdge : IRelationVariant {
            [In]       CodeSymbolId Source { get; set; }
            [Out]      CodeSymbolId Target { get; set; }
            [Property] string Confidence { get; set; }
        }

        // Empty body — variant inherits its shape from the annotated interface.
        [Calls]
        public partial class CallsRelation : ICodeSymbolEdge;

        [CompositionRoot] public partial class Workspace { }
        """;

    [Fact]
    public void Lift_EmptyBodyVariant_GetsFullPropertyImplementations()
    {
        // The variant body is `;` — the user declared no [In]/[Out]/[Property] of its
        // own. The linker copies the annotated interface's shape onto the variant and
        // the emitter generates non-partial property bodies that satisfy the interface
        // contract via partial-class declaration merging.
        var asm = GeneratorHarness.CompileAndLoad(LiftSpike);

        var iface  = asm.GetType("M.ICodeSymbolEdge",   throwOnError: true)!;
        var calls  = asm.GetType("M.CallsRelation",     throwOnError: true)!;
        var symId  = asm.GetType("M.CodeSymbolId",      throwOnError: true)!;
        Assert.True(iface.IsAssignableFrom(calls));

        var src = Activator.CreateInstance(symId, "01HZ0000000000000000000001")!;
        var tgt = Activator.CreateInstance(symId, "01HZ0000000000000000000002")!;
        var instance = Activator.CreateInstance(calls)!;

        // Interface members route through the emitted full-property declarations.
        iface.GetProperty("Source")!.SetValue(instance, src);
        iface.GetProperty("Target")!.SetValue(instance, tgt);
        iface.GetProperty("Confidence")!.SetValue(instance, "high");

        Assert.Equal(src, iface.GetProperty("Source")!.GetValue(instance));
        Assert.Equal(tgt, iface.GetProperty("Target")!.GetValue(instance));
        Assert.Equal("high", iface.GetProperty("Confidence")!.GetValue(instance));
    }

    [Fact]
    public void Lift_GeneratedPropertiesAreNotMarkedPartial()
    {
        // Source-level shape assertion: the .g.cs for an empty-body variant must declare
        // properties WITHOUT the `partial` keyword (interface members aren't partial, so
        // the variant satisfies the contract via partial-class merging instead). A regression
        // here would surface as a CS0759 / CS0763 in the consumer compile, but catch it at
        // the source layer for a clearer signal.
        var (result, _, _, _) = GeneratorHarness.Run(LiftSpike);
        var variant = GeneratorHarness.FindGeneratedFile(result, "CallsRelation.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant!.ToString();

        // The full property declarations look like `public T Source { get => ...; set => ...; }`
        // with no `partial` keyword; the lifted-from-interface case never emits `partial T Name`.
        Assert.Contains("public global::M.CodeSymbolId Source", src);
        Assert.Contains("public global::M.CodeSymbolId Target", src);
        Assert.Contains("public string Confidence", src);
        Assert.DoesNotContain("partial global::M.CodeSymbolId Source", src);
        Assert.DoesNotContain("partial global::M.CodeSymbolId Target", src);
        Assert.DoesNotContain("partial string Confidence", src);
    }

    [Fact]
    public void Lift_SchemaPicksUpLiftedPayloadAndEndpoints()
    {
        // The lifted [Property] on the interface contributes a column on the edge table;
        // the lifted [In]/[Out] contribute the FROM/TO clauses. End-to-end check that the
        // lift propagates beyond the variant emitter into schema generation.
        var (result, _, _, _) = GeneratorHarness.Run(LiftSpike);
        var schema = GeneratorHarness.FindGeneratedFile(result, "Workspace.Schema.g.cs");
        Assert.NotNull(schema);
        var ddl = schema!.ToString();

        Assert.Contains("DEFINE TABLE IF NOT EXISTS calls SCHEMAFULL", ddl);
        Assert.Contains("FROM code_symbols", ddl);
        Assert.Contains("TO code_symbols", ddl);
        Assert.Contains("DEFINE FIELD IF NOT EXISTS confidence ON calls TYPE string", ddl);
    }

    [Fact]
    public void Lift_OwnAnnotatedMembersStillWin_NoLiftClobbering()
    {
        // Even when the interface is annotated, a variant that declares its own
        // [In]/[Out]/[Property] members opts OUT of the lift — the linker only fills
        // in null In/Out endpoints. This preserves the preview.55 self-describing shape
        // for variants that need per-variant payload customisation.
        var source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;

            [Table, AggregateRoot]
            public partial class CodeSymbol {
                [Id]       public partial CodeSymbolId Id { get; set; }
                [Property] public partial string Fqn { get; set; }
            }

            public partial interface ICodeSymbolEdge : IRelationVariant {
                [In]       CodeSymbolId Source { get; set; }
                [Out]      CodeSymbolId Target { get; set; }
                [Property] string Confidence { get; set; }
            }

            // Variant declares its own attributed partial members — interface annotations
            // are visible but the lift path is skipped because In/Out aren't null at link
            // time. The emitted property is `partial`, since the user provided the stub.
            [Calls]
            public partial class CallsRelation : ICodeSymbolEdge {
                [In]       public partial CodeSymbolId Source { get; set; }
                [Out]      public partial CodeSymbolId Target { get; set; }
                [Property] public partial string Confidence { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(source);
        var variant = GeneratorHarness.FindGeneratedFile(result, "CallsRelation.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant!.ToString();

        // Self-declared variant retains the `partial` keyword on its emitted props.
        Assert.Contains("partial global::M.CodeSymbolId Source", src);
        Assert.Contains("partial global::M.CodeSymbolId Target", src);
        Assert.Contains("partial string Confidence", src);
    }

    [Fact]
    public void Lift_UnannotatedInterface_LeavesEmptyVariantInert()
    {
        // The preview.55 spike shape — interface members with no model attributes — is
        // the no-op case for the lift. An empty-body variant under such an interface
        // produces NO RelationVariant emit (the linker drops it silently because lift
        // had no source) so the substrate's edge table is correctly never created from
        // a half-formed variant declaration.
        var source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;

            [Table, AggregateRoot]
            public partial class CodeSymbol {
                [Id]       public partial CodeSymbolId Id { get; set; }
                [Property] public partial string Fqn { get; set; }
            }

            // Interface members carry NO [In]/[Out]/[Property] — original preview.55
            // contract shape. Variants under this MUST self-declare their members.
            public partial interface ICodeSymbolEdge : IRelationVariant {
                CodeSymbolId Source { get; set; }
                CodeSymbolId Target { get; set; }
                string Confidence { get; set; }
            }

            // Empty body + unannotated interface → nothing for the lift to copy.
            [Calls]
            public partial class CallsRelation : ICodeSymbolEdge;

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(source);
        var variant = GeneratorHarness.FindGeneratedFile(result, "CallsRelation.RelationVariant.g.cs");
        Assert.Null(variant);
    }

    [Fact]
    public void Lift_InheritedPayloadInterfaceMembers_AreMerged()
    {
        // Payload can live on an ordinary base contract; the shared-shape relation
        // interface contributes endpoints and inherits the payload. The lift walks the
        // interface closure, so the empty variant still gets Confidence.
        var source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;

            [Table, AggregateRoot]
            public partial class CodeSymbol {
                [Id]       public partial CodeSymbolId Id { get; set; }
                [Property] public partial string Fqn { get; set; }
            }

            public interface IEdgePayload {
                [Property] string Confidence { get; set; }
            }

            public partial interface ICodeSymbolEdge : IEdgePayload, IRelationVariant {
                [In]  CodeSymbolId Source { get; set; }
                [Out] CodeSymbolId Target { get; set; }
            }

            [Calls]
            public partial class CallsRelation : ICodeSymbolEdge;

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, compileDiags) = GeneratorHarness.Run(source);
        Assert.Empty(compileDiags);

        var variant = GeneratorHarness.FindGeneratedFile(result, "CallsRelation.RelationVariant.g.cs");
        Assert.NotNull(variant);
        Assert.Contains("public string Confidence", variant!.ToString());
    }

    [Fact]
    public void Lift_CompatibleLayeredSharedShapeInterfaces_AreMerged()
    {
        // Multiple annotated relation-shape interfaces are fine when they contribute
        // compatible pieces of one shape. This keeps "endpoint shape" and "payload
        // shape" reusable without forcing every variant to repeat either one.
        var source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;

            [Table, AggregateRoot]
            public partial class CodeSymbol {
                [Id]       public partial CodeSymbolId Id { get; set; }
                [Property] public partial string Fqn { get; set; }
            }

            public partial interface IEdgePayload : IRelationVariant {
                [Property] string Confidence { get; set; }
            }

            public partial interface ICodeSymbolEdge : IEdgePayload {
                [In]  CodeSymbolId Source { get; set; }
                [Out] CodeSymbolId Target { get; set; }
            }

            [Calls]
            public partial class CallsRelation : ICodeSymbolEdge;

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, compileDiags) = GeneratorHarness.Run(source);
        Assert.Empty(compileDiags);

        var variant = GeneratorHarness.FindGeneratedFile(result, "CallsRelation.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant!.ToString();
        Assert.Contains("public global::M.CodeSymbolId Source", src);
        Assert.Contains("public string Confidence", src);
    }

    [Fact]
    public void Lift_LocalPayloadCombinesWithSharedEndpoints()
    {
        // A variant can lift the common endpoint/payload shape and still add one
        // per-variant payload field. Local members keep their partial implementation.
        var source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;

            [Table, AggregateRoot]
            public partial class CodeSymbol {
                [Id]       public partial CodeSymbolId Id { get; set; }
                [Property] public partial string Fqn { get; set; }
            }

            public partial interface ICodeSymbolEdge : IRelationVariant {
                [In]       CodeSymbolId Source { get; set; }
                [Out]      CodeSymbolId Target { get; set; }
                [Property] string Confidence { get; set; }
            }

            [Calls]
            public partial class CallsRelation : ICodeSymbolEdge {
                [Property] public partial string Notes { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, compileDiags) = GeneratorHarness.Run(source);
        Assert.Empty(compileDiags);

        var variant = GeneratorHarness.FindGeneratedFile(result, "CallsRelation.RelationVariant.g.cs");
        Assert.NotNull(variant);
        var src = variant!.ToString();
        Assert.Contains("public global::M.CodeSymbolId Source", src);
        Assert.Contains("public string Confidence", src);
        Assert.Contains("public partial string Notes", src);
    }

    [Fact]
    public void Lift_ConflictingAnnotatedInterfaces_DropsVariant()
    {
        // Compatible fragments merge, but conflicting endpoint contracts still fail
        // closed. The linker must not guess which Source type defines the edge table.
        var source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class CallsAttribute : ForwardRelation;

            [Table, AggregateRoot]
            public partial class CodeSymbol {
                [Id]       public partial CodeSymbolId Id { get; set; }
                [Property] public partial string Fqn { get; set; }
            }

            [Table]
            public partial class OtherSymbol {
                [Id] public partial OtherSymbolId Id { get; set; }
            }

            public partial interface ICodeSymbolEdgeA : IRelationVariant {
                [In]       CodeSymbolId Source { get; set; }
                [Out]      CodeSymbolId Target { get; set; }
                [Property] string Confidence { get; set; }
            }

            public partial interface ICodeSymbolEdgeB : IRelationVariant {
                [In]       OtherSymbolId Source { get; set; }
                [Out]      CodeSymbolId Target { get; set; }
                [Property] string Reason { get; set; }
            }

            // Conflicting Source endpoint types → variant dropped.
            [Calls]
            public partial class CallsRelation : ICodeSymbolEdgeA, ICodeSymbolEdgeB;

            [CompositionRoot] public partial class Workspace { }
            """;

        var (result, _, _, _) = GeneratorHarness.Run(source);
        var variant = GeneratorHarness.FindGeneratedFile(result, "CallsRelation.RelationVariant.g.cs");
        Assert.Null(variant);
    }

    [Fact]
    public void UnionEndpoint_DeadUnion_FiresCG032AsWarning()
    {
        // A union interface attributed for a kind but with no per-table marker
        // partial opting any table in is unreachable. Warning, not error — the union
        // still resolves as a type and won't break compilation.
        var src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            namespace M;

            public sealed class RestrictsAttribute : ForwardRelation;
            public sealed class OrphanedAttribute : Out<RestrictsAttribute>;

            [Orphaned] public partial interface IOrphaned : IRecordId;

            [Table] public partial class A { [Id] public partial AId Id { get; set; } }

            [CompositionRoot] public partial class Workspace { }
            """;

        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG032");
    }
}
