using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

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
            IsPartial: PartialDeclaration.IsDeclared(type, ct));
    }
}
