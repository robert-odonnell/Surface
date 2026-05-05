namespace Disruptor.Surface.Runtime;

/// <summary>
/// Transport-side exception surfaced when a SurrealQL statement returns
/// <c>status: "ERR"</c>. Both the HTTP and embedded transports normalise statement-
/// level failures into this single type so consumer code can <c>catch</c> uniformly
/// without caring which path delivered the error.
/// <para>
/// <see cref="Retryable"/> is set when the transport judges the failure transient
/// (HTTP 5xx, timeouts, transactional conflicts, "resource busy"). The lease-stolen
/// case is identified by message-content sniff via <c>WriterLease.IsStolen</c>;
/// <c>SurrealSession.CommitAsync</c> translates that path into a typed
/// <see cref="WriterLeaseStolenException"/> before it leaves the boundary.
/// </para>
/// </summary>
public sealed class SurrealException(string message, bool retryable) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}
