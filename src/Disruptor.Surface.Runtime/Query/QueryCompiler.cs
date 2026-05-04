using System.Text;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Compiles a predicate AST + optional pinned id into a SurrealQL <c>SELECT</c> string
/// plus a parameter dictionary suitable for <see cref="ISurrealTransport.ExecuteAsync"/>.
/// Every literal value (predicate operand, pinned id) is parameterised — the SQL itself
/// only carries identifiers and operators, so the transport's <c>LET</c> prefix renderer
/// handles the actual values via <see cref="SurrealFormatter"/>.
/// </summary>
internal static class QueryCompiler
{
    /// <summary>
    /// Build the SurrealQL + bindings for a flat <c>SELECT * FROM table WHERE …</c>.
    /// </summary>
    /// <param name="table">Snake-cased SurrealDB table name. Validated by <see cref="SurrealFormatter.Identifier"/>.</param>
    /// <param name="filter">Optional predicate AST. AND-merged with the pinned-id constraint when both are present.</param>
    /// <param name="pinnedId">Optional record id pin. When set, emits <c>id = $pN</c> as a leading WHERE clause.</param>
    public static (string Sql, IReadOnlyDictionary<string, object?>? Bindings) Compile(
        string table,
        IPredicate? filter,
        RecordId? pinnedId)
    {
        var bindings = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append("SELECT * FROM ").Append(SurrealFormatter.Identifier(table));

        var clauses = new List<string>(2);
        if (pinnedId is { } id)
        {
            clauses.Add($"id = ${NextParam(bindings, id)}");
        }
        if (filter is not null)
        {
            clauses.Add(CompilePredicate(filter, bindings));
        }
        if (clauses.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", clauses));
        }
        sb.Append(';');

        return (sb.ToString(), bindings.Count == 0 ? null : bindings);
    }

    private static string CompilePredicate(IPredicate p, Dictionary<string, object?> bindings) => p switch
    {
        EqPredicate eq  => $"{SurrealFormatter.Identifier(eq.Field)} = ${NextParam(bindings, eq.Value)}",
        AndPredicate a  => $"({string.Join(" AND ", a.Operands.Select(o => CompilePredicate(o, bindings)))})",
        OrPredicate  o  => $"({string.Join(" OR ", o.Operands.Select(op => CompilePredicate(op, bindings)))})",
        NotPredicate n  => $"!({CompilePredicate(n.Operand, bindings)})",
        _ => throw new NotSupportedException($"Predicate type {p.GetType().FullName} is not supported by QueryCompiler.")
    };

    private static string NextParam(Dictionary<string, object?> bindings, object? value)
    {
        var name = $"p{bindings.Count}";
        bindings[name] = NormalizeBoundValue(value);
        return name;
    }

    /// <summary>
    /// Collapses typed record ids (e.g. generator-emitted <c>ConstraintId</c>) to canonical
    /// <see cref="RecordId"/> before they reach the transport's parameter renderer. The
    /// transport's <c>RenderValue</c> matches on concrete <c>RecordId</c>, not any
    /// <see cref="IRecordId"/>, so without this hop a typed id would fall through to
    /// <c>JsonSerializer.Serialize</c> and emit a quoted string instead of a record literal.
    /// </summary>
    private static object? NormalizeBoundValue(object? value) => value switch
    {
        null => null,
        RecordId => value,
        IRecordId rid => RecordId.From(rid),
        _ => value,
    };
}
