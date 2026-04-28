using Surface.Annotations;

namespace Surface.Sample.Relations;

public sealed class ValidatesAttribute : ForwardRelation;
public sealed class FulfilledByAttribute : InverseRelation<ValidatesAttribute>;