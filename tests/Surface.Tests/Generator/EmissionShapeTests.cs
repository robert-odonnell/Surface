using Xunit;

namespace Surface.Tests.Generator;

/// <summary>
/// Shape assertions on what the generator emits for representative inputs. Not full
/// snapshot matching — just enough to pin down the contracts that consumer code depends
/// on (typed id structs, IRelationKind markers, [CompositionRoot] load methods).
/// </summary>
public sealed class EmissionShapeTests
{
    private const string MinimalModel = """
        using Surface.Annotations;
        using Surface.Runtime;
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
        Assert.Contains(": global::Surface.Runtime.IRecordId", allSrc);
    }

    [Fact]
    public void Emits_RelationKindMarker_PerForwardKind_WithEdgeName()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // Marker class without the Attribute suffix, implementing IRelationKind, with
        // the snake-cased edge name as a static property.
        Assert.Contains("public sealed class Restricts : global::Surface.Runtime.IRelationKind", allSrc);
        Assert.Contains("public static string EdgeName => \"restricts\";", allSrc);
        // No marker for the inverse — the edge is named after the forward.
        Assert.DoesNotContain("public sealed class RestrictedBy : global::Surface.Runtime.IRelationKind", allSrc);
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
        Assert.Contains("global::Surface.Runtime.ISurrealTransport transport", src);
        Assert.Contains("global::M.DesignId rootId", src);
        // No ctor, no fields — minimal-intrusion contract.
        Assert.DoesNotContain("public Workspace(", src);
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
    public void NoCompositionRoot_SuppressesLoadMethodEmission()
    {
        var src = """
            using Surface.Annotations;
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
        // through IEntity.Bind / explicit-impl Session.
        Assert.Contains("void global::Surface.Runtime.IEntity.Bind(global::Surface.Runtime.SurrealSession session) => _session = session;", allSrc);
        Assert.Contains("private global::Surface.Runtime.SurrealSession? _session;", allSrc);
        Assert.DoesNotContain(".Current", allSrc);
    }

    [Fact]
    public void RelationCollectionRead_UsesTypedKind()
    {
        var (result, _, _, _) = GeneratorHarness.Run(MinimalModel);
        var allSrc = GeneratorHarness.AllGeneratedSource(result);

        // Restrictions on Constraint should read via QueryRelated<Restricts, IEntity>.
        Assert.Contains("Session.QueryRelated<global::M.Restricts, global::Surface.Runtime.IEntity>(this)", allSrc);
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
}
