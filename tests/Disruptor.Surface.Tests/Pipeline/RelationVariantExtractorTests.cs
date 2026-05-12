using Disruptor.Surface.Generator.Model;
using Disruptor.Surface.Generator.Pipeline;
using Disruptor.Surface.Tests.Generator;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Disruptor.Surface.Tests.Pipeline;

/// <summary>
/// Unit tests for <see cref="RelationVariantExtractor"/>. The extractor surfaces a
/// <see cref="RelationVariantModel"/> for each user-declared class annotated with a
/// <c>ForwardRelation</c>- or <c>InverseRelation&lt;T&gt;</c>-derived attribute applied
/// to the class itself. Phase 1b: extraction only — no consumer wires it into emit yet,
/// so these tests poke <c>TryExtractFromSymbol</c> directly via a synthesised
/// <see cref="INamedTypeSymbol"/>.
/// </summary>
public sealed class RelationVariantExtractorTests
{
    /// <summary>
    /// Common preamble — declares the forward/inverse attribute pair plus a couple of
    /// lightweight entity types so the [In]/[Out] property declarations in fixtures bind.
    /// </summary>
    private const string Preamble = """
        using Disruptor.Surface.Annotations;
        using System.Collections.Generic;
        namespace M;

        public sealed class RestrictsAttribute : ForwardRelation;
        public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;

        [Table] public partial class Constraint {
            [Id] public partial ConstraintId Id { get; set; }
        }
        [Table] public partial class UserStory {
            [Id] public partial UserStoryId Id { get; set; }
        }
        """;

    [Fact]
    public void Forward_Variant_With_In_Out_And_Optional_Id_And_Property_Builds_Model()
    {
        const string fixture = Preamble + """

            [Restricts]
            public partial class ConstraintRestrictsUserStory {
                [Id] public partial RestrictsId Id { get; set; }
                [In] public partial Constraint Source { get; set; }
                [Out] public partial UserStory Target { get; set; }
                [Property] public partial string Reason { get; set; }
                [Property] public partial int Weight { get; set; }
            }
            """;

        var compilation = GeneratorHarness.CreateCompilation(fixture);
        var cls = compilation.GetTypeByMetadataName("M.ConstraintRestrictsUserStory");
        Assert.NotNull(cls);

        var model = RelationVariantExtractor.TryExtractFromSymbol(cls!, CancellationToken.None);
        Assert.NotNull(model);

        Assert.Equal("M.ConstraintRestrictsUserStory", model!.FullName);
        Assert.Equal("M", model.Namespace);
        Assert.Equal("ConstraintRestrictsUserStory", model.Name);
        Assert.True(model.IsPartial);
        Assert.Equal("M.RestrictsAttribute", model.KindAttributeFqn);

        // Endpoints land in In/Out slots regardless of declaration order; the [Property]
        // members fall into PayloadProperties; [Id] lands in Id.
        Assert.NotNull(model.Id);
        Assert.Equal("Id", model.Id!.Name);
        Assert.Equal(RelationVariantPropertyRole.Id, model.Id.Role);

        Assert.NotNull(model.In);
        Assert.Equal("Source", model.In!.Name);
        Assert.Equal("source", model.In.FieldName);
        Assert.Equal(RelationVariantPropertyRole.In, model.In.Role);

        Assert.NotNull(model.Out);
        Assert.Equal("Target", model.Out!.Name);
        Assert.Equal("target", model.Out.FieldName);
        Assert.Equal(RelationVariantPropertyRole.Out, model.Out.Role);

        Assert.Equal(2, model.PayloadProperties.Count);
        Assert.Contains(model.PayloadProperties, p => p.Name == "Reason" && p.FieldName == "reason");
        Assert.Contains(model.PayloadProperties, p => p.Name == "Weight" && p.FieldName == "weight");
        foreach (var p in model.PayloadProperties)
        {
            Assert.Equal(RelationVariantPropertyRole.Property, p.Role);
        }
    }

    [Fact]
    public void Inverse_Variant_Builds_Model_Just_Like_Forward()
    {
        // Direction (forward vs inverse) is encoded in KindAttributeFqn; the extractor
        // doesn't branch on it. RestrictedBy : InverseRelation<RestrictsAttribute> on the
        // class should produce a variant model just the same.
        const string fixture = Preamble + """

            [RestrictedBy]
            public partial class UserStoryRestrictedByConstraint {
                [In] public partial UserStory Source { get; set; }
                [Out] public partial Constraint Target { get; set; }
            }
            """;

        var compilation = GeneratorHarness.CreateCompilation(fixture);
        var cls = compilation.GetTypeByMetadataName("M.UserStoryRestrictedByConstraint");
        Assert.NotNull(cls);

        var model = RelationVariantExtractor.TryExtractFromSymbol(cls!, CancellationToken.None);
        Assert.NotNull(model);
        Assert.Equal("M.RestrictedByAttribute", model!.KindAttributeFqn);
        Assert.Equal("Source", model.In!.Name);
        Assert.Equal("Target", model.Out!.Name);
        Assert.Null(model.Id);
        Assert.Empty(model.PayloadProperties);
    }

    [Fact]
    public void Missing_In_Returns_Null()
    {
        const string fixture = Preamble + """

            [Restricts]
            public partial class MissingIn {
                [Out] public partial UserStory Target { get; set; }
            }
            """;

        var compilation = GeneratorHarness.CreateCompilation(fixture);
        var cls = compilation.GetTypeByMetadataName("M.MissingIn");
        Assert.NotNull(cls);

        Assert.Null(RelationVariantExtractor.TryExtractFromSymbol(cls!, CancellationToken.None));
    }

    [Fact]
    public void Missing_Out_Returns_Null()
    {
        const string fixture = Preamble + """

            [Restricts]
            public partial class MissingOut {
                [In] public partial Constraint Source { get; set; }
            }
            """;

        var compilation = GeneratorHarness.CreateCompilation(fixture);
        var cls = compilation.GetTypeByMetadataName("M.MissingOut");
        Assert.NotNull(cls);

        Assert.Null(RelationVariantExtractor.TryExtractFromSymbol(cls!, CancellationToken.None));
    }

    [Fact]
    public void Multiple_In_Returns_Null()
    {
        const string fixture = Preamble + """

            [Restricts]
            public partial class TwoIns {
                [In] public partial Constraint SourceA { get; set; }
                [In] public partial Constraint SourceB { get; set; }
                [Out] public partial UserStory Target { get; set; }
            }
            """;

        var compilation = GeneratorHarness.CreateCompilation(fixture);
        var cls = compilation.GetTypeByMetadataName("M.TwoIns");
        Assert.NotNull(cls);

        Assert.Null(RelationVariantExtractor.TryExtractFromSymbol(cls!, CancellationToken.None));
    }

    [Fact]
    public void Multiple_Out_Returns_Null()
    {
        const string fixture = Preamble + """

            [Restricts]
            public partial class TwoOuts {
                [In] public partial Constraint Source { get; set; }
                [Out] public partial UserStory TargetA { get; set; }
                [Out] public partial UserStory TargetB { get; set; }
            }
            """;

        var compilation = GeneratorHarness.CreateCompilation(fixture);
        var cls = compilation.GetTypeByMetadataName("M.TwoOuts");
        Assert.NotNull(cls);

        Assert.Null(RelationVariantExtractor.TryExtractFromSymbol(cls!, CancellationToken.None));
    }

    [Fact]
    public void Class_With_Unrelated_Attribute_Returns_Null()
    {
        // The class carries [Table] (unrelated to the relation system); no
        // ForwardRelation / InverseRelation attribute applied to the class itself, so
        // the extractor short-circuits before walking members.
        const string fixture = Preamble + """

            [Table] public partial class JustATable {
                [Id] public partial JustATableId Id { get; set; }
                [In] public partial Constraint Source { get; set; }
                [Out] public partial UserStory Target { get; set; }
            }
            """;

        var compilation = GeneratorHarness.CreateCompilation(fixture);
        var cls = compilation.GetTypeByMetadataName("M.JustATable");
        Assert.NotNull(cls);

        Assert.Null(RelationVariantExtractor.TryExtractFromSymbol(cls!, CancellationToken.None));
    }
}
