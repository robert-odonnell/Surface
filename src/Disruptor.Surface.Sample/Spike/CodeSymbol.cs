using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Spike;

// SPIKE — shared-shape interface across relation variants. Not committed; here to
// explore whether the partial properties we already emit satisfy a user-declared
// interface contract without generator changes.
//
// Each CodeSymbol is its own [AggregateRoot] so edges between two CodeSymbols
// cross aggregates — modelled with typed-id endpoints on the variants, which
// matches the code-indexer scenario from the spike conversation.
[Table, AggregateRoot]
public partial class CodeSymbol
{
    [Id]       public partial CodeSymbolId Id { get; set; }
    [Property] public partial string Fqn { get; set; }
}
