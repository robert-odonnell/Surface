namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// One scalar field on an edge-table payload, harvested from a
/// <c>ForwardRelation&lt;TPayload&gt;</c>'s payload type. Mirrors the shape of
/// <see cref="PropertyModel"/> for entity tables — a name, a type, the SurrealDB-side
/// snake-cased field name — but stays a tiny equatable record so the incremental
/// pipeline can dedupe by value without dragging in entity-only state (relation roles,
/// reference-delete behaviour, etc).
/// </summary>
/// <param name="Name">C# property name on the payload type, e.g. <c>RunId</c>.</param>
/// <param name="FieldName">Snake-cased SurrealDB field name, e.g. <c>run_id</c>.</param>
/// <param name="Type">Payload property's type — fed into <c>SchemaEmitter.MapScalarType</c> at emit time.</param>
public sealed record EdgePayloadFieldModel(
    string Name,
    string FieldName,
    TypeRef Type);
