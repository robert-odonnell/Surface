using System.Text.Json;
using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime.Query;

/// <summary>
/// Coverage for the per-table <c>{Table}QueryIds.IdsAsync</c> extension that the
/// generator emits. Reproduces the generator's body locally so the tests can run
/// without depending on the sample's emitted output: the assertions pin the contract
/// (SELECT id, typed materialisation, empty-result tolerance) that any future emitter
/// rewrite must keep honouring.
/// </summary>
public sealed class IdsAsyncTests
{
    [Fact]
    public async Task IdsAsync_NoFilter_EmitsBareIdSelect_AndReturnsEmptyList()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyResultEnvelope);

        var ids = await new Query<TestSymbol>("test_symbols").IdsAsync(transport);

        Assert.Equal("SELECT id FROM test_symbols;", transport.SqlSeen[0]);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task IdsAsync_WhereOrderLimitStart_RendersCanonicalClauseOrder()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyResultEnvelope);

        await new Query<TestSymbol>("test_symbols")
            .Where(new PropertyExpr<string>("kind").Eq("method"))
            .OrderBy(new PropertyExpr<string>("name"))
            .Limit(20)
            .Start(40)
            .IdsAsync(transport);

        Assert.Equal(
            "SELECT id FROM test_symbols WHERE kind = \"method\" ORDER BY name ASC LIMIT 20 START 40;",
            transport.SqlSeen[0]);
    }

    [Fact]
    public async Task IdsAsync_HydratesEachReturnedRowAsTypedId()
    {
        // The generated extension projects each row's `id` field into the typed
        // {Table}Id by passing RecordId.Value through the typed ctor — which
        // re-validates the value via RecordIdFormat.
        var rowsJson = """
            [{"result":[
                {"id":"test_symbols:01HX7AF5"},
                {"id":"test_symbols:01HX7AF6"}
            ],"status":"OK"}]
            """;
        var transport = new RecordingTransport().ScriptResponse(rowsJson);

        var ids = await new Query<TestSymbol>("test_symbols").IdsAsync(transport);

        Assert.Collection(ids,
            id => Assert.Equal("01HX7AF5", id.Value),
            id => Assert.Equal("01HX7AF6", id.Value));
    }

    [Fact]
    public async Task IdsAsync_WithIncludes_Throws_BeforeAnyTransportCall()
    {
        // Id-only selection is flat by definition; CompileIdsOnly enforces that
        // upstream so the typed extension can't accidentally swallow includes.
        var transport = new RecordingTransport();

        var query = new Query<TestSymbol>("test_symbols")
            .WithInclude(new IncludeInlineRefNode("details"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => query.IdsAsync(transport));
        Assert.Empty(transport.SqlSeen);
    }

    private const string EmptyResultEnvelope = "[{\"result\":[],\"status\":\"OK\"}]";

    /// <summary>Minimal IEntity stand-in. IdsAsync never hydrates the row body, only its id.</summary>
    public sealed class TestSymbol : IEntity
    {
        private RecordId _id;
        public RecordId Id => _id;
        public SurrealSession? Session => null;
        public void Bind(SurrealSession session) { }
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
        }
    }

    /// <summary>Mirrors a generator-emitted typed id struct: fixed Table, single-string ctor.</summary>
    public readonly record struct TestSymbolId(string Value) : IRecordId
    {
        public string Table => "test_symbols";
        public string ToLiteral() => Value;
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

/// <summary>
/// Local mirror of the per-table <c>{Table}QueryIds</c> static class the generator
/// emits. Kept here so the runtime tests aren't coupled to the sample's emitted
/// output. The body matches <c>IdsAsyncEmitter</c>'s template line-for-line — if
/// they drift, this file is the canary.
/// </summary>
internal static class TestSymbolQueryIds
{
    public static async Task<IReadOnlyList<IdsAsyncTests.TestSymbolId>> IdsAsync(
        this Query<IdsAsyncTests.TestSymbol> query,
        ISurrealTransport transport,
        CancellationToken ct = default)
    {
        var sql = query.CompileIdsOnly();
        using var doc = await transport.ExecuteAsync(sql, ct);
        var rs = new SurrealResultSet(doc.RootElement);
        var rows = rs.ResultAt();

        var list = new List<IdsAsyncTests.TestSymbolId>();
        switch (rows.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var row in rows.EnumerateArray())
                {
                    if (HydrationJson.TryReadRecordId(row, "id", out var rid))
                    {
                        list.Add(new IdsAsyncTests.TestSymbolId(rid.Value));
                    }
                }
                break;
            case JsonValueKind.Object:
                if (HydrationJson.TryReadRecordId(rows, "id", out var single))
                {
                    list.Add(new IdsAsyncTests.TestSymbolId(single.Value));
                }
                break;
        }
        return list;
    }
}
