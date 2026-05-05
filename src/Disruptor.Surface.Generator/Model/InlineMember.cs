namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// One member of an inline-record element type used inside a <c>SurrealArray&lt;T&gt;</c>
/// <c>[Property]</c>. Captured at extraction time so <see cref="Disruptor.Surface.Generator.Emit.SchemaEmitter"/>
/// can emit per-member sub-field DDL (e.g. <c>scenarios.*.kind</c>, <c>scenarios.*.description</c>)
/// without re-resolving the type at emit time.
/// </summary>
public sealed record InlineMember(string Name, TypeRef Type);
