using Disruptor.Surface.Annotations;
using Disruptor.Surface.Runtime;

namespace Disruptor.Surface.Sample.Spike;

// Two relation kinds that share an endpoint+payload shape — exactly the pattern
// the spike conversation predicted would benefit from a shared interface contract.
public sealed class CallsAttribute      : ForwardRelation;
public sealed class ReferencesAttribute : ForwardRelation;

// Shared contract over every variant whose endpoints are CodeSymbolId and whose
// payload is a single Confidence string. Extends IRelationVariant so the generator
// recognises the interface and emits a static Create<TKind> factory fragment onto
// it; `partial` is required to graft the static method.
//
// The partial Source / Target / Confidence properties emitted by the generator for
// each variant satisfy this contract without any further help — partial properties
// with matching accessor shape just are interface impls.
public partial interface ICodeSymbolEdge : IRelationVariant
{
    CodeSymbolId Source { get; set; }
    CodeSymbolId Target { get; set; }
    string Confidence { get; set; }
}
