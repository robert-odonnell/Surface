using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

public sealed class ResolvesAttribute : ForwardRelation;
public sealed class ResolvedByAttribute : InverseRelation<ResolvesAttribute>;

