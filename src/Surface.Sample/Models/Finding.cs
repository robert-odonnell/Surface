using Surface.Annotations;
using Surface.Sample.Relations;

namespace Surface.Sample.Models;

[Table]
public partial class Finding
{
    [Id] public partial FindingId Id { get; set; }

    [Reference, Cascade] public partial Details? Details { get; set; }

    [Parent] public partial Review Review { get; set; }

    [Property] public partial string Kind { get; set; }
    [Property] public partial string Recommendation { get; set; }

    [Informs] public partial IReadOnlyCollection<Issue> InformedIssues { get; }
    [Cites] public partial IReadOnlyCollection<Observation> Citations { get; }
}