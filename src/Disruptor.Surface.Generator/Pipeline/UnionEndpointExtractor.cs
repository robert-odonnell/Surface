using System.Collections.Immutable;
using Disruptor.Surface.Generator.Annotations;
using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// Intermediate record produced by <see cref="UnionEndpointExtractor.TryExtractUnionInterface"/>
/// — one entry per user-declared interface attributed with an attribute deriving from
/// <c>In&lt;TKind&gt;</c> or <c>Out&lt;TKind&gt;</c>. Memberless until the linker pairs
/// these candidates with <see cref="UnionMembershipCandidate"/>s to compute the final
/// <see cref="UnionEndpointModel"/>.
/// </summary>
public sealed record UnionInterfaceCandidate(
    string InterfaceFullName,
    string KindFullName,
    UnionEndpointDirection Direction);

/// <summary>
/// Intermediate record produced by <see cref="UnionEndpointExtractor.TryExtractMembership"/>
/// — one entry per user-declared partial <c>I{Name}RecordId</c> interface with a non-empty
/// base list. The linker matches each base FQN against the known union interfaces to
/// resolve which unions this marker is a member of.
/// </summary>
public sealed record UnionMembershipCandidate(
    string MarkerInterfaceFullName,
    EquatableArray<string> BaseFullNames);

/// <summary>
/// Discovers user-declared record-type-union endpoint metadata in two complementary
/// passes: (a) interfaces attributed with anything deriving from
/// <c>Disruptor.Surface.Annotations.In&lt;TKind&gt;</c> or <c>Out&lt;TKind&gt;</c> (the
/// union declarations themselves), and (b) partial <c>I{Name}RecordId</c> interface
/// declarations that extend other interfaces (the per-table opt-ins). The
/// <see cref="RelationLinker"/> stitches the two candidate sets into the final
/// <see cref="UnionEndpointModel"/> list.
/// </summary>
internal static class UnionEndpointExtractor
{
    /// <summary>
    /// Syntactic predicate for pass (a). Cheap pre-filter on interfaces with attribute
    /// lists; the transform short-circuits on anything whose attributes don't actually
    /// derive from one of the union bases.
    /// </summary>
    public static bool IsInterfaceWithAttributeList(SyntaxNode node, CancellationToken _)
        => node is InterfaceDeclarationSyntax decl && decl.AttributeLists.Count > 0;

    /// <summary>
    /// Pass (a): an interface attributed with <c>[Foo]</c> where <c>Foo</c> derives from
    /// <c>In&lt;TKind&gt;</c> or <c>Out&lt;TKind&gt;</c> becomes a union declaration.
    /// Direction is taken from which generic base appeared in the attribute's chain;
    /// <c>TKind</c> is lifted from the constructed type arguments.
    /// </summary>
    public static UnionInterfaceCandidate? TryExtractUnionInterface(GeneratorSyntaxContext ctx, CancellationToken ct)
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

        foreach (var attr in iface.GetAttributes())
        {
            if (attr.AttributeClass is not { } attrClass)
            {
                continue;
            }

            var found = WalkForUnionBase(attrClass);
            if (found is null)
            {
                continue;
            }

            return new UnionInterfaceCandidate(
                InterfaceFullName: TableExtractor.NormaliseFullName(iface),
                KindFullName: found.Value.KindFullName,
                Direction: found.Value.Direction);
        }

        return null;
    }

    /// <summary>
    /// Syntactic predicate for pass (b). Cheap pre-filter on interface declarations whose
    /// name pattern matches the per-table marker shape (<c>I…RecordId</c>) and that
    /// declare additional bases (the opt-in into a union interface). The transform
    /// resolves each base to its FQN via the semantic model.
    /// </summary>
    public static bool IsPerTableMarkerWithBases(SyntaxNode node, CancellationToken _)
    {
        if (node is not InterfaceDeclarationSyntax decl)
        {
            return false;
        }

        if (decl.BaseList is null || decl.BaseList.Types.Count == 0)
        {
            return false;
        }

        var name = decl.Identifier.ValueText;
        return name.Length > "IRecordId".Length
            && name.StartsWith("I")
            && name.EndsWith("RecordId");
    }

    /// <summary>
    /// Pass (b): collect every base type FQN declared on a per-table marker partial.
    /// The linker will check each base against the union-interface candidates from
    /// pass (a) to decide which unions this marker enrols its table into. <c>IRecordId</c>
    /// itself is filtered out (it's always present and never a union signal).
    /// </summary>
    public static UnionMembershipCandidate? TryExtractMembership(GeneratorSyntaxContext ctx, CancellationToken ct)
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

        // Capture only the bases declared on this specific partial — not the merged set
        // across all partials of the interface. The linker filters by name match, so
        // catching IRecordId once (from the generator's primary partial) would be noise.
        var bases = ImmutableArray.CreateBuilder<string>();
        if (decl.BaseList is { } baseList)
        {
            foreach (var baseTypeSyntax in baseList.Types)
            {
                var typeInfo = ctx.SemanticModel.GetTypeInfo(baseTypeSyntax.Type, ct);
                if (typeInfo.Type is not INamedTypeSymbol baseSymbol)
                {
                    continue;
                }

                var fqn = TableExtractor.NormaliseFullName(baseSymbol);
                if (fqn == AnnotationsMetadata.RecordIdInterface)
                {
                    continue;
                }

                bases.Add(fqn);
            }
        }

        if (bases.Count == 0)
        {
            return null;
        }

        return new UnionMembershipCandidate(
            MarkerInterfaceFullName: TableExtractor.NormaliseFullName(iface),
            BaseFullNames: new EquatableArray<string>(bases.ToImmutable()));
    }

    /// <summary>
    /// Walks the base type chain of an attribute class looking for a constructed
    /// <c>In&lt;TKind&gt;</c> or <c>Out&lt;TKind&gt;</c>. Returns the (TKind FQN, direction)
    /// pair when found; null when neither base appears in the chain.
    /// </summary>
    private static (string KindFullName, UnionEndpointDirection Direction)? WalkForUnionBase(INamedTypeSymbol attrClass)
    {
        for (var current = attrClass.BaseType; current is not null; current = current.BaseType)
        {
            if (!current.IsGenericType)
            {
                continue;
            }

            var openFullName = TableExtractor.NormaliseFullName(current.ConstructedFrom);
            UnionEndpointDirection? direction = openFullName switch
            {
                AnnotationsMetadata.InUnionBase  => UnionEndpointDirection.In,
                AnnotationsMetadata.OutUnionBase => UnionEndpointDirection.Out,
                _ => null,
            };
            if (direction is null)
            {
                continue;
            }

            if (current.TypeArguments.Length == 0
                || current.TypeArguments[0] is not INamedTypeSymbol kindArg)
            {
                return null;
            }

            return (TableExtractor.NormaliseFullName(kindArg), direction.Value);
        }

        return null;
    }
}
