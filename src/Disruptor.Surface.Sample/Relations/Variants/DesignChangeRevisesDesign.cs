using Disruptor.Surface.Annotations;
using Disruptor.Surface.Sample.Models;

namespace Disruptor.Surface.Sample.Relations.Variants;

[Revises]
public partial class DesignChangeRevisesDesign
{
    [In] public partial DesignChangeId Source { get; set; }
    [Out] public partial DesignId Target { get; set; }
}
