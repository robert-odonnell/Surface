using Surface.Annotations;

namespace Surface.Sample.Relations;

public sealed class RevisesAttribute : ForwardRelation;
public sealed class RevisedByAttribute : InverseRelation<RevisesAttribute>;
