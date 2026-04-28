using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// Extracts the user's <c>[CompositionRoot]</c>-tagged class. Only one is allowed per
/// compilation; collection-time deduplication happens in <see cref="RelationLinker"/> /
/// <see cref="ModelGenerator"/> where the count can be diagnosed (CG018).
/// </summary>
internal static class CompositionRootExtractor
{
    public static CompositionRootModel? TryExtract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
        {
            return null;
        }

        if (type.TypeKind != TypeKind.Class)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var fullName = string.IsNullOrEmpty(ns) ? type.MetadataName : $"{ns}.{type.MetadataName}";

        return new CompositionRootModel(
            FullName: fullName,
            Namespace: ns,
            Name: type.Name,
            DeclaredAccessibility: type.DeclaredAccessibility.ToString(),
            IsPartial: IsDeclaredPartial(type, ct));
    }

    private static bool IsDeclaredPartial(INamedTypeSymbol type, CancellationToken ct)
    {
        foreach (var r in type.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            if (r.GetSyntax(ct) is TypeDeclarationSyntax tds)
            {
                foreach (var modifier in tds.Modifiers)
                {
                    if (modifier.ValueText == "partial")
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
