using Surface.Annotations;
using Surface.Sample.Relations;
using Surface.Runtime;

namespace Surface.Sample.Models;

[Table]
public partial class Observation
{
    [Id] public partial ObservationId Id { get; set; }

    [Reference, Cascade, Inline] public partial Details? Details { get; set; }

    [Parent] public partial Review Review { get; set; }

    [Property] public partial string Kind { get; set; }
    [Property] public partial string Description { get; set; }
    [Property] public partial string Excerpt { get; set; }
    [Property] public partial string Confidence { get; set; }

    [CitedBy] public partial IReadOnlyCollection<Finding> Citations { get; }

    [References] public partial IReadOnlyCollection<IRecordId> References { get; }
}
