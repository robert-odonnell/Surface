using Disruptor.Surface.Generator.Annotations;
using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// Intermediate record produced by <see cref="SharedShapeExtractor.TryExtract"/> — one
/// entry per user-declared interface deriving from <c>IRelationVariant</c>. The linker
/// pairs each candidate with the relation variants that implement it to compute the
/// final <see cref="SharedShapeModel"/>.
/// </summary>
public sealed record SharedShapeInterfaceCandidate(
    string InterfaceFullName,
    string Namespace,
    string Name,
    bool IsPartial,
    string DeclaredAccessibility);

/// <summary>
/// Discovers user-declared interfaces that opt their members into the shared-shape API
/// surface — a typed <c>Create&lt;TKind&gt;</c> factory + polymorphic
/// <c>QueryOutgoingAsync</c> / <c>QueryIncomingAsync</c> terminals — by deriving from
/// <see cref="AnnotationsMetadata.RelationVariantInterface"/>. Union-endpoint
/// interfaces (derived from <c>IRecordId</c>, handled by
/// <see cref="UnionEndpointExtractor"/>) are intentionally excluded; the two features
/// occupy disjoint design spaces (multiple endpoints under one kind vs. multiple kinds
/// under one endpoint+payload shape).
/// </summary>
internal static class SharedShapeExtractor
{
    /// <summary>
    /// Syntactic predicate. Cheap pre-filter on interface declarations with a base list;
    /// the semantic transform confirms the base chain reaches IRelationVariant.
    /// </summary>
    public static bool IsInterfaceWithBaseList(SyntaxNode node, CancellationToken _)
        => node is InterfaceDeclarationSyntax decl && decl.BaseList is { } b && b.Types.Count > 0;

    /// <summary>
    /// Returns a <see cref="SharedShapeInterfaceCandidate"/> when the interface derives
    /// (transitively) from <c>Disruptor.Surface.Runtime.IRelationVariant</c> and is not
    /// the marker interface itself. Returns null for union-endpoint interfaces (derived
    /// from <c>IRecordId</c>), framework types, and unrelated interfaces.
    /// </summary>
    public static SharedShapeInterfaceCandidate? TryExtract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not InterfaceDeclarationSyntax decl)
        {
            return null;
        }

        if (ctx.SemanticModel.GetDeclaredSymbol(decl, ct) is not INamedTypeSymbol iface)
        {
            return null;
        }

        ct.ThrowIfCancellationRequested();

        var fqn = TableExtractor.NormaliseFullName(iface);
        if (fqn == AnnotationsMetadata.RelationVariantInterface)
        {
            return null;
        }

        // Walk AllInterfaces (the transitive closure) so an interface that derives from
        // IRelationVariant via an intermediate user interface still qualifies.
        var derivesFromRelationVariant = false;
        var derivesFromRecordId = false;
        foreach (var baseIface in iface.AllInterfaces)
        {
            var baseFqn = TableExtractor.NormaliseFullName(baseIface);
            if (baseFqn == AnnotationsMetadata.RelationVariantInterface)
            {
                derivesFromRelationVariant = true;
            }
            else if (baseFqn == AnnotationsMetadata.RecordIdInterface)
            {
                derivesFromRecordId = true;
            }
        }

        // Exclude union-endpoint interfaces — those are a different shape and have their
        // own emit path. The two features can be combined per use-site (an interface
        // could derive from both), but emitting both surfaces onto one interface is
        // out of scope; bias to "shared-shape only when not also a record-id union".
        if (!derivesFromRelationVariant || derivesFromRecordId)
        {
            return null;
        }

        return new SharedShapeInterfaceCandidate(
            InterfaceFullName: fqn,
            Namespace: iface.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            Name: iface.Name,
            IsPartial: PartialDeclaration.IsDeclared(iface, ct),
            DeclaredAccessibility: iface.DeclaredAccessibility.ToString());
    }
}
