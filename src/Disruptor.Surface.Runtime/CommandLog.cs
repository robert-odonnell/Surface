namespace Disruptor.Surface.Runtime;

/// <summary>
/// Append-only diagnostic log of model commands recorded by sync write methods on
/// <see cref="SurrealSession"/>. Useful for tests asserting "what intent did the
/// session capture?" and for application-side telemetry; the log is not consumed by
/// the per-entity Save dispatch path (which reads the entity's current state directly
/// instead of replaying recorded commands).
/// </summary>
public sealed class CommandLog
{
    private readonly List<Command> entries = [];

    public IReadOnlyList<Command> Entries => entries;
    public int Count => entries.Count;

    public void Append(Command c) => entries.Add(c);
    public void Clear() => entries.Clear();
}
