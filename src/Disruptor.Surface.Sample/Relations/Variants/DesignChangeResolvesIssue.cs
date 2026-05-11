using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Resolves]
public partial class DesignChangeResolvesIssue
{
    [In] public partial DesignChange Source { get; set; }
    [Out] public partial Issue Target { get; set; }
}
