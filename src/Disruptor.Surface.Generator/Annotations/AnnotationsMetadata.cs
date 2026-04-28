namespace Disruptor.Surface.Generator.Annotations;

/// <summary>
/// Fully-qualified metadata names the generator pipelines bind to. The attribute types
/// themselves live in <c>Disruptor.Surface.Runtime</c> as ordinary <c>.cs</c> files —
/// the generator finds them by these strings via <c>ForAttributeWithMetadataName</c>,
/// so the FQNs here and the runtime declarations must stay in lockstep.
/// </summary>
internal static class AnnotationsMetadata
{
    public const string Namespace = "Disruptor.Surface.Annotations";

    public const string Table                = $"{Namespace}.TableAttribute";
    public const string AggregateRoot        = $"{Namespace}.AggregateRootAttribute";
    public const string CompositionRoot      = $"{Namespace}.CompositionRootAttribute";
    public const string Id                   = $"{Namespace}.IdAttribute";
    public const string Property             = $"{Namespace}.PropertyAttribute";
    public const string Parent               = $"{Namespace}.ParentAttribute";
    public const string Children             = $"{Namespace}.ChildrenAttribute";
    public const string Reference            = $"{Namespace}.ReferenceAttribute";
    public const string Inline               = $"{Namespace}.InlineAttribute";
    public const string Reject               = $"{Namespace}.RejectAttribute";
    public const string Unset                = $"{Namespace}.UnsetAttribute";
    public const string Cascade              = $"{Namespace}.CascadeAttribute";
    public const string Ignore               = $"{Namespace}.IgnoreAttribute";
    public const string RelationAttribute    = $"{Namespace}.RelationAttribute";
    public const string ForwardRelation      = $"{Namespace}.ForwardRelation";
    public const string InverseRelation      = $"{Namespace}.InverseRelation`1";
    public const string RecordIdValue        = $"{Namespace}.RecordIdValueAttribute`1";
}
