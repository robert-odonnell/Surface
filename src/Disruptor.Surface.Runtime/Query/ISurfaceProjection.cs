using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// A typed projection from a SurrealDB row to <typeparamref name="TRow"/>. Owns both the
/// SELECT field list (so the compiler emits the minimal projection) and the per-row
/// materialiser (so the runtime turns each response object into a strongly-typed CLR
/// instance — typically an immutable record).
/// <para>
/// Constructed via <see cref="SurfaceProjection.For{TRow}"/>, which takes the user's
/// materialise lambda and probes it once at construction time to derive the field list.
/// The library does not generate projection types: the user owns the <typeparamref
/// name="TRow"/> shape and the lambda that builds it.
/// </para>
/// </summary>
public interface ISurfaceProjection<TRow>
{
    /// <summary>Snake-cased SurrealDB field names captured during discovery; ordered by first read.</summary>
    IReadOnlyList<string> SelectFields { get; }

    /// <summary>Materialise a single row into <typeparamref name="TRow"/>.</summary>
    TRow Materialise(SurrealObjectValue row);
}
