using Disruptor.Surface.Annotations;

namespace Disruptor.Surface.Sample.Relations;

public sealed class ValidatesAttribute : ForwardRelation;
public sealed class FulfilledByAttribute : InverseRelation<ValidatesAttribute>;