using System.Collections;
using System.Text;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Compiles a predicate AST + traversal AST + optional pinned id into a SurrealQL
/// <c>SELECT</c> string plus a parameter dictionary suitable for
/// <see cref="ISurrealTransport.ExecuteAsync"/>. Every literal value (predicate operand,
/// pinned id) is parameterised — the SQL itself only carries identifiers and operators,
/// so the transport's <c>LET</c> prefix renderer handles the actual values via
/// <see cref="SurrealFormatter"/>.
/// <para>
/// Traversals expand to nested <c>(SELECT … FROM child WHERE parent_field = $parent.id) AS child</c>
/// subselects. Each level's WHERE merges the parent-link constraint with the user's
/// per-level <see cref="IncludeChildrenNode.Filter"/>. Parameter naming is shared across
/// all levels (one global <c>$pN</c> counter) so the wire SQL has no scoping ambiguity.
/// </para>
/// </summary>
internal static class QueryCompiler
{
    /// <summary>
    /// Build the SurrealQL + bindings for a (possibly nested) <c>SELECT</c>.
    /// </summary>
    /// <param name="table">Snake-cased SurrealDB table name. Validated by <see cref="SurrealFormatter.Identifier"/>.</param>
    /// <param name="filter">Optional predicate AST. AND-merged with the pinned-id constraint when both are present.</param>
    /// <param name="pinnedId">Optional record id pin. When set, emits <c>id = $pN</c> as a leading WHERE clause.</param>
    /// <param name="includes">Traversal AST — projection extensions and nested children subselects.</param>
    public static (string Sql, IReadOnlyDictionary<string, object?>? Bindings) Compile(
        string table,
        IPredicate? filter,
        RecordId? pinnedId,
        IReadOnlyList<IIncludeNode> includes)
    {
        var bindings = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sb = new StringBuilder();

        var projection = BuildProjection(includes, bindings);

        sb.Append("SELECT ").Append(projection).Append(" FROM ").Append(SurrealFormatter.Identifier(table));

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

    /// <summary>Backwards-compat overload — flat select, no traversals.</summary>
    public static (string Sql, IReadOnlyDictionary<string, object?>? Bindings) Compile(
        string table, IPredicate? filter, RecordId? pinnedId)
        => Compile(table, filter, pinnedId, Array.Empty<IIncludeNode>());

    /// <summary>
    /// Compile a predicate AST in isolation — returns the WHERE-clause text plus the
    /// parameter dictionary, without wrapping it in a SELECT. Used by callers that build
    /// their own statement shape (e.g. <see cref="EdgeQueryCompiler"/>) but want to reuse
    /// the predicate vocabulary and parameter normalisation.
    /// </summary>
    public static (string WhereClause, Dictionary<string, object?> Bindings) CompilePredicate(IPredicate predicate)
    {
        var bindings = new Dictionary<string, object?>(StringComparer.Ordinal);
        var clause = CompilePredicate(predicate, bindings);
        return (clause, bindings);
    }

    /// <summary>
    /// Compose a level's projection list: starts with <c>*</c>, then adds <c>field.*</c>
    /// per inline-ref include, then adds the rendered subselect per children-include.
    /// Order is stable: inline-refs precede subselects so the <c>SELECT</c>'s scalar +
    /// inline-record half stays adjacent and easy to scan in transport logs.
    /// </summary>
    private static string BuildProjection(IReadOnlyList<IIncludeNode> includes, Dictionary<string, object?> bindings)
    {
        if (includes.Count == 0) return "*";

        var sb = new StringBuilder("*");
        foreach (var node in includes)
        {
            if (node is IncludeInlineRefNode inline)
            {
                sb.Append(", ").Append(SurrealFormatter.Identifier(inline.Field)).Append(".*");
            }
        }
        foreach (var node in includes)
        {
            if (node is IncludeChildrenNode children)
            {
                sb.Append(", ").Append(BuildChildSubselect(children, bindings));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render a single <see cref="IncludeChildrenNode"/> as a parenthesised subselect
    /// aliased to the child table name:
    /// <c>(SELECT projection FROM child WHERE parent_field = $parent.id [AND filter]) AS child</c>.
    /// Recurses into <see cref="IncludeChildrenNode.Nested"/> for the inner projection.
    /// </summary>
    private static string BuildChildSubselect(IncludeChildrenNode node, Dictionary<string, object?> bindings)
    {
        var childTable = SurrealFormatter.Identifier(node.ChildTable);
        var parentField = SurrealFormatter.Identifier(node.ParentField);
        var innerProjection = BuildProjection(node.Nested, bindings);

        var where = $"{parentField} = $parent.id";
        if (node.Filter is not null)
        {
            where = $"{where} AND {CompilePredicate(node.Filter, bindings)}";
        }

        return $"(SELECT {innerProjection} FROM {childTable} WHERE {where}) AS {childTable}";
    }

    private static string CompilePredicate(IPredicate p, Dictionary<string, object?> bindings) => p switch
    {
        EqPredicate eq        => $"{SurrealFormatter.Identifier(eq.Field)} = ${NextParam(bindings, eq.Value)}",
        RangePredicate rp     => $"{SurrealFormatter.Identifier(rp.Field)} {RangeOpText(rp.Op)} ${NextParam(bindings, rp.Value)}",
        InPredicate ip        => $"{SurrealFormatter.Identifier(ip.Field)} IN ${NextParam(bindings, ip.Values)}",
        ContainsPredicate cp  => $"string::contains({SurrealFormatter.Identifier(cp.Field)}, ${NextParam(bindings, cp.Substring)})",
        AndPredicate a        => $"({string.Join(" AND ", a.Operands.Select(o => CompilePredicate(o, bindings)))})",
        OrPredicate  o        => $"({string.Join(" OR ", o.Operands.Select(op => CompilePredicate(op, bindings)))})",
        NotPredicate n        => $"!({CompilePredicate(n.Operand, bindings)})",
        _ => throw new NotSupportedException($"Predicate type {p.GetType().FullName} is not supported by QueryCompiler.")
    };

    private static string RangeOpText(RangeOp op) => op switch
    {
        RangeOp.Lt => "<",
        RangeOp.Le => "<=",
        RangeOp.Gt => ">",
        RangeOp.Ge => ">=",
        _ => throw new NotSupportedException($"Unknown RangeOp: {op}")
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
    /// Recurses into collections so <c>InPredicate.Values</c> with mixed typed-id elements
    /// gets each element normalised.
    /// </summary>
    private static object? NormalizeBoundValue(object? value) => value switch
    {
        null => null,
        string => value, // strings are IEnumerable<char>; never decompose them
        RecordId => value,
        IRecordId rid => RecordId.From(rid),
        IEnumerable e => NormalizeEnumerable(e),
        _ => value,
    };

    private static List<object?> NormalizeEnumerable(IEnumerable e)
    {
        var result = new List<object?>();
        foreach (var item in e)
        {
            result.Add(NormalizeBoundValue(item));
        }
        return result;
    }
}
