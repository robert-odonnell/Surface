using Surface.Annotations;

namespace Surface.Sample.Relations;

public sealed class InformsAttribute : ForwardRelation;
public sealed class InformedByAttribute : InverseRelation<InformsAttribute>;