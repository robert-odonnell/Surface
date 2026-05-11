using System.Text;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Shared building blocks for emitters that produce <c>IEntity</c>-implementing partial
/// classes. <see cref="PartialEmitter"/> covers <c>[Table]</c> entities;
/// <see cref="RelationVariantEmitter"/> covers relation-variant classes (typed-edge
/// payload carriers). Both share the per-entity session plumbing — <c>_session</c>
/// backing field, explicit <c>IEntity.Bind</c> / <c>IEntity.Session</c> impls, the
/// protected <c>Session</c> accessor that throws when unbound, and the
/// <c>__EnsureSliceLoaded</c> guard navigable read paths use.
/// </summary>
internal static class EntityEmitterCommon
{
    public const string SessionType = "global::Disruptor.Surface.Runtime.SurrealSession";
    public const string EntityInterface = "global::Disruptor.Surface.Runtime.IEntity";
    public const string HydrationSinkType = "global::Disruptor.Surface.Runtime.IHydrationSink";
    public const string RelationVariantInterface = "global::Disruptor.Surface.Runtime.IRelationVariant";

    /// <summary>
    /// Emits per-entity session plumbing: a private <c>_session</c> field, the explicit
    /// <c>IEntity.Session</c> getter (nullable), <c>IEntity.Bind</c> (one-shot setter for
    /// <c>_session</c>), the protected <c>Session</c> property for entity-body reads
    /// (throws when unbound — reading from an unbound entity is a programmer error), and
    /// the <c>__EnsureSliceLoaded</c> guard the navigable read paths use to enforce
    /// strict-with-escape.
    /// </summary>
    public static void WriteSessionPlumbing(StringBuilder builder, string indent)
    {
        builder
            .Append(indent)
            .Append("private ")
            .Append(SessionType)
            .AppendLine("? _session;")
            .AppendLine();

        builder
            .Append(indent)
            .Append(SessionType)
            .Append("? ")
            .Append(EntityInterface)
            .AppendLine(".Session => _session;")
            .AppendLine()
            .Append(indent)
            .Append("void ")
            .Append(EntityInterface)
            .Append(".Bind(")
            .Append(SessionType)
            .AppendLine(" session)")
            .Append(indent).AppendLine("{")
            .Append(indent).AppendLine("    if (_session is not null && !global::System.Object.ReferenceEquals(_session, session))")
            .Append(indent).AppendLine("        throw new global::System.InvalidOperationException(\"Entity is already bound to a different session.\");")
            .Append(indent).AppendLine("    _session = session;")
            .Append(indent).AppendLine("}")
            .AppendLine();

        builder
            .Append(indent)
            .Append("protected ")
            .Append(SessionType)
            .AppendLine(" Session")
            .Append(indent)
            .AppendLine("    => _session ?? throw new global::System.InvalidOperationException(\"Entity is not bound to a session — call session.Track(...) or hydrate via Sessions.Load*Async first.\");")
            .AppendLine()
            // Slice guard — called by every navigable read path before walking the in-memory
            // cache. Reuses the protected Session accessor so unbound entities still get the
            // existing "not tracked" error; once bound, the slice check throws
            // LoadShapeViolationException with a hint at FetchAsync.
            .Append(indent)
            .AppendLine("private void __EnsureSliceLoaded(string sliceKey, string fetchHint)")
            .Append(indent).AppendLine("{")
            .Append(indent).Append("    var __id = ((").Append(EntityInterface).AppendLine(")this).Id;")
            .Append(indent).AppendLine("    if (!Session.IsSliceLoaded(__id, sliceKey))")
            .Append(indent).AppendLine("        throw new global::Disruptor.Surface.Runtime.LoadShapeViolationException(__id, sliceKey, fetchHint);")
            .Append(indent).AppendLine("}");
    }
}
