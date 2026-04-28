using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

public sealed class InformsAttribute : ForwardRelation;
public sealed class InformedByAttribute : InverseRelation<InformsAttribute>;