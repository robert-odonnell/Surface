using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Restricts]
public partial class ConstraintRestrictsTest
{
    [In] public partial Constraint Source { get; set; }
    [Out] public partial Test Target { get; set; }
}
