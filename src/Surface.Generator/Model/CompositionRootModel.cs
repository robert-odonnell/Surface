namespace Surface.Generator.Model;

/// <summary>
/// The user's <c>[CompositionRoot]</c>-tagged class. Holds just enough metadata to emit
/// a partial declaration that grafts the per-aggregate <c>Load{Root}Async</c> instance
/// methods onto it. The class itself is the user's domain — accessibility, ctors,
/// dependencies are entirely their concern; the generator only contributes the load
/// methods.
/// </summary>
public sealed record CompositionRootModel(
    string FullName,
    string Namespace,
    string Name,
    string DeclaredAccessibility,
    bool IsPartial);
