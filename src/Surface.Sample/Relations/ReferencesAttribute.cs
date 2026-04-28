using Surface.Annotations;

namespace Surface.Sample.Relations;

public sealed class ReferencesAttribute : ForwardRelation;
public sealed class ReferencedByAttribute : InverseRelation<ReferencesAttribute>;