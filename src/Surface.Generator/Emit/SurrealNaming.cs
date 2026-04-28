using Humanizer;

namespace Surface.Generator.Emit;

/// <summary>
/// Translates C# type and member names into SurrealDB naming conventions.
/// <list type="bullet">
///   <item><c>ToFieldName</c> — lower_snake_case for table fields and reference/parent
///         column names (driven off C# property/method logical names).</item>
///   <item><c>ToTableName</c> — lower_snake_case + pluralised, for SurrealDB table
///         names (driven off the entity class name).</item>
///   <item><c>ToEdgeName</c> — lower_snake_case after stripping the trailing
///         <c>Attribute</c> suffix, for forward/inverse relation kind names.</item>
/// </list>
/// All work is at codegen time — string literals are baked into the emitted source so
/// the runtime never needs to know about C# type names. Type-parameter dispatch
/// (e.g. <c>typeof(T).Name</c>) is left to callers because the only such case
/// (generic <c>[Children]</c>) is blocked by CG009.
/// </summary>
internal static class SurrealNaming
{
    public static string ToFieldName(string memberName) => memberName.Underscore();

    /// <summary>
    /// <c>Pluralize(inputIsKnownToBeSingular: false)</c> tells Humanizer to detect
    /// already-plural input and leave it alone — covers <c>Details</c> (stays
    /// <c>details</c>) and <c>AcceptanceCriteria</c> (stays <c>acceptance_criteria</c>)
    /// without us hand-rolling the rules.
    /// </summary>
    public static string ToTableName(string typeName) =>
        typeName.Pluralize(inputIsKnownToBeSingular: false).Underscore();

    public static string ToEdgeName(string attributeClassName) =>
        StripAttributeSuffix(attributeClassName).Underscore();

    /// <summary>
    /// English singularizer — used for derived names where a forward relation attribute
    /// (a verb in third-person singular form, like <c>Restricts</c>) needs to be turned
    /// into a noun-ish marker name (<c>IRestrict</c>). Past-participle names like
    /// <c>RestrictedBy</c> are already singular and pass through untouched.
    /// </summary>
    public static string Singularize(string word) =>
        word.Singularize(inputIsKnownToBePlural: false);

    /// <summary>
    /// Reduces a fully-qualified type name (with optional <c>global::</c> prefix,
    /// generic args, and trailing nullability marker) to its bare type identifier.
    /// </summary>
    public static string SimpleName(string fullyQualifiedName)
    {
        var name = fullyQualifiedName;
        var lt = name.IndexOf('<');
        if (lt >= 0)
        {
            name = name[..lt];
        }

        if (name.EndsWith("?"))
        {
            name = name[..^1];
        }

        var lastDot = name.LastIndexOf('.');
        if (lastDot >= 0)
        {
            name = name[(lastDot + 1)..];
        }

        return name;
    }

    public static string StripAttributeSuffix(string s) =>
        s.EndsWith("Attribute") ? s[..^"Attribute".Length] : s;
}
