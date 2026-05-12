using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Disruptor.Surface.Generator.Pipeline;

internal static class PartialDeclaration
{
    public static bool IsDeclared(INamedTypeSymbol type, CancellationToken ct)
    {
        foreach (var r in type.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();
            if (r.GetSyntax(ct) is not TypeDeclarationSyntax tds)
            {
                continue;
            }

            foreach (var modifier in tds.Modifiers)
            {
                if (modifier.ValueText == "partial")
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool IsMember(ISymbol member)
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
