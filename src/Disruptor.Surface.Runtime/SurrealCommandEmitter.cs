using System.Text;

namespace Disruptor.Surface.Runtime;

public static class SurrealCommandEmitter
{
    /// <summary>
    /// Render every command into a single SurrealQL script, with each statement
    /// terminated by <c>;\n</c>. Used by <c>SurrealSession.RenderBatch</c> for
    /// diagnostics and tests; production commit goes per-command via
    /// <see cref="EmitOne"/> so each statement is its own RPC inside an open
    /// transaction.
    /// </summary>
    public static string Emit(IReadOnlyList<Command> commands)
    {
        var sb = new StringBuilder();
        foreach (var c in commands)
        {
            EmitOne(c, sb);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render a single command as a SurrealQL statement (terminated by <c>;\n</c>).
    /// Convenience wrapper over <see cref="EmitOne(Command, StringBuilder)"/>.
    /// </summary>
    public static string EmitOne(Command command)
    {
        var sb = new StringBuilder();
        EmitOne(command, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Append one command's SurrealQL form to <paramref name="sb"/> (terminated by
    /// <c>;\n</c>). The streamed-commit path calls this per command and dispatches
    /// each rendered statement as its own RPC inside the open transaction; the
    /// single-string <see cref="Emit(IReadOnlyList{Command})"/> form just loops it.
    /// </summary>
    public static void EmitOne(Command c, StringBuilder sb)
    {
        switch (c.Op)
        {
            case CommandOp.Create:
                sb.Append("CREATE ").Append(FormatId(c.Target));
                if (c.Value is IReadOnlyDictionary<string, object?> { Count: > 0 } createContent)
                {
                    sb.Append(" CONTENT ");
                    AppendContent(sb, createContent);
                }
                sb.Append(";\n");
                break;

            case CommandOp.Upsert:
                sb.Append("UPSERT ").Append(FormatId(c.Target));
                if (c.Value is IReadOnlyDictionary<string, object?> { Count: > 0 } upsertContent)
                {
                    sb.Append(" CONTENT ");
                    AppendContent(sb, upsertContent);
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
            {
                // Edge id strategy resolves at emit:
                //   - Resolved (Random Ulid pre-minted, or Slug)  → render the value verbatim
                //   - Idempotent (sentinel: empty value)          → hash the triple
                // Schema-level `DEFINE INDEX … COLUMNS in, out UNIQUE` is the
                // duplicate guard; the planner's ExistedAtStart check skips edges
                // that already exist so re-running on a loaded aggregate is a no-op.
                var src = c.Target;
                var tgt = (RecordId)c.Value!;
                var edge = c.Edge.Resolve(src, tgt);
                sb.Append("RELATE ").Append(FormatId(src))
                  .Append("->").Append(FormatId(edge))
                  .Append("->").Append(FormatId(tgt));
                if (c.EdgeContent is { Count: > 0 } content)
                {
                    sb.Append(" CONTENT ");
                    AppendContent(sb, content);
                }
                sb.Append(";\n");
                break;
            }

            case CommandOp.Unrelate:
            {
                // Merged shape — at least one of source/target is non-null.
                //   both:        DELETE edge WHERE in = source AND out = target
                //   source-only: DELETE edge WHERE in = source
                //   target-only: DELETE edge WHERE out = target
                // Source absence is encoded as `default(RecordId)` (Table is null);
                // target absence is encoded as Value = null. The Command.Unrelate
                // factory enforces the at-least-one invariant.
                var hasSource = c.Target.Table is not null;
                var target = (RecordId?)c.Value;
                sb.Append("DELETE ").Append(c.Key!.Identifier()).Append(" WHERE ");
                if (hasSource)
                {
                    sb.Append("in = ").Append(FormatId(c.Target));
                }
                if (hasSource && target is not null)
                {
                    sb.Append(" AND ");
                }
                if (target is { } t)
                {
                    sb.Append("out = ").Append(FormatId(t));
                }
                sb.Append(";\n");
                break;
            }
        }
    }

    private static void AppendContent(StringBuilder sb, IReadOnlyDictionary<string, object?> content)
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

    private static string FormatId(RecordId id) => id.RecordId();
}

public enum CommandOp
{
    Create,    // CREATE record:id
    Upsert,    // UPSERT record:id CONTENT { … }
    Set,       // UPDATE record:id SET key = value
    Unset,     // UPDATE record:id UNSET key
    Delete,    // DELETE record:id
    Relate,    // RELATE source->edge->target [CONTENT { … }]          — uniqueness enforced by the schema-level UNIQUE INDEX
    Unrelate,  // DELETE edge WHERE [in = source] [AND] [out = target] — at least one endpoint non-null
}

/// <summary>
/// Atomic SurrealDB intent.
/// </summary>
/// <param name="Op">The kind of operation.</param>
/// <param name="Target">
/// For Create/Upsert/Set/Delete: the record. For Relate/Unrelate: the <c>in</c> endpoint.
/// </param>
/// <param name="Key">
/// For Set: the field name. For Relate/Unrelate: the edge-table name (mirrors
/// <c>Edge.Table</c> for relate; sole identifier for unrelate). Null otherwise.
/// </param>
/// <param name="Value">
/// For Set: the scalar. For Upsert: the CONTENT dictionary. For Relate/Unrelate: the <c>out</c> endpoint <see cref="RecordId"/>.
/// </param>
/// <param name="Edge">
/// For Relate only: the full edge id, carrying the strategy. The <see cref="RecordId.Value"/>
/// is rendered verbatim when present (Slug or pre-minted Ulid); when
/// <see cref="RecordId.IsIdempotent"/> the emitter resolves it to a deterministic hash
/// of <c>{source}|{Table}|{target}</c>.
/// </param>
public readonly record struct Command(
    CommandOp Op,
    RecordId Target,
    string? Key = null,
    object? Value = null,
    IReadOnlyDictionary<string, object?>? EdgeContent = null,
    RecordId Edge = default)
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

    /// <summary>
    /// Relate <paramref name="source"/> to <paramref name="target"/> via the
    /// <paramref name="edge"/> table. The <c>edge</c> RecordId carries the id strategy:
    /// <list type="bullet">
    ///   <item><b>Random</b> — caller mints a Ulid via <see cref="RecordId.New"/>; each
    ///         Relate with the same triple but a fresh Ulid is the same logical edge
    ///         (PendingState dedups by triple).</item>
    ///   <item><b>Slug</b> — caller passes <c>new RecordId(table, slug)</c>; the slug
    ///         renders verbatim as the edge row id.</item>
    ///   <item><b>Idempotent</b> — caller passes <see cref="RecordId.Idempotent"/>; the
    ///         emit layer computes the hash from the linkage triple at write time.</item>
    /// </list>
    /// </summary>
    public static Command Relate(RecordId source, RecordId edge, RecordId target, IReadOnlyDictionary<string, object?>? content = null) =>
        new(CommandOp.Relate, source, Key: edge.Table, Value: target, EdgeContent: Freeze(content), Edge: edge);

    /// <summary>
    /// Edge removal. At least one of <paramref name="source"/> / <paramref name="target"/>
    /// must be non-null. Both non-null targets a single edge; one-side-null is the bulk
    /// form (every matching edge of <paramref name="edgeTable"/>, persisted-or-not).
    /// </summary>
    public static Command Unrelate(RecordId? source, string edgeTable, RecordId? target)
    {
        if (source is null && target is null)
        {
            throw new ArgumentException(
                "Unrelate requires at least one of source or target to be non-null.");
        }
        // `default(RecordId)` (Table = null, Value = null) is the in-band "no source"
        // sentinel — Command.Target is non-nullable so we can't store null directly.
        // The emitter checks `Target.Table is not null` to decide whether to emit
        // `WHERE in = …`.
        return new(CommandOp.Unrelate, source ?? default, edgeTable, Value: target);
    }

    private static Dictionary<string, object?>? Freeze(IEnumerable<KeyValuePair<string, object?>>? source) =>
        source is null ? null : new Dictionary<string, object?>(source, StringComparer.Ordinal);
}


