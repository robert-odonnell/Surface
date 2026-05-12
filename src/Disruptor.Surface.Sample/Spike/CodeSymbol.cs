using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Spike;

// Each CodeSymbol is its own [AggregateRoot]; edges between two of them cross
// aggregates and so use typed-id endpoints (see CallsRelation / ReferencesRelation).
[Table, AggregateRoot]
public partial class CodeSymbol
{
    [Id]       public partial CodeSymbolId Id { get; set; }
    [Property] public partial string Fqn { get; set; }
}
