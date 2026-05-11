using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Assesses]
public partial class ReviewAssessesDesign
{
    [In] public partial ReviewId Source { get; set; }
    [Out] public partial DesignId Target { get; set; }
}
