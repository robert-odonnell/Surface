namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// The root model a generated partial is emitted from. Every collection is stored as
/// <see cref="EquatableArray{T}"/> so the entire record is structurally equatable —
/// the incremental generator needs that to skip downstream stages when nothing changed.
/// </summary>
public sealed record TableModel(
    string FullName,
    string Namespace,
    string Name,
    bool IsPartial,
    bool IsAbstract,
    bool IsSealed,
    bool IsAggregateRoot,
    string DeclaredAccessibility,
    EquatableArray<string> TypeParameters,
    //EquatableArray<TypeRef> BaseInterfaces,
    EquatableArray<PropertyModel> Properties,
    EquatableArray<MethodModel> Methods,
    string FileHintName)
{
    public PropertyModel? IdProperty => 
            Properties.FirstOrDefault(p => p.Kinds.HasFlag(PropertyKind.Id));

    public PropertyModel? ParentProperty =>
        Properties.FirstOrDefault(p => p.Kinds.HasFlag(PropertyKind.Parent));

    public IEnumerable<PropertyModel> ChildrenProperties =>
        Properties.Where(p => p.Kinds.HasFlag(PropertyKind.Children));

    public IEnumerable<PropertyModel> ReferenceProperties =>
        Properties.Where(p => p.Kinds.HasFlag(PropertyKind.Reference));

    public IEnumerable<PropertyModel> RelationProperties =>
        Properties.Where(p => p.RelationRole is MethodRole.ForwardRelation or MethodRole.InverseRelation);
}
