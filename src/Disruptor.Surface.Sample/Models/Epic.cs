using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Relations;
using Disruptor.Surface.Runtime;

namespace Disruptor.Surface.Sample.Models;

[Table]
public partial class Epic
{
    [Reference, Cascade, Inline] public partial Details? Details { get; set; }

    [Parent] public partial Design Design { get; set; }

    [Property] public partial string Description { get; set; }

    [Children] public partial IReadOnlyCollection<Feature> Features { get; }

    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }
 
    [ReferencedBy] public partial IReadOnlyCollection<IRecordId> References { get; }
    [ConcernedBy] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
    [RevisedBy] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
}