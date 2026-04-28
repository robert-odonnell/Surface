using Surface.Annotations;

namespace Surface.Sample.Relations;

public sealed class CitesAttribute : ForwardRelation;
public sealed class CitedByAttribute : InverseRelation<CitesAttribute>;