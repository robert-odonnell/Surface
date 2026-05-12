using Disruptor.Surface.Annotations;
using Disruptor.Surface.Runtime;

namespace Disruptor.Surface.Sample.Spike;

public sealed class CallsAttribute      : ForwardRelation;
public sealed class ReferencesAttribute : ForwardRelation;

// Shared-shape contract: any variant whose endpoints are CodeSymbolId and whose
// payload matches receives a generator-emitted `static ICodeSymbolEdge Create<TKind>`
// factory. `partial` is required so the generator can graft the static method on.
public partial interface ICodeSymbolEdge : IRelationVariant
{
    CodeSymbolId Source { get; set; }
    CodeSymbolId Target { get; set; }
    string Confidence { get; set; }
}
