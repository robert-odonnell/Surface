using System.Collections.Immutable;
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
/// <para>
/// When the interface members carry <c>[In]</c> / <c>[Out]</c> / <c>[Property]</c> /
/// <c>[Id]</c> annotations (preview.56) the extractor lifts those into the
/// <see cref="LiftedIn"/> / <see cref="LiftedOut"/> / <see cref="LiftedId"/> /
/// <see cref="LiftedPayload"/> fields. The linker uses them to fill in any variant whose
/// own annotated members are empty so the user can collapse the variant body to
/// <c>[Calls] partial class CallsRelation : ICodeSymbolEdge;</c>. Interfaces whose
/// members carry no model attributes leave all four fields null and the lift path is
/// skipped — variants under such interfaces stay self-describing.
/// </para>
/// </summary>
public sealed record SharedShapeInterfaceCandidate(
    string InterfaceFullName,
    string Namespace,
    string Name,
    bool IsPartial,
    string DeclaredAccessibility,
    RelationVariantPropertyModel? LiftedIn,
    RelationVariantPropertyModel? LiftedOut,
    RelationVariantPropertyModel? LiftedId,
    EquatableArray<RelationVariantPropertyModel> LiftedPayload);

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

        // Walk the interface's own properties for [In] / [Out] / [Id] / [Property]
        // attributes. When the interface declares its endpoints/payload, variants opting
        // in via base list alone (preview.56) inherit the shape — RelationLinker copies
        // these into any variant whose own annotated members are empty so the variant
        // body collapses to `[Calls] partial class CallsRelation : ICodeSymbolEdge;`.
        // Interfaces that stay attribute-free (the preview.55 spike shape) leave all four
        // fields null and the lift path is skipped — the variants under them remain
        // self-describing per the original design.
        RelationVariantPropertyModel? liftedIn = null;
        RelationVariantPropertyModel? liftedOut = null;
        RelationVariantPropertyModel? liftedId = null;
        var liftedPayload = ImmutableArray.CreateBuilder<RelationVariantPropertyModel>();
        foreach (var member in iface.GetMembers())
        {
            ct.ThrowIfCancellationRequested();
            if (member is not IPropertySymbol p)
            {
                continue;
            }

            var role = RelationVariantExtractor.ResolveRole(p.GetAttributes());
            if (role == RelationVariantPropertyRole.None)
            {
                continue;
            }

            var pm = RelationVariantExtractor.BuildProperty(p, role);
            switch (role)
            {
                case RelationVariantPropertyRole.In:
                    liftedIn ??= pm;
                    break;
                case RelationVariantPropertyRole.Out:
                    liftedOut ??= pm;
                    break;
                case RelationVariantPropertyRole.Id:
                    liftedId ??= pm;
                    break;
                case RelationVariantPropertyRole.Property:
                    liftedPayload.Add(pm);
                    break;
            }
        }

        return new SharedShapeInterfaceCandidate(
            InterfaceFullName: fqn,
            Namespace: iface.ContainingNamespace?.ToDisplayString() ?? string.Empty,
            Name: iface.Name,
            IsPartial: PartialDeclaration.IsDeclared(iface, ct),
            DeclaredAccessibility: iface.DeclaredAccessibility.ToString(),
            LiftedIn: liftedIn,
            LiftedOut: liftedOut,
            LiftedId: liftedId,
            LiftedPayload: new EquatableArray<RelationVariantPropertyModel>(liftedPayload.ToImmutable()));
    }
}
