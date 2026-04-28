using System.Text.Json;
using Disruptor.Surface.Runtime;
using Xunit;

namespace Disruptor.Surface.Tests.Runtime;

/// <summary>
/// <see cref="HydrationJson"/> covers the three id-shapes Surreal can return: bare
/// <c>"table:value"</c> string, RPC-style <c>{ tb, id }</c> envelope, and the inline-record
/// expansion <c>{ id: "table:value", …content }</c> the loader's <c>field.*</c> projection
/// produces. The inline form is special — it should also let
/// <see cref="HydrationJson.HydrateReference{T}"/> construct the linked entity.
/// </summary>
public sealed class HydrationJsonTests
{
    [Fact]
    public void ReadRecordId_ParsesBareString()
    {
        using var doc = JsonDocument.Parse("\"designs:01HX\"");
        var id = HydrationJson.ReadRecordId(doc.RootElement);
        Assert.Equal(new RecordId("designs", "01HX"), id);
    }

    [Fact]
    public void ReadRecordId_ParsesRpcEnvelope()
    {
        using var doc = JsonDocument.Parse("""{ "tb": "designs", "id": "01HX" }""");
        var id = HydrationJson.ReadRecordId(doc.RootElement);
        Assert.Equal(new RecordId("designs", "01HX"), id);
    }

    [Fact]
    public void ReadRecordId_ParsesInlineRecord()
    {
        using var doc = JsonDocument.Parse("""{ "id": "designs:01HX", "description": "x" }""");
        var id = HydrationJson.ReadRecordId(doc.RootElement);
        Assert.Equal(new RecordId("designs", "01HX"), id);
    }

    [Fact]
    public void HydrateReference_OnIdOnly_RegistersLink_ButDoesNotConstructEntity()
    {
        var session = new SurrealSession();
        using var doc = JsonDocument.Parse("""{ "details": "details:01" }""");
        var ownerId = new RecordId("designs", "x");

        HydrationJson.HydrateReference<StubReferenceTarget>(doc.RootElement, "details", ownerId, (IHydrationSink)session);

        Assert.Equal(0, StubReferenceTarget.HydrationCount);
        // The reference link IS registered — Get<StubReferenceTarget> resolves only after
        // some path constructs the entity (a separate row hydration). The id-only path
        // skips construction by design.
    }

    [Fact]
    public void HydrateReference_OnInlineRecord_ConstructsAndHydratesEntity()
    {
        // Reset the static counter for isolation; xUnit doesn't guarantee ordering but the
        // counter only matters within this test.
        StubReferenceTarget.HydrationCount = 0;

        var session = new SurrealSession();
        using var doc = JsonDocument.Parse("""{ "details": { "id": "details:01", "header": "h" } }""");
        var ownerId = new RecordId("designs", "x");

        HydrationJson.HydrateReference<StubReferenceTarget>(doc.RootElement, "details", ownerId, (IHydrationSink)session);

        Assert.Equal(1, StubReferenceTarget.HydrationCount);
        // The constructed entity should be findable in the session.
        Assert.NotNull(session.Get<StubReferenceTarget>(new RecordId("details", "01")));
    }

    [Fact]
    public void HydrateReference_OnInlineRecord_SkipsConstruction_WhenAlreadyTracked()
    {
        // Polish-2: the same Details record can be inline-expanded under multiple
        // owners. Constructing+hydrating each time only to discard the duplicate is
        // wasteful — first occurrence wins, subsequent ones just register the link.
        StubReferenceTarget.HydrationCount = 0;

        var session = new SurrealSession();
        using var doc = JsonDocument.Parse("""{ "details": { "id": "details:01", "header": "h" } }""");
        var owner1 = new RecordId("designs", "a");
        var owner2 = new RecordId("constraints", "b");

        HydrationJson.HydrateReference<StubReferenceTarget>(doc.RootElement, "details", owner1, (IHydrationSink)session);
        HydrationJson.HydrateReference<StubReferenceTarget>(doc.RootElement, "details", owner2, (IHydrationSink)session);

        Assert.Equal(1, StubReferenceTarget.HydrationCount);
    }

    [Fact]
    public void HydrateReference_OnNullField_IsNoOp()
    {
        StubReferenceTarget.HydrationCount = 0;

        var session = new SurrealSession();
        using var doc = JsonDocument.Parse("""{ "details": null }""");
        var ownerId = new RecordId("designs", "x");

        HydrationJson.HydrateReference<StubReferenceTarget>(doc.RootElement, "details", ownerId, (IHydrationSink)session);

        Assert.Equal(0, StubReferenceTarget.HydrationCount);
    }

    [Fact]
    public void HydrateReference_OnMissingField_IsNoOp()
    {
        StubReferenceTarget.HydrationCount = 0;

        var session = new SurrealSession();
        using var doc = JsonDocument.Parse("""{ "other": "ignored" }""");
        var ownerId = new RecordId("designs", "x");

        HydrationJson.HydrateReference<StubReferenceTarget>(doc.RootElement, "details", ownerId, (IHydrationSink)session);

        Assert.Equal(0, StubReferenceTarget.HydrationCount);
    }

    /// <summary>Simulates a generator-emitted entity for the HydrationJson path.</summary>
    private sealed class StubReferenceTarget : IEntity
    {
        public static int HydrationCount;

        private RecordId _id;

        public RecordId Id => _id;
        public SurrealSession? Session { get; private set; }

        public void Bind(SurrealSession session) => Session = session;
        public void Initialize(SurrealSession session) { }
        public void Flush(SurrealSession session) { }

        public void Hydrate(JsonElement json, IHydrationSink sink)
        {
            HydrationCount++;
            if (json.TryGetProperty("id", out var idE))
            {
                _id = HydrationJson.ReadRecordId(idE);
            }
            sink.Track(this);
        }

        public void OnDeleting() { }
    }
}
