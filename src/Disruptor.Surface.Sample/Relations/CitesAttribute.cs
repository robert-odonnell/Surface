using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

public sealed class CitesAttribute : ForwardRelation;
public sealed class CitedByAttribute : InverseRelation<CitesAttribute>;