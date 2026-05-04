namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Marker for a node in the predicate AST. Compiled by <see cref="QueryCompiler"/> into a
/// SurrealQL <c>WHERE</c> fragment plus a parameter map. Strings (field names) are baked
/// in at the factory layer (<see cref="PropertyExpr{T}"/>) — this surface is intentionally
/// untyped so the compiler stays narrow and the same AST shape can serve every operator
/// the factory layer exposes.
/// </summary>
public interface IPredicate { }

/// <summary>Equality test: <c>field = $param</c>.</summary>
public sealed record EqPredicate(string Field, object? Value) : IPredicate;

/// <summary>Comparison operators emitted as <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>.</summary>
public enum RangeOp
{
    Lt,
    Le,
    Gt,
    Ge,
}

/// <summary>Range comparison: <c>field {op} $param</c>.</summary>
public sealed record RangePredicate(string Field, RangeOp Op, object? Value) : IPredicate;

/// <summary>
/// Set membership: <c>field IN $param</c>. The parameter renderer expands a collection
/// value into <c>[v0, v1, …]</c>, so a single bound parameter holds the whole array.
/// </summary>
public sealed record InPredicate(string Field, IReadOnlyList<object?> Values) : IPredicate;

/// <summary>
/// Substring match for string fields. Compiles to <c>string::contains(field, $param)</c>.
/// Case-sensitive — SurrealDB's <c>string::contains</c> doesn't fold case; pre-lower the
/// substring or wrap in a case-insensitive variant if the schema needs one.
/// </summary>
public sealed record ContainsPredicate(string Field, string Substring) : IPredicate;

/// <summary>Logical conjunction. Operands are AND-merged in compile order.</summary>
public sealed record AndPredicate(IReadOnlyList<IPredicate> Operands) : IPredicate;

/// <summary>Logical disjunction. Operands are OR-merged in compile order.</summary>
public sealed record OrPredicate(IReadOnlyList<IPredicate> Operands) : IPredicate;

/// <summary>Logical negation.</summary>
public sealed record NotPredicate(IPredicate Operand) : IPredicate;
