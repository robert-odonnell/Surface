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

    /// <summary>
    /// Reflection trampoline — <see cref="QueryCompiler"/> is internal-by-design (the only
    /// supported entry point is <c>Query&lt;T&gt;.ExecuteAsync</c>). This keeps the test
    /// suite honest about that boundary without forcing an InternalsVisibleTo on the
    /// runtime assembly.
    /// </summary>
    private static (string Sql, IReadOnlyDictionary<string, object?>? Bindings) Invoke(
        string table, IPredicate? filter, RecordId? pinnedId)
    {
        var method = typeof(IPredicate).Assembly
            .GetType("Disruptor.Surface.Runtime.Query.QueryCompiler", throwOnError: true)!
            .GetMethod("Compile", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static
                                | System.Reflection.BindingFlags.NonPublic)!;
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

    /// <summary>Test stand-in for the generator-emitted typed id structs.</summary>
    private readonly record struct TypedTestId(string Table, string Value) : IRecordId
    {
        public string ToLiteral() => Value;
    }
}
