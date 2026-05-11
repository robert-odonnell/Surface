namespace Disruptor.Surface.Runtime;

/// <summary>
/// A buffered outgoing edge surfaced by <see cref="SurrealSession.GetNewOutgoingEdges{TKind}"/>
/// for the generated <c>SaveAsync</c> dispatcher to RELATE. Carries the full intent the
/// caller expressed when invoking <c>Session.Relate&lt;TKind&gt;(...)</c> — the target
/// endpoint, the explicit edge id (or the deferred Idempotent sentinel synthesised when
/// the caller did not supply one), and any payload that was attached for
/// <c>RELATE … CONTENT { … }</c>.
/// <para>
/// Storing target alone (the previous shape) lost both the explicit edge id and the
/// payload between the buffered <c>Relate</c> call and the eventual save dispatch — a
/// silent contract violation. <see cref="PendingEdge"/> closes that gap.
/// </para>
/// </summary>
public sealed record PendingEdge(
    RecordId Target,
    RecordId Edge,
    IReadOnlyDictionary<string, object?>? Payload);
