using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[References]
public partial class ObservationReferencesDesign
{
    [In] public partial ObservationId Source { get; set; }
    [Out] public partial DesignId Target { get; set; }
}
