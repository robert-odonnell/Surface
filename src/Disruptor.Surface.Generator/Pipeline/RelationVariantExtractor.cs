using System.Collections.Immutable;
using Disruptor.Surface.Generator.Annotations;
using Disruptor.Surface.Generator.Emit;
using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// Discovers relation variant classes — user-declared classes annotated with a
/// <c>ForwardRelation</c>- or <c>InverseRelation&lt;T&gt;</c>-derived attribute applied
/// to the class itself (e.g. <c>[Restricts]</c> / <c>[RestrictedBy]</c>). Each variant
/// carries an <c>[In]</c> / <c>[Out]</c> endpoint pair, an optional <c>[Id]</c>, and
/// zero or more <c>[Property]</c> payload members.
/// <para>
/// The same attribute classes can be applied either to a property (entity-side read
/// collection — handled by <see cref="TableExtractor"/>) or to a class (variant
/// declaration — handled here). Predicate is syntax-only ("class has any attribute
/// list") so the incremental pipeline still caches well; the transform uses the
/// semantic model to confirm the attribute's ancestry and walks the class's members.
/// </para>
/// <para>
/// Malformed shapes (missing <c>[In]</c> / <c>[Out]</c>, multiple of either, multiple
/// <c>[Id]</c>) return <c>null</c>. Phase 1b leaves the corresponding diagnostics for a
/// later phase.
/// </para>
/// </summary>
internal static class RelationVariantExtractor
{
    /// <summary>
    /// Syntactic predicate — cheap pre-filter for the incremental pipeline. Catches every
    /// class declaration with a non-empty attribute list. The semantic transform that
    /// follows short-circuits on anything that doesn't actually carry a relation-derived
    /// attribute.
    /// </summary>
    public static bool IsClassWithAttributeList(SyntaxNode node, CancellationToken _)
        => node is ClassDeclarationSyntax cls && cls.AttributeLists.Count > 0;

    public static RelationVariantModel? TryExtract(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not ClassDeclarationSyntax decl)
        {
            return null;
        }

        if (ctx.SemanticModel.GetDeclaredSymbol(decl, ct) is not INamedTypeSymbol cls)
        {
            return null;
        }

        return TryExtractFromSymbol(cls, ct);
    }

    /// <summary>
    /// Symbol-only entry point. Same logic as <see cref="TryExtract"/> but skips the
    /// <c>GeneratorSyntaxContext</c> dance — useful for unit tests that synthesise an
    /// <see cref="INamedTypeSymbol"/> directly via <see cref="Microsoft.CodeAnalysis.Compilation.GetTypeByMetadataName(string)"/>.
    /// </summary>
    public static RelationVariantModel? TryExtractFromSymbol(INamedTypeSymbol cls, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Find the relation-derived attribute applied to the class. We accept the first
        // match; the per-property RelationAttribute usage on entities is irrelevant here
        // (this extractor only sees classes, not properties).
        string? kindAttributeFqn = null;
        foreach (var attr in cls.GetAttributes())
        {
            var attrClass = attr.AttributeClass;
            if (attrClass is null)
            {
                continue;
            }

            if (TableExtractor.InheritsFromForwardRelation(attrClass)
                || TableExtractor.InheritsFromInverseRelation(attrClass))
            {
                kindAttributeFqn = TableExtractor.AttributeFullName(attr);
                break;
            }
        }

        if (kindAttributeFqn is null)
        {
            return null;
        }

        // Walk the class's properties, classifying each by role attribute. Multiple of
        // any restricted role ([In], [Out], [Id]) collapses the variant — we bail to null
        // and leave the diagnostic for a later phase.
        RelationVariantPropertyModel? inProp = null;
        RelationVariantPropertyModel? outProp = null;
        RelationVariantPropertyModel? idProp = null;
        var inCount = 0;
        var outCount = 0;
        var idCount = 0;
        var payloadBuilder = ImmutableArray.CreateBuilder<RelationVariantPropertyModel>();

        foreach (var member in cls.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            if (member is not IPropertySymbol p)
            {
                continue;
            }

            var role = ResolveRole(p.GetAttributes());
            if (role == RelationVariantPropertyRole.None)
            {
                continue;
            }

            var pm = BuildProperty(p, role);

            switch (role)
            {
                case RelationVariantPropertyRole.In:
                    inProp = pm;
                    inCount++;
                    break;
                case RelationVariantPropertyRole.Out:
                    outProp = pm;
                    outCount++;
                    break;
                case RelationVariantPropertyRole.Id:
                    idProp = pm;
                    idCount++;
                    break;
                case RelationVariantPropertyRole.Property:
                    payloadBuilder.Add(pm);
                    break;
            }
        }

        // Required shape: exactly one [In], exactly one [Out], at most one [Id]. Anything
        // else is malformed; bail here so the variant doesn't reach the emitters with
        // a half-formed model. Diagnostics for these cases land in a later phase.
        if (inCount != 1 || outCount != 1 || idCount > 1)
        {
            return null;
        }

        var ns = cls.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var fullName = TableExtractor.NormaliseFullName(cls);

        return new RelationVariantModel(
            FullName: fullName,
            Namespace: ns,
            Name: cls.Name,
            KindAttributeFqn: kindAttributeFqn,
            In: inProp!,
            Out: outProp!,
            Id: idProp,
            PayloadProperties: payloadBuilder.ToImmutable(),
            IsPartial: IsDeclaredPartial(cls, ct),
            DeclaredAccessibility: cls.DeclaredAccessibility.ToString());
    }

    private static RelationVariantPropertyModel BuildProperty(IPropertySymbol p, RelationVariantPropertyRole role)
    {
        // Only [In] / [Out] participate in delete-policy resolution; for other roles the
        // policy field is ignored downstream. We default to Reject so the field has a
        // stable value and the equatable record stays well-defined.
        var deletePolicy = role is RelationVariantPropertyRole.In or RelationVariantPropertyRole.Out
            ? ResolveDeletePolicy(p.GetAttributes())
            : ReferenceDeletePolicy.Reject;

        return new RelationVariantPropertyModel(
            Name: p.Name,
            FieldName: SurrealNaming.ToFieldName(p.Name),
            Type: TypeRefBuilder.Build(p.Type),
            Role: role,
            DeletePolicy: deletePolicy,
            IsPartial: IsPartialMember(p),
            HasSetter: p.SetMethod is { IsInitOnly: false },
            HasInitOnlySetter: p.SetMethod is { IsInitOnly: true },
            IsStatic: p.IsStatic,
            DeclaredAccessibility: p.DeclaredAccessibility.ToString());
    }

    /// <summary>
    /// Picks the role for a single property. <c>[In]</c> / <c>[Out]</c> / <c>[Id]</c>
    /// take precedence over <c>[Property]</c> when both appear (the role attributes pin
    /// the endpoint; a stray <c>[Property]</c> on the same member is harmless and the
    /// emitter doesn't touch it). Returns <see cref="RelationVariantPropertyRole.None"/>
    /// for any property that carries none of these.
    /// </summary>
    private static RelationVariantPropertyRole ResolveRole(ImmutableArray<AttributeData> attrs)
    {
        var sawProperty = false;
        foreach (var attr in attrs)
        {
            var fqn = TableExtractor.AttributeFullName(attr);
            if (fqn is null)
            {
                continue;
            }

            switch (fqn)
            {
                case AnnotationsMetadata.In:
                    return RelationVariantPropertyRole.In;
                case AnnotationsMetadata.Out:
                    return RelationVariantPropertyRole.Out;
                case AnnotationsMetadata.Id:
                    return RelationVariantPropertyRole.Id;
                case AnnotationsMetadata.Property:
                    sawProperty = true;
                    break;
            }
        }
        return sawProperty ? RelationVariantPropertyRole.Property : RelationVariantPropertyRole.None;
    }

    private static ReferenceDeletePolicy ResolveDeletePolicy(ImmutableArray<AttributeData> attrs)
    {
        foreach (var attr in attrs)
        {
            var fqn = TableExtractor.AttributeFullName(attr);
            if (fqn is null)
            {
                continue;
            }

            if (fqn == AnnotationsMetadata.Reject)
            {
                return ReferenceDeletePolicy.Reject;
            }

            if (fqn == AnnotationsMetadata.Unset)
            {
                return ReferenceDeletePolicy.Unset;
            }

            if (fqn == AnnotationsMetadata.Cascade)
            {
                return ReferenceDeletePolicy.Cascade;
            }

            if (fqn == AnnotationsMetadata.Ignore)
            {
                return ReferenceDeletePolicy.Ignore;
            }
        }
        return ReferenceDeletePolicy.Reject;
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

    private static bool IsPartialMember(ISymbol member)
    {
        foreach (var r in member.DeclaringSyntaxReferences)
        {
            if (r.GetSyntax() is not PropertyDeclarationSyntax pds)
            {
                continue;
            }
            foreach (var modifier in pds.Modifiers)
            {
                if (modifier.ValueText == "partial")
                {
                    return true;
                }
            }
        }
        return false;
    }
}
