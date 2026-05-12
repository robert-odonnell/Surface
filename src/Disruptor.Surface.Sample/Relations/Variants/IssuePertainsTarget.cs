using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

// preview.54 union endpoint demo. A single variant covers every member of the
// IPertainsTarget union — Constraint, UserStory, … — instead of duplicating the
// variant class per target type. The endpoint setter accepts any participating
// typed id (`new IssuePertainsTarget { Source = issueId, Target = constraintId }`),
// and the hydration dispatcher's (in.tb, out.tb) switch enumerates every member
// table so a row pointing at any of them lands on this variant.
[Pertains]
public partial class IssuePertainsTarget
{
    [In] public partial IssueId Source { get; set; }
    [Out] public partial IPertainsTarget Target { get; set; }
}
