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

/// <summary>Logical conjunction. Operands are AND-merged in compile order.</summary>
public sealed record AndPredicate(IReadOnlyList<IPredicate> Operands) : IPredicate;

/// <summary>Logical disjunction. Operands are OR-merged in compile order.</summary>
public sealed record OrPredicate(IReadOnlyList<IPredicate> Operands) : IPredicate;

/// <summary>Logical negation.</summary>
public sealed record NotPredicate(IPredicate Operand) : IPredicate;
