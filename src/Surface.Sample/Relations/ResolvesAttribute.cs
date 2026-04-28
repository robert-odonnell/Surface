using Surface.Annotations;

namespace Surface.Sample.Relations;

public sealed class ResolvesAttribute : ForwardRelation;
public sealed class ResolvedByAttribute : InverseRelation<ResolvesAttribute>;

