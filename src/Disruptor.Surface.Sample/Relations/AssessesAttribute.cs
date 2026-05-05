using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

public sealed class AssessesAttribute : ForwardRelation;
public sealed class AssessedByAttribute : InverseRelation<AssessesAttribute>;