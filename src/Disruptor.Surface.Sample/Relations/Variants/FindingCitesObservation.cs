using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Cites]
public partial class FindingCitesObservation
{
    [In] public partial Finding Source { get; set; }
    [Out] public partial Observation Target { get; set; }
}
