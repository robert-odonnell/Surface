namespace Surface.Generator.Model;

/// <summary>
/// The leading verb of a relation/children method, validated against the convention
/// documented by the user: every partial method must start with one of these.
/// </summary>
public enum MethodVerb
{
    Unknown,
    Add,
    Remove,
    Clear,
    Set,
    List,
    Get,
}

/// <summary>
/// Which kind of annotation pulled this method (or relation-bearing property) into the model.
/// <c>[Children]</c> lives on properties only, so it does not appear here.
/// </summary>
public enum MethodRole
{
    None,
    ForwardRelation,
    InverseRelation,
}

/// <summary>
/// A method on a <c>[Table]</c> type that participates in the generated model.
/// <para>
/// <see cref="RelationKindFullName"/> is the fully-qualified name of the attribute class
/// that decorated this method (e.g. <c>Disruptor.Sample.RestrictsAttribute</c>). During
/// pipeline linking this name is resolved against <see cref="RelationKindModel"/> to pair
/// forward/inverse methods across tables.
/// </para>
/// </summary>
public sealed record MethodModel(
    string Name,
    MethodVerb Verb,
    MethodRole Role,
    PropertyKind Kinds,
    string? RelationKindFullName,
    TypeRef ReturnType,
    EquatableArray<ParameterModel> Parameters,
    EquatableArray<string> TypeParameters,
    bool IsPartial,
    bool IsStatic,
    bool ReturnsVoid,
    string DeclaredAccessibility);
