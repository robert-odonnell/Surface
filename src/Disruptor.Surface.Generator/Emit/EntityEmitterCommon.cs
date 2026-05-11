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

    public static void WriteSessionPlumbing(CodeWriter writer)
    {
        writer.Line($"private {SessionType}? _session;");
        writer.Line();

        writer.Line($"{SessionType}? {EntityInterface}.Session => _session;");
        writer.Line();
        using (writer.Block($"void {EntityInterface}.Bind({SessionType} session)"))
        {
            writer.Line("if (_session is not null && !global::System.Object.ReferenceEquals(_session, session))");
            using (writer.Indent())
            {
                writer.Line("throw new global::System.InvalidOperationException(\"Entity is already bound to a different session.\");");
            }

            writer.Line("_session = session;");
        }

        writer.Line();
        writer.Line($"protected {SessionType} Session");
        using (writer.Indent())
        {
            writer.Line("=> _session ?? throw new global::System.InvalidOperationException(\"Entity is not bound to a session — call session.Track(...) or hydrate via Sessions.Load*Async first.\");");
        }

        writer.Line();
        using (writer.Block("private void __EnsureSliceLoaded(string sliceKey, string fetchHint)"))
        {
            writer.Line($"var __id = (({EntityInterface})this).Id;");
            writer.Line("if (!Session.IsSliceLoaded(__id, sliceKey))");
            using (writer.Indent())
            {
                writer.Line("throw new global::Disruptor.Surface.Runtime.LoadShapeViolationException(__id, sliceKey, fetchHint);");
            }
        }
    }
}
