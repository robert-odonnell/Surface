#nullable enable

namespace Disruptor.Surface.Runtime;

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
/// The model-scoped registry of <c>[Reference]</c> field metadata. The generator emits
/// an implementation per consumer (<c>GeneratedReferenceRegistry</c>) and exposes it as
/// a static property on the user's <c>[CompositionRoot]</c> partial — e.g.
/// <c>Workspace.ReferenceRegistry</c>. <see cref="SurrealSession"/> takes one in its
/// constructor and <see cref="CommitPlanner.Build"/> reads through it; there is no
/// process-global registry, so multiple Disruptor.Surface-generated models can coexist in the
/// same process without trampling each other.
/// </summary>
public interface IReferenceRegistry
{
    IReadOnlyList<ReferenceFieldInfo> IncomingReferencesTo(string referencedTable);
}

/// <summary>
/// Empty <see cref="IReferenceRegistry"/> for sessions constructed without a registry —
/// suitable for tests that don't exercise reference-delete planning. A real consumer
/// passes in <c>{CompositionRoot}.ReferenceRegistry</c>.
/// </summary>
public sealed class NullReferenceRegistry : IReferenceRegistry
{
    public static readonly NullReferenceRegistry Instance = new();
    private NullReferenceRegistry() { }
    public IReadOnlyList<ReferenceFieldInfo> IncomingReferencesTo(string referencedTable) => [];
}
