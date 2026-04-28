using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

public sealed class ConcernsAttribute : ForwardRelation;
public sealed class ConcernedByAttribute : InverseRelation<ConcernsAttribute>;