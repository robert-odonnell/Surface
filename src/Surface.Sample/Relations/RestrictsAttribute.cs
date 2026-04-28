using Surface.Annotations;

namespace Surface.Sample.Relations;

public sealed class RestrictsAttribute : ForwardRelation;
public sealed class RestrictedByAttribute : InverseRelation<RestrictsAttribute>;