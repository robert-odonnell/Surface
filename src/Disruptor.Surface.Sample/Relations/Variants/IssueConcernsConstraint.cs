using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Concerns]
public partial class IssueConcernsConstraint
{
    [In] public partial IssueId Source { get; set; }
    [Out] public partial ConstraintId Target { get; set; }
}
