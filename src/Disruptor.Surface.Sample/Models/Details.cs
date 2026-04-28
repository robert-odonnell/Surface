using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Models;

[Table]
public partial class Details
{
    [Id] public partial DetailsId Id { get; set; }

    [Property] public partial string Header { get; set; }
    [Property] public partial string Summary { get; set; }
    [Property] public partial string Text { get; set; }
}
