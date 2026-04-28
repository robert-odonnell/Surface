namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// Minimal, equatable description of a type use-site. Captures just enough to drive
/// code generation and relation resolution without holding on to <c>ISymbol</c> (which
/// would break incremental caching).
/// </summary>
/// <param name="DisplayName">Source-ready C# representation, e.g. <c>IReadOnlyCollection&lt;Constraint&gt;</c>.</param>
/// <param name="FullyQualifiedName">Metadata-style name without type arguments, e.g. <c>global::Disruptor.Surface.Sample.Constraint</c>.</param>
/// <param name="MetadataName">Ecma-335 metadata name, e.g. <c>IReadOnlyCollection`1</c> — what <c>ForAttributeWithMetadataName</c> keys on.</param>
/// <param name="IsNullable">True when the annotated reference type is <c>T?</c>.</param>
/// <param name="IsTypeParameter">True when this references a method/type generic parameter rather than a concrete type.</param>
/// <param name="IsTableType">True when the underlying type carries <c>[Table]</c> (resolved during linking, not during extraction).</param>
/// <param name="IsCollection">True when the type is a well-known read-only collection (<c>IReadOnlyCollection&lt;T&gt;</c>, etc.).</param>
/// <param name="ElementType">For collection types, the element <see cref="TypeRef"/>; otherwise null.</param>
/// <param name="TypeArguments">All type arguments in source order — empty for non-generic types.</param>
public sealed record TypeRef(
    string DisplayName,
    string FullyQualifiedName,
    string MetadataName,
    bool IsNullable,
    bool IsTypeParameter,
    bool IsTableType,
    bool IsCollection,
    TypeRef? ElementType,
    EquatableArray<TypeRef> TypeArguments);
