using System.Text.Json;
using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime.Query;

/// <summary>
/// End-to-end coverage of the projection terminal: <c>SurfaceProjection.For&lt;TRow&gt;(...)</c>
/// → <c>Query&lt;T&gt;.Select(projection).ExecuteAsync(transport)</c> → typed result rows.
/// Pins the SQL shape (compiler emits the discovered field list, in order, with no
/// other columns), the materialise contract (JSON columns flow through the projection
/// lambda's <c>Read&lt;T&gt;</c> calls), and the safety nets (Includes rejected,
/// throwing constructors surface a typed discovery error).
/// </summary>
public sealed class ProjectionQueryTests
{
    private static readonly PropertyExpr<string> NameExpr = new("name");
    private static readonly PropertyExpr<string> KindExpr = new("kind");
    private static readonly PropertyExpr<int> LineExpr = new("line");

    [Fact]
    public void For_DiscoversFieldsInLambdaOrder()
    {
        // Discovery captures field names in the order the lambda first reads them.
        // The compiler then emits SELECT in that same order.
        var projection = SurfaceProjection.For<SearchResult>(row => new SearchResult(
            Name: row.Read(NameExpr),
            Kind: row.Read(KindExpr),
            Line: row.Read(LineExpr)));

        Assert.Equal(["name", "kind", "line"], projection.SelectFields);
    }

    [Fact]
    public void For_ThrowsWhenLambdaReadsNoFields()
    {
        // A no-op lambda discovers nothing; the resulting SELECT would be empty SurrealQL.
        Assert.Throws<InvalidOperationException>(
            () => SurfaceProjection.For<SearchResult>(_ => new SearchResult("a", "b", 0)));
    }

    [Fact]
    public void For_WrapsConstructorThrows_AsTypedDiscoveryException()
    {
        // Records that validate inputs in the constructor reject the discovery probe's
        // default values; the failure surfaces as a typed exception with hints.
        var ex = Assert.Throws<ProjectionDiscoveryException>(
            () => SurfaceProjection.For<RejectsNullName>(row => new RejectsNullName(
                Name: row.Read(NameExpr))));

        Assert.IsAssignableFrom<ArgumentException>(ex.InnerException);
        Assert.Contains("RejectsNullName", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_EmitsProjectedSelect_WithCanonicalClauseOrder()
    {
        var transport = new RecordingTransport().ScriptResponse(EmptyResultEnvelope);
        var projection = SurfaceProjection.For<SearchResult>(row => new SearchResult(
            Name: row.Read(NameExpr),
            Kind: row.Read(KindExpr),
            Line: row.Read(LineExpr)));

        await new Query<TestSymbol>("symbols")
            .Where(KindExpr.Eq("method"))
            .OrderBy(NameExpr)
            .Limit(20)
            .Start(40)
            .Select(projection)
            .ExecuteAsync(transport);

        Assert.Equal(
            "SELECT name, kind, line FROM symbols WHERE kind = \"method\" ORDER BY name ASC LIMIT 20 START 40;",
            transport.SqlSeen[0]);
    }

    [Fact]
    public async Task ExecuteAsync_MaterialisesEachRowThroughProjection()
    {
        var rowsJson = """
            [{"result":[
                {"name":"Parse","kind":"method","line":12},
                {"name":"Render","kind":"method","line":48}
            ],"status":"OK"}]
            """;
        var transport = new RecordingTransport().ScriptResponse(rowsJson);
        var projection = SurfaceProjection.For<SearchResult>(row => new SearchResult(
            Name: row.Read(NameExpr),
            Kind: row.Read(KindExpr),
            Line: row.Read(LineExpr)));

        var rows = await new Query<TestSymbol>("symbols")
            .Select(projection)
            .ExecuteAsync(transport);

        Assert.Collection(rows,
            r => { Assert.Equal("Parse", r.Name); Assert.Equal("method", r.Kind); Assert.Equal(12, r.Line); },
            r => { Assert.Equal("Render", r.Name); Assert.Equal("method", r.Kind); Assert.Equal(48, r.Line); });
    }

    [Fact]
    public async Task ExecuteAsync_TolereatesMissingAndNullColumns()
    {
        // Surreal omits null fields from SELECT projections by default; the JSON row
        // path returns default(T) for missing fields and explicit JSON null alike.
        var rowsJson = """
            [{"result":[
                {"name":"Parse","line":12},
                {"name":"Render","kind":null,"line":48}
            ],"status":"OK"}]
            """;
        var transport = new RecordingTransport().ScriptResponse(rowsJson);
        var projection = SurfaceProjection.For<SearchResult>(row => new SearchResult(
            Name: row.Read(NameExpr),
            Kind: row.Read(KindExpr),
            Line: row.Read(LineExpr)));

        var rows = await new Query<TestSymbol>("symbols").Select(projection).ExecuteAsync(transport);

        Assert.Equal(2, rows.Count);
        Assert.Null(rows[0].Kind);
        Assert.Null(rows[1].Kind);
    }

    [Fact]
    public void Select_RejectsQueryWithIncludes()
    {
        // Projection compiles to a flat SELECT; Includes would silently render as a
        // SELECT-list mismatch against the expected projection field list.
        var projection = SurfaceProjection.For<SearchResult>(row => new SearchResult(
            Name: row.Read(NameExpr), Kind: row.Read(KindExpr), Line: row.Read(LineExpr)));

        var query = new Query<TestSymbol>("symbols")
            .WithInclude(new IncludeInlineRefNode("details"));

        Assert.Throws<InvalidOperationException>(() => query.Select(projection));
    }

    [Fact]
    public void Select_NullProjection_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new Query<TestSymbol>("symbols").Select<SearchResult>(null!));
    }

    private const string EmptyResultEnvelope = "[{\"result\":[],\"status\":\"OK\"}]";

    public sealed record SearchResult(string? Name, string? Kind, int Line);

    public sealed record RejectsNullName
    {
        public string Name { get; }
        public RejectsNullName(string Name)
        {
            ArgumentException.ThrowIfNullOrEmpty(Name);
            this.Name = Name;
        }
    }

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
        public void Hydrate(JsonElement json, IHydrationSink sink) { }
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
