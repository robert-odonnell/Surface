using Surface.Annotations;

namespace Surface.Sample.Relations;

public sealed class ConcernsAttribute : ForwardRelation;
public sealed class ConcernedByAttribute : InverseRelation<ConcernsAttribute>;