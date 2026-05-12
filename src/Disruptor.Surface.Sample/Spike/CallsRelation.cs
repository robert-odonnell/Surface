using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Spike;

[Calls]
public partial class CallsRelation : ICodeSymbolEdge
{
    [In]       public partial CodeSymbolId Source { get; set; }
    [Out]      public partial CodeSymbolId Target { get; set; }
    [Property] public partial string Confidence { get; set; }
}
