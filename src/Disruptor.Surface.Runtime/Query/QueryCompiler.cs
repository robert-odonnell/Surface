using System.Collections;
using System.Text;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Compiles a predicate AST + traversal AST + optional pinned id into a SurrealQL
/// <c>SELECT</c> string plus a parameter dictionary suitable for
/// <see cref="ISurrealTransport.ExecuteAsync"/>. Every literal value (predicate operand,
/// pinned id) is rendered inline in the SQL via <see cref="SurrealFormatter"/>.
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
    public static string Compile(
        string table,
        IPredicate? filter,
        RecordId? pinnedId,
        IReadOnlyList<IIncludeNode> includes)
    {
        var sb = new StringBuilder();

        var projection = BuildProjection(includes);

        sb.Append("SELECT ").Append(projection).Append(" FROM ").Append(table.Identifier());

        var clauses = new List<string>(2);
        if (pinnedId is { } id)
        {
            clauses.Add($"id = {NextParam(id)}");
        }
        if (filter is not null)
        {
            clauses.Add(filter.CompilePredicate());
        }
        if (clauses.Count > 0)
        {
            sb.Append(" WHERE ").Append(string.Join(" AND ", clauses));
        }
        sb.Append(';');

        return sb.ToString();
    }

    /// <summary>Backwards-compat overload — flat select, no traversals.</summary>
    public static string Compile(
        string table, IPredicate? filter, RecordId? pinnedId)
        => Compile(table, filter, pinnedId, []);

    /// <summary>
    /// Compile a predicate AST in isolation — returns the WHERE-clause text plus the
    /// parameter dictionary, without wrapping it in a SELECT. Used by callers that build
    /// their own statement shape (e.g. <see cref="EdgeQueryCompiler"/>) but want to reuse
    /// the predicate vocabulary and parameter normalisation.
    /// </summary>
    //public static string CompilePredicate(IPredicate predicate)
    //{
    //    var clause = CompilePredicate(predicate);
    //    return (clause);
    //}

    /// <summary>
    /// Compose a level's projection list: starts with <c>*</c>, then adds <c>field.*</c>
    /// per inline-ref include, then adds the rendered subselect per children-include.
    /// Order is stable: inline-refs precede subselects so the <c>SELECT</c>'s scalar +
    /// inline-record half stays adjacent and easy to scan in transport logs.
    /// </summary>
    private static string BuildProjection(IReadOnlyList<IIncludeNode> includes)
    {
        if (includes.Count == 0) return "*";

        var sb = new StringBuilder("*");
        foreach (var node in includes)
        {
            if (node is IncludeInlineRefNode inline)
            {
                sb.Append(", ").Append(inline.Field.Identifier()).Append(".*");
            }
        }
        foreach (var node in includes)
        {
            if (node is IncludeChildrenNode children)
            {
                sb.Append(", ").Append(BuildChildSubselect(children));
            }
            else if (node is IncludeRelationNode relation)
            {
                sb.Append(", ").Append(BuildRelationSubselect(relation));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render an <see cref="IncludeRelationNode"/> as a SurrealQL projection element.
    /// Two shapes:
    /// <list type="bullet">
    ///   <item><b>Within-aggregate</b> (<c>IdsOnly == false</c>) — graph-traversal projection
    ///         <c>(->edge->? .*) AS slice</c>, with arrow direction flipped for inverse and
    ///         the target step narrowed to a specific table when single-target so a
    ///         target-side filter can attach via <c>[WHERE …]</c>.</item>
    ///   <item><b>Cross-aggregate</b> (<c>IdsOnly == true</c>) — edge subselect
    ///         <c>(SELECT id, in, out FROM edge WHERE in/out = $parent.id) AS slice</c>.
    ///         Mirrors the legacy aggregate loader's edge-row shape so the runtime can
    ///         feed the session's edges dict without hydrating target entities.</item>
    /// </list>
    /// </summary>
    private static string BuildRelationSubselect(IncludeRelationNode node)
    {
        var alias = node.ParentSliceKey.Identifier();
        var edge = node.EdgeName.Identifier();

        if (node.IdsOnly)
        {
            // Cross-aggregate: edge subselect with id/in/out so the session's edges dict
            // and (for single-target consumers) Pending.Relations can be populated.
            var sideField = node.IsOutgoing ? "in" : "out";
            return $"(SELECT id, in, out FROM {edge} WHERE {sideField} = $parent.id) AS {alias}";
        }

        // Within-aggregate: graph traversal. `?` matches any target type when the
        // relation has multiple members; when a single concrete target is known, narrow
        // to it so a target-side filter can attach.
        var arrow = node.IsOutgoing ? "->" : "<-";
        var step = node.SingleTargetTable is { } target ? target.Identifier() : "?";
        var filterClause = node.Filter is null ? "" : $"[WHERE {node.Filter.CompilePredicate()}]";
        return $"({arrow}{edge}{arrow}{step}{filterClause}.*) AS {alias}";
    }

    /// <summary>
    /// Render a single <see cref="IncludeChildrenNode"/> as a parenthesised subselect
    /// aliased to the child table name:
    /// <c>(SELECT projection FROM child WHERE parent_field = $parent.id [AND filter]) AS child</c>.
    /// Recurses into <see cref="IncludeChildrenNode.Nested"/> for the inner projection.
    /// </summary>
    private static string BuildChildSubselect(IncludeChildrenNode node)
    {
        var childTable = node.ChildTable.Identifier();
        var parentField = node.ParentField.Identifier();
        var innerProjection = BuildProjection(node.Nested);

        var where = $"{parentField} = $parent.id";
        if (node.Filter is not null)
        {
            where = $"{where} AND {node.Filter.CompilePredicate()}";
        }

        return $"(SELECT {innerProjection} FROM {childTable} WHERE {where}) AS {childTable}";
    }

    public static string CompilePredicate(this IPredicate p) => p switch
    {
        EqPredicate eq        => $"{eq.Field.Identifier()} = {NextParam(eq.Value)}",
        RangePredicate rp     => $"{rp.Field.Identifier()} {RangeOpText(rp.Op)} {NextParam(rp.Value)}",
        InPredicate ip        => $"{ip.Field.Identifier()} IN {NextParam(ip.Values)}",
        ContainsPredicate cp  => $"string::contains({cp.Field.Identifier()}, {NextParam(cp.Substring)})",
        AndPredicate a        => $"({string.Join(" AND ", a.Operands.Select(o => o.CompilePredicate()))})",
        OrPredicate  o        => $"({string.Join(" OR ", o.Operands.Select(op => op.CompilePredicate()))})",
        NotPredicate n        => $"!({n.Operand.CompilePredicate()})",
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

    private static string NextParam(object? value)
    {
        //var name = $"p{bindings.Count}";
        //bindings[name] = NormalizeBoundValue(value);
        return NormalizeBoundValue(value).RenderSurrealLiteral();
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
