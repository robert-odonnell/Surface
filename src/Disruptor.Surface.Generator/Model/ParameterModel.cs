namespace Disruptor.Surface.Generator.Model;

public sealed record ParameterModel(
    string Name,
    TypeRef Type,
    string RefKind,
    bool HasDefaultValue);
