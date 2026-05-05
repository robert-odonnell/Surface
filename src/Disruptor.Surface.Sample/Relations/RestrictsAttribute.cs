using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

public sealed class RestrictsAttribute : ForwardRelation;
public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;