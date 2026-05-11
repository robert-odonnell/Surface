using System.Collections;
using System.Text;
using Disruptor.Surreal.Values;

namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Compiles a predicate AST + traversal AST + optional pinned id into a SurrealQL
/// <c>SELECT</c> string plus a typed-CBOR <see cref="SurrealObject"/> bindings dict.
/// Every leaf value (predicate operand, pinned id, IN list element) is allocated a
/// <c>$_pN</c> placeholder and pushed into bindings as the appropriate
/// <see cref="SurrealValue"/> variant. The wire path stays end-to-end CBOR — no
/// SurrealQL string literals for user values, no escape rules, no JSON.
/// <para>
/// Traversals expand to nested <c>(SELECT … FROM child WHERE parent_field = $parent.id) AS child</c>
/// subselects. Identifiers (table names, field names, edge names, slice keys) stay
/// inlined in the SQL — they're trusted, regex-validated by
/// <see cref="SurrealFormatter.Identifier"/>. <c>LIMIT</c> / <c>START</c> integers also
/// stay inlined (no escape concern; SurrealQL parses them directly).
/// </para>
/// </summary>
internal static class QueryCompiler
{
    /// <summary>Build the SurrealQL + bindings for a (possibly nested) <c>SELECT</c>.</summary>
    public static (string Sql, SurrealObject Bindings) Compile(
        string table,
        IPredicate? filter,
        RecordId? pinnedId,
        IReadOnlyList<IIncludeNode> includes,
        IReadOnlyList<OrderClause>? orderClauses = null,
        int? limit = null,
        int? start = null)
    {
        var b = new Builder();
        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(b.BuildProjection(includes)).Append(" FROM ").Append(table.Identifier());
        b.AppendWhereOrderLimitStart(sb, filter, pinnedId, orderClauses, limit, start);
        sb.Append(';');
        return (sb.ToString(), b.Bindings);
    }

    /// <summary>
    /// Build the SurrealQL + bindings for a projection selection:
    /// <c>SELECT field1, field2 FROM table …</c>. Includes are not supported on this path —
    /// projections are flat by definition.
    /// </summary>
    public static (string Sql, SurrealObject Bindings) CompileProjection(
        string table,
        IReadOnlyList<string> selectFields,
        IPredicate? filter,
        RecordId? pinnedId,
        IReadOnlyList<OrderClause>? orderClauses = null,
        int? limit = null,
        int? start = null)
    {
        if (selectFields.Count == 0)
        {
            throw new ArgumentException("Projection requires at least one field.", nameof(selectFields));
        }

        var b = new Builder();
        var sb = new StringBuilder();
        sb.Append("SELECT ");
        for (var i = 0; i < selectFields.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(selectFields[i].Identifier());
        }
        sb.Append(" FROM ").Append(table.Identifier());
        b.AppendWhereOrderLimitStart(sb, filter, pinnedId, orderClauses, limit, start);
        sb.Append(';');
        return (sb.ToString(), b.Bindings);
    }

    /// <summary>
    /// Build the SurrealQL + bindings for an id-only selection:
    /// <c>SELECT id FROM table …</c>. Includes are not supported (flat by definition).
    /// </summary>
    public static (string Sql, SurrealObject Bindings) CompileIdsOnly(
        string table,
        IPredicate? filter,
        RecordId? pinnedId,
        IReadOnlyList<OrderClause>? orderClauses = null,
        int? limit = null,
        int? start = null)
    {
        var b = new Builder();
        var sb = new StringBuilder();
        sb.Append("SELECT id FROM ").Append(table.Identifier());
        b.AppendWhereOrderLimitStart(sb, filter, pinnedId, orderClauses, limit, start);
        sb.Append(';');
        return (sb.ToString(), b.Bindings);
    }

    /// <summary>
    /// Per-compile mutable state: the bindings accumulator + a monotonic counter that
    /// names each placeholder. Keeps the <see cref="QueryCompiler"/> static surface clean
    /// while letting the recursive subselect / predicate walk share the binding stream.
    /// </summary>
    internal sealed class Builder
    {
        public SurrealObject Bindings { get; } = new();
        private int counter;

        /// <summary>
        /// Allocate a fresh <c>$_pN</c>, push <paramref name="value"/> into bindings as
        /// the right <see cref="SurrealValue"/> variant, and return the placeholder text.
        /// Typed-CBOR end-to-end: no string formatting, no escape rules.
        /// </summary>
        public string Allocate(object? value)
        {
            var name = $"_p{counter++}";
            Bindings[name] = WrapAsSurrealValue(value);
            return "$" + name;
        }

        public void AppendWhereOrderLimitStart(
            StringBuilder sb,
            IPredicate? filter,
            RecordId? pinnedId,
            IReadOnlyList<OrderClause>? orderClauses,
            int? limit,
            int? start)
        {
            var hasWhere = false;
            if (pinnedId is { } id)
            {
                sb.Append(" WHERE id = ").Append(Allocate(id));
                hasWhere = true;
            }
            if (filter is not null)
            {
                sb.Append(hasWhere ? " AND " : " WHERE ").Append(CompilePredicate(filter));
            }

            // SurrealQL clause order is fixed: ORDER BY → LIMIT → START. Stable order
            // matches the docs and avoids "why doesn't this paginate" surprises.
            AppendOrderBy(sb, orderClauses);
            AppendLimit(sb, limit);
            AppendStart(sb, start);
        }

        private static void AppendOrderBy(StringBuilder sb, IReadOnlyList<OrderClause>? clauses)
        {
            if (clauses is null || clauses.Count == 0) return;

            sb.Append(" ORDER BY ");
            for (var i = 0; i < clauses.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var c = clauses[i];
                sb.Append(c.Field.Identifier()).Append(c.Direction == OrderDirection.Descending ? " DESC" : " ASC");
            }
        }

        private static void AppendLimit(StringBuilder sb, int? limit)
        {
            // LIMIT/START are integer literals — no escape concern, no value typing
            // benefit from binding. Inline directly.
            if (limit is { } n && n > 0) sb.Append(" LIMIT ").Append(n);
        }

        private static void AppendStart(StringBuilder sb, int? start)
        {
            if (start is { } n && n > 0) sb.Append(" START ").Append(n);
        }

        /// <summary>
        /// Compose a level's projection list: starts with <c>*</c>, then adds
        /// <c>field.*</c> per inline-ref include, then adds the rendered subselect per
        /// children-include / relation-include. Order is stable: inline-refs precede
        /// subselects so the SELECT's scalar + inline-record half stays adjacent.
        /// </summary>
        public string BuildProjection(IReadOnlyList<IIncludeNode> includes)
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
        /// </summary>
        private string BuildRelationSubselect(IncludeRelationNode node)
        {
            var alias = node.ParentSliceKey.Identifier();
            var edge = node.EdgeName.Identifier();

            if (node.IdsOnly)
            {
                // Cross-aggregate: edge subselect with id/in/out so the session's edges
                // dict can be populated without target hydration.
                var sideField = node.IsOutgoing ? "in" : "out";
                return $"(SELECT id, in, out FROM {edge} WHERE {sideField} = $parent.id) AS {alias}";
            }

            // Within-aggregate: graph traversal. `?` matches any target type when the
            // relation has multiple members; single concrete target narrows to it.
            var arrow = node.IsOutgoing ? "->" : "<-";
            var step = node.SingleTargetTable is { } target ? target.Identifier() : "?";

            if (node.Nested.Count == 0)
            {
                var filterClause = node.Filter is null ? "" : $"[WHERE {CompilePredicate(node.Filter)}]";
                return $"({arrow}{edge}{arrow}{step}{filterClause}.*) AS {alias}";
            }

            var inner = BuildProjection(node.Nested);
            var traversal = $"{arrow}{edge}{arrow}{step}";
            var where = node.Filter is null ? "" : $" WHERE {CompilePredicate(node.Filter)}";
            return $"(SELECT {inner} FROM {traversal}{where}) AS {alias}";
        }

        /// <summary>
        /// Render a single <see cref="IncludeChildrenNode"/> as a parenthesised subselect
        /// aliased to the child table name.
        /// </summary>
        private string BuildChildSubselect(IncludeChildrenNode node)
        {
            var childTable = node.ChildTable.Identifier();
            var parentField = node.ParentField.Identifier();
            var innerProjection = BuildProjection(node.Nested);

            var where = $"{parentField} = $parent.id";
            if (node.Filter is not null)
            {
                where = $"{where} AND {CompilePredicate(node.Filter)}";
            }

            return $"(SELECT {innerProjection} FROM {childTable} WHERE {where}) AS {childTable}";
        }

        public string CompilePredicate(IPredicate p) => p switch
        {
            EqPredicate eq        => $"{eq.Field.Identifier()} = {Allocate(eq.Value)}",
            RangePredicate rp     => $"{rp.Field.Identifier()} {RangeOpText(rp.Op)} {Allocate(rp.Value)}",
            InPredicate ip        => $"{ip.Field.Identifier()} IN {Allocate(ip.Values)}",
            ContainsPredicate cp  => $"string::contains({cp.Field.Identifier()}, {Allocate(cp.Substring)})",
            AndPredicate a        => $"({string.Join(" AND ", a.Operands.Select(CompilePredicate))})",
            OrPredicate  o        => $"({string.Join(" OR ", o.Operands.Select(CompilePredicate))})",
            NotPredicate n        => $"!({CompilePredicate(n.Operand)})",
            _ => throw new NotSupportedException($"Predicate type {p.GetType().FullName} is not supported by QueryCompiler.")
        };
    }

    private static string RangeOpText(RangeOp op) => op switch
    {
        RangeOp.Lt => "<",
        RangeOp.Le => "<=",
        RangeOp.Gt => ">",
        RangeOp.Ge => ">=",
        _ => throw new NotSupportedException($"Unknown RangeOp: {op}")
    };

    /// <summary>
    /// Wrap a CLR value as the right <see cref="SurrealValue"/> variant. Typed-CBOR all
    /// the way down: typed record ids land as <see cref="SurrealRecordIdValue"/>
    /// (preserves Thing typing); IN lists land as <see cref="SurrealListValue"/>; null
    /// becomes <see cref="SurrealValue.Null"/>. Throws for anything we don't recognise
    /// — better a build-time-visible failure than a silent string-fallback.
    /// </summary>
    internal static SurrealValue WrapAsSurrealValue(object? value) => value switch
    {
        null => SurrealValue.Null,
        bool b => b,
        sbyte sb => (long)sb,
        byte by => (long)by,
        short s => (long)s,
        ushort us => (long)us,
        int i => i,
        uint ui => (long)ui,
        long l => l,
        ulong ul => (long)ul,
        float f => (double)f,
        double d => d,
        decimal m => m,
        // strings are IEnumerable<char>; the explicit case stops the IEnumerable arm
        // from decomposing them into character lists.
        string str => str,
        Guid g => g,
        Ulid u => new StringSurrealValue(u.ToString()),
        DateTime dt => (DateTimeOffset)dt,
        DateTimeOffset dto => dto,
        TimeSpan ts => ts,
        Enum e => new StringSurrealValue(e.ToString()),
        RecordId rid => new SurrealRecordIdValue(rid.ToSdk()),
        IRecordId irid => new SurrealRecordIdValue(RecordId.From(irid).ToSdk()),
        IEnumerable e => new SurrealListValue(WrapEnumerable(e)),
        _ => throw new NotSupportedException(
            $"Cannot wrap value of type {value.GetType().FullName} as a SurrealValue. "
            + "Add a case to QueryCompiler.WrapAsSurrealValue if a new type needs binding support.")
    };

    private static SurrealList WrapEnumerable(IEnumerable e)
    {
        var list = new SurrealList();
        foreach (var item in e) list.Add(WrapAsSurrealValue(item));
        return list;
    }
}
