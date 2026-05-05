using System.Text.Json;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// The next-generation transport boundary: takes a <see cref="SurrealCommand"/>
/// instead of a bare SQL string, returns the same raw <see cref="JsonDocument"/> as
/// <see cref="ISurrealTransport"/>. Same return shape, richer input shape — opens
/// the door for parameter-aware execution paths (embedded engines that bind natively,
/// future HTTP/JSON-RPC paths that don't lose <c>Thing</c> types) without touching
/// today's inlined-literal callers.
/// <para>
/// Phase 4 of the query/load split lands the contract; runtime callsites stay on
/// <see cref="ISurrealTransport"/> for now and migrate as use cases land.
/// Implementations of HTTP and Embedded transport implement both — the
/// <see cref="ISurrealExecutor"/> path delegates to the legacy ExecuteAsync,
/// discarding parameters because everything's already inlined upstream.
/// </para>
/// </summary>
public interface ISurrealExecutor : IAsyncDisposable
{
    /// <summary>
    /// Execute the command against the underlying engine. Returns the raw response
    /// envelope as a <see cref="JsonDocument"/> — caller is responsible for
    /// disposal (typically wrapped in a <c>using</c> at the call site, then read
    /// via <see cref="SurrealResultSet"/>).
    /// </summary>
    Task<JsonDocument> ExecuteAsync(SurrealCommand command, CancellationToken ct = default);
}

/// <summary>
/// Bridge that exposes any <see cref="ISurrealTransport"/> as an
/// <see cref="ISurrealExecutor"/> by passing through the SQL and discarding the
/// parameter dictionary. Useful for callers that hold an <see cref="ISurrealTransport"/>
/// (e.g. legacy code, third-party transports) and need to satisfy a newer API that
/// only takes <see cref="ISurrealExecutor"/>.
/// </summary>
public sealed class SurrealTransportExecutorAdapter(ISurrealTransport transport) : ISurrealExecutor
{
    public Task<JsonDocument> ExecuteAsync(SurrealCommand command, CancellationToken ct = default)
        => transport.ExecuteAsync(command.Sql, ct);

    public ValueTask DisposeAsync() => transport.DisposeAsync();
}

/// <summary>Convenience extensions bridging the two transport shapes.</summary>
public static class SurrealExecutorExtensions
{
    /// <summary>Wrap an <see cref="ISurrealTransport"/> as an <see cref="ISurrealExecutor"/>.</summary>
    public static ISurrealExecutor AsExecutor(this ISurrealTransport transport)
        => transport as ISurrealExecutor ?? new SurrealTransportExecutorAdapter(transport);
}
