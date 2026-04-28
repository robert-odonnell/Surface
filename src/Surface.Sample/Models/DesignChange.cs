using Surface.Annotations;
using Surface.Sample.Relations;
using Surface.Runtime;

namespace Surface.Sample.Models;

[Table]
public partial class DesignChange
{
    [Id] public partial DesignChangeId Id { get; set; }

    [Reference, Cascade, Inline] public partial Details? Details { get; set; }

    [Parent] public partial Review Review { get; set; }

    [Resolves] public partial IReadOnlyCollection<Issue> Resolutions { get; }

    [Revises] public partial IReadOnlyCollection<IRecordId> Revisions { get; }
}