using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

public sealed class SurrealCommandEmitterTests
{
    [Fact]
    public void Create_EmitsBareCreateStatement()
    {
        var sql = SurrealCommandEmitter.Emit([Command.Create(new RecordId("designs", "x"))]);

        Assert.Equal("CREATE designs:x;\n", sql);
    }

    [Fact]
    public void Set_OnRecordId_Value_Inlines_TheId_NotAParameter()
    {
        var owner = new RecordId("designs", "d");
        var details = new RecordId("details", "x");

        var sql = SurrealCommandEmitter.Emit([Command.Set(owner, "details", details)]);

        // Record-id values get inlined as `table:value` literals so the SurrealQL
        // parser sees them as a record link, not a string.
        Assert.Equal("UPDATE designs:d SET details = details:x;\n", sql);
    }

    [Fact]
    public void Set_OnScalar_Value_AddsParameter()
    {
        var sql = SurrealCommandEmitter.Emit([Command.Set(new RecordId("designs", "d"), "description", "hello")]);

        Assert.Contains("\"hello\"", sql);
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

        var sql = SurrealCommandEmitter.Emit([Command.Upsert(owner, content)]);

        Assert.StartsWith("UPSERT designs:d CONTENT { ", sql);
        Assert.Contains("details: details:x", sql);
        Assert.Contains("\"hello\"", sql);
    }

    [Fact]
    public void Relate_WithSlugEdge_EmitsArrowSyntax_WithExplicitEdgeId()
    {
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        // Slug strategy: caller supplies the edge value directly. The emitter renders
        // it verbatim as the edge row id. Useful for stable-named relations the caller
        // wants to address by name.
        var sql = SurrealCommandEmitter.Emit(
            [Command.Relate(src, new RecordId("restricts", "main_link"), tgt)]);

        Assert.Equal("RELATE constraints:c->restricts:main_link->user_stories:u;\n", sql);
    }

    [Fact]
    public void Relate_WithIdempotentEdge_RendersDeterministicHash_AcrossInvocations()
    {
        // Idempotent strategy: edge value is deferred at the type level, the emitter
        // computes HashText("{src}|{table}|{tgt}") at write time. Same triple always
        // produces the same edge id, so re-running the same Relate lands on the same
        // row instead of erroring against the schema's UNIQUE INDEX.
        var src = new RecordId("findings", "f");
        var tgt = new RecordId("issues", "i");

        var first = SurrealCommandEmitter.Emit(
            [Command.Relate(src, RecordId.Idempotent("stub_edge"), tgt)]);
        var second = SurrealCommandEmitter.Emit(
            [Command.Relate(src, RecordId.Idempotent("stub_edge"), tgt)]);

        Assert.Equal(first, second);
        Assert.StartsWith("RELATE findings:f->stub_edge:", first);
        Assert.EndsWith("->issues:i;\n", first);
    }

    [Fact]
    public void Unrelate_EmitsDeleteWithWhere()
    {
        var src = new RecordId("constraints", "c");
        var tgt = new RecordId("user_stories", "u");

        var sql = SurrealCommandEmitter.Emit([Command.Unrelate(src, "restricts", tgt)]);

        Assert.Equal("DELETE restricts WHERE in = constraints:c AND out = user_stories:u;\n", sql);
    }

    [Fact]
    public void Upsert_WithNullContent_EmitsBareUpsert_NoContentClause()
    {
        // Polish-3: Upserted intent must always render as UPSERT, never CREATE — even
        // with no fields. CREATE errors when the row exists; UPSERT is create-or-update.
        var sql = SurrealCommandEmitter.Emit([Command.Upsert(new RecordId("designs", "x"))]);

        Assert.Equal("UPSERT designs:x;\n", sql);
    }

    [Fact]
    public void Unrelate_SourceOnly_RendersAsBulkDelete_WithInWhereClause()
    {
        var sql = SurrealCommandEmitter.Emit(
            [Command.Unrelate(new RecordId("constraints", "c"), "restricts", target: null)]);

        Assert.Equal("DELETE restricts WHERE in = constraints:c;\n", sql);
    }

    [Fact]
    public void Unrelate_TargetOnly_RendersAsBulkDelete_WithOutWhereClause()
    {
        var sql = SurrealCommandEmitter.Emit(
            [Command.Unrelate(source: null, "restricts", new RecordId("user_stories", "u"))]);

        Assert.Equal("DELETE restricts WHERE out = user_stories:u;\n", sql);
    }

    [Fact]
    public void Unrelate_BothEndpoints_RendersAsDelete_WithBothWhereClauses()
    {
        var sql = SurrealCommandEmitter.Emit(
            [Command.Unrelate(new RecordId("constraints", "c"), "restricts", new RecordId("user_stories", "u"))]);

        Assert.Equal("DELETE restricts WHERE in = constraints:c AND out = user_stories:u;\n", sql);
    }

    [Fact]
    public void Unrelate_BothNull_Throws()
    {
        Assert.Throws<ArgumentException>(() => Command.Unrelate(source: null, "edge", target: null));
    }

    [Fact]
    public void Multiple_Commands_AreEmitted_InOrder_AsSemicolonTerminatedStatements()
    {
        var a = new RecordId("designs", "a");
        var b = new RecordId("constraints", "b");

        var sql = SurrealCommandEmitter.Emit(
        [
            Command.Create(a),
                Command.Create(b),
                Command.Delete(b)
        ]
        );

        var lines = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("CREATE designs:a;", lines[0]);
        Assert.Equal("CREATE constraints:b;", lines[1]);
        Assert.Equal("DELETE constraints:b;", lines[2]);
    }
}
