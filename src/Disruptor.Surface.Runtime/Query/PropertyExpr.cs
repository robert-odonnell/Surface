namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Typed accessor for a single SurrealDB column on a <c>[Table]</c>. The generator emits
/// one <c>readonly PropertyExpr&lt;T&gt;</c> per <c>[Property]</c>/<c>[Id]</c> on the
/// table's <c>{Name}Q</c> static class — e.g. <c>ConstraintQ.Description</c> is
/// <c>PropertyExpr&lt;string&gt;("description")</c>.
/// <para>
/// The struct's role is to keep the compile-time type of the value flowing into the
/// predicate — <c>Eq(T value)</c> rejects mismatched types at the call site instead of
/// silently boxing them. Once a predicate node is constructed the type tag is dropped:
/// the AST stores <c>object?</c> values and the runtime parameter renderer (transport's
/// <c>BuildLetPrefix</c>) figures out the shape.
/// </para>
/// </summary>
public readonly record struct PropertyExpr<T>(string Field)
{
    /// <summary>Equality predicate: <c>field = value</c>.</summary>
    public IPredicate Eq(T value) => new EqPredicate(Field, value);

    /// <summary>Less-than: <c>field &lt; value</c>.</summary>
    public IPredicate Lt(T value) => new RangePredicate(Field, RangeOp.Lt, value);

    /// <summary>Less-than-or-equal: <c>field &lt;= value</c>.</summary>
    public IPredicate Le(T value) => new RangePredicate(Field, RangeOp.Le, value);

    /// <summary>Greater-than: <c>field &gt; value</c>.</summary>
    public IPredicate Gt(T value) => new RangePredicate(Field, RangeOp.Gt, value);

    /// <summary>Greater-than-or-equal: <c>field &gt;= value</c>.</summary>
    public IPredicate Ge(T value) => new RangePredicate(Field, RangeOp.Ge, value);

    /// <summary>Set membership: <c>field IN [v0, v1, …]</c>.</summary>
    public IPredicate In(params T[] values) => new InPredicate(Field, ToObjectArray(values));

    /// <summary>Set membership: <c>field IN [v0, v1, …]</c> from any enumerable source.</summary>
    public IPredicate In(IEnumerable<T> values) => new InPredicate(Field, ToObjectArray(values));

    private static object?[] ToObjectArray(IEnumerable<T> values)
    {
        if (values is ICollection<T> col)
        {
            var arr = new object?[col.Count];
            var i = 0;
            foreach (var v in col)
            {
                arr[i++] = v;
            }
            return arr;
        }

        var list = new List<object?>();
        foreach (var v in values)
        {
            list.Add(v);
        }
        return list.ToArray();
    }
}

/// <summary>
/// String-only operators on <see cref="PropertyExpr{T}"/>. <c>Contains</c> doesn't fit on
/// the generic struct itself — it's string-shaped, not <c>T</c>-shaped — so it lives here.
/// One overload covers both <c>PropertyExpr&lt;string&gt;</c> and
/// <c>PropertyExpr&lt;string?&gt;</c> because nullable annotations on reference type
/// arguments are erased at the CLR level: both materialise as the same generic
/// instantiation, so a single <c>this PropertyExpr&lt;string&gt;</c> extension binds to
/// either at the call site (with a nullable-warning only when the annotated form differs).
/// </summary>
public static class PropertyExprStringExtensions
{
    /// <summary>Substring containment: <c>string::contains(field, $substring)</c>.</summary>
    public static IPredicate Contains(this PropertyExpr<string> expr, string substring)
        => new ContainsPredicate(expr.Field, substring);
}
