using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Informs]
public partial class FindingInformsIssue
{
    [In] public partial Finding Source { get; set; }
    [Out] public partial Issue Target { get; set; }
}
