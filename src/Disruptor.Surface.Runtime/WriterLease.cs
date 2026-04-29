#nullable enable
using System.Text.Json;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Cross-process writer-coordination indicator. One <c>writer_lease:&lt;aggregate&gt;</c>
/// record per aggregate, holding a monotonically increasing <c>seq</c> counter. The lease
/// uses optimistic concurrency via compare-and-swap on the sequence — no TTL, no holder
/// id, no clock skew, no theft-recovery timer.
/// <para>
/// Protocol:
/// <list type="number">
///   <item><see cref="AcquireAsync"/> reads the current <c>seq</c> for the aggregate
///         (defaulting to 0 if no row exists) and captures it on the returned lease.</item>
///   <item>The caller loads, mutates, and calls
///         <see cref="SurrealSession.CommitAsync"/>(transport, lease). The session splices
///         a transactional CAS clause into the same script — if the on-DB <c>seq</c>
///         still matches what the lease captured, the data writes commit and the seq
///         advances by 1. If another writer slipped in and bumped <c>seq</c> first,
///         the script throws and the whole transaction rolls back; <see cref="WriterLeaseStolenException"/>
///         surfaces to the caller.</item>
///   <item><see cref="DisposeAsync"/> is a no-op. There's no row to delete, no lock to
///         release. A crashed writer leaves their captured seq in memory only — the next
///         acquirer reads the current seq fresh and proceeds. No coordination needed.</item>
/// </list>
/// </para>
/// <para>
/// This is optimistic concurrency, not pessimistic locking. Two writers can both
/// <see cref="AcquireAsync"/> the same aggregate and both mutate locally; only the first
/// to commit wins, the second gets <see cref="WriterLeaseStolenException"/> at commit
/// time and must reload-and-retry. Suits the library's one-shot session character: load
/// → mutate → commit happens fast enough that races are rare and retries are cheap.
/// </para>
/// </summary>
public sealed class WriterLease : IAsyncDisposable, IDisposable
{
    /// <summary>SurrealDB DDL for the <c>writer_lease</c> table. Splices into the generated schema chunk list.</summary>
    public const string SchemaScript = """
        DEFINE TABLE IF NOT EXISTS writer_lease SCHEMAFULL;
        DEFINE FIELD IF NOT EXISTS seq ON writer_lease TYPE int;
        """;

    // Sentinel embedded in Surreal's THROW message so SurrealSession.CommitAsync can
    // recognise the stolen-lease error without parsing free-form text. Includes the
    // aggregate name for diagnostics on the receiving end.
    internal const string StolenMarker = "writer_lease_stolen";

    public string AggregateName { get; }

    /// <summary>The seq value this lease was acquired with. Advances by 1 on each successful commit.</summary>
    public long ExpectedSequence { get; private set; }

    private WriterLease(string aggregateName, long expectedSequence)
    {
        AggregateName = aggregateName;
        ExpectedSequence = expectedSequence;
    }

    /// <summary>
    /// Reads the current <c>seq</c> for the aggregate's lease record (defaulting to 0
    /// when no row exists yet) and returns a lease holding that captured value. Cannot
    /// fail logically — anyone can acquire any time. Whether your acquire turns into a
    /// successful commit is determined later by
    /// <see cref="SurrealSession.CommitAsync"/>'s CAS check.
    /// </summary>
    public static async Task<WriterLease> AcquireAsync(
        ISurrealTransport transport,
        string aggregateName,
        CancellationToken ct = default)
    {
        // Slug-validate up front — the aggregate name lands as the value half of a
        // RecordId (writer_lease:<slug>), so it has to satisfy the same shape rule
        // every other id value does. Catches typos like "Design" or "writer-lease"
        // at the call site instead of producing a malformed Surreal id.
        RecordIdFormat.Validate(aggregateName);

        var sql = $"SELECT seq FROM writer_lease:{aggregateName};";
        using var doc = await transport.ExecuteAsync(sql, null, ct);
        var seq = ParseSequence(doc);
        return new WriterLease(aggregateName, seq);
    }

    /// <summary>
    /// SQL fragment that opens the commit transaction, asserts the on-DB <c>seq</c>
    /// equals what we captured at acquire (THROW with <see cref="StolenMarker"/> on
    /// mismatch), and UPSERTs the lease row with <c>seq + 1</c>. Spliced before the
    /// session's data writes by <see cref="SurrealSession.CommitAsync"/>.
    /// </summary>
    internal string RenderPreCommitFragment() => $@"BEGIN TRANSACTION;
LET $writer_lease_row = (SELECT seq FROM writer_lease:{AggregateName})[0];
LET $writer_lease_expected = {ExpectedSequence};
LET $writer_lease_current = IF $writer_lease_row = NONE THEN 0 ELSE $writer_lease_row.seq END;
IF $writer_lease_current != $writer_lease_expected THEN
    THROW ""{StolenMarker}:{AggregateName}"";
END;
UPSERT writer_lease:{AggregateName} CONTENT {{ seq: $writer_lease_expected + 1 }};
";

    /// <summary>SQL fragment closing the commit transaction. Spliced after the session's data writes.</summary>
    internal const string PostCommitFragment = "COMMIT TRANSACTION;";

    /// <summary>Called by <see cref="SurrealSession.CommitAsync"/> after a successful commit so a re-used lease has the correct expectation for the next round.</summary>
    internal void OnCommitSucceeded() => ExpectedSequence++;

    /// <summary>True iff the exception's message carries the stolen-lease sentinel from a failed CAS check.</summary>
    internal static bool IsStolen(Exception ex) =>
        ex.Message.Contains(StolenMarker, StringComparison.Ordinal);

    public ValueTask DisposeAsync() => default;
    public void Dispose() { }

    private static long ParseSequence(JsonDocument doc)
    {
        // Transport response shape: [{"result": [{"seq": N}], ...}] or [{"result": [], ...}]
        // when the row doesn't exist yet. The default-zero path is just "no rows".
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return 0;
        }

        var stmt = root[0];
        if (!stmt.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        if (result.GetArrayLength() == 0)
        {
            return 0;
        }

        var row = result[0];
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("seq", out var seqE))
        {
            return 0;
        }

        return seqE.ValueKind == JsonValueKind.Number ? seqE.GetInt64() : 0;
    }
}

/// <summary>
/// Thrown from <see cref="SurrealSession.CommitAsync"/> when the lease's CAS check
/// fails — another writer advanced the aggregate's <c>writer_lease.seq</c> between this
/// lease's <see cref="WriterLease.AcquireAsync"/> and this commit. The caller should
/// abandon the in-flight writes, reload the aggregate from a fresh snapshot, and retry.
/// </summary>
public sealed class WriterLeaseStolenException(string aggregateName, long expectedSequence)
    : Exception($"Writer lease for aggregate '{aggregateName}' was stolen — captured sequence {expectedSequence} no longer matches the current seq on writer_lease:{aggregateName}. Reload and retry.")
{
    public string AggregateName { get; } = aggregateName;
    public long ExpectedSequence { get; } = expectedSequence;
}
