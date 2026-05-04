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
    public IPredicate Eq(T value) => new EqPredicate(Field, value);
}
