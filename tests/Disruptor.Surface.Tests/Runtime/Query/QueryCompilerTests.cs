using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime.Query;

public sealed class QueryCompilerTests
{
    [Fact]
    public void Compile_NoFilter_NoPin_EmitsBareSelect()
    {
        var (sql, bindings) = Invoke("constraints", filter: null, pinnedId: null);

        Assert.Equal("SELECT * FROM constraints;", sql);
        Assert.Null(bindings);
    }

    [Fact]
    public void Compile_EqPredicate_EmitsParameterizedWhere()
    {
        var pred = new EqPredicate("description", "security");

        var (sql, bindings) = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal("SELECT * FROM constraints WHERE description = $p0;", sql);
        Assert.NotNull(bindings);
        Assert.Equal("security", bindings!["p0"]);
    }

    [Fact]
    public void Compile_PinnedId_EmitsLeadingIdClause()
    {
        var pin = new RecordId("constraints", "01HX7AF5");

        var (sql, bindings) = Invoke("constraints", filter: null, pinnedId: pin);

        Assert.Equal("SELECT * FROM constraints WHERE id = $p0;", sql);
        Assert.NotNull(bindings);
        Assert.Equal(pin, bindings!["p0"]);
    }

    [Fact]
    public void Compile_PinnedId_AndFilter_AndsBoth()
    {
        var pin = new RecordId("constraints", "01HX7AF5");
        var pred = new EqPredicate("description", "security");

        var (sql, bindings) = Invoke("constraints", pred, pin);

        Assert.Equal(
            "SELECT * FROM constraints WHERE id = $p0 AND description = $p1;",
            sql);
        Assert.Equal(pin, bindings!["p0"]);
        Assert.Equal("security", bindings["p1"]);
    }

    [Fact]
    public void Compile_AndPredicate_WrapsInParens()
    {
        var pred = new AndPredicate(new IPredicate[]
        {
            new EqPredicate("description", "security"),
            new EqPredicate("status", "open"),
        });

        var (sql, bindings) = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM constraints WHERE (description = $p0 AND status = $p1);",
            sql);
        Assert.Equal("security", bindings!["p0"]);
        Assert.Equal("open", bindings["p1"]);
    }

    [Fact]
    public void Compile_OrPredicate_UsesOr()
    {
        var pred = new OrPredicate(new IPredicate[]
        {
            new EqPredicate("status", "open"),
            new EqPredicate("status", "acknowledged"),
        });

        var (sql, bindings) = Invoke("issues", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM issues WHERE (status = $p0 OR status = $p1);",
            sql);
        Assert.Equal("open", bindings!["p0"]);
        Assert.Equal("acknowledged", bindings["p1"]);
    }

    [Fact]
    public void Compile_NotPredicate_PrefixesBangAndParens()
    {
        var pred = new NotPredicate(new EqPredicate("description", "x"));

        var (sql, _) = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal("SELECT * FROM constraints WHERE !(description = $p0);", sql);
    }

    [Fact]
    public void Compile_TypedRecordId_NormalisedToCanonicalRecordId()
    {
        // PropertyExpr<TId>.Eq(typedId) flows the typed id through as the predicate value;
        // QueryCompiler must collapse it to canonical RecordId so the transport's
        // RenderValue formats it as a record literal rather than a JSON-quoted string.
        var typed = new TypedTestId("constraints", "01HX7AF5");
        var pred = new EqPredicate("id", typed);

        var (_, bindings) = Invoke("constraints", pred, pinnedId: null);

        var bound = Assert.IsType<RecordId>(bindings!["p0"]);
        Assert.Equal("constraints", bound.Table);
        Assert.Equal("01HX7AF5", bound.Value);
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

    // ─────────────────────── PR2 operator coverage ───────────────────────

    [Theory]
    [InlineData(RangeOp.Lt, "<")]
    [InlineData(RangeOp.Le, "<=")]
    [InlineData(RangeOp.Gt, ">")]
    [InlineData(RangeOp.Ge, ">=")]
    public void Compile_RangePredicate_EmitsExpectedOperator(RangeOp op, string surreal)
    {
        var pred = new RangePredicate("priority", op, 5);

        var (sql, bindings) = Invoke("issues", pred, pinnedId: null);

        Assert.Equal($"SELECT * FROM issues WHERE priority {surreal} $p0;", sql);
        Assert.Equal(5, bindings!["p0"]);
    }

    [Fact]
    public void Compile_InPredicate_BindsValuesAsCollection()
    {
        var pred = new InPredicate("status", new object?[] { "open", "acknowledged" });

        var (sql, bindings) = Invoke("issues", pred, pinnedId: null);

        Assert.Equal("SELECT * FROM issues WHERE status IN $p0;", sql);
        var values = Assert.IsAssignableFrom<IReadOnlyList<object?>>(bindings!["p0"]);
        Assert.Equal(2, values.Count);
        Assert.Equal("open", values[0]);
        Assert.Equal("acknowledged", values[1]);
    }

    [Fact]
    public void Compile_InPredicate_WithTypedIds_NormalisesEachElement()
    {
        // Each element of an In list passes through the same canonicalisation as a single
        // typed-id binding — the transport's RenderValue iterates the collection and
        // matches each element on its concrete CLR type.
        var ids = new object?[]
        {
            new TypedTestId("constraints", "01HX7AF5"),
            new TypedTestId("constraints", "01HX7AF6"),
        };
        var pred = new InPredicate("id", ids);

        var (_, bindings) = Invoke("constraints", pred, pinnedId: null);

        var values = (IReadOnlyList<object?>)bindings!["p0"]!;
        var first = Assert.IsType<RecordId>(values[0]);
        var second = Assert.IsType<RecordId>(values[1]);
        Assert.Equal("01HX7AF5", first.Value);
        Assert.Equal("01HX7AF6", second.Value);
    }

    [Fact]
    public void Compile_ContainsPredicate_EmitsStringContainsCall()
    {
        var pred = new ContainsPredicate("description", "security");

        var (sql, bindings) = Invoke("constraints", pred, pinnedId: null);

        Assert.Equal("SELECT * FROM constraints WHERE string::contains(description, $p0);", sql);
        Assert.Equal("security", bindings!["p0"]);
    }

    [Fact]
    public void Compile_ContainsPredicate_PreservesStringAsScalarNotEnumerable()
    {
        // string is IEnumerable<char>; if the normaliser decomposed strings it would
        // bind a list of characters and the resulting SurrealQL would be nonsense. This
        // test pins down that strings stay scalar through the binding path.
        var pred = new ContainsPredicate("description", "abc");

        var (_, bindings) = Invoke("constraints", pred, pinnedId: null);

        Assert.IsType<string>(bindings!["p0"]);
        Assert.Equal("abc", bindings["p0"]);
    }

    [Fact]
    public void Compile_MixedOperators_ParameteriseInOrder()
    {
        var pred = Predicate.And(
            new RangePredicate("priority", RangeOp.Gt, 3),
            new InPredicate("status", new object?[] { "open", "acknowledged" }),
            new ContainsPredicate("description", "security"));

        var (sql, bindings) = Invoke("issues", pred, pinnedId: null);

        Assert.Equal(
            "SELECT * FROM issues WHERE (priority > $p0 AND status IN $p1 AND string::contains(description, $p2));",
            sql);
        Assert.Equal(3, bindings!["p0"]);
        Assert.IsAssignableFrom<IReadOnlyList<object?>>(bindings["p1"]);
        Assert.Equal("security", bindings["p2"]);
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
        var fromEnumerable = (InPredicate)expr.In((IEnumerable<int>)new[] { 1, 2, 3 });

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

    // ─────────────────────── PR4 traversal coverage ───────────────────────

    [Fact]
    public void Compile_InlineRefInclude_AddsFieldDotStarToProjection()
    {
        var includes = new IIncludeNode[] { new IncludeInlineRefNode("details") };

        var (sql, _) = InvokeWithIncludes("constraints", filter: null, pinnedId: null, includes: includes);

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
                Nested: Array.Empty<IIncludeNode>())
        };

        var (sql, _) = InvokeWithIncludes("designs", filter: null, pinnedId: null, includes: includes);

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
                Nested: Array.Empty<IIncludeNode>())
        };

        var (sql, bindings) = InvokeWithIncludes("designs", filter: null, pinnedId: null, includes: includes);

        Assert.Equal(
            "SELECT *, (SELECT * FROM constraints WHERE design = $parent.id AND description = $p0) AS constraints FROM designs;",
            sql);
        Assert.Equal("x", bindings!["p0"]);
    }

    [Fact]
    public void Compile_NestedChildren_DescendsRecursively()
    {
        var grandchild = new IncludeChildrenNode(
            "acceptance_criteria", "user_story",
            Filter: null,
            Nested: Array.Empty<IIncludeNode>());

        var child = new IncludeChildrenNode(
            "user_stories", "feature",
            Filter: null,
            Nested: new IIncludeNode[] { grandchild });

        var includes = new IIncludeNode[] { child };

        var (sql, _) = InvokeWithIncludes("features", filter: null, pinnedId: null, includes: includes);

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
        // Inline-refs come before subselects in the projection list — this keeps the
        // wire SQL's "scalar half" adjacent to the SELECT, which is much easier to scan
        // when debugging via transport logs.
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode("constraints", "design", null, Array.Empty<IIncludeNode>()),
            new IncludeInlineRefNode("details"),
        };

        var (sql, _) = InvokeWithIncludes("designs", filter: null, pinnedId: null, includes: includes);

        Assert.Equal(
            "SELECT *, details.*, (SELECT * FROM constraints WHERE design = $parent.id) AS constraints FROM designs;",
            sql);
    }

    [Fact]
    public void Compile_TopLevelFilterAndChildrenFilter_DontShareParameterSpace_ButCounterIsGlobal()
    {
        // Each predicate slot pulls a new $pN; the counter is shared across the whole
        // statement so an inner filter doesn't accidentally collide with outer params.
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode(
                "constraints", "design",
                new EqPredicate("description", "inner"),
                Array.Empty<IIncludeNode>())
        };

        var (sql, bindings) = InvokeWithIncludes(
            "designs",
            filter: new EqPredicate("description", "outer"),
            pinnedId: null,
            includes: includes);

        Assert.Equal(
            "SELECT *, (SELECT * FROM constraints WHERE design = $parent.id AND description = $p0) AS constraints "
            + "FROM designs WHERE description = $p1;",
            sql);
        Assert.Equal("inner", bindings!["p0"]);
        Assert.Equal("outer", bindings["p1"]);
    }

    [Fact]
    public void Compile_InvalidChildTable_Throws()
    {
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode("123-bad", "design", null, Array.Empty<IIncludeNode>())
        };

        Assert.Throws<SurrealFormatException>(() =>
            InvokeWithIncludes("designs", filter: null, pinnedId: null, includes: includes));
    }

    [Fact]
    public void Compile_InvalidParentField_Throws()
    {
        var includes = new IIncludeNode[]
        {
            new IncludeChildrenNode("constraints", "has-dash", null, Array.Empty<IIncludeNode>())
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
    private static (string Sql, IReadOnlyDictionary<string, object?>? Bindings) Invoke(
        string table, IPredicate? filter, RecordId? pinnedId)
    {
        // Two Compile overloads exist (with and without an IIncludeNode list); GetMethod
        // needs the parameter-type array to disambiguate.
        var method = typeof(IPredicate).Assembly
            .GetType("Disruptor.Surface.Runtime.Query.QueryCompiler", throwOnError: true)!
            .GetMethod(
                "Compile",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(string), typeof(IPredicate), typeof(RecordId?) },
                modifiers: null)!;
        try
        {
            var result = method.Invoke(null, new object?[] { table, filter, pinnedId });
            return ((string, IReadOnlyDictionary<string, object?>?))result!;
        }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is not null)
        {
            throw tie.InnerException;
        }
    }

    private static (string Sql, IReadOnlyDictionary<string, object?>? Bindings) InvokeWithIncludes(
        string table, IPredicate? filter, RecordId? pinnedId, IReadOnlyList<IIncludeNode> includes)
    {
        var method = typeof(IPredicate).Assembly
            .GetType("Disruptor.Surface.Runtime.Query.QueryCompiler", throwOnError: true)!
            .GetMethod(
                "Compile",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(string), typeof(IPredicate), typeof(RecordId?), typeof(IReadOnlyList<IIncludeNode>) },
                modifiers: null)!;
        try
        {
            var result = method.Invoke(null, new object?[] { table, filter, pinnedId, includes });
            return ((string, IReadOnlyDictionary<string, object?>?))result!;
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
