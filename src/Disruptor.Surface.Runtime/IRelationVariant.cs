namespace Disruptor.Surface.Runtime;

/// <summary>
/// Marker interface that relation variants implement so <see cref="SurrealSession"/>
/// can branch on edge-shaped vs table-shaped entities at <see cref="ISaveContext.MarkSaved"/>
/// and <c>CleanupLocalState</c> time. Methodless on purpose — variants already expose
/// everything Session needs via <see cref="IEntity"/>: <c>Id.Table</c> for the edge name,
/// <c>EnumerateReferences()</c> for the endpoint ids.
/// <para>
/// Emitted onto every variant class by
/// <see cref="Disruptor.Surface.Generator.Emit"/>'s
/// <c>RelationVariantEmitter</c>; user code never declares this directly.
/// </para>
/// </summary>
public interface IRelationVariant : IEntity { }
