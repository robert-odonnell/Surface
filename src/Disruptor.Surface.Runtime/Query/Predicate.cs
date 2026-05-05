namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Composition factories for <see cref="IPredicate"/>. The leaf factories live on
/// <see cref="PropertyExpr{T}"/> (e.g. <c>ConstraintQ.Description.Eq("x")</c>); these
/// combine the leaves into trees.
/// </summary>
public static class Predicate
{
    public static IPredicate And(params IPredicate[] operands) => operands.Length switch
    {
        0 => throw new ArgumentException("And requires at least one operand.", nameof(operands)),
        1 => operands[0],
        _ => new AndPredicate(operands),
    };

    public static IPredicate Or(params IPredicate[] operands) => operands.Length switch
    {
        0 => throw new ArgumentException("Or requires at least one operand.", nameof(operands)),
        1 => operands[0],
        _ => new OrPredicate(operands),
    };

    public static IPredicate Not(IPredicate operand) => new NotPredicate(operand);
}
