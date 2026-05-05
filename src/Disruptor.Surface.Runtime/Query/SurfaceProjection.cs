using System.Text.Json;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Static entry point for constructing <see cref="ISurfaceProjection{TRow}"/> instances.
/// The library does not generate projection types — users define the <c>TRow</c> shape
/// (typically a positional record) and pass a materialise lambda that reads each
/// column via <see cref="IProjectionRow.Read{T}"/>:
/// <code>
/// public sealed record SymbolSearchResult(string Name, string QualifiedName, int Line);
///
/// public static class SymbolProjections
/// {
///     public static readonly ISurfaceProjection&lt;SymbolSearchResult&gt; SearchResult =
///         SurfaceProjection.For&lt;SymbolSearchResult&gt;(row =&gt; new SymbolSearchResult(
///             Name:          row.Read(CodeSymbolQ.Name),
///             QualifiedName: row.Read(CodeSymbolQ.QualifiedName),
///             Line:          row.Read(CodeSymbolQ.Line)));
/// }
/// </code>
/// <para>
/// At construction time the lambda runs once with a discovery probe row that captures
/// each <c>Read</c>'d field; that list becomes the SurrealQL SELECT projection. At
/// query time the lambda runs once per result row with a real JSON-backed row.
/// </para>
/// </summary>
public static class SurfaceProjection
{
    /// <summary>
    /// Build a projection from a materialise lambda. The lambda runs once at
    /// construction time with a probe row to discover the field list; if that probe
    /// throws (typically because the target type's constructor rejects the default
    /// values the probe hands back), the failure surfaces as
    /// <see cref="ProjectionDiscoveryException"/> with hints on how to make the
    /// constructor probe-safe.
    /// </summary>
    public static ISurfaceProjection<TRow> For<TRow>(Func<IProjectionRow, TRow> materialise)
    {
        ArgumentNullException.ThrowIfNull(materialise);

        var discovery = new DiscoveryProjectionRow();
        try
        {
            _ = materialise(discovery);
        }
        catch (Exception ex)
        {
            throw new ProjectionDiscoveryException(
                $"Failed to discover projection fields for {typeof(TRow).Name}. The materialise " +
                "lambda must run cleanly with default values during the construction-time probe — " +
                "ensure the target type's constructor accepts default/null values without throwing. " +
                "Common cause: ArgumentException.ThrowIfNullOrEmpty(...) in a record constructor.",
                ex);
        }

        if (discovery.Fields.Count == 0)
        {
            throw new InvalidOperationException(
                $"Projection for {typeof(TRow).Name} discovered zero fields. The materialise lambda " +
                "must call IProjectionRow.Read at least once.");
        }

        return new SurfaceProjection<TRow>(discovery.Fields, materialise);
    }
}

/// <summary>
/// Default <see cref="ISurfaceProjection{TRow}"/> implementation: holds the discovered
/// field list and the user's materialise lambda. Each <see cref="Materialise"/> call
/// wraps the row in a <see cref="JsonProjectionRow"/> and runs the lambda again; the
/// lambda's <see cref="IProjectionRow.Read{T}"/> calls hit the real JSON values.
/// </summary>
internal sealed class SurfaceProjection<TRow> : ISurfaceProjection<TRow>
{
    private readonly Func<IProjectionRow, TRow> materialise;

    public IReadOnlyList<string> SelectFields { get; }

    public SurfaceProjection(IReadOnlyList<string> selectFields, Func<IProjectionRow, TRow> materialise)
    {
        SelectFields = selectFields;
        this.materialise = materialise;
    }

    public TRow Materialise(JsonElement row) => materialise(new JsonProjectionRow(row));
}
