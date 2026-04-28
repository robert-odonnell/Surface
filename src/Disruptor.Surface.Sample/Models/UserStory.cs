using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Relations;
using Disruptor.Surface.Runtime;

namespace Disruptor.Surface.Sample.Models;

[Table]
public partial class UserStory
{
    [Id] public partial UserStoryId Id { get; set; }

    [Reference, Cascade, Inline] public partial Details? Details { get; set; }
    [Reference, Unset] public partial Design? Design { get; set; }

    [Parent] public partial Feature Feature { get; set; }

    [Property] public partial string AsA { get; set; }
    [Property] public partial string IWant { get; set; }
    [Property] public partial string SoThat { get; set; }

    [Children] public partial IReadOnlyCollection<AcceptanceCriteria> AcceptanceCriteria { get; }
    [Children] public partial IReadOnlyCollection<Test> Tests { get; }

    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }
    [ReferencedBy] public partial IReadOnlyCollection<IRecordId> References { get; }
    [ConcernedBy] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
    [RevisedBy] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
}
