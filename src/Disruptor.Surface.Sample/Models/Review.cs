using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Relations;
using Disruptor.Surface.Runtime;

namespace Disruptor.Surface.Sample.Models;

[Table]
[AggregateRoot]
public partial class Review
{
    [Id] public partial ReviewId Id { get; set; }

    [Reference, Cascade, Inline] public partial Details? Details { get; set; }

    [Property] public partial string Outcome { get; set; }
    [Property] public partial string Mode { get; set; }
    [Property] public partial string State { get; set; }

    [Children] public partial IReadOnlyCollection<Finding> Findings { get; }
    [Children] public partial IReadOnlyCollection<Observation> Observations { get; }
    [Children] public partial IReadOnlyCollection<Issue> Issues { get; }
    [Children] public partial IReadOnlyCollection<DesignChange> DesignChanges { get; }

    [Assesses] public partial IReadOnlyCollection<IRecordId> Assessments { get; }
}
