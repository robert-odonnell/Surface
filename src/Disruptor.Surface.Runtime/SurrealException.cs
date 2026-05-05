namespace Disruptor.Surface.Runtime;

/// <summary>
/// Transport-side exception surfaced when a SurrealQL statement returns
/// <c>status: "ERR"</c>. Both the HTTP and embedded transports normalise statement-
/// level failures into this single type so consumer code can <c>catch</c> uniformly
/// without caring which path delivered the error.
/// </summary>
public sealed class SurrealException(string message) : Exception(message);
