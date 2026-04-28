using Surface.Annotations;
using Surface.Sample.Relations;
using Surface.Runtime;

namespace Surface.Sample.Models;

public sealed record Scenario(string Kind, string Description);

[Table]
public partial class AcceptanceCriteria
{
    [Id] public partial AcceptanceCriteriaId Id { get; set; }

    [Reference, Cascade] public partial Details? Details { get; set; }
    [Reference, Unset] public partial Design? Design { get; set; }

    [Parent] public partial UserStory UserStory { get; set; }

    [Property] public partial SurrealArray<Scenario> Scenarios { get; }

    [FulfilledBy] public partial IReadOnlyCollection<Test> Validations { get; }
    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }
 
    [ReferencedBy] public partial IReadOnlyCollection<IRecordId> References { get; }
    [ConcernedBy] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
    [RevisedBy] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
}