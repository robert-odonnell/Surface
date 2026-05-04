using System.Text.Json;
using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime.Query;

/// <summary>
/// End-to-end exercise of <c>Query&lt;T&gt;.ExecuteAsync</c>: compile an AST, send it
/// through a recording transport, hydrate the response into detached entities. Doesn't
/// need a live SurrealDB — the recording transport replays canned JSON envelopes.
/// </summary>
public sealed class QueryExecutionTests
{
    [Fact]
    public async Task ExecuteAsync_SendsCompiledSqlAndBindings()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyResultEnvelope);

        await new Query<TestEntity>("test_entities")
            .Where(new EqPredicate("description", "hello"))
            .ExecuteAsync(transport);

        Assert.Single(transport.SqlSeen);
        Assert.Equal("SELECT * FROM test_entities WHERE description = $p0;", transport.SqlSeen[0]);

        var vars = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(transport.VarsSeen[0]);
        Assert.Equal("hello", vars["p0"]);
    }

    [Fact]
    public async Task ExecuteAsync_WithIdPin_FlowsCanonicalRecordId()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyResultEnvelope);
        var typed = new TestEntityId("test_entities", "01HX7AF5");

        await new Query<TestEntity>("test_entities")
            .WithId(typed)
            .ExecuteAsync(transport);

        Assert.Equal("SELECT * FROM test_entities WHERE id = $p0;", transport.SqlSeen[0]);

        var vars = (IReadOnlyDictionary<string, object?>)transport.VarsSeen[0]!;
        var bound = Assert.IsType<RecordId>(vars["p0"]);
        Assert.Equal("test_entities", bound.Table);
        Assert.Equal("01HX7AF5", bound.Value);
    }

    [Fact]
    public async Task ExecuteAsync_HydratesEachReturnedRow()
    {
        // Two rows in the result; each populates the entity's _description backing
        // field via Hydrate. The detached entities should expose those values through
        // the public Description property.
        var rowsJson = """
            [{"result":[
                {"id":"test_entities:01HX7AF5","description":"first"},
                {"id":"test_entities:01HX7AF6","description":"second"}
            ],"status":"OK"}]
            """;
        var transport = new RecordingTransport().ScriptResponse(rowsJson);

        var results = await new Query<TestEntity>("test_entities")
            .ExecuteAsync(transport);

        Assert.Collection(results,
            e => Assert.Equal("first", e.Description),
            e => Assert.Equal("second", e.Description));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsEmptyListWhenResultIsNullOrUndefined()
    {
        var transport = new RecordingTransport().ScriptResponse(
            "[{\"result\":null,\"status\":\"OK\"}]");

        var results = await new Query<TestEntity>("test_entities")
            .ExecuteAsync(transport);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ExecuteAsync_DetachedEntities_HaveNoSession()
    {
        var rowsJson = """
            [{"result":[{"id":"test_entities:01HX7AF5","description":"x"}],"status":"OK"}]
            """;
        var transport = new RecordingTransport().ScriptResponse(rowsJson);

        var results = await new Query<TestEntity>("test_entities")
            .ExecuteAsync(transport);

        var entity = Assert.Single(results);
        Assert.Null(((IEntity)entity).Session);
    }

    private const string EmptyResultEnvelope = "[{\"result\":[],\"status\":\"OK\"}]";

    /// <summary>Test stand-in for a generated entity. Implements just enough of IEntity to compile.</summary>
    public sealed class TestEntity : IEntity
    {
        private RecordId _id;
        public string? Description { get; private set; }

        public RecordId Id => _id;
        public SurrealSession? Session => null;
        public void Bind(SurrealSession session) { }
        public void Initialize(SurrealSession session) { }
        public void Flush(SurrealSession session) { }
        public void OnDeleting() { }

        public void Hydrate(JsonElement json, IHydrationSink sink)
        {
            if (json.TryGetProperty("id", out var idElem))
            {
                var rid = HydrationJson.ReadRecordId(idElem);
                _id = rid;
            }
            Description = HydrationJson.ReadString(json, "description");
        }
    }

    private readonly record struct TestEntityId(string Table, string Value) : IRecordId
    {
        public string ToLiteral() => Value;
    }

    private sealed class RecordingTransport : ISurrealTransport
    {
        private readonly Queue<string> responses = new();

        public List<string> SqlSeen { get; } = new();
        public List<object?> VarsSeen { get; } = new();

        public RecordingTransport ScriptResponse(string json)
        {
            responses.Enqueue(json);
            return this;
        }

        public Task<JsonDocument> ExecuteAsync(string sql, object? vars = null, CancellationToken ct = default)
        {
            SqlSeen.Add(sql);
            VarsSeen.Add(vars);
            var json = responses.Count > 0 ? responses.Dequeue() : "[{\"result\":[],\"status\":\"OK\"}]";
            return Task.FromResult(JsonDocument.Parse(json));
        }

        public ValueTask DisposeAsync() => default;
    }
}
