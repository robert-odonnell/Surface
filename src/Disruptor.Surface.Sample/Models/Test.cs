using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Relations;
using Disruptor.Surface.Runtime;

namespace Disruptor.Surface.Sample.Models;

public sealed record Fact(string Kind, string Arrange, string Act, string Assert);

[Table]
public partial class Test
{
    [Id] public partial TestId Id { get; set; }

    [Reference, Cascade, Inline] public partial Details? Details { get; set; }
    [Reference, Unset] public partial Design? Design { get; set; }

    [Parent] public partial UserStory UserStory { get; set; }

    [Property] public partial SurrealArray<Fact> Facts { get; }

    [Validates] public partial IReadOnlyCollection<AcceptanceCriteria> Validations { get; }

    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }
    [ReferencedBy] public partial IReadOnlyCollection<IRecordId> References { get; }
    [ConcernedBy] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
    [RevisedBy] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
}