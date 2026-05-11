using System.Text;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Allocates stable, collision-free generated local names from readable hints.
/// </summary>
internal sealed class NameAllocator(string prefix = "__")
{
    private readonly string prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
    private readonly HashSet<string> used = new(StringComparer.Ordinal);

    public void Reserve(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Reserved name cannot be empty.", nameof(name));
        }

        used.Add(name);
    }

    public string Create(string hint)
    {
        var baseName = Prefix(Sanitize(hint));
        var candidate = baseName;
        var suffix = 2;

        while (!used.Add(candidate))
        {
            candidate = baseName + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            suffix++;
        }

        return candidate;
    }

    private string Prefix(string name)
    {
        if (prefix.Length == 0)
        {
            return IsIdentifierStart(name[0]) ? name : "value" + name;
        }

        return name.StartsWith(prefix, StringComparison.Ordinal)
            ? name
            : prefix + name;
    }

    private static string Sanitize(string hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return "value";
        }

        var builder = new StringBuilder(hint.Length);
        foreach (var ch in hint)
        {
            if (IsIdentifierPart(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length == 0 || builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        if (builder.Length == 0)
        {
            return "value";
        }

        while (builder.Length > 0 && builder[0] == '_')
        {
            builder.Remove(0, 1);
        }

        return builder.Length == 0 ? "value" : builder.ToString();
    }

    private static bool IsIdentifierStart(char ch)
        => ch == '_' || char.IsLetter(ch);

    private static bool IsIdentifierPart(char ch)
        => ch == '_' || char.IsLetterOrDigit(ch);
}
