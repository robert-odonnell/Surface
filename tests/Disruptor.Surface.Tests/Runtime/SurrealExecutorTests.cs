using System.Text.Json;
using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Coverage for the next-generation transport boundary: <see cref="ISurrealExecutor"/>
/// + <see cref="SurrealCommand"/>. Pins the wrapper/passthrough behaviour and the
/// AsExecutor extension so callers holding <see cref="ISurrealTransport"/> can up-cast
/// without extra ceremony.
/// </summary>
public sealed class SurrealExecutorTests
{
    [Fact]
    public async Task Adapter_PassesSqlThrough_DiscardsParameters()
    {
        // Today's compilers inline every literal upstream, so executors that wrap a
        // legacy ISurrealTransport drop the parameter dict and just hand the SQL over.
        var transport = new RecordingTransport();
        ISurrealExecutor executor = new SurrealTransportExecutorAdapter(transport);

        await executor.ExecuteAsync(new SurrealCommand(
            "SELECT * FROM symbols;",
            new Dictionary<string, object?> { ["ignored"] = "value" }));

        Assert.Single(transport.SqlSeen);
        Assert.Equal("SELECT * FROM symbols;", transport.SqlSeen[0]);
    }

    [Fact]
    public async Task AsExecutor_ReturnsTransportItself_WhenItAlreadyImplementsBoth()
    {
        // Optimisation: if the underlying transport already implements
        // ISurrealExecutor (the production HTTP/Embedded transports do), AsExecutor
        // skips the adapter and returns it directly.
        var transport = new DualTransport();
        var executor = ((ISurrealTransport)transport).AsExecutor();

        Assert.Same(transport, executor);

        await executor.ExecuteAsync(new SurrealCommand("SELECT 1;"));
        Assert.Equal("SELECT 1;", transport.LastSql);
    }

    [Fact]
    public async Task AsExecutor_WrapsLegacyTransport_InAdapter()
    {
        var transport = new RecordingTransport();
        var executor = transport.AsExecutor();

        Assert.IsType<SurrealTransportExecutorAdapter>(executor);

        await executor.ExecuteAsync(new SurrealCommand("INFO FOR DB;"));
        Assert.Equal("INFO FOR DB;", transport.SqlSeen[0]);
    }

    [Fact]
    public async Task DisposeAsync_PropagatesToWrappedTransport()
    {
        var transport = new RecordingTransport();
        var adapter = new SurrealTransportExecutorAdapter(transport);

        await adapter.DisposeAsync();

        Assert.True(transport.Disposed);
    }

    private sealed class RecordingTransport : ISurrealTransport
    {
        public List<string> SqlSeen { get; } = [];
        public bool Disposed { get; private set; }

        public Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
        {
            SqlSeen.Add(sql);
            return Task.FromResult(JsonDocument.Parse("[{\"result\":[],\"status\":\"OK\"}]"));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return default;
        }
    }

    /// <summary>Mirrors what production transports do — implements both interfaces directly.</summary>
    private sealed class DualTransport : ISurrealTransport, ISurrealExecutor
    {
        public string? LastSql { get; private set; }

        public Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
        {
            LastSql = sql;
            return Task.FromResult(JsonDocument.Parse("[{\"result\":[],\"status\":\"OK\"}]"));
        }

        public Task<JsonDocument> ExecuteAsync(SurrealCommand command, CancellationToken ct = default)
            => ExecuteAsync(command.Sql, ct);

        public ValueTask DisposeAsync() => default;
    }
}
