using Surface.Runtime;
using Xunit;

namespace Surface.Tests.Runtime;

public sealed class SurrealCommandEmitterTests
{
    [Fact]
    public void Create_EmitsBareCreateStatement()
    {
        var (sql, parameters) = SurrealCommandEmitter.Emit(
            new[] { Command.Create(new RecordId("designs", "x")) });

        Assert.Equal("CREATE designs:x;\n", sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void Set_OnRecordId_Value_Inlines_TheId_NotAParameter()
    {
        var owner = new RecordId("designs", "d");
        var details = new RecordId("details", "x");

        var (sql, parameters) = SurrealCommandEmitter.Emit(
            new[] { Command.Set(owner, "details", details) });

        // Record-id values get inlined as `table:value` literals so the SurrealQL
        // parser sees them as a record link, not a string. Scalars go through $params.
        Assert.Equal("UPDATE designs:d SET details = details:x;\n", sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void Set_OnScalar_Value_AddsParameter()
    {
        var (sql, parameters) = SurrealCommandEmitter.Emit(
            new[] { Command.Set(new RecordId("designs", "d"), "description", "hello") });

        Assert.Contains("$p0", sql);
        Assert.Equal("hello", parameters["p0"]);
    }

    [Fact]
    public void Upsert_EmitsContent_WithRecordIdsInlined_AndScalarsParameterised()
    {
        var owner = new RecordId("designs", "d");
        var details = new RecordId("details", "x");
        var content = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            { "details", details },
            { "description", "hello" },
        };

        var (sql, parameters) = SurrealCommandEmitter.Emit(
            new[] { Command.Upsert(owner, content) });

        Assert.StartsWith("UPSERT designs:d CONTENT { ", sql);
        Assert.Contains("details: details:x", sql);
        Assert.Contains("description: $p0", sql);
        Assert.Equal("hello", parameters["p0"]);
    }

    [Fact]
    public void Relate_EmitsArrowSyntax_WithoutContent()
    {
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        var (sql, _) = SurrealCommandEmitter.Emit(
            new[] { Command.Relate(src, "restricts", tgt) });

        Assert.Equal("RELATE constraints:c->restricts->user_stories:u;\n", sql);
    }

    [Fact]
    public void Unrelate_EmitsDeleteWithWhere()
    {
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        var (sql, _) = SurrealCommandEmitter.Emit(
            new[] { Command.Unrelate(src, "restricts", tgt) });

        Assert.Equal("DELETE restricts WHERE in = constraints:c AND out = user_stories:u;\n", sql);
    }

    [Fact]
    public void Multiple_Commands_AreEmitted_InOrder_AsSemicolonTerminatedStatements()
    {
        var a = new RecordId("designs", "a");
        var b = new RecordId("constraints", "b");

        var (sql, _) = SurrealCommandEmitter.Emit(
            new[]
            {
                Command.Create(a),
                Command.Create(b),
                Command.Delete(b),
            });

        var lines = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("CREATE designs:a;", lines[0]);
        Assert.Equal("CREATE constraints:b;", lines[1]);
        Assert.Equal("DELETE constraints:b;", lines[2]);
    }
}
