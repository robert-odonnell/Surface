using Surface.Annotations;
using Surface.Sample.Relations;
using Surface.Runtime;

namespace Surface.Sample.Models;

[Table]
public partial class Issue
{
    [Id] public partial IssueId Id { get; set; }

    [Reference, Cascade] public partial Details? Details { get; set; }

    [Parent] public partial Review Review { get; set; }

    [Property] public partial string Severity { get; set; }
    [Property] public partial string Disposition { get; set; }
    [Property] public partial string DispositionReason { get; set; }

    [InformedBy] public partial IReadOnlyCollection<Finding> InformingFindings { get; }
    [ResolvedBy] public partial IReadOnlyCollection<DesignChange> Resolutions { get; }

    [Concerns] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
}