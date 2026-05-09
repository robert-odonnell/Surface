using System.Threading.Channels;
using Disruptor.Surreal;
using Disruptor.Surreal.Connection;
using Disruptor.Surreal.Values;
using SdkSurreal = Disruptor.Surreal.Surreal;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// Builds a <see cref="SdkSurreal"/> wrapping a fake <see cref="IConnection"/> for
/// tests that need to drive <c>SurrealSession.CommitAsync</c> without standing up a
/// real WebSocket. Mirrors the <c>RecordingConnection</c> pattern in the SDK's own
/// <c>FakeConnectionTests</c>; lifted here so multiple Surface tests can share it.
/// </summary>
internal static class FakeSurreal
{
    /// <summary>
    /// Returns a <see cref="SdkSurreal"/> that swallows every command silently —
    /// "begin" returns a stable txn-id, every other RPC returns <see cref="Value.None"/>.
    /// Use for tests where the wire shape doesn't matter (closure, lifecycle).
    /// </summary>
    public static SdkSurreal Null() => new(new RecordingConnection());

    /// <summary>
    /// Returns a <see cref="SdkSurreal"/> whose RPCs all throw <paramref name="ex"/>.
    /// "begin" still succeeds (so CommitAsync's failure-mode tests probe the per-RPC
    /// dispatch path, not the txn-open path).
    /// </summary>
    public static SdkSurreal Throwing(Exception ex) => new(new RecordingConnection
    {
        Responder = (method, _, _) => method == "begin"
            ? new UuidValue(Guid.NewGuid())
            : throw ex,
    });

    /// <summary>
    /// A fake <see cref="IConnection"/> that records every dispatched RPC and replies
    /// with the configured <see cref="Responder"/>. Defaults to a no-op response: "begin"
    /// returns a stable txn-id, everything else returns <see cref="Value.None"/>.
    /// </summary>
    public sealed class RecordingConnection : IConnection
    {
        public List<(string Method, Value? Params, Guid? TxnId)> Sent { get; } = new();
        public Func<string, Value?, Guid?, Value>? Responder { get; set; }

        public bool IsConnected { get; set; } = true;
        public Func<CancellationToken, Task>? ReauthHandler { get; set; }

        public Task<Value> SendAsync(string method, Value? @params, Guid? txnId, CancellationToken ct = default)
        {
            Sent.Add((method, @params, txnId));
            var result = Responder?.Invoke(method, @params, txnId)
                ?? (method == "begin" ? new UuidValue(Guid.NewGuid()) : Value.None);
            return Task.FromResult(result);
        }

        public void RegisterLiveSubscription(Guid liveQueryId, ChannelWriter<Notification> writer) { }
        public void UnregisterLiveSubscription(Guid liveQueryId) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
