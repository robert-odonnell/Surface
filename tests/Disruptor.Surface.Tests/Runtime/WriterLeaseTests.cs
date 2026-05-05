using System.Text.Json;
using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Locks in the single-writer CAS-on-sequence protocol. <see cref="WriterLease.AcquireAsync"/>
/// reads the current seq from the workspace's single <c>writer_lease:main</c> row
/// (defaulting to 0), <see cref="SurrealSession.CommitAsync"/> splices a transactional
/// CAS clause around the data writes, stolen leases surface as
/// <see cref="WriterLeaseStolenException"/>, and successful commits advance the lease's
/// <c>ExpectedSequence</c>.
/// </summary>
public sealed class WriterLeaseTests
{
    [Fact]
    public async Task AcquireAsync_NoExistingRow_CapturesSeqZero()
    {
        var transport = new RecordingTransport().ScriptResponse(NoRowResponse);

        var lease = await WriterLease.AcquireAsync(transport);

        Assert.Equal(0, lease.ExpectedSequence);
        Assert.Single(transport.SqlSeen);
        Assert.Contains("SELECT seq FROM writer_lease:main", transport.SqlSeen[0]);
    }

    [Fact]
    public async Task AcquireAsync_ExistingRow_CapturesItsSeq()
    {
        var transport = new RecordingTransport().ScriptResponse("[{\"result\":[{\"seq\":42}],\"status\":\"OK\"}]");

        var lease = await WriterLease.AcquireAsync(transport);

        Assert.Equal(42, lease.ExpectedSequence);
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

        var lease = await WriterLease.AcquireAsync(transport);
        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        session.SetField(entity.Id, "description", "edited");

        await session.CommitAsync(transport, lease);

        var commitSql = transport.SqlSeen[1];
        Assert.StartsWith("BEGIN TRANSACTION", commitSql.TrimStart());
        Assert.EndsWith("COMMIT TRANSACTION;", commitSql.TrimEnd());
        Assert.Contains("$writer_lease_expected = 7", commitSql);
        Assert.Contains("THROW \"writer_lease_stolen\"", commitSql);
        Assert.Contains("UPSERT writer_lease:main", commitSql);
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

        var lease = await WriterLease.AcquireAsync(transport);
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
        // Surreal's THROW "writer_lease_stolen" surfaces from the transport as a
        // SurrealException whose message contains the marker. SurrealSession.CommitAsync
        // catches and translates.
        var transport = new RecordingTransport()
            .ScriptResponse("[{\"result\":[{\"seq\":5}],\"status\":\"OK\"}]")
            .ScriptThrow(new SurrealException("SurrealDB statement failed: writer_lease_stolen (current 9, expected 5)"));

        var lease = await WriterLease.AcquireAsync(transport);
        var session = new SurrealSession();
        var entity = session.Track(new StubEntity(new RecordId("designs", "x")));
        session.SetField(entity.Id, "description", "edited");

        var ex = await Assert.ThrowsAsync<WriterLeaseStolenException>(
            () => session.CommitAsync(transport, lease));
        Assert.Equal(5, ex.ExpectedSequence);
    }

    [Fact]
    public async Task CommitAsync_WithLease_OnStolenError_ClosesSession()
    {
        var transport = new RecordingTransport()
            .ScriptResponse("[{\"result\":[{\"seq\":5}],\"status\":\"OK\"}]")
            .ScriptThrow(new SurrealException("writer_lease_stolen"));

        var lease = await WriterLease.AcquireAsync(transport);
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
            .ScriptThrow(new SurrealException("HTTP 503 service unavailable"));

        var lease = await WriterLease.AcquireAsync(transport);
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

        var lease = await WriterLease.AcquireAsync(transport);
        var session = new SurrealSession();
        await session.CommitAsync(transport, lease);

        Assert.Equal(2, transport.SqlSeen.Count);
        Assert.Contains("UPSERT writer_lease:main", transport.SqlSeen[1]);
        Assert.Equal(2, lease.ExpectedSequence);
    }

    [Fact]
    public async Task DisposeAsync_IsNoOp_NoTransportCalls()
    {
        var transport = new RecordingTransport().ScriptResponse(NoRowResponse);
        var lease = await WriterLease.AcquireAsync(transport);
        var sqlBefore = transport.SqlSeen.Count;

        await lease.DisposeAsync();

        Assert.Equal(sqlBefore, transport.SqlSeen.Count);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private const string NoRowResponse = "[{\"result\":[],\"status\":\"OK\"}]";
    private const string EmptySuccessResponse = "[{\"result\":null,\"status\":\"OK\"}]";

    /// <summary>Test-only entity — stand-in for a generated [Table] partial.</summary>
    private sealed class StubEntity(RecordId id) : IEntity
    {
        public RecordId Id { get; } = id;
        public SurrealSession? Session { get; private set; }
        public void Bind(SurrealSession session) => Session = session;
        public void Initialize(SurrealSession session) { }
        public void Flush(SurrealSession session) { }
        public void Hydrate(JsonElement json, IHydrationSink sink) { }
        public void HydratePartial(JsonElement json, IHydrationSink sink) { }
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

        public List<string> SqlSeen { get; } = [];

        public RecordingTransport ScriptResponse(string json)
            => ScriptResponse(() => JsonDocument.Parse(json));

        private RecordingTransport ScriptResponse(Func<JsonDocument> producer)
        {
            responses.Enqueue(producer);
            return this;
        }

        public RecordingTransport ScriptThrow(Exception ex)
        {
            responses.Enqueue(() => throw ex);
            return this;
        }

        public Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
        {
            SqlSeen.Add(sql);
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
