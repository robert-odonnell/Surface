namespace Disruptor.Surface.Runtime;

/// <summary>
/// Thrown by generator-emitted property reads when the user navigates to a slice that
/// wasn't loaded into the session. Strict-with-escape contract: the load shape declares
/// up-front what's reachable; touching anything outside it errors loudly with a hint at
/// <see cref="SurrealSession.FetchAsync{T}"/>, the (PR8) extension-query escape hatch.
/// <para>
/// The exception carries enough state for tooling — <see cref="Owner"/> identifies the
/// entity whose slice was missed, <see cref="Field"/> names the C# property the read
/// came from, and <see cref="FetchHint"/> is a human-readable suggestion for how to
/// extend the load shape (e.g. <c>"Workspace.Query.Designs.WithId(designId).IncludeConstraints(...)"</c>).
/// </para>
/// </summary>
public sealed class LoadShapeViolationException : InvalidOperationException
{
    public RecordId Owner { get; }
    public string Field { get; }
    public string FetchHint { get; }

    public LoadShapeViolationException(RecordId owner, string field, string fetchHint)
        : base(BuildMessage(owner, field, fetchHint))
    {
        Owner = owner;
        Field = field;
        FetchHint = fetchHint;
    }

    private static string BuildMessage(RecordId owner, string field, string fetchHint)
        => $"Slice {owner}.{field} was not loaded. Extend the load shape via session.FetchAsync({fetchHint}).";
}
