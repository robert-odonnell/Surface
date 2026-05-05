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
    }

    [Fact]
    public async Task ExecuteAsync_WhereIn_EmitsInClause_WithInlinedRecordLiterals()
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

        Assert.Equal(
            "SELECT id, in, out FROM restricts WHERE in IN [constraints:01HX7AF5, constraints:01HX7AF6];",
            transport.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_WhereOut_EmitsOutInClause()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var ids = new[] { new TestTgtId("user_stories", "01HX7AF7") };

        await new EdgeQuery<TestSrcId, TestTgtId>("restricts")
            .WhereOut(ids)
            .ExecuteAsync(transport);

        Assert.Equal(
            "SELECT id, in, out FROM restricts WHERE out IN [user_stories:01HX7AF7];",
            transport.SqlSeen[0]);
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
            "SELECT id, in, out FROM restricts WHERE (in IN [constraints:01HX7AF5] AND out IN [user_stories:01HX7AF7]);",
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
            "SELECT id, in, out FROM restricts WHERE (in IN [constraints:01HX7AF5] AND note = \"x\");",
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
    public async Task DirectionAliases_OutgoingFromAndWhereSource_EmitInColumnFilter()
    {
        // Aliases (WhereSource / OutgoingFrom) must produce the same SQL as the
        // canonical WhereIn — the names exist for read clarity at the call site, not
        // for any semantic difference. Same wire SQL = no behavioural drift.
        var src = new[] { new TestSrcId("constraints", "01HX7AF5") };
        var canonical = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var sourceAlias = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var outgoingAlias = new RecordingTransport().ScriptResponse(EmptyEnvelope);

        await new EdgeQuery<TestSrcId, TestTgtId>("restricts").WhereIn(src).ExecuteAsync(canonical);
        await new EdgeQuery<TestSrcId, TestTgtId>("restricts").WhereSource(src).ExecuteAsync(sourceAlias);
        await new EdgeQuery<TestSrcId, TestTgtId>("restricts").OutgoingFrom(src).ExecuteAsync(outgoingAlias);

        Assert.Equal(canonical.SqlSeen[0], sourceAlias.SqlSeen[0]);
        Assert.Equal(canonical.SqlSeen[0], outgoingAlias.SqlSeen[0]);
        Assert.Contains("WHERE in IN", canonical.SqlSeen[0]);
    }

    [Fact]
    public async Task DirectionAliases_IncomingToAndWhereTarget_EmitOutColumnFilter()
    {
        var tgt = new[] { new TestTgtId("user_stories", "01HX7AF7") };
        var canonical = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var targetAlias = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var incomingAlias = new RecordingTransport().ScriptResponse(EmptyEnvelope);

        await new EdgeQuery<TestSrcId, TestTgtId>("restricts").WhereOut(tgt).ExecuteAsync(canonical);
        await new EdgeQuery<TestSrcId, TestTgtId>("restricts").WhereTarget(tgt).ExecuteAsync(targetAlias);
        await new EdgeQuery<TestSrcId, TestTgtId>("restricts").IncomingTo(tgt).ExecuteAsync(incomingAlias);

        Assert.Equal(canonical.SqlSeen[0], targetAlias.SqlSeen[0]);
        Assert.Equal(canonical.SqlSeen[0], incomingAlias.SqlSeen[0]);
        Assert.Contains("WHERE out IN", canonical.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResult_ReturnsEmptyList()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);

        var rows = await new EdgeQuery<TestSrcId, TestTgtId>("restricts").ExecuteAsync(transport);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ExecuteAsync_WithLimit_AppendsLimitClause()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);

        await new EdgeQuery<TestSrcId, TestTgtId>("restricts")
            .Limit(25)
            .ExecuteAsync(transport);

        Assert.Equal("SELECT id, in, out FROM restricts LIMIT 25;", transport.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_WithOrderByOnPayloadField_AppendsOrderByClause()
    {
        // Edge payload predicate factories produce PropertyExpr<T> instances; they
        // flow into EdgeQuery.OrderBy the same way they do for entity Query<T>.
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);

        await new EdgeQuery<TestSrcId, TestTgtId>("uses")
            .OrderBy(new PropertyExpr<int>("line"), OrderDirection.Descending)
            .Limit(5)
            .ExecuteAsync(transport);

        Assert.Equal(
            "SELECT id, in, out FROM uses ORDER BY line DESC LIMIT 5;",
            transport.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_WhereOnPayloadField_FiltersServerSide()
    {
        // The whole point of the edge-payload factory + EdgeQuery.Where(IPredicate)
        // pairing — `UsesEdgeQ.Kind.Eq("call")` materialises as a server-side WHERE
        // clause instead of pulling rows and filtering in-process.
        var transport = new RecordingTransport().ScriptResponse(EmptyEnvelope);
        var src = new[] { new TestSrcId("symbols", "01HX") };

        await new EdgeQuery<TestSrcId, TestTgtId>("uses")
            .OutgoingFrom(src)
            .Where(new PropertyExpr<string>("kind").Eq("call"))
            .ExecuteAsync(transport);

        Assert.Equal(
            "SELECT id, in, out FROM uses WHERE (in IN [symbols:01HX] AND kind = \"call\");",
            transport.SqlSeen[0]);
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

        public List<string> SqlSeen { get; } = [];

        public RecordingTransport ScriptResponse(string json)
        {
            responses.Enqueue(json);
            return this;
        }

        public Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
        {
            SqlSeen.Add(sql);
            var json = responses.Count > 0 ? responses.Dequeue() : EmptyEnvelope;
            return Task.FromResult(JsonDocument.Parse(json));
        }

        public ValueTask DisposeAsync() => default;
    }
}
