using Surface.Annotations;
using Surface.Sample.Relations;
using Surface.Runtime;

namespace Surface.Sample.Models;

[Table]
public partial class Epic
{
    [Id] public partial EpicId Id { get; set; }

    [Reference, Cascade] public partial Details? Details { get; set; }

    [Parent] public partial Design Design { get; set; }

    [Property] public partial string Description { get; set; }

    [Children] public partial IReadOnlyCollection<Feature> Features { get; }

    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }
 
    [ReferencedBy] public partial IReadOnlyCollection<IRecordId> References { get; }
    [ConcernedBy] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
    [RevisedBy] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
}