using System.Reflection;
using System.Text.Json;
using Disruptor.Surface.Runtime;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Disruptor.Surface.Tests.Generator;

/// <summary>
/// End-to-end tests: compile a fixture model + the generator's output into an in-memory
/// assembly, load it, instantiate generated entity types via reflection, and exercise
/// them through the live runtime (<see cref="SurrealSession"/>, <see cref="WriterLease"/>,
/// recording transport). Shape tests catch drift in the emitted *source*; these catch
/// drift in the emitted *behavior* — bugs where the source compiles fine but the runtime
/// flow doesn't actually work (e.g. routing the wrong field name, missing IEntity wiring,
/// generated id ctors validating differently than expected).
/// </summary>
public sealed class EmissionRoundTripTests
{
    private const string MinimalSource = """
        using Disruptor.Surface.Annotations;
        using Disruptor.Surface.Runtime;
        using System.Collections.Generic;
        namespace M;

        [Table, AggregateRoot] public partial class Design {
            [Id] public partial DesignId Id { get; set; }
            [Property] public partial string Description { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    [Fact]
    public void GeneratedEntity_ImplementsIEntity_AndIsInstantiable()
    {
        var assembly = GeneratorHarness.CompileAndLoad(MinimalSource);
        var design = NewEntity(assembly, "M.Design");

        Assert.IsAssignableFrom<IEntity>(design);
    }

    [Fact]
    public void GeneratedId_FromMintFactory_IsValidUlidShape()
    {
        // The typed {Name}Id's New() factory mints via Ulid.NewUlid().ToString(); the
        // primary-ctor parameter routes through RecordIdFormat.Validate. End-to-end
        // assertion that the mint path produces a value the validator accepts.
        var assembly = GeneratorHarness.CompileAndLoad(MinimalSource);
        var idType = assembly.GetType("M.DesignId")!;
        var newMethod = idType.GetMethod("New", BindingFlags.Public | BindingFlags.Static)!;

        var minted = newMethod.Invoke(null, null)!;
        var value = (string)idType.GetProperty("Value")!.GetValue(minted)!;

        Assert.True(RecordIdFormat.IsValid(value));
        Assert.Equal(26, value.Length);  // Ulid stringification is exactly 26 chars
    }

    [Fact]
    public void GeneratedId_RejectsBadValueAtConstruction()
    {
        // Anyone who reaches in and tries `new DesignId("bad value")` should hit the
        // RecordIdFormat validator inside the typed id's ctor, NOT silently end up with
        // a malformed Surreal record id.
        var assembly = GeneratorHarness.CompileAndLoad(MinimalSource);
        var idType = assembly.GetType("M.DesignId")!;
        var ctor = idType.GetConstructor([typeof(string)])!;

        var ex = Assert.Throws<TargetInvocationException>(
            () => ctor.Invoke(["Bad Value With Spaces"]));
        Assert.IsType<FormatException>(ex.InnerException);
    }

    [Fact]
    public void Track_ThenSetField_ThenCommit_RendersExpectedSql()
    {
        // Round-trip through the live runtime: instantiate a generated entity, Track it,
        // mutate a property via the generated setter (which routes through __WriteField
        // → Session.SetField), commit, and assert the rendered SurrealQL contains the
        // create + the field set.
        var assembly = GeneratorHarness.CompileAndLoad(MinimalSource);
        var design = NewEntity(assembly, "M.Design");

        // Set Description via the generated property setter — exercises the partial-
        // property body emitted by PartialEmitter, including the buffer-when-unbound
        // logic.
        SetProperty(design, "Description", "round-trip test");

        var session = new SurrealSession();
        var transport = new RecordingTransport();
        session.Track((IEntity)design);

        // After Track + Flush, the buffered Description write should have replayed into
        // the session's pending state. The value travels in the parameters dictionary
        // (not inlined in the SQL) — the emitter binds via $p0 / $p1 / … bindings.
        var rendered = session.RenderBatch();
        Assert.Contains("designs:", rendered.Sql);
        Assert.Contains("description", rendered.Sql);
        Assert.Contains("round-trip test", rendered.Parameters.Values.OfType<string>());
    }

    [Fact]
    public void Hydrate_FromJson_PopulatesPropertyAndSetsId()
    {
        // The loader emits Hydrate calls; here we drive Hydrate directly with a hand-
        // crafted row so we can verify (a) the typed id parses out of the "id" field
        // through RecordIdFormat, (b) the property value lands in the backing field
        // and is reachable via the generated getter.
        var assembly = GeneratorHarness.CompileAndLoad(MinimalSource);
        var design = NewEntity(assembly, "M.Design");

        var session = new SurrealSession();
        var sink = (IHydrationSink)session;

        // Use a real Ulid so RecordIdFormat.Validate passes.
        var ulidStr = Ulid.NewUlid().ToString();
        var json = JsonDocument.Parse($"{{\"id\":\"designs:{ulidStr}\",\"description\":\"loaded\"}}");
        ((IEntity)design).Hydrate(json.RootElement, sink);

        var description = (string)design.GetType().GetProperty("Description")!.GetValue(design)!;
        Assert.Equal("loaded", description);

        var idValue = ((IEntity)design).Id.Value;
        Assert.Equal(ulidStr, idValue);
    }

    // ─────────────────────────── helpers ───────────────────────────

    private static object NewEntity(Assembly assembly, string fullName)
    {
        var type = assembly.GetType(fullName)
            ?? throw new InvalidOperationException($"Type {fullName} not found in compiled assembly.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not instantiate {fullName}.");
    }

    private static void SetProperty(object instance, string name, object? value)
        => instance.GetType().GetProperty(name)!.SetValue(instance, value);

    /// <summary>Captures every SQL string passed through the transport for assertion.</summary>
    private sealed class RecordingTransport : ISurrealTransport
    {
        public List<string> SqlSeen { get; } = new();
        public Task<JsonDocument> ExecuteAsync(string sql, object? vars = null, CancellationToken ct = default)
        {
            SqlSeen.Add(sql);
            return Task.FromResult(JsonDocument.Parse("[]"));
        }
        public ValueTask DisposeAsync() => default;
    }
}
