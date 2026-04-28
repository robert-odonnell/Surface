using System.Text;

namespace Disruptor.Surface.Runtime;

public static class SurrealCommandEmitter
{
    public static (string Sql, IReadOnlyDictionary<string, object?> Parameters) Emit(IReadOnlyList<Command> commands)
    {
        var sb = new StringBuilder();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var counter = 0;

        foreach (var c in commands)
        {
            switch (c.Op)
            {
                case CommandOp.Create:
                    sb.Append("CREATE ").Append(FormatId(c.Target)).Append(";\n");
                    break;

                case CommandOp.Upsert:
                    sb.Append("UPSERT ").Append(FormatId(c.Target));
                    if (c.Value is IReadOnlyDictionary<string, object?> { Count: > 0 } upsertContent)
                    {
                        sb.Append(" CONTENT ");
                        AppendContent(upsertContent);
                    }
                    sb.Append(";\n");
                    break;

                case CommandOp.Set:
                    sb.Append("UPDATE ").Append(FormatId(c.Target))
                      .Append(" SET ").Append(SurrealFormatter.Identifier(c.Key!)).Append(" = ");
                    if (c.Value is RecordId setRid)
                    {
                        sb.Append(FormatId(setRid));
                    }
                    else
                    {
                        sb.Append(AddParam(c.Value));
                    }
                    sb.Append(";\n");
                    break;

                case CommandOp.Unset:
                    sb.Append("UPDATE ").Append(FormatId(c.Target))
                      .Append(" UNSET ").Append(SurrealFormatter.Identifier(c.Key!)).Append(";\n");
                    break;

                case CommandOp.Delete:
                    sb.Append("DELETE ").Append(FormatId(c.Target)).Append(";\n");
                    break;

                case CommandOp.Relate:
                    sb.Append("RELATE ").Append(FormatId(c.Target))
                      .Append("->").Append(SurrealFormatter.Identifier(c.Key!)).Append("->")
                      .Append(FormatId((RecordId)c.Value!));
                    if (c.EdgeContent is { Count: > 0 } content)
                    {
                        sb.Append(" CONTENT ");
                        AppendContent(content);
                    }
                    sb.Append(";\n");
                    break;

                case CommandOp.Unrelate:
                    sb.Append("DELETE ").Append(SurrealFormatter.Identifier(c.Key!))
                      .Append(" WHERE in = ").Append(FormatId(c.Target))
                      .Append(" AND out = ").Append(FormatId((RecordId)c.Value!))
                      .Append(";\n");
                    break;

                case CommandOp.UnrelateAllFrom:
                    sb.Append("DELETE ").Append(SurrealFormatter.Identifier(c.Key!))
                      .Append(" WHERE in = ").Append(FormatId(c.Target))
                      .Append(";\n");
                    break;

                case CommandOp.UnrelateAllTo:
                    sb.Append("DELETE ").Append(SurrealFormatter.Identifier(c.Key!))
                      .Append(" WHERE out = ").Append(FormatId(c.Target))
                      .Append(";\n");
                    break;
            }
        }
        return (sb.ToString(), parameters);

        string AddParam(object? value)
        {
            var name = $"p{counter++}";
            parameters[name] = value;
            return $"${name}";
        }

        void AppendContent(IReadOnlyDictionary<string, object?> content)
        {
            sb.Append("{ ");
            var first = true;
            foreach (var (k, v) in content)
            {
                if (!first)
                {
                    sb.Append(", ");
                }

                first = false;
                sb.Append(SurrealFormatter.Identifier(k)).Append(": ");
                if (v is RecordId rid)
                {
                    sb.Append(FormatId(rid));
                }
                else
                {
                    sb.Append(AddParam(v));
                }
            }
            sb.Append(" }");
        }
    }

    private static string FormatId(RecordId id) => SurrealFormatter.RecordId(id);
}

public enum CommandOp
{
    Create,            // CREATE record:id
    Upsert,            // UPSERT record:id CONTENT { … }
    Set,               // UPDATE record:id SET key = value
    Unset,             // UPDATE record:id UNSET key
    Delete,            // DELETE record:id
    Relate,            // RELATE source->edge->target
    Unrelate,          // DELETE edge WHERE in = source AND out = target
    UnrelateAllFrom,   // DELETE edge WHERE in = source         — bulk; persisted edges too
    UnrelateAllTo      // DELETE edge WHERE out = target        — bulk; persisted edges too
}

/// <summary>
/// Atomic SurrealDB intent in a uniform 4-tuple (Op, Id, Field?, Value?).
/// </summary>
/// <param name="Op">The kind of operation.</param>
/// <param name="Target">
/// For Create/Upsert/Set/Delete: the record. For Relate/Unrelate: the <c>in</c> endpoint.
/// </param>
/// <param name="Key">
/// For Set: the field name. For Relate/Unrelate: the edge-table name. Null otherwise.
/// </param>
/// <param name="Value">
/// For Set: the scalar. For Upsert: the CONTENT dictionary. For Relate/Unrelate: the <c>out</c> endpoint <see cref="RecordId"/>.
/// </param>
public readonly record struct Command(
    CommandOp Op,
    RecordId Target,
    string? Key = null,
    object? Value = null,
    IReadOnlyDictionary<string, object?>? EdgeContent = null)
{
    public static Command Create(RecordId target) =>
        new(CommandOp.Create, target);

    public static Command Upsert(RecordId target, IDictionary<string, object?>? content = null) =>
        new(CommandOp.Upsert, target, Value: content is null ? null : Freeze(content));

    public static Command Set(RecordId target, string key, object? value) =>
        new(CommandOp.Set, target, key, value);

    public static Command Unset(RecordId target, string key) =>
        new(CommandOp.Unset, target, key);

    public static Command Delete(RecordId target) =>
        new(CommandOp.Delete, target);

    public static Command Relate(RecordId source, string edgeTable, RecordId target, IReadOnlyDictionary<string, object?>? content = null) =>
        new(CommandOp.Relate, source, edgeTable, target, EdgeContent: Freeze(content));

    public static Command Unrelate(RecordId source, string edgeTable, RecordId target) =>
        new(CommandOp.Unrelate, source, edgeTable, target);

    public static Command UnrelateAllFrom(RecordId source, string edgeTable) =>
        new(CommandOp.UnrelateAllFrom, source, edgeTable);

    public static Command UnrelateAllTo(RecordId target, string edgeTable) =>
        new(CommandOp.UnrelateAllTo, target, edgeTable);

    private static IReadOnlyDictionary<string, object?>? Freeze(IEnumerable<KeyValuePair<string, object?>>? source) =>
        source is null ? null : new Dictionary<string, object?>(source, StringComparer.Ordinal);
}


