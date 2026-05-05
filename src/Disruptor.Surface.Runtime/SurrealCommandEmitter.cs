using System.Text;

namespace Disruptor.Surface.Runtime;

public static class SurrealCommandEmitter
{
    public static string Emit(IReadOnlyList<Command> commands)
    {
        var sb = new StringBuilder();
        //var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        //var counter = 0;

        foreach (var c in commands)
        {
            switch (c.Op)
            {
                case CommandOp.Create:
                    sb.Append("CREATE ").Append(FormatId(c.Target));
                    if (c.Value is IReadOnlyDictionary<string, object?> { Count: > 0 } createContent)
                    {
                        sb.Append(" CONTENT ");
                        AppendContent(createContent);
                    }
                    sb.Append(";\n");
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
                      .Append(" SET ").Append(c.Key!.Identifier()).Append(" = ");
                    if (c.Value is RecordId setRid)
                    {
                        sb.Append(FormatId(setRid));
                    }
                    else
                    {
                        sb.Append(c.Value.RenderSurrealLiteral());
                    }
                    sb.Append(";\n");
                    break;

                case CommandOp.Unset:
                    sb.Append("UPDATE ").Append(FormatId(c.Target))
                      .Append(" UNSET ").Append(c.Key!.Identifier()).Append(";\n");
                    break;

                case CommandOp.Delete:
                    sb.Append("DELETE ").Append(FormatId(c.Target)).Append(";\n");
                    break;

                case CommandOp.Relate:
                    sb.Append("RELATE ").Append(FormatId(c.Target))
                      .Append("->").Append(c.Key!.Identifier()).Append("->")
                      .Append(FormatId((RecordId)c.Value!));
                    if (c.EdgeContent is { Count: > 0 } content)
                    {
                        sb.Append(" CONTENT ");
                        AppendContent(content);
                    }
                    sb.Append(";\n");
                    break;

                case CommandOp.RelateOnce:
                {
                    // Deterministic edge id: same (source, kind, target) triple always
                    // produces the same hash, so re-issuing the same relate lands on the
                    // same edge row rather than stacking duplicates. The hash key uses
                    // pipe separators (illegal in SurrealQL identifiers, so unambiguous)
                    // and includes both table names so cross-table aliasing can't blur
                    // the (designs:abc, owns, tasks:abc) and (designs:abc, owns, notes:abc)
                    // triples into the same edge row.
                    //
                    // The render shape is RELATE-with-explicit-id rather than
                    // UPSERT-with-CONTENT. UPSERT writes a row to the edge table but
                    // SurrealDB rejects it on `TYPE RELATION ENFORCED` tables ("Found
                    // record … which is not a relation, but expected a RELATION") because
                    // the row never gets registered as a graph edge — the `->edge->`
                    // traversal can't see it. RELATE with an explicit edge id produces a
                    // proper graph edge AND honours the deterministic id, which is the
                    // shape SurrealDB accepts. `in` / `out` come from the RELATE syntax
                    // itself, not the CONTENT clause, so EdgeContent stays user-payload-
                    // only.
                    var src = c.Target;
                    var tgt = (RecordId)c.Value!;
                    var edgeTable = c.Key!;
                    _ = edgeTable.Identifier(); // validate; rendered identifier flows through FormatId(edgeId) below
                    var hash = RecordIdFormat.HashText($"{src.Table}:{src.Value}|{edgeTable}|{tgt.Table}:{tgt.Value}");
                    var edgeId = new RecordId(edgeTable, hash);
                    sb.Append("RELATE ").Append(FormatId(src))
                      .Append("->").Append(FormatId(edgeId))
                      .Append("->").Append(FormatId(tgt));
                    if (c.EdgeContent is { Count: > 0 } onceContent)
                    {
                        sb.Append(" CONTENT ");
                        AppendContent(onceContent);
                    }
                    sb.Append(";\n");
                    break;
                }

                case CommandOp.Unrelate:
                    sb.Append("DELETE ").Append(c.Key!.Identifier())
                      .Append(" WHERE in = ").Append(FormatId(c.Target))
                      .Append(" AND out = ").Append(FormatId((RecordId)c.Value!))
                      .Append(";\n");
                    break;

                case CommandOp.UnrelateAllFrom:
                    sb.Append("DELETE ").Append(c.Key!.Identifier())
                      .Append(" WHERE in = ").Append(FormatId(c.Target))
                      .Append(";\n");
                    break;

                case CommandOp.UnrelateAllTo:
                    sb.Append("DELETE ").Append(c.Key!.Identifier())
                      .Append(" WHERE out = ").Append(FormatId(c.Target))
                      .Append(";\n");
                    break;
            }
        }

        return sb.ToString();
        //return (sb.ToString(), parameters);

        //string AddParam(object? value)
        //{
        //    var name = $"p{counter++}";
        //    parameters[name] = value;
        //    return $"${name}";
        //}

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
                sb.Append(k.Identifier()).Append(": ");
                if (v is RecordId rid)
                {
                    sb.Append(FormatId(rid));
                }
                else
                {
                    sb.Append(v.RenderSurrealLiteral());
                }
            }
            sb.Append(" }");
        }
    }

    private static string FormatId(RecordId id) => id.RecordId();
}

public enum CommandOp
{
    Create,            // CREATE record:id
    Upsert,            // UPSERT record:id CONTENT { … }
    Set,               // UPDATE record:id SET key = value
    Unset,             // UPDATE record:id UNSET key
    Delete,            // DELETE record:id
    Relate,            // RELATE source->edge->target
    RelateOnce,        // RELATE source -> edge:<deterministic-hash(source, edge, target)> -> target [CONTENT { … }] — idempotent re-relate, valid on TYPE RELATION ENFORCED
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

    /// <summary>
    /// Fresh record create with full payload — renders as
    /// <c>CREATE record:id CONTENT { … };</c>. Used by <see cref="CommitPlanner"/> for
    /// any newly-tracked record so endpoints exist with their full schema-required
    /// content before <c>RELATE</c> statements run within the same transaction.
    /// SurrealDB's <c>TYPE RELATION ENFORCED</c> validates relation endpoints against
    /// the in-progress transactional state at the moment of <c>RELATE</c>; bare
    /// <c>CREATE id;</c> followed by <c>UPDATE id SET …</c> can leave the endpoint
    /// in a state the enforcer rejects, while <c>CREATE id CONTENT { … }</c> lands
    /// the record fully populated in one statement.
    /// </summary>
    public static Command Create(RecordId target, IDictionary<string, object?>? content) =>
        new(CommandOp.Create, target, Value: content is null ? null : Freeze(content));

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

    /// <summary>
    /// Idempotent edge create. The edge id is derived deterministically from
    /// <c>(source, edgeTable, target)</c> via <see cref="RecordIdFormat.HashText"/> at
    /// emit time, so re-running the same triple lands on the same row regardless of
    /// how many times <c>RelateOnce</c> is called. Renders as
    /// <c>UPSERT edge_table:&lt;hash&gt; CONTENT { in, out, … }</c>.
    /// </summary>
    public static Command RelateOnce(RecordId source, string edgeTable, RecordId target, IReadOnlyDictionary<string, object?>? content = null) =>
        new(CommandOp.RelateOnce, source, edgeTable, target, EdgeContent: Freeze(content));

    public static Command Unrelate(RecordId source, string edgeTable, RecordId target) =>
        new(CommandOp.Unrelate, source, edgeTable, target);

    public static Command UnrelateAllFrom(RecordId source, string edgeTable) =>
        new(CommandOp.UnrelateAllFrom, source, edgeTable);

    public static Command UnrelateAllTo(RecordId target, string edgeTable) =>
        new(CommandOp.UnrelateAllTo, target, edgeTable);

    private static Dictionary<string, object?>? Freeze(IEnumerable<KeyValuePair<string, object?>>? source) =>
        source is null ? null : new Dictionary<string, object?>(source, StringComparer.Ordinal);
}


