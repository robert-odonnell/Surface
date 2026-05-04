using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime.Query;

/// <summary>
/// QueryCompiler now returns a single SurrealQL string with all bindings inlined as
/// literals via <see cref="SurrealFormatter"/>. Strings are quoted via
/// <see cref="SurrealFormatter.StringLiteral"/>; record ids via
/// <see cref="SurrealFormatter.RecordId"/>; collections become array literals.
/// </summary>
public sealed class QueryCompilerTests
{
    [Fact]
    public void Compile_NoFilter_NoPin_EmitsBareSelect()
    {
        var sql = Invoke("constraints", filter: null, pinnedId: null);

        Assert.Equal("SELECT * FROM constraints;", sql);
    }

    [Fact]
    public void Compile_EqPredicate_InlinesStringLiteral()
    {
        var pred = new EqPredicate("description", "security");

        var sql = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal("SELECT * FROM constraints WHERE description = \"security\";", sql);
    }

    [Fact]
    public void Compile_PinnedId_InlinesRecordLiteral()
    {
        var pin = new RecordId("constraints", "01HX7AF5");

        var sql = Invoke("constraints", filter: null, pinnedId: pin);

        Assert.Equal("SELECT * FROM constraints WHERE id = constraints:01HX7AF5;", sql);
    }

    [Fact]
    public void Compile_PinnedId_AndFilter_AndsBoth()
    {
        var pin = new RecordId("constraints", "01HX7AF5");
        var pred = new EqPredicate("description", "security");

        var sql = Invoke("constraints", pred, pin);

        Assert.Equal(
            "SELECT * FROM constraints WHERE id = constraints:01HX7AF5 AND description = \"security\";",
            sql);
    }

    [Fact]
    public void Compile_AndPredicate_WrapsInParens()
    {
        var pred = new AndPredicate(
        [
            new EqPredicate("description", "security"),
            new EqPredicate("status", "open")
        ]);

        var sql = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM constraints WHERE (description = \"security\" AND status = \"open\");",
            sql);
    }

    [Fact]
    public void Compile_OrPredicate_UsesOr()
    {
        var pred = new OrPredicate(
        [
            new EqPredicate("status", "open"),
            new EqPredicate("status", "acknowledged")
        ]);

        var sql = Invoke("issues", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM issues WHERE (status = \"open\" OR status = \"acknowledged\");",
            sql);
    }

    [Fact]
    public void Compile_NotPredicate_PrefixesBangAndParens()
    {
        var pred = new NotPredicate(new EqPredicate("description", "x"));

        var sql = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal("SELECT * FROM constraints WHERE !(description = \"x\");", sql);
    }

    [Fact]
    public void Compile_TypedRecordId_NormalisedToRecordLiteral()
    {
        // PropertyExpr<TId>.Eq(typedId) flows the typed id through; the compiler
        // collapses IRecordId → canonical RecordId before SurrealFormatter renders it,
        // so the wire form is the SurrealQL record literal — not a JSON-quoted string.
        var typed = new TypedTestId("constraints", "01HX7AF5");
        var pred = new EqPredicate("id", typed);

        var sql = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal("SELECT * FROM constraints WHERE id = constraints:01HX7AF5;", sql);
    }

    [Fact]
    public void Compile_InvalidTableName_Throws()
    {
        Assert.Throws<SurrealFormatException>(() =>
            Invoke("123-bad-table", filter: null, pinnedId: null));
    }

    [Fact]
    public void Compile_InvalidFieldName_Throws()
    {
        var pred = new EqPredicate("has-dash", "x");

        Assert.Throws<SurrealFormatException>(() =>
            Invoke("constraints", pred, pinnedId: null));
    }

    [Fact]
    public void PropertyExpr_Eq_BuildsEqPredicate()
    {
        var expr = new PropertyExpr<string>("description");

        var pred = expr.Eq("test-value");

        var eq = Assert.IsType<EqPredicate>(pred);
        Assert.Equal("description", eq.Field);
        Assert.Equal("test-value", eq.Value);
    }

    [Fact]
    public void Predicate_And_SingleOperand_Unwraps()
    {
        var leaf = new EqPredicate("x", 1);

        var combined = Predicate.And(leaf);

        Assert.Same(leaf, combined);
    }

    [Fact]
    public void Predicate_And_TwoOperands_Wraps()
    {
        var a = new EqPredicate("x", 1);
        var b = new EqPredicate("y", 2);

        var combined = Predicate.And(a, b);

        var and = Assert.IsType<AndPredicate>(combined);
        Assert.Equal(2, and.Operands.Count);
        Assert.Same(a, and.Operands[0]);
        Assert.Same(b, and.Operands[1]);
    }

    [Fact]
    public void Predicate_Empty_ThrowsForBothAndAndOr()
    {
        Assert.Throws<ArgumentException>(() => Predicate.And());
        Assert.Throws<ArgumentException>(() => Predicate.Or());
    }

    // ─────────────────────── Operator coverage ───────────────────────

    [Theory]
    [InlineData(RangeOp.Lt, "<")]
    [InlineData(RangeOp.Le, "<=")]
    [InlineData(RangeOp.Gt, ">")]
    [InlineData(RangeOp.Ge, ">=")]
    public void Compile_RangePredicate_EmitsExpectedOperator(RangeOp op, string surreal)
    {
        var pred = new RangePredicate("priority", op, 5);

        var sql = Invoke("issues", pred, pinnedId: null);

        Assert.Equal($"SELECT * FROM issues WHERE priority {surreal} 5;", sql);
    }

    [Fact]
    public void Compile_InPredicate_InlinesArrayLiteral()
    {
        var pred = new InPredicate("status", ["open", "acknowledged"]);

        var sql = Invoke("issues", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM issues WHERE status IN [\"open\", \"acknowledged\"];",
            sql);
    }

    [Fact]
    public void Compile_InPredicate_WithTypedIds_NormalisesEachElement()
    {
        // Each element passes through the same canonicalisation as a single typed-id
        // binding — the compiler walks the collection and renders each element with
        // SurrealFormatter so typed ids end up as record literals, not strings.
        var ids = new object?[]
        {
            new TypedTestId("constraints", "01HX7AF5"),
            new TypedTestId("constraints", "01HX7AF6"),
        };
        var pred = new InPredicate("id", ids);

        var sql = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM constraints WHERE id IN [constraints:01HX7AF5, constraints:01HX7AF6];",
            sql);
    }

    [Fact]
    public void Compile_ContainsPredicate_EmitsStringContainsCall()
    {
        var pred = new ContainsPredicate("description", "security");

        var sql = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM constraints WHERE string::contains(description, \"security\");",
            sql);
    }

    [Fact]
    public void Compile_ContainsPredicate_PreservesStringAsScalarNotEnumerable()
    {
        // string is IEnumerable<char>; if the normaliser decomposed strings it would
        // emit a list of characters. This test pins down that strings stay scalar.
        var pred = new ContainsPredicate("description", "abc");

        var sql = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM constraints WHERE string::contains(description, \"abc\");",
            sql);
    }

    [Fact]
    public void Compile_MixedOperators_ComposeInOrder()
    {
        var pred = Predicate.And(
            new RangePredicate("priority", RangeOp.Gt, 3),
            new InPredicate("status", ["open", "acknowledged"]),
            new ContainsPredicate("description", "security"));

        var sql = Invoke("issues", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM issues WHERE (priority > 3 AND status IN [\"open\", \"acknowledged\"] AND string::contains(description, \"security\"));",
            sql);
    }

    [Fact]
    public void PropertyExpr_Range_Operators_BuildExpectedNodes()
    {
        var expr = new PropertyExpr<int>("priority");

        var lt = (RangePredicate)expr.Lt(5);
        var le = (RangePredicate)expr.Le(5);
        var gt = (RangePredicate)expr.Gt(5);
        var ge = (RangePredicate)expr.Ge(5);

        Assert.Equal(RangeOp.Lt, lt.Op);
        Assert.Equal(RangeOp.Le, le.Op);
        Assert.Equal(RangeOp.Gt, gt.Op);
        Assert.Equal(RangeOp.Ge, ge.Op);
    }

    [Fact]
    public void PropertyExpr_In_Params_AndEnumerable_BothBuildSameAst()
    {
        var expr = new PropertyExpr<int>("priority");

        var fromParams = (InPredicate)expr.In(1, 2, 3);
        var fromEnumerable = (InPredicate)expr.In((IEnumerable<int>)[1, 2, 3]);

        Assert.Equal("priority", fromParams.Field);
        Assert.Equal("priority", fromEnumerable.Field);
        Assert.Equal(3, fromParams.Values.Count);
        Assert.Equal(3, fromEnumerable.Values.Count);
        Assert.Equal(1, fromParams.Values[0]);
        Assert.Equal(1, fromEnumerable.Values[0]);
    }

    [Fact]
    public void PropertyExpr_String_Contains_ExtensionBuildsContainsPredicate()
    {
        var expr = new PropertyExpr<string>("description");

        var pred = expr.Contains("security");

        var c = Assert.IsType<ContainsPredicate>(pred);
        Assert.Equal("description", c.Field);
        Assert.Equal("security", c.Substring);
    }

    // ─────────────────────── Traversal coverage ───────────────────────

    [Fact]
    public void Compile_InlineRefInclude_AddsFieldDotStarToProjection()
    {
        var includes = new IIncludeNode[] { new IncludeInlineRefNode("details") };

        var sql = InvokeWithIncludes("constraints", filter: null, pinnedId: null, includes: includes);

        Assert.Equal("SELECT *, details.* FROM constraints;", sql);
    }

    [Fact]
    public void Compile_ChildrenInclude_EmitsScopedSubselect()
    {
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode(
                ChildTable: "constraints",
                ParentField: "design",
                Filter: null,
                Nested: [])
        };

        var sql = InvokeWithIncludes("designs", filter: null, pinnedId: null, includes: includes);

        Assert.Equal(
            "SELECT *, (SELECT * FROM constraints WHERE design = $parent.id) AS constraints FROM designs;",
            sql);
    }

    [Fact]
    public void Compile_ChildrenInclude_FilterMergesWithParentLink()
    {
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode(
                "constraints", "design",
                Filter: new EqPredicate("description", "x"),
                Nested: [])
        };

        var sql = InvokeWithIncludes("designs", filter: null, pinnedId: null, includes: includes);

        Assert.Equal(
            "SELECT *, (SELECT * FROM constraints WHERE design = $parent.id AND description = \"x\") AS constraints FROM designs;",
            sql);
    }

    [Fact]
    public void Compile_NestedChildren_DescendsRecursively()
    {
        var grandchild = new IncludeChildrenNode(
            "acceptance_criteria", "user_story",
            Filter: null,
            Nested: []);

        var child = new IncludeChildrenNode(
            "user_stories", "feature",
            Filter: null,
            Nested: [grandchild]);

        var includes = new IIncludeNode[] { child };

        var sql = InvokeWithIncludes("features", filter: null, pinnedId: null, includes: includes);

        Assert.Equal(
            "SELECT *, "
            + "(SELECT *, "
                + "(SELECT * FROM acceptance_criteria WHERE user_story = $parent.id) AS acceptance_criteria "
                + "FROM user_stories WHERE feature = $parent.id) AS user_stories "
            + "FROM features;",
            sql);
    }

    [Fact]
    public void Compile_NestedInlineRefAndChildren_AppearInDeterministicOrder()
    {
        // Inline-refs come before subselects in the projection list — keeps the SQL's
        // scalar half adjacent to the SELECT, easier to scan in transport logs.
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode("constraints", "design", null, []),
            new IncludeInlineRefNode("details"),
        };

        var sql = InvokeWithIncludes("designs", filter: null, pinnedId: null, includes: includes);

        Assert.Equal(
            "SELECT *, details.*, (SELECT * FROM constraints WHERE design = $parent.id) AS constraints FROM designs;",
            sql);
    }

    [Fact]
    public void Compile_TopLevelFilterAndChildrenFilter_ComposeIndependently()
    {
        // No shared parameter space — each filter inlines its own literal in place.
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode(
                "constraints", "design",
                new EqPredicate("description", "inner"),
                [])
        };

        var sql = InvokeWithIncludes(
            "designs",
            filter: new EqPredicate("description", "outer"),
            pinnedId: null,
            includes: includes);

        Assert.Equal(
            "SELECT *, (SELECT * FROM constraints WHERE design = $parent.id AND description = \"inner\") AS constraints "
            + "FROM designs WHERE description = \"outer\";",
            sql);
    }

    [Fact]
    public void Compile_InvalidChildTable_Throws()
    {
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode("123-bad", "design", null, [])
        };

        Assert.Throws<SurrealFormatException>(() =>
            InvokeWithIncludes("designs", filter: null, pinnedId: null, includes: includes));
    }

    [Fact]
    public void Compile_InvalidParentField_Throws()
    {
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode("constraints", "has-dash", null, [])
        };

        Assert.Throws<SurrealFormatException>(() =>
            InvokeWithIncludes("designs", filter: null, pinnedId: null, includes: includes));
    }

    /// <summary>
    /// Reflection trampoline — <see cref="QueryCompiler"/> is internal-by-design (the only
    /// supported entry point is <c>Query&lt;T&gt;.ExecuteAsync</c>). This keeps the test
    /// suite honest about that boundary without forcing an InternalsVisibleTo on the
    /// runtime assembly.
    /// </summary>
    private static string Invoke(string table, IPredicate? filter, RecordId? pinnedId)
    {
        var method = typeof(IPredicate).Assembly
            .GetType("Disruptor.Surface.Runtime.Query.QueryCompiler", throwOnError: true)!
            .GetMethod(
                "Compile",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string), typeof(IPredicate), typeof(RecordId?)],
                modifiers: null)!;
        try
        {
            return (string)method.Invoke(null, [table, filter, pinnedId])!;
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    private static string InvokeWithIncludes(
        string table, IPredicate? filter, RecordId? pinnedId, IReadOnlyList<IIncludeNode> includes)
    {
        var method = typeof(IPredicate).Assembly
            .GetType("Disruptor.Surface.Runtime.Query.QueryCompiler", throwOnError: true)!
            .GetMethod(
                "Compile",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string), typeof(IPredicate), typeof(RecordId?), typeof(IReadOnlyList<IIncludeNode>)],
                modifiers: null)!;
        try
        {
            return (string)method.Invoke(null, [table, filter, pinnedId, includes])!;
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    /// <summary>Test stand-in for the generator-emitted typed id structs.</summary>
    private readonly record struct TypedTestId(string Table, string Value) : IRecordId
    {
        public string ToLiteral() => Value;
    }
}
