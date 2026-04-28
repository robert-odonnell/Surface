using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

public sealed class ReferencesAttribute : ForwardRelation;
public sealed class ReferencedByAttribute : InverseRelation<ReferencesAttribute>;