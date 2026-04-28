using Disruptor.Surface.Generator.Annotations;
using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// Discovers user-defined relation attribute classes by walking class declarations whose
/// base list mentions a potential <see cref="RelationAttribute"/> ancestor. Direction
/// (forward/inverse) is determined from the base chain — there are no marker attributes
/// to look for. For inverse kinds, the paired forward attribute's full name is lifted
/// out of the <c>InverseRelation&lt;T&gt;</c> generic argument.
/// </summary>
internal static class RelationKindExtractor
{
    /// <summary>
    /// Syntactic predicate — cheap pre-filter for the incremental pipeline. Catches every
    /// class declaration with a non-empty base list. The semantic transform that follows
    /// short-circuits on anything that doesn't actually derive from
    /// <see cref="RelationAttribute"/>.
    /// </summary>
    public static bool IsClassWithBaseList(SyntaxNode node, CancellationToken _)
        => node is ClassDeclarationSyntax cls && cls.BaseList is not null;

    public static RelationKindModel? TryExtract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not ClassDeclarationSyntax decl)
        {
            return null;
        }

        if (ctx.SemanticModel.GetDeclaredSymbol(decl, ct) is not INamedTypeSymbol cls)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        var ns = cls.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var fullName = TableExtractor.NormaliseFullName(cls);

        if (TableExtractor.InheritsFromForwardRelation(cls))
        {
            return new RelationKindModel(
                FullName: fullName,
                Namespace: ns,
                Name: cls.Name,
                Direction: RelationDirection.Forward,
                PairedForwardFullName: null);
        }

        if (TableExtractor.InheritsFromInverseRelation(cls))
        {
            return new RelationKindModel(
                FullName: fullName,
                Namespace: ns,
                Name: cls.Name,
                Direction: RelationDirection.Inverse,
                PairedForwardFullName: ResolveInverseForwardArgument(cls));
        }

        return null;
    }

    /// <summary>
    /// For an inverse attribute <c>Foo : InverseRelation&lt;Bar&gt;</c>, returns the
    /// fully-qualified metadata name of <c>Bar</c>.
    /// </summary>
    private static string? ResolveInverseForwardArgument(INamedTypeSymbol cls)
    {
        for (var current = cls.BaseType; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType)
            {
                continue;
            }

            if (TableExtractor.NormaliseFullName(current.ConstructedFrom) != AnnotationsMetadata.InverseRelation)
            {
                continue;
            }

            if (current.TypeArguments.Length == 0)
            {
                return null;
            }

            if (current.TypeArguments[0] is INamedTypeSymbol arg)
            {
                return TableExtractor.NormaliseFullName(arg);
            }
        }
        return null;
    }
}
