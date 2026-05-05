using System.Text.Json;
using Dahomey.Cbor;
using Disruptor.Surface.Runtime;
using SurrealDb.Embedded.Options;
using SurrealDb.Embedded.RocksDb;
using SurrealDb.Net;

namespace Disruptor.Surface.Transport.Embedded;

/// <summary>
/// In-process <see cref="ISurrealTransport"/> backed by SurrealDB embedded with a
/// RocksDB file store (<see cref="SurrealDbRocksDbClient"/>). Drop-in replacement for
/// <c>Disruptor.Surface.Runtime.SurrealHttpClient</c> when consumers want to skip the
/// HTTP round-trip — code-index full rebuilds, bulk imports, single-process workloads
/// where the network ceiling is the bottleneck.
/// <para>
/// <b>Single-writer in-process</b> — RocksDB removes the cross-process write
/// coordination problem, but multiple threads sharing one client can still race into
/// the engine and burn CPU on doomed CAS-on-sequence checks. A
/// <see cref="SemaphoreSlim"/> serialises any script that opens a transactional
/// fragment (<c>BEGIN TRANSACTION</c>), so the WriterLease's optimistic
/// compare-and-swap stays the contention point of last resort rather than a
/// permanent hot path. Read-only and non-transactional scripts skip the lock —
/// concurrent reads stay parallel.
/// </para>
/// <para>
/// <b>CBOR-to-JSON projection</b> — SurrealDB's embedded engine speaks CBOR. The
/// runtime's parsers expect the JSON-RPC envelope shape (<c>[{result, status}, …]</c>),
/// so each <see cref="ExecuteAsync"/> hop projects the SDK's
/// <see cref="SurrealDb.Net.Models.Response.SurrealDbResponse"/> into that shape via
/// <see cref="CborJsonProjection"/>. SurrealQL literals on the input side stay
/// inlined via <c>SurrealFormatter</c>, exactly like the HTTP path — no Things flow
/// through any binding dictionary.
/// </para>
/// </summary>
public sealed class SurrealEmbeddedTransport : ISurrealTransport
{
    /// <summary>SurrealQL prefix the WriterLease emits to open a transactional commit. Used as the lock predicate — anything starting with this is a write that needs single-writer serialisation.</summary>
    private const string TransactionPrefix = "BEGIN TRANSACTION";

    private readonly ISurrealDbClient client;
    private readonly bool ownsClient;
    private readonly SemaphoreSlim writerSemaphore = new(initialCount: 1, maxCount: 1);

    /// <summary>
    /// Create a transport against a RocksDB file at <paramref name="filePath"/>.
    /// Creates the underlying client and disposes it on <see cref="DisposeAsync"/>.
    /// </summary>
    public SurrealEmbeddedTransport(
        string filePath,
        string @namespace,
        string database,
        SurrealDbEmbeddedOptions? engineOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);

        var rocks = new SurrealDbRocksDbClient(filePath, engineOptions);
        // Use(ns, db) is the SDK's namespace-and-database setter; runs synchronously
        // against the in-process engine so blocking once at construction is fine.
        rocks.Use(@namespace, database).GetAwaiter().GetResult();
        client = rocks;
        ownsClient = true;
    }

    /// <summary>
    /// Create a transport over an existing SDK client. Use when consumer code wants to
    /// share a single <see cref="SurrealDbRocksDbClient"/> across multiple
    /// abstractions (e.g. Disruptor.Surface plus the SDK's typed read APIs). Caller
    /// owns the client's lifetime — <see cref="DisposeAsync"/> does not dispose it.
    /// </summary>
    public SurrealEmbeddedTransport(ISurrealDbClient existingClient)
    {
        ArgumentNullException.ThrowIfNull(existingClient);
        client = existingClient;
        ownsClient = false;
    }

    /// <summary>The underlying SDK client. Exposed for callers that need to layer the SDK's typed APIs alongside the Disruptor.Surface session abstraction.</summary>
    public ISurrealDbClient Client => client;

    /// <inheritdoc />
    public async Task<JsonDocument> ExecuteAsync(string sql, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sql);

        var requiresWriterLock = StartsWithTransaction(sql);
        if (requiresWriterLock)
        {
            await writerSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var response = await client.RawQuery(sql, parameters: null, ct).ConfigureAwait(false);
            var cborOptions = ResolveCborOptions(client);
            return CborJsonProjection.BuildEnvelope(response, cborOptions);
        }
        finally
        {
            if (requiresWriterLock)
            {
                writerSemaphore.Release();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        writerSemaphore.Dispose();
        if (ownsClient && client is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (ownsClient && client is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    /// <summary>
    /// Cheap predicate — true iff <paramref name="sql"/> opens a transactional script.
    /// The WriterLease's pre-commit fragment leads with <c>BEGIN TRANSACTION;</c>, so
    /// matching that prefix is sufficient for the single-writer gate. Read-only
    /// queries (<c>SELECT</c>, projection-only commits without a lease) skip the lock.
    /// </summary>
    private static bool StartsWithTransaction(string sql)
    {
        // Skip leading whitespace cheaply without allocating.
        var i = 0;
        while (i < sql.Length && char.IsWhiteSpace(sql[i]))
        {
            i++;
        }

        return sql.AsSpan(i).StartsWith(TransactionPrefix.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pull the CBOR options out of the SDK client. Used to feed the projection layer
    /// so the same converter set drives encode and decode. The SDK exposes options via
    /// <c>BaseSurrealDbClient.GetCborOptions()</c> internally; we reach for that
    /// reflectively rather than duplicating the option set.
    /// </summary>
    private static CborOptions ResolveCborOptions(ISurrealDbClient client)
    {
        var clientType = client.GetType();
        // Walk up to BaseSurrealDbClient where the options field lives.
        for (var t = clientType; t is not null; t = t.BaseType)
        {
            var field = t.GetField("_cborOptions",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(client) is CborOptions options)
            {
                return options;
            }
        }

        // Last-resort: a default option set. Tagged-type round-tripping for SurrealDB
        // values won't work, but the projection layer already coerces the most common
        // tagged types via type-pattern matching, so primitive-only payloads still go
        // through cleanly.
        return new CborOptions();
    }
}
