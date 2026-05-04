using System.Text.Json;
using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime.Query;

public sealed class EdgeQueryTests
{
    [Fact]
    public async Task ExecuteAsync_NoFilters_EmitsBareEdgeSelect()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);

        await new EdgeQuery<TestSrcId, TestTgtId>("restricts").ExecuteAsync(transport);

        Assert.Equal("SELECT id, in, out FROM restricts;", transport.SqlSeen[0]);
        Assert.Null(transport.VarsSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_WhereIn_EmitsInClause_AndNormalisesIds()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var ids = new[]
        {
            new TestSrcId("constraints", "01HX7AF5"),
            new TestSrcId("constraints", "01HX7AF6"),
        };

        await new EdgeQuery<TestSrcId, TestTgtId>("restricts")
            .WhereIn(ids)
            .ExecuteAsync(transport);

        Assert.Equal("SELECT id, in, out FROM restricts WHERE in IN $p0;", transport.SqlSeen[0]);

        var bindings = (IReadOnlyDictionary<string, object?>)transport.VarsSeen[0]!;
        var list = Assert.IsAssignableFrom<IReadOnlyList<object?>>(bindings["p0"]);
        Assert.Equal(2, list.Count);
        Assert.IsType<RecordId>(list[0]);
        Assert.IsType<RecordId>(list[1]);
    }

    [Fact]
    public async Task ExecuteAsync_WhereOut_EmitsOutInClause()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var ids = new[] { new TestTgtId("user_stories", "01HX7AF7") };

        await new EdgeQuery<TestSrcId, TestTgtId>("restricts")
            .WhereOut(ids)
            .ExecuteAsync(transport);

        Assert.Equal("SELECT id, in, out FROM restricts WHERE out IN $p0;", transport.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_BothSides_AndsTheClauses()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var src = new[] { new TestSrcId("constraints", "01HX7AF5") };
        var tgt = new[] { new TestTgtId("user_stories", "01HX7AF7") };

        await new EdgeQuery<TestSrcId, TestTgtId>("restricts")
            .WhereIn(src)
            .WhereOut(tgt)
            .ExecuteAsync(transport);

        Assert.Equal(
            "SELECT id, in, out FROM restricts WHERE (in IN $p0 AND out IN $p1);",
            transport.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_ExtraPredicate_AndsAfterSideFilters()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var src = new[] { new TestSrcId("constraints", "01HX7AF5") };

        await new EdgeQuery<TestSrcId, TestTgtId>("restricts")
            .WhereIn(src)
            .Where(new EqPredicate("note", "x"))
            .ExecuteAsync(transport);

        Assert.Equal(
            "SELECT id, in, out FROM restricts WHERE (in IN $p0 AND note = $p1);",
            transport.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_HydratesEdgeRowsFromInOutColumns()
    {
        var rowsJson = """
            [{"result":[
                {"id":"restricts:e1","in":"constraints:01HX7AF5","out":"user_stories:01HX7AF7"},
                {"id":"restricts:e2","in":"constraints:01HX7AF5","out":"user_stories:01HX7AF8"}
            ],"status":"OK"}]
            """;
        var transport = new RecordingTransport().ScriptResponse(rowsJson);

        var rows = await new EdgeQuery<TestSrcId, TestTgtId>("restricts").ExecuteAsync(transport);

        Assert.Collection(rows,
            r =>
            {
                Assert.Equal(new RecordId("constraints", "01HX7AF5"), r.Source);
                Assert.Equal(new RecordId("user_stories", "01HX7AF7"), r.Target);
            },
            r =>
            {
                Assert.Equal(new RecordId("constraints", "01HX7AF5"), r.Source);
                Assert.Equal(new RecordId("user_stories", "01HX7AF8"), r.Target);
            });
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResult_ReturnsEmptyList()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);

        var rows = await new EdgeQuery<TestSrcId, TestTgtId>("restricts").ExecuteAsync(transport);

        Assert.Empty(rows);
    }

    private const string EmptyEnvelope = "[{\"result\":[],\"status\":\"OK\"}]";

    private readonly record struct TestSrcId(string Table, string Value) : IRecordId
    {
        public string ToLiteral() => Value;
    }

    private readonly record struct TestTgtId(string Table, string Value) : IRecordId
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
            var json = responses.Count > 0 ? responses.Dequeue() : EmptyEnvelope;
            return Task.FromResult(JsonDocument.Parse(json));
        }

        public ValueTask DisposeAsync() => default;
    }
}
