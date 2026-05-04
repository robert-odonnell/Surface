using System.Text.Json;
using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Locks in the CAS-on-sequence protocol redesign: AcquireAsync reads the current seq
/// (defaulting to 0 if no row), CommitAsync splices a transactional CAS clause around
/// the data writes, stolen leases surface as <see cref="WriterLeaseStolenException"/>,
/// successful commits advance the lease's <c>ExpectedSequence</c>, and the slug-id
/// validation kicks in at the call site.
/// </summary>
public sealed class WriterLeaseTests
{
    [Fact]
    public async Task AcquireAsync_NoExistingRow_CapturesSeqZero()
    {
        var transport = new RecordingTransport().ScriptResponse(NoRowResponse);

        var lease = await WriterLease.AcquireAsync(transport, "design");

        Assert.Equal("design", lease.AggregateName);
        Assert.Equal(0, lease.ExpectedSequence);
        Assert.Single(transport.SqlSeen);
        Assert.Contains("SELECT seq FROM writer_lease:design", transport.SqlSeen[0]);
    }

    [Fact]
    public async Task AcquireAsync_ExistingRow_CapturesItsSeq()
    {
        var transport = new RecordingTransport().ScriptResponse("[{\"result\":[{\"seq\":42}],\"status\":\"OK\"}]");

        var lease = await WriterLease.AcquireAsync(transport, "design");

        Assert.Equal(42, lease.ExpectedSequence);
    }

    [Theory]
    [InlineData("Design")]                                       // uppercase
    [InlineData("design-1")]                                     // hyphen
    [InlineData("design 1")]                                     // space
    [InlineData("01HXY7Z8K2N5VQDM9PE3WBFTGRX")]                  // 27-char Ulid (slug max 32 — but starts with digit)
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]            // 33 chars
    public async Task AcquireAsync_InvalidAggregateName_ThrowsBeforeSqlIsSent(string aggregate)
    {
        var transport = new RecordingTransport();

        await Assert.ThrowsAsync<FormatException>(
            () => WriterLease.AcquireAsync(transport, aggregate));
        Assert.Empty(transport.SqlSeen);
    }

    [Fact]
    public async Task CommitAsync_WithLease_SplicesCASClause_AroundDataWrites()
    {
        // Acquire pulls seq=7. The CAS fragment must reference seq 7, the script must
        // open with BEGIN TRANSACTION, the data writes must sit inside it, and it must
        // close with COMMIT TRANSACTION.
        var transport = new RecordingTransport()
            .ScriptResponse("[{\"result\":[{\"seq\":7}],\"status\":\"OK\"}]")  // acquire
            .ScriptResponse(EmptySuccessResponse);                              // commit

        var lease = await WriterLease.AcquireAsync(transport, "design");
        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        session.SetField(entity.Id, "description", "edited");

        await session.CommitAsync(transport, lease);

        var commitSql = transport.SqlSeen[1];
        Assert.StartsWith("BEGIN TRANSACTION", commitSql.TrimStart());
        Assert.EndsWith("COMMIT TRANSACTION;", commitSql.TrimEnd());
        Assert.Contains("$writer_lease_expected = 7", commitSql);
        Assert.Contains("THROW \"writer_lease_stolen:design\"", commitSql);
        Assert.Contains("UPSERT writer_lease:design", commitSql);
        Assert.Contains("seq: $writer_lease_expected + 1", commitSql);
        // Data write is in the same script.
        Assert.Contains("designs:x", commitSql);
    }

    [Fact]
    public async Task CommitAsync_WithLease_OnSuccess_AdvancesExpectedSequence()
    {
        var transport = new RecordingTransport()
            .ScriptResponse("[{\"result\":[{\"seq\":3}],\"status\":\"OK\"}]")
            .ScriptResponse(EmptySuccessResponse);

        var lease = await WriterLease.AcquireAsync(transport, "design");
        Assert.Equal(3, lease.ExpectedSequence);

        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        session.SetField(entity.Id, "description", "edited");
        await session.CommitAsync(transport, lease);

        Assert.Equal(4, lease.ExpectedSequence);
    }

    [Fact]
    public async Task CommitAsync_WithLease_OnStolenError_ThrowsWriterLeaseStolen()
    {
        // Surreal's THROW "writer_lease_stolen:design" surfaces from the transport as a
        // SurrealException whose message contains the marker. SurrealSession.CommitAsync
        // catches and translates.
        var transport = new RecordingTransport()
            .ScriptResponse("[{\"result\":[{\"seq\":5}],\"status\":\"OK\"}]")
            .ScriptThrow(new SurrealException(
                "SurrealDB statement failed: writer_lease_stolen:design (current 9, expected 5)",
                retryable: false));

        var lease = await WriterLease.AcquireAsync(transport, "design");
        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        session.SetField(entity.Id, "description", "edited");

        var ex = await Assert.ThrowsAsync<WriterLeaseStolenException>(
            () => session.CommitAsync(transport, lease));
        Assert.Equal("design", ex.AggregateName);
        Assert.Equal(5, ex.ExpectedSequence);
    }

    [Fact]
    public async Task CommitAsync_WithLease_OnStolenError_ClosesSession()
    {
        var transport = new RecordingTransport()
            .ScriptResponse("[{\"result\":[{\"seq\":5}],\"status\":\"OK\"}]")
            .ScriptThrow(new SurrealException("writer_lease_stolen:design", retryable: false));

        var lease = await WriterLease.AcquireAsync(transport, "design");
        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        session.SetField(entity.Id, "description", "edited");

        await Assert.ThrowsAsync<WriterLeaseStolenException>(
            () => session.CommitAsync(transport, lease));

        // Fail-closed boundary: the session is closed even though the failure was a
        // typed lease exception, not a transport failure.
        Assert.True(session.IsClosed);
        Assert.Throws<InvalidOperationException>(
            () => session.Track(new StubEntity(new RecordId("designs", "y"))));
    }

    [Fact]
    public async Task CommitAsync_WithLease_OnNonStolenError_PropagatesAsIs()
    {
        // Generic transport errors (network, timeout, malformed SQL) flow through the
        // session's normal fail-closed path, NOT the stolen-lease translator.
        var transport = new RecordingTransport()
            .ScriptResponse("[{\"result\":[{\"seq\":5}],\"status\":\"OK\"}]")
            .ScriptThrow(new SurrealException("HTTP 503 service unavailable", retryable: true));

        var lease = await WriterLease.AcquireAsync(transport, "design");
        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        session.SetField(entity.Id, "description", "edited");

        var ex = await Assert.ThrowsAsync<SurrealException>(
            () => session.CommitAsync(transport, lease));
        Assert.Contains("503", ex.Message);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public async Task CommitAsync_WithoutLease_NoCASInSql()
    {
        // Regression: lease = null path unchanged. No BEGIN/COMMIT, no writer_lease writes.
        var transport = new RecordingTransport().ScriptResponse(EmptySuccessResponse);
        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        session.SetField(entity.Id, "description", "edited");

        await session.CommitAsync(transport);

        var commitSql = transport.SqlSeen[0];
        Assert.DoesNotContain("BEGIN TRANSACTION", commitSql);
        Assert.DoesNotContain("writer_lease", commitSql);
    }

    [Fact]
    public async Task CommitAsync_WithLease_AndNoData_StillRunsCAS()
    {
        // A "heartbeat" commit with nothing to flush still advances the seq — the lease
        // counts as work that needs to land. Useful as a touch-the-lease idiom.
        var transport = new RecordingTransport()
            .ScriptResponse("[{\"result\":[{\"seq\":1}],\"status\":\"OK\"}]")
            .ScriptResponse(EmptySuccessResponse);

        var lease = await WriterLease.AcquireAsync(transport, "design");
        var session = new SurrealSession();
        await session.CommitAsync(transport, lease);

        Assert.Equal(2, transport.SqlSeen.Count);
        Assert.Contains("UPSERT writer_lease:design", transport.SqlSeen[1]);
        Assert.Equal(2, lease.ExpectedSequence);
    }

    [Fact]
    public async Task DisposeAsync_IsNoOp_NoTransportCalls()
    {
        var transport = new RecordingTransport().ScriptResponse(NoRowResponse);
        var lease = await WriterLease.AcquireAsync(transport, "design");
        var sqlBefore = transport.SqlSeen.Count;

        await lease.DisposeAsync();

        Assert.Equal(sqlBefore, transport.SqlSeen.Count);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private const string NoRowResponse = "[{\"result\":[],\"status\":\"OK\"}]";
    private const string EmptySuccessResponse = "[{\"result\":null,\"status\":\"OK\"}]";

    /// <summary>Test-only entity — stand-in for a generated [Table] partial.</summary>
    private sealed class StubEntity : IEntity
    {
        public StubEntity(RecordId id) => Id = id;
        public RecordId Id { get; }
        public SurrealSession? Session { get; private set; }
        public void Bind(SurrealSession session) => Session = session;
        public void Initialize(SurrealSession session) { }
        public void Flush(SurrealSession session) { }
        public void Hydrate(JsonElement json, IHydrationSink sink) { }
        public void OnDeleting() { }
        public void MarkAllSlicesLoaded(IHydrationSink sink) { }
    }

    /// <summary>
    /// Minimal <see cref="ISurrealTransport"/> for capture+replay. Tests script
    /// per-call responses (or exceptions) and assert against <see cref="SqlSeen"/>.
    /// </summary>
    private sealed class RecordingTransport : ISurrealTransport
    {
        private readonly Queue<Func<JsonDocument>> responses = new();

        public List<string> SqlSeen { get; } = new();
        public List<object?> VarsSeen { get; } = new();

        public RecordingTransport ScriptResponse(string json)
            => ScriptResponse(() => JsonDocument.Parse(json));

        public RecordingTransport ScriptResponse(Func<JsonDocument> producer)
        {
            responses.Enqueue(producer);
            return this;
        }

        public RecordingTransport ScriptThrow(Exception ex)
        {
            responses.Enqueue(() => throw ex);
            return this;
        }

        public Task<JsonDocument> ExecuteAsync(string sql, object? vars = null, CancellationToken ct = default)
        {
            SqlSeen.Add(sql);
            VarsSeen.Add(vars);
            if (responses.Count == 0)
            {
                return Task.FromResult(JsonDocument.Parse("[]"));
            }
            var producer = responses.Dequeue();
            try
            {
                return Task.FromResult(producer());
            }
            catch (Exception ex)
            {
                return Task.FromException<JsonDocument>(ex);
            }
        }

        public ValueTask DisposeAsync() => default;
    }
}
