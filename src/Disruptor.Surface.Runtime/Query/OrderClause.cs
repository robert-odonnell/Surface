namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Direction modifier for an <see cref="OrderClause"/>. Renders as <c>ASC</c> or
/// <c>DESC</c> after the field name in SurrealQL's <c>ORDER BY</c> clause.
/// </summary>
public enum OrderDirection
{
    Ascending,
    Descending
}

/// <summary>
/// One sort specification on a query — a field name plus a direction. Built from a
/// generator-emitted <see cref="PropertyExpr{T}"/> via <see cref="SurfaceQuery{T}.OrderBy"/>
/// / <see cref="SurfaceQuery{T}.ThenBy"/>; the QueryCompiler emits each entry as
/// <c>field ASC|DESC</c> joined with commas.
/// </summary>
/// <param name="Field">Snake-cased SurrealDB field name to order on.</param>
/// <param name="Direction">Ascending (default for <c>OrderBy</c>) or descending.</param>
public sealed record OrderClause(string Field, OrderDirection Direction);
