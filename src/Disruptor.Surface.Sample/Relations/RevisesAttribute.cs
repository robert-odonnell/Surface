using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

public sealed class RevisesAttribute : ForwardRelation;
public sealed class RevisedByAttribute : InverseRelation<RevisesAttribute>;
