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

        var src = compositionRootFile!.ToString();
        Assert.Contains("public partial class Workspace", src);
        Assert.Contains("LoadDesignAsync", src);
        // Instance method, not static; takes transport + typed id + cancellation token.
        Assert.Contains("global::Disruptor.Surface.Runtime.ISurrealTransport transport", src);
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

        var loaderSrc = loader!.ToString();

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

        var loaderSrc = loader!.ToString();
        // The edge subselect must reference _restricts AND OR over both source paths
        // (constraints' and rules' parent paths back to design — both empty paths here
        // since both are direct children of Design root).
        Assert.Contains("_restricts", loaderSrc);
        Assert.Contains("HydrateEdges(rootRow.Value, \"_restricts\"", loaderSrc);
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

        var partialSrc = partial!.ToString();
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

        var src = loader!.ToString();
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

        var src = designBuilder!.ToString();

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
        Assert.Contains("this global::Disruptor.Surface.Runtime.Query.Query<global::M.Design> query", src);
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

        var s = rootBuilder!.ToString();
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
        Assert.Contains("LoadAsync(this global::Disruptor.Surface.Runtime.Query.Query<global::M.Design>", allSrc);
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

        var src = loadFile!.ToString();
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

        var src = loadFile!.ToString();

        Assert.Contains("if (query.PinnedId is null)", src);
        Assert.Contains("throw new global::System.InvalidOperationException", src);
        Assert.Contains(".WithId(DesignId)", src);

        // Two-path body: Includes non-empty → ExecuteIntoSessionAsync; empty → legacy
        // aggregate loader. The NIE throw is gone in PR6.
        Assert.Contains("if (query.Includes.Count > 0)", src);
        Assert.Contains("await query.ExecuteIntoSessionAsync(session, transport, ct);", src);
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

        var s = leafBuilder!.ToString();
        Assert.Contains("public sealed class LeafTraversalBuilder", s);
        Assert.Contains("public LeafTraversalBuilder Where(", s);
        // No Include* methods — Leaf has none of [Children] / [Reference, Inline].
        Assert.DoesNotContain("public LeafTraversalBuilder Include", s);
        // No sibling extension class either.
        Assert.DoesNotContain("LeafQueryIncludes", s);
    }
}
