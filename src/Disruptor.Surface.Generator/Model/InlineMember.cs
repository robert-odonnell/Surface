namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// One member of an inline-record element type used inside an inline-element collection
/// <c>[Property]</c> (<c>IReadOnlyList&lt;T&gt;</c> / <c>IList&lt;T&gt;</c> / <c>List&lt;T&gt;</c> of records).
/// Captured at extraction time so <see cref="Disruptor.Surface.Generator.Emit.SchemaEmitter"/>
/// can emit per-member sub-field DDL (e.g. <c>scenarios.*.kind</c>, <c>scenarios.*.description</c>)
/// and so <see cref="Disruptor.Surface.Generator.Emit.PartialEmitter"/> can emit typed
/// Hydrate / Save bodies without re-resolving the type at emit time.
/// </summary>
public sealed record InlineMember(string Name, TypeRef Type);
