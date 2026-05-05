using Disruptor.Surface.Generator.Annotations;
using Disruptor.Surface.Generator.Emit;
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
                PairedForwardFullName: null,
                PayloadFields: ExtractPayloadFields(cls));
        }

        if (TableExtractor.InheritsFromInverseRelation(cls))
        {
            return new RelationKindModel(
                FullName: fullName,
                Namespace: ns,
                Name: cls.Name,
                Direction: RelationDirection.Inverse,
                PairedForwardFullName: ResolveInverseForwardArgument(cls),
                PayloadFields: EquatableArray<EdgePayloadFieldModel>.Empty);
        }

        return null;
    }

    /// <summary>
    /// Walks <paramref name="cls"/>'s base chain looking for
    /// <c>ForwardRelation&lt;TPayload&gt;</c>; when found, harvests
    /// <typeparamref>TPayload</typeparamref>'s public scalar properties as edge-table
    /// fields. Empty when the class derives from the non-generic <c>ForwardRelation</c>
    /// (no payload — bare-edge schema, the existing behaviour).
    /// </summary>
    private static EquatableArray<EdgePayloadFieldModel> ExtractPayloadFields(INamedTypeSymbol cls)
    {
        for (var current = cls.BaseType; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType) continue;
            if (TableExtractor.NormaliseFullName(current.ConstructedFrom) != AnnotationsMetadata.ForwardRelationOfT) continue;
            if (current.TypeArguments.Length == 0) return EquatableArray<EdgePayloadFieldModel>.Empty;
            if (current.TypeArguments[0] is not INamedTypeSymbol payload) return EquatableArray<EdgePayloadFieldModel>.Empty;

            return HarvestPayloadFields(payload);
        }

        return EquatableArray<EdgePayloadFieldModel>.Empty;
    }

    /// <summary>
    /// Discovers the payload type's public instance properties — getter visible to the
    /// outside, mutable or immutable — and projects them onto
    /// <see cref="EdgePayloadFieldModel"/>. Static / private / write-only / indexer /
    /// override (already harvested via base) properties are skipped. The
    /// <c>SchemaEmitter</c> is the gate for "is this type actually mappable as a SurrealDB
    /// scalar?" — fields with un-mappable types still flow through here and get reported
    /// at emit time, same as <c>[Property]</c> handling.
    /// </summary>
    private static EquatableArray<EdgePayloadFieldModel> HarvestPayloadFields(INamedTypeSymbol payload)
    {
        var fields = new List<EdgePayloadFieldModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var current = (ITypeSymbol?)payload; current is not null && current.SpecialType != Microsoft.CodeAnalysis.SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IPropertySymbol prop) continue;
                if (prop.IsStatic) continue;
                if (prop.IsIndexer) continue;
                if (prop.DeclaredAccessibility != Accessibility.Public) continue;
                if (prop.GetMethod is null) continue;
                if (!seen.Add(prop.Name)) continue;

                fields.Add(new EdgePayloadFieldModel(
                    Name: prop.Name,
                    FieldName: SurrealNaming.ToFieldName(prop.Name),
                    Type: TypeRefBuilder.Build(prop.Type)));
            }
        }

        return new EquatableArray<EdgePayloadFieldModel>(fields.ToArray());
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
