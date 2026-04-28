using Surface.Annotations;

namespace Surface.Sample.Relations;

public sealed class AssessesAttribute : ForwardRelation;
public sealed class AssessedByAttribute : InverseRelation<AssessesAttribute>;