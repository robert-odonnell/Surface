using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Restricts]
public partial class ConstraintRestrictsAcceptanceCriteria
{
    [In] public partial Constraint Source { get; set; }
    [Out] public partial AcceptanceCriteria Target { get; set; }
}
