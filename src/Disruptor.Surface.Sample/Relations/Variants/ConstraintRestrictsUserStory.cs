using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Restricts]
public partial class ConstraintRestrictsUserStory
{
    [In] public partial Constraint Source { get; set; }
    [Out] public partial UserStory Target { get; set; }
}
