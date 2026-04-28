#nullable enable

namespace Surface.Runtime;

/// <summary>
/// What happens to a referencing record when the record it references is deleted.
/// Declared per-<c>[Reference]</c> in user code via the marker attributes
/// <c>[Reject]</c> / <c>[Unset]</c> / <c>[Cascade]</c> / <c>[Ignore]</c>; defaults to
/// <see cref="Reject"/> when none is supplied. Resolved at commit-plan time against the
/// <em>effective</em> incoming reference set, not the database state at delete-record
/// time — so reassigning a reference earlier in the same packet avoids the block.
/// </summary>
public enum ReferenceDeleteBehavior
{
    /// <summary>Block deletion of the referenced record while this reference still points at it.</summary>
    Reject,
    /// <summary>Clear the referencing field when the referenced record is deleted (requires nullable storage).</summary>
    Unset,
    /// <summary>Delete the referencing record when the referenced record is deleted.</summary>
    Cascade,
    /// <summary>Leave the referencing record unchanged; the reference may dangle.</summary>
    Ignore,
}
