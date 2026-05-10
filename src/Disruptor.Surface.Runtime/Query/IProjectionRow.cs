using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Per-row reader handed to the projection materialiser. The user's projection lambda
/// calls <see cref="Read{T}"/> once per column it wants out of the row; the typed
/// <see cref="PropertyExpr{T}"/> argument carries both the snake-cased SurrealDB field
/// name and the expected CLR type, so the lambda stays self-contained — no string
/// keys, no positional indices, no out-of-band field declaration.
/// <para>
/// Two implementations live alongside this interface: <see cref="DiscoveryProjectionRow"/>
/// for the construction-time probe pass that captures which fields the lambda reads
/// (so the compiler can emit the right SELECT list), and <see cref="JsonProjectionRow"/>
/// for the per-row materialise pass that reads each value out of the response JSON.
/// </para>
/// </summary>
public interface IProjectionRow
{
    /// <summary>
    /// Read a single column from the row, deserialising into <typeparamref name="T"/>.
    /// During the construction-time discovery pass this records the field name and
    /// returns <c>default(T)!</c>; during a real materialise pass it reads the value
    /// from the underlying JSON element.
    /// </summary>
    T Read<T>(PropertyExpr<T> property);
}

/// <summary>
/// Probe row used at projection-construction time to capture the snake-cased field
/// names the user's materialise lambda touches. Each <see cref="Read{T}"/> call
/// records the field and returns a default value of the requested type.
/// <para>
/// Discovery requires the user's record/class constructor to accept default values
/// (typically <c>null</c> for reference types and <c>default(T)</c> for value types)
/// without throwing. Records with positional constructors and no inline validation
/// are the typical shape; a constructor that rejects nulls will surface as a
/// <see cref="ProjectionDiscoveryException"/> when the projection is built.
/// </para>
/// </summary>
internal sealed class DiscoveryProjectionRow : IProjectionRow
{
    private readonly List<string> fields = [];

    public IReadOnlyList<string> Fields => fields;

    public T Read<T>(PropertyExpr<T> property)
    {
        if (!fields.Contains(property.Field))
        {
            fields.Add(property.Field);
        }
        return default!;
    }
}

/// <summary>
/// Production row backed by an <see cref="ObjectValue"/> (one row from a SurrealDB
/// query response). <see cref="Read{T}"/> looks up the snake-cased field on the
/// object and converts via <see cref="HydrationValue.ReadOrDefault{T}"/> — same path
/// entity hydration uses, so naming + nullability + numeric coercion stays consistent.
/// </summary>
internal sealed class ValueProjectionRow(ObjectValue row) : IProjectionRow
{
    public T Read<T>(PropertyExpr<T> property)
        => HydrationValue.ReadOrDefault<T>(row, property.Field);
}

/// <summary>
/// Thrown when <see cref="SurfaceProjection.For{TRow}"/> can't run the user's
/// materialise lambda during the discovery probe — usually because the target
/// type's constructor rejects the default values the discovery row hands back.
/// </summary>
public sealed class ProjectionDiscoveryException(string message, Exception inner)
    : Exception(message, inner);
