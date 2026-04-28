using System.Collections.Immutable;
using Surface.Generator.Annotations;
using Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Surface.Generator.Pipeline;

/// <summary>
/// Lowers a Roslyn <see cref="ITypeSymbol"/> into an equatable <see cref="TypeRef"/>.
/// </summary>
/// <remarks>
/// Intentionally does not read the <c>[Table]</c> attribute directly. At extraction time we
/// don't yet have the full set of tables in hand, so <see cref="TypeRef.IsTableType"/> is
/// seeded from what we can see on this symbol and later rewritten by
/// <see cref="RelationLinker"/> once the whole graph has been collected.
/// </remarks>
internal static class TypeRefBuilder
{
    /// <summary>
    /// Fully-qualified format that preserves <c>?</c> on nullable reference types.
    /// The default <see cref="SymbolDisplayFormat.FullyQualifiedFormat"/> drops the
    /// nullability modifier, which would cause partial-property implementations to
    /// mismatch their declarations (compiler error CS9256).
    /// </summary>
    private static readonly SymbolDisplayFormat SignatureFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private static readonly ImmutableHashSet<string> ReadOnlyCollectionNames = ImmutableHashSet.Create(
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Immutable.ImmutableArray`1",
        "System.Collections.Immutable.ImmutableList`1");

    public static TypeRef Build(ITypeSymbol type)
    {
        var displayName = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var fqn = type.ToDisplayString(SignatureFormat);
        var metadataName = GetMetadataName(type);

        var isNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        var isTypeParam = type.TypeKind == TypeKind.TypeParameter;
        var isTable = HasTableAttribute(type);

        (bool isCollection, TypeRef? element) = TryGetCollectionElement(type);

        var typeArgs = type is INamedTypeSymbol named
            ? named.TypeArguments.Select(Build).ToEquatableArray()
            : EquatableArray<TypeRef>.Empty;

        return new TypeRef(
            DisplayName: displayName,
            FullyQualifiedName: fqn,
            MetadataName: metadataName,
            IsNullable: isNullable,
            IsTypeParameter: isTypeParam,
            IsTableType: isTable,
            IsCollection: isCollection,
            ElementType: element,
            TypeArguments: typeArgs);
    }

    private static string GetMetadataName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            var ns = named.ContainingNamespace?.ToDisplayString() ?? string.Empty;
            var nameWithArity = named.MetadataName;
            return string.IsNullOrEmpty(ns) ? nameWithArity : $"{ns}.{nameWithArity}";
        }
        return type.Name;
    }

    private static bool HasTableAttribute(ITypeSymbol type)
    {
        // Resolve against the type parameter's constraint types as well — generic methods
        // such as ListRestrictions<T>() where T : ITable will still flag correctly once we
        // link the graph. For bare type parameters we return false here; the linker re-checks.
        if (type is ITypeParameterSymbol tp)
        {
            foreach (var constraint in tp.ConstraintTypes)
            {
                if (HasTableAttribute(constraint))
                {
                    return true;
                }
            }

            return false;
        }
        foreach (var attr in type.GetAttributes())
        {
            var cls = attr.AttributeClass;
            if (cls is null)
            {
                continue;
            }

            if (cls.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    .Replace("global::", string.Empty) == AnnotationsMetadata.Table)
            {
                return true;
            }
        }
        return false;
    }

    private static (bool isCollection, TypeRef? element) TryGetCollectionElement(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || named.TypeArguments.Length != 1)
        {
            return (false, null);
        }

        var ns = named.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var metadata = string.IsNullOrEmpty(ns) ? named.MetadataName : $"{ns}.{named.MetadataName}";
        if (!ReadOnlyCollectionNames.Contains(metadata))
        {
            return (false, null);
        }

        return (true, Build(named.TypeArguments[0]));
    }
}
