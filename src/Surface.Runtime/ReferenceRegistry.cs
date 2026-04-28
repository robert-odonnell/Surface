#nullable enable

namespace Surface.Runtime;

/// <summary>
/// One <c>[Reference]</c> field's metadata: which referencing table holds it, the
/// snake-cased field name, the table it references, the configured delete behavior,
/// and whether the field is nullable in C# (matters for the SURF-R001 diagnostic
/// validating <see cref="ReferenceDeleteBehavior.Unset"/> against nullable storage).
/// </summary>
public sealed record ReferenceFieldInfo(
    string ReferencerTable,
    string FieldName,
    string ReferencedTable,
    ReferenceDeleteBehavior Behavior,
    bool IsNullable);

/// <summary>
/// Implemented by the generator-emitted registry in the consumer assembly. Lets the
/// runtime stay decoupled from the consumer-specific table set; the consumer registers
/// its instance via a <c>[ModuleInitializer]</c> on first assembly load.
/// </summary>
public interface IReferenceRegistry
{
    IReadOnlyList<ReferenceFieldInfo> IncomingReferencesTo(string referencedTable);
}

/// <summary>
/// Static facade the commit planner queries. Empty by default; consuming assemblies
/// register their generator-emitted <see cref="IReferenceRegistry"/> on load via
/// <c>[ModuleInitializer]</c>. Last writer wins — multiple consumers in the same
/// process aren't supported today.
/// </summary>
public static class ReferenceRegistry
{
    private static IReferenceRegistry? _instance;

    public static void Register(IReferenceRegistry instance) => _instance = instance;

    public static IReadOnlyList<ReferenceFieldInfo> IncomingReferencesTo(string referencedTable) =>
        _instance?.IncomingReferencesTo(referencedTable) ?? [];
}
