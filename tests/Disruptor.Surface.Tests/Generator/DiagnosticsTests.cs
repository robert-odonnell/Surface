using Microsoft.CodeAnalysis;
using Xunit;

namespace Disruptor.Surface.Tests.Generator;

/// <summary>
/// Each test feeds a tiny fixture that violates one rule and asserts that the
/// corresponding CGxxx diagnostic fires. Catches accidental tightening / loosening
/// of the diagnostics surface from generator refactors.
/// </summary>
public sealed class DiagnosticsTests
{
    [Fact]
    public void CG001_TableMustBePartial()
    {
        var src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public class NotPartial {
                [Id] public partial NotPartialId Id { get; set; }
            }
            """;
        var (_, _, _, compileDiags) = GeneratorHarness.Run(src);
        // CG001 reports against `compileDiags` (Errors), not run-time diagnostics.
        // Actually wait — CG001 fires from ModelGenerator.Emit, so it shows up in run diagnostics.
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG001");
    }

    [Fact]
    public void NoIdAttribute_IsLegal_AnchorIsAlwaysEmitted()
    {
        // [Id] is optional — without it, the entity has no public-facing Id surface, but
        // the runtime still works because PartialEmitter unconditionally emits the _id
        // anchor and IEntity.Id, and IdEmitter unconditionally emits the {Name}Id struct.
        // CG007 was retired alongside this change.
        var src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class NoId {
                [Property] public partial string Name { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id == "CG007");
        Assert.Empty(runDiags.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void CG008_TableHasMultipleIds()
    {
        var src = """
            using Disruptor.Surface.Annotations;
            namespace M;
            [Table] public partial class TwoIds {
                [Id] public partial TwoIdsId IdA { get; set; }
                [Id] public partial TwoIdsId IdB { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG008");
    }

    [Fact]
    public void CG011_EntityReachable_FromTwoAggregateRoots()
    {
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class A {
                [Id] public partial AId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Shared> Items { get; }
            }

            [Table, AggregateRoot] public partial class B {
                [Id] public partial BId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Shared> Items { get; }
            }

            [Table] public partial class Shared {
                [Id] public partial SharedId Id { get; set; }
                [Parent] public partial A ParentA { get; set; }
                [Parent] public partial B ParentB { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG011");
    }

    [Fact]
    public void CG018_MultipleCompositionRoots()
    {
        var src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [CompositionRoot] public partial class WorkspaceA { }
            [CompositionRoot] public partial class WorkspaceB { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG018");
    }

    [Fact]
    public void CG019_CompositionRoot_NotPartial()
    {
        var src = """
            using Disruptor.Surface.Annotations;
            namespace M;

            [CompositionRoot] public class NotPartialWorkspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG019");
    }

    [Fact]
    public void CG020_Children_Without_MatchingParent_Path()
    {
        // `Lonely` is listed as Design's Children member but has no [Parent] back to
        // Design — the loader's $parent.id-scoped query has nothing to anchor on.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Lonely> Lonelies { get; }
            }

            [Table] public partial class Lonely {
                [Id] public partial LonelyId Id { get; set; }
                // No [Parent] property pointing at Design.
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG020");
    }

    [Fact]
    public void CG021_Reference_CrossesAggregateBoundary()
    {
        // Constraint is in the Design aggregate (via [Children]); Finding is in the
        // Review aggregate. A [Reference] from Constraint to Finding crosses
        // aggregate boundaries — should fire CG021.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
            }

            [Table, AggregateRoot] public partial class Review {
                [Id] public partial ReviewId Id { get; set; }
                [Children] public partial IReadOnlyCollection<Finding> Findings { get; }
            }

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
                [Parent] public partial Design Design { get; set; }
                [Reference] public partial Finding? OffendingFinding { get; set; }   // crosses aggregate
            }

            [Table] public partial class Finding {
                [Id] public partial FindingId Id { get; set; }
                [Parent] public partial Review Review { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG021");
    }

    [Fact]
    public void CG021_Allows_Reference_ToSharedRecord_NotInAnyAggregate()
    {
        // Details is referenced from Design but isn't a member of any aggregate
        // (no [Parent] back to a root). Shared/foreign records like this are fine.
        var src = """
            using Disruptor.Surface.Annotations;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Reference] public partial Details? Details { get; set; }
            }

            [Table] public partial class Details {
                [Id] public partial DetailsId Id { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.DoesNotContain(runDiags, d => d.Id == "CG021");
    }

    [Fact]
    public void HappyPath_ProducesNoDiagnostics()
    {
        var src = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Property] public partial string Description { get; set; }
                [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
            }

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
                [Parent] public partial Design Design { get; set; }
                [Property] public partial string Description { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        // No CGxxx errors. (Warnings — like CG017 — are allowed; the model has none here.)
        Assert.DoesNotContain(runDiags, d => d.Severity == DiagnosticSeverity.Error);
    }
}
