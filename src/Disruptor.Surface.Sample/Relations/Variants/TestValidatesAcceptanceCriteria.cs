using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Validates]
public partial class TestValidatesAcceptanceCriteria
{
    [In] public partial Test Source { get; set; }
    [Out] public partial AcceptanceCriteria Target { get; set; }
}
