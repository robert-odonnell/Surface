using Disruptor.Surface.Annotations;
using Disruptor.Surface.Runtime;

namespace Disruptor.Surface.Sample.Spike;

public sealed class InheritsAttribute : ForwardRelation;

// preview.56 — annotated shared-shape interface. The interface members carry
// [In]/[Out]/[Property], so any variant whose body is collapsed to ";" lifts those
// declarations into its own emitted IEntity scaffolding. The user trades the per-
// variant boilerplate for a single contract declaration on the interface.
public partial interface ICodeSymbolInheritsRelation : IRelationVariant
{
    [In]       CodeSymbolId Source { get; set; }
    [Out]      CodeSymbolId Target { get; set; }
    [Property] string Confidence { get; set; }
}

// Empty body — the linker lifts In/Out/Property from the interface above. Compare
// CallsRelation / ReferencesRelation which keep the per-variant declarations even
// though their shape is identical; preview.56 is opt-in, the existing self-describing
// shape stays valid.
[Inherits]
public partial class CodeSymbolInheritsCodeSymbol : ICodeSymbolInheritsRelation;
