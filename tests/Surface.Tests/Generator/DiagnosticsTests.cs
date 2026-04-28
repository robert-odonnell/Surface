using Microsoft.CodeAnalysis;
using Xunit;

namespace Surface.Tests.Generator;

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
            using Surface.Annotations;
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
    public void CG007_TableMissingId()
    {
        var src = """
            using Surface.Annotations;
            namespace M;
            [Table] public partial class NoId {
                [Property] public partial string Name { get; set; }
            }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG007");
    }

    [Fact]
    public void CG008_TableHasMultipleIds()
    {
        var src = """
            using Surface.Annotations;
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
            using Surface.Annotations;
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
            using Surface.Annotations;
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
            using Surface.Annotations;
            namespace M;

            [CompositionRoot] public class NotPartialWorkspace { }
            """;
        var (_, _, runDiags, _) = GeneratorHarness.Run(src);
        Assert.Contains(runDiags, d => d.Id == "CG019");
    }

    [Fact]
    public void HappyPath_ProducesNoDiagnostics()
    {
        var src = """
            using Surface.Annotations;
            using Surface.Runtime;
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
