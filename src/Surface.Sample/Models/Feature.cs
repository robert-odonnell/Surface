using Surface.Annotations;
using Surface.Sample.Relations;
using Surface.Runtime;

namespace Surface.Sample.Models;

[Table]
public partial class Feature
{
    [Id] public partial FeatureId Id { get; set; }

    [Reference, Cascade] public partial Details? Details { get; set; }
    [Reference, Unset] public partial Design? Design { get; set; }

    [Parent] public partial Epic Epic { get; set; }

    [Property] public partial string Description { get; set; }

    [Children] public partial IReadOnlyCollection<UserStory> UserStories { get; }

    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }

    [ReferencedBy] public partial IReadOnlyCollection<IRecordId> References { get; }
    [ConcernedBy] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
    [RevisedBy] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
}