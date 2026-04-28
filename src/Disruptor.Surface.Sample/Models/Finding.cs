using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Relations;

namespace Disruptor.Surface.Sample.Models;

[Table]
public partial class Finding
{
    [Reference, Cascade, Inline] public partial Details? Details { get; set; }

    [Parent] public partial Review Review { get; set; }

    [Property] public partial string Kind { get; set; }
    [Property] public partial string Recommendation { get; set; }

    [Informs] public partial IReadOnlyCollection<Issue> InformedIssues { get; }
    [Cites] public partial IReadOnlyCollection<Observation> Citations { get; }
}