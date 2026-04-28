using System.Collections.Immutable;
using Surface.Generator.Annotations;
using Surface.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Surface.Generator.Pipeline;

internal static class TableExtractor
{
    public static TableModel? TryExtract(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
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

        var isPartial = IsDeclaredPartial(type, ct);
        var isAggregateRoot = HasAttribute(type, AnnotationsMetadata.AggregateRoot);

        var typeParameters = type.TypeParameters.Select(p => p.Name).ToEquatableArray();

        var propertiesBuilder = ImmutableArray.CreateBuilder<PropertyModel>();
        var methodsBuilder = ImmutableArray.CreateBuilder<MethodModel>();

        foreach (var member in type.GetMembers())
        {
            ct.ThrowIfCancellationRequested();

            switch (member)
            {
                case IPropertySymbol p when TryBuildProperty(p) is { } pm:
                    propertiesBuilder.Add(pm);
                    break;
                case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary && TryBuildMethod(m) is { } mm:
                    methodsBuilder.Add(mm);
                    break;
            }
        }

        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var fullName = string.IsNullOrEmpty(ns) ? type.MetadataName : $"{ns}.{type.MetadataName}";
        var hint = $"{fullName}.g.cs";

        return new TableModel(
            FullName: fullName,
            Namespace: ns,
            Name: type.Name,
            IsPartial: isPartial,
            IsAbstract: type.IsAbstract,
            IsSealed: type.IsSealed,
            IsAggregateRoot: isAggregateRoot,
            DeclaredAccessibility: type.DeclaredAccessibility.ToString(),
            TypeParameters: typeParameters,
            Properties: propertiesBuilder.ToImmutable(),
            Methods: methodsBuilder.ToImmutable(),
            FileHintName: hint);
    }

    private static PropertyModel? TryBuildProperty(IPropertySymbol p)
    {
        var attrs = p.GetAttributes();
        var kinds = ResolvePropertyKinds(attrs);
        var (role, kindFullName) = ResolveRelationRole(attrs);
        if (kinds == PropertyKind.None && role == MethodRole.None)
        {
            return null;
        }

        // Reference delete behavior — only meaningful for [Reference] members; default
        // is Reject (per spec §10.6: nullable shape never implies Unset). For non-Reference
        // members we still capture explicit-vs-default and multiplicity bits so CG015
        // (delete behavior on [Parent]) can fire.
        var (deletePolicy, hasExplicit, hasMultiple) = ResolveReferenceDelete(attrs);

        return new PropertyModel(
            Name: p.Name,
            Type: TypeRefBuilder.Build(p.Type),
            Kinds: kinds,
            RelationRole: role,
            RelationKindFullName: kindFullName,
            ReferenceDelete: deletePolicy,
            HasExplicitDeleteBehavior: hasExplicit,
            HasMultipleDeleteBehaviors: hasMultiple,
            HasGetter: p.GetMethod is not null,
            HasSetter: p.SetMethod is { IsInitOnly: false },
            HasInitOnlySetter: p.SetMethod is { IsInitOnly: true },
            IsPartial: IsPartialMember(p),
            IsStatic: p.IsStatic,
            DeclaredAccessibility: p.DeclaredAccessibility.ToString(),
            InlineMembers: ResolveInlineMembers(p.Type));
    }

    /// <summary>
    /// For a <c>SurrealArray&lt;T&gt;</c> property, walks <c>T</c>'s public instance
    /// properties so the schema emitter can produce <c>scenarios.*.kind</c>-style
    /// sub-field DDL. Returns empty for anything else.
    /// </summary>
    private static EquatableArray<InlineMember> ResolveInlineMembers(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named) return [];
        if (named.Arity != 1) return [];

        var def = named.ConstructedFrom;
        var ns = def.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (ns != "Surface.Runtime" || def.Name != "SurrealArray") return [];

        var element = named.TypeArguments[0];
        var members = new List<InlineMember>();
        foreach (var member in element.GetMembers())
        {
            // Public instance properties only — covers both classic class properties
            // and record positional parameters (Roslyn synthesises a property per param).
            if (member is not IPropertySymbol prop) continue;
            if (prop.DeclaredAccessibility != Accessibility.Public) continue;
            if (prop.IsStatic) continue;
            if (prop.GetMethod is null) continue;

            members.Add(new InlineMember(prop.Name, TypeRefBuilder.Build(prop.Type)));
        }
        return members.ToEquatableArray();
    }

    private static (ReferenceDeletePolicy Policy, bool HasExplicit, bool HasMultiple) ResolveReferenceDelete(ImmutableArray<AttributeData> attrs)
    {
        var found = new List<ReferenceDeletePolicy>();
        foreach (var attr in attrs)
        {
            var fqn = AttributeFullName(attr);
            if (fqn is null)
            {
                continue;
            }

            if (fqn == AnnotationsMetadata.Reject)
            {
                found.Add(ReferenceDeletePolicy.Reject);
            }

            if (fqn == AnnotationsMetadata.Unset)
            {
                found.Add(ReferenceDeletePolicy.Unset);
            }

            if (fqn == AnnotationsMetadata.Cascade)
            {
                found.Add(ReferenceDeletePolicy.Cascade);
            }

            if (fqn == AnnotationsMetadata.Ignore)
            {
                found.Add(ReferenceDeletePolicy.Ignore);
            }
        }
        var policy = found.Count > 0 ? found[0] : ReferenceDeletePolicy.Reject;
        return (policy, found.Count > 0, found.Count > 1);
    }

    private static MethodModel? TryBuildMethod(IMethodSymbol m)
    {
        var kinds = ResolvePropertyKinds(m.GetAttributes());
        var (role, kindFullName) = ResolveRelationRole(m.GetAttributes());
        if (kinds == PropertyKind.None && role == MethodRole.None)
        {
            return null;
        }

        var verb = ParseVerb(m.Name);

        var parameters = m.Parameters
            .Select(p => new ParameterModel(
                Name: p.Name,
                Type: TypeRefBuilder.Build(p.Type),
                RefKind: p.RefKind.ToString(),
                HasDefaultValue: p.HasExplicitDefaultValue))
            .ToEquatableArray();

        var typeParams = m.TypeParameters.Select(t => t.Name).ToEquatableArray();

        return new MethodModel(
            Name: m.Name,
            Verb: verb,
            Role: role,
            Kinds: kinds,
            RelationKindFullName: kindFullName,
            ReturnType: TypeRefBuilder.Build(m.ReturnType),
            Parameters: parameters,
            TypeParameters: typeParams,
            IsPartial: IsPartialMember(m),
            IsStatic: m.IsStatic,
            ReturnsVoid: m.ReturnsVoid,
            DeclaredAccessibility: m.DeclaredAccessibility.ToString());
    }

    private static PropertyKind ResolvePropertyKinds(ImmutableArray<AttributeData> attrs)
    {
        var kinds = PropertyKind.None;
        foreach (var attr in attrs)
        {
            var fqn = AttributeFullName(attr);
            if (fqn is null)
            {
                continue;
            }

            kinds |= fqn switch
            {
                AnnotationsMetadata.Id         => PropertyKind.Id,
                AnnotationsMetadata.Property   => PropertyKind.Property,
                AnnotationsMetadata.Parent     => PropertyKind.Parent,
                AnnotationsMetadata.Children   => PropertyKind.Children,
                AnnotationsMetadata.Reference  => PropertyKind.Reference,
                _ => PropertyKind.None,
            };
        }
        return kinds;
    }

    /// <summary>
    /// A member (method or property) joins the relation side of the model when one of its
    /// attributes derives from <c>ForwardRelationAttribute</c> or <c>InverseRelationAttribute&lt;T&gt;</c>.
    /// Returns the role plus the fully-qualified attribute name so the linker can pair
    /// forward/inverse kinds later.
    /// </summary>
    private static (MethodRole Role, string? KindFullName) ResolveRelationRole(ImmutableArray<AttributeData> attrs)
    {
        foreach (var attr in attrs)
        {
            var cls = attr.AttributeClass;
            if (cls is null)
            {
                continue;
            }

            if (InheritsFromForwardRelation(cls))
            {
                return (MethodRole.ForwardRelation, AttributeFullName(attr));
            }

            if (InheritsFromInverseRelation(cls))
            {
                return (MethodRole.InverseRelation, AttributeFullName(attr));
            }
        }
        return (MethodRole.None, null);
    }

    internal static bool InheritsFromForwardRelation(INamedTypeSymbol cls)
    {
        for (var current = cls.BaseType; current is not null; current = current.BaseType)
            if (NormaliseFullName(current) == AnnotationsMetadata.ForwardRelation)
            {
                return true;
            }

        return false;
    }

    internal static bool InheritsFromInverseRelation(INamedTypeSymbol cls)
    {
        for (var current = cls.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType &&
                NormaliseFullName(current.ConstructedFrom) == AnnotationsMetadata.InverseRelation)
            {
                return true;
            }
        }
        return false;
    }

    internal static string? AttributeFullName(AttributeData attr)
    {
        var cls = attr.AttributeClass;
        return cls is null ? null : NormaliseFullName(cls);
    }

    internal static string NormaliseFullName(INamedTypeSymbol symbol)
    {
        var ns = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        var name = symbol.MetadataName;
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    private static MethodVerb ParseVerb(string name)
    {
        if (Starts(name, "Add"))
        {
            return MethodVerb.Add;
        }

        if (Starts(name, "Remove"))
        {
            return MethodVerb.Remove;
        }

        if (Starts(name, "Clear"))
        {
            return MethodVerb.Clear;
        }

        if (Starts(name, "Set"))
        {
            return MethodVerb.Set;
        }

        if (Starts(name, "List"))
        {
            return MethodVerb.List;
        }

        if (Starts(name, "Get"))
        {
            return MethodVerb.Get;
        }

        return MethodVerb.Unknown;

        static bool Starts(string s, string prefix) =>
            s.Length >= prefix.Length &&
            s.StartsWith(prefix, StringComparison.Ordinal) &&
            (s.Length == prefix.Length || char.IsUpper(s[prefix.Length]));
    }

    private static bool HasAttribute(INamedTypeSymbol type, string attributeFullMetadataName)
    {
        foreach (var attr in type.GetAttributes())
        {
            if (attr.AttributeClass is null)
            {
                continue;
            }

            if (NormaliseFullName(attr.AttributeClass) == attributeFullMetadataName)
            {
                return true;
            }
        }
        return false;
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
            var node = r.GetSyntax();
            var modifiers = node switch
            {
                MethodDeclarationSyntax mds   => mds.Modifiers,
                PropertyDeclarationSyntax pds => pds.Modifiers,
                _                             => default,
            };
            foreach (var modifier in modifiers)
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
