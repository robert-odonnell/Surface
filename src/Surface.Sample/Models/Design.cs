using Surface.Annotations;
using Surface.Sample.Relations;
using Surface.Runtime;

namespace Surface.Sample.Models;

[Table]
[AggregateRoot]
public partial class Design
{
    [Id] public partial DesignId Id { get; set; }

    [Reference, Cascade, Inline] public partial Details? Details { get; set; }

    [Property] public partial string RepositoryRoot { get; set; }
    [Property] public partial string Description { get; set; }

    [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
    [Children] public partial IReadOnlyCollection<Epic> Epics { get; }

    [RestrictedBy] public partial IReadOnlyCollection<Constraint> Restrictions { get; }

    [ReferencedBy] public partial IReadOnlyCollection<IRecordId> References { get; }
    [ConcernedBy] public partial IReadOnlyCollection<IRecordId> Concerns { get; }
    [RevisedBy] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
    [AssessedBy] public partial IReadOnlyCollection<IRecordId> Assessments { get; }
}