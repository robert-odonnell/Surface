#nullable enable
using System.Text.Json;

namespace Disruptor.Surface.Runtime;

/// <summary>
/// Cross-process writer-coordination indicator stored as a record in SurrealDB. One
/// lease per aggregate (<c>writer_lease:design</c>, <c>writer_lease:review</c>, …) so
/// concurrent writers across different aggregates don't block each other. Each lease
/// carries a holder id (per-process <see cref="Ulid"/>) and a TTL — defaults to 5 min,
/// stale leases can be stolen by another writer once expired.
/// <para>
/// Lifecycle: <see cref="AcquireAsync"/> at the top of a writer session;
/// <see cref="RenewAsync"/> from <see cref="SurrealSession.CommitAsync"/> to detect theft
/// and bump the TTL; <see cref="ReleaseAsync"/> on graceful dispose. A crashed
/// writer leaves the lease in place — once <c>expires_at</c> falls behind <c>time::now()</c>
/// the next acquirer steals it, capping the worst-case downtime to one TTL window.
/// </para>
/// </summary>
public sealed class WriterLease : IAsyncDisposable, IDisposable
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// SurrealDB DDL for the <c>writer_lease</c> table the lease implementation needs.
    /// Generator-emitted <c>GeneratedSchema.FullScript</c> prepends this to the per-table
    /// model DDL; consumers can also apply it directly.
    /// </summary>
    public const string SchemaScript = """
        DEFINE TABLE IF NOT EXISTS writer_lease SCHEMAFULL;
        DEFINE FIELD IF NOT EXISTS holder_id ON writer_lease TYPE string;
        DEFINE FIELD IF NOT EXISTS acquired_at ON writer_lease TYPE datetime;
        DEFINE FIELD IF NOT EXISTS expires_at ON writer_lease TYPE datetime;
        """;

    private readonly ISurrealTransport transport;
    private readonly TimeSpan ttl;
    private bool released;

    public string AggregateName { get; }
    public Ulid HolderId { get; }

    private WriterLease(ISurrealTransport transport, string aggregateName, Ulid holderId, TimeSpan ttl)
    {
        this.transport = transport;
        this.ttl = ttl;
        AggregateName = aggregateName;
        HolderId = holderId;
    }

    /// <summary>
    /// Try to take the per-aggregate writer lease. Throws
    /// <see cref="WriterLeaseUnavailableException"/> if the lease is held by another
    /// holder and not yet expired.
    /// </summary>
    public static async Task<WriterLease> AcquireAsync(
        ISurrealTransport transport,
        string aggregateName,
        TimeSpan? ttl = null,
        CancellationToken ct = default)
    {
        var effectiveTtl = ttl ?? DefaultTtl;
        var holderId = Ulid.NewUlid();
        var ttlSeconds = (int)effectiveTtl.TotalSeconds;

        // Conservative SurrealQL: read current row, decide in-band, write only if free /
        // expired / held-by-us. Wrapped in a transaction to close the TOCTOU window
        // between the SELECT and the UPSERT. Returns a single-row payload describing the
        // outcome — caller parses { ok, holder, expires_at }.
        var sql = $@"
BEGIN TRANSACTION;
LET $now = time::now();
LET $expires = $now + {ttlSeconds}s;
LET $current = (SELECT * FROM writer_lease:{aggregateName})[0];
LET $can_take = $current = NONE OR $current.expires_at < $now OR $current.holder_id = '{holderId}';
LET $result = IF $can_take THEN {{
    UPSERT writer_lease:{aggregateName} CONTENT {{ holder_id: '{holderId}', acquired_at: $now, expires_at: $expires }};
    RETURN {{ ok: true, holder: '{holderId}', expires_at: $expires }};
}} ELSE {{
    RETURN {{ ok: false, holder: $current.holder_id, expires_at: $current.expires_at }};
}} END;
COMMIT TRANSACTION;
";

        using var doc = await transport.ExecuteAsync(sql, null, ct);
        var (ok, currentHolder, currentExpiry) = ParseLeaseResult(doc);
        if (!ok)
        {
            throw new WriterLeaseUnavailableException(aggregateName, currentHolder, currentExpiry);
        }
        return new WriterLease(transport, aggregateName, holderId, effectiveTtl);
    }

    /// <summary>
    /// Bump <c>expires_at</c> on the lease if we still hold it. Throws
    /// <see cref="WriterLeaseStolenException"/> if the lease has been taken by another
    /// holder since acquire — caller must abandon the in-flight writes, reload, and
    /// retry from the new snapshot.
    /// </summary>
    public async Task RenewAsync(CancellationToken ct = default)
    {
        if (released)
        {
            throw new InvalidOperationException("Lease has already been released.");
        }

        var ttlSeconds = (int)ttl.TotalSeconds;
        var sql = $@"
BEGIN TRANSACTION;
LET $now = time::now();
LET $current = (SELECT * FROM writer_lease:{AggregateName})[0];
LET $still_ours = $current != NONE AND $current.holder_id = '{HolderId}';
LET $result = IF $still_ours THEN {{
    UPDATE writer_lease:{AggregateName} SET expires_at = $now + {ttlSeconds}s;
    RETURN {{ ok: true, holder: '{HolderId}', expires_at: $now + {ttlSeconds}s }};
}} ELSE {{
    RETURN {{ ok: false, holder: $current.holder_id, expires_at: $current.expires_at }};
}} END;
COMMIT TRANSACTION;
";
        using var doc = await transport.ExecuteAsync(sql, null, ct);
        var (ok, currentHolder, currentExpiry) = ParseLeaseResult(doc);
        if (!ok)
        {
            throw new WriterLeaseStolenException(AggregateName, HolderId, currentHolder, currentExpiry);
        }
    }

    /// <summary>
    /// Release the lease — deletes the record if we still hold it. Idempotent: subsequent
    /// calls are no-ops. A no-op release is also fine if the lease was already stolen
    /// (someone else now owns the record and our DELETE WHERE clause won't match).
    /// </summary>
    public async Task ReleaseAsync(CancellationToken ct = default)
    {
        if (released)
        {
            return;
        }

        released = true;
        var sql = $@"DELETE writer_lease:{AggregateName} WHERE holder_id = '{HolderId}';";
        await transport.ExecuteAsync(sql, null, ct);
    }

    public ValueTask DisposeAsync() => new(ReleaseAsync());

    /// <summary>
    /// Sync dispose is best-effort fire-and-forget — prefer <see cref="DisposeAsync"/>
    /// or an explicit <see cref="ReleaseAsync"/> when you can. The fire-and-forget path
    /// is here so a <c>using</c> over a writable <see cref="SurrealSession"/> never blocks the
    /// consumer's sync code path.
    /// </summary>
    public void Dispose()
    {
        if (released)
        {
            return;
        }

        _ = ReleaseAsync();
    }

    private static (bool Ok, string? Holder, string? ExpiresAt) ParseLeaseResult(JsonDocument doc)
    {
        // The transport returns an array of per-statement envelopes — `LET`/`COMMIT`/
        // standalone `RETURN $var` all surface with `result: null`, while the IF/RETURN
        // expression that produced the outcome carries the actual `{ ok, holder,
        // expires_at }` object. Walk from the end and grab the first non-null result.
        var root = doc.RootElement;
        JsonElement payload = default;
        var found = false;

        if (root.ValueKind == JsonValueKind.Array)
        {
            for (var i = root.GetArrayLength() - 1; i >= 0; i--)
            {
                var stmt = root[i];
                if (stmt.ValueKind == JsonValueKind.Object && stmt.TryGetProperty("result", out var result))
                {
                    if (result.ValueKind == JsonValueKind.Null) continue;
                    payload = result;
                    found = true;
                    break;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            payload = root;
            found = true;
        }

        if (!found)
        {
            return (false, null, null);
        }

        // Payload may be { ok, holder, expires_at } directly, or a single-element array
        // wrapping that object — Surreal's tx blocks vary by version.
        if (payload.ValueKind == JsonValueKind.Array && payload.GetArrayLength() > 0)
        {
            payload = payload[0];
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            return (false, null, null);
        }

        var ok = payload.TryGetProperty("ok", out var okE) && okE.ValueKind == JsonValueKind.True;
        var holder = payload.TryGetProperty("holder", out var hE) ? hE.GetString() : null;
        var expiresAt = payload.TryGetProperty("expires_at", out var eE) ? eE.GetString() : null;
        return (ok, holder, expiresAt);
    }
}

/// <summary>Thrown when <see cref="WriterLease.AcquireAsync"/> finds the lease held by another non-expired holder.</summary>
public sealed class WriterLeaseUnavailableException(string aggregateName, string? currentHolder, string? expiresAt)
    : Exception($"Writer lease for aggregate '{aggregateName}' is held by '{currentHolder ?? "<unknown>"}' until {expiresAt ?? "<unknown>"}.")
{
    public string AggregateName { get; } = aggregateName;
    public string? CurrentHolder { get; } = currentHolder;
    public string? ExpiresAt { get; } = expiresAt;
}

/// <summary>Thrown when <see cref="WriterLease.RenewAsync"/> detects another holder took the lease since acquire — caller must reload and retry.</summary>
public sealed class WriterLeaseStolenException(string aggregateName, Ulid expectedHolder, string? currentHolder, string? expiresAt)
    : Exception($"Writer lease for aggregate '{aggregateName}' was held by '{expectedHolder}' but is now held by '{currentHolder ?? "<unknown>"}' until {expiresAt ?? "<unknown>"}.")
{
    public string AggregateName { get; } = aggregateName;
    public Ulid ExpectedHolder { get; } = expectedHolder;
    public string? CurrentHolder { get; } = currentHolder;
    public string? ExpiresAt { get; } = expiresAt;
}
