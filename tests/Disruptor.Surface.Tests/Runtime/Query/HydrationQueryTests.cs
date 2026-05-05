using System.Text.Json;
using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime.Query;

/// <summary>
/// End-to-end coverage of the Hydrate terminal: take a list of ids, materialise into
/// a tracked <see cref="SurrealSession"/>. Pins the wire SQL shape (single
/// <c>WHERE id IN [...]</c> round-trip), the include reuse (any
/// <see cref="IIncludeNode"/> the read-mode query accepts also slots in here), and
/// the empty-list short-circuit.
/// </summary>
public sealed class HydrationQueryTests
{
    [Fact]
    public async Task ExecuteAsync_NoIncludes_EmitsBareWhereIdInRoundTrip()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyResultEnvelope);
        var ids = new[]
        {
            new RecordId("symbols", "01HX7AF5"),
            new RecordId("symbols", "01HX7AF6"),
        };

        await new HydrationQuery<TestSymbol>("symbols", ids, NullReferenceRegistry.Instance)
            .ExecuteAsync(transport);

        Assert.Single(transport.SqlSeen);
        Assert.Equal(
            "SELECT * FROM symbols WHERE id IN [symbols:01HX7AF5, symbols:01HX7AF6];",
            transport.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_WithInclude_PassesThroughToCompiler()
    {
        // Includes flow into the underlying Query<T> compiler unchanged — same wire
        // shape as Query<T>.Where(IdIn).WithInclude(...).ExecuteAsync would emit.
        var transport = new RecordingTransport().ScriptResponse(EmptyResultEnvelope);
        var ids = new[] { new RecordId("designs", "01HX7AF5") };

        await new HydrationQuery<TestSymbol>("designs", ids, NullReferenceRegistry.Instance)
            .WithInclude(new IncludeInlineRefNode("details"))
            .ExecuteAsync(transport);

        Assert.Equal(
            "SELECT *, details.* FROM designs WHERE id IN [designs:01HX7AF5];",
            transport.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyIds_ShortCircuits_NoTransportCall()
    {
        // No ids → no root rows; round-tripping `WHERE id IN []` would just return
        // empty, so skip the network entirely and hand back an empty session.
        var transport = new RecordingTransport();

        var session = await new HydrationQuery<TestSymbol>(
            "symbols", [], NullReferenceRegistry.Instance).ExecuteAsync(transport);

        Assert.NotNull(session);
        Assert.Empty(transport.SqlSeen);
    }

    [Fact]
    public async Task ExecuteAsync_HydratesEachRowIntoTheSession()
    {
        var rowsJson = """
            [{"result":[
                {"id":"symbols:01HX7AF5","description":"first"},
                {"id":"symbols:01HX7AF6","description":"second"}
            ],"status":"OK"}]
            """;
        var transport = new RecordingTransport().ScriptResponse(rowsJson);
        var ids = new[] { new RecordId("symbols", "01HX7AF5"), new RecordId("symbols", "01HX7AF6") };

        var session = await new HydrationQuery<TestSymbol>(
            "symbols", ids, NullReferenceRegistry.Instance).ExecuteAsync(transport);

        var first = session.Get<TestSymbol>(new RecordId("symbols", "01HX7AF5"));
        var second = session.Get<TestSymbol>(new RecordId("symbols", "01HX7AF6"));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("first", first!.Description);
        Assert.Equal("second", second!.Description);
    }

    [Fact]
    public async Task ExecuteAsync_NullLease_Throws()
    {
        // The write-mode overload requires a non-null lease — the lease itself is
        // never stored, but its presence at the call site advertises write intent
        // and ensures the caller has the same handle to pass to CommitAsync later.
        var transport = new RecordingTransport();
        var ids = new[] { new RecordId("symbols", "01HX7AF5") };
        var hq = new HydrationQuery<TestSymbol>("symbols", ids, NullReferenceRegistry.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() => hq.ExecuteAsync(transport, lease: null!));
    }

    private const string EmptyResultEnvelope = "[{\"result\":[],\"status\":\"OK\"}]";

    public sealed class TestSymbol : IEntity
    {
        private RecordId _id;
        private SurrealSession? _session;
        public string? Description { get; private set; }

        public RecordId Id => _id;
        public SurrealSession? Session => _session;
        public void Bind(SurrealSession session) => _session = session;
        public void Initialize(SurrealSession session) { }
        public void Flush(SurrealSession session) { }
        public void OnDeleting() { }
        public void MarkAllSlicesLoaded(IHydrationSink sink) { }
        public void HydratePartial(JsonElement json, IHydrationSink sink) => Hydrate(json, sink);
        public void Hydrate(JsonElement json, IHydrationSink sink)
        {
            if (json.TryGetProperty("id", out var idElem))
            {
                _id = HydrationJson.ReadRecordId(idElem);
            }
            Description = HydrationJson.ReadString(json, "description");
            sink.Track(this);
        }
    }

    private sealed class RecordingTransport : ISurrealTransport
    {
        private readonly Queue<string> responses = new();

        public List<string> SqlSeen { get; } = [];

        public RecordingTransport ScriptResponse(string json)
        {
            responses.Enqueue(json);
            return this;
        }

        public Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
        {
            SqlSeen.Add(sql);
            var json = responses.Count > 0 ? responses.Dequeue() : EmptyResultEnvelope;
            return Task.FromResult(JsonDocument.Parse(json));
        }

        public ValueTask DisposeAsync() => default;
    }
}
