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



[Fact]
    public async Task TraversalQuery_EmitsNestedSelect_AndHydratesChildrenIntoSession()
    {
        // Compile a fixture with parent + child, exercise the user-facing query surface
        // reflectively, and verify (a) the SurrealQL on the wire carries the nested
        // SELECT with $parent.id scoping, (b) the parent's [Children] accessor returns
        // the hydrated children — proving the per-include hydrator delegate dispatches
        // to the right concrete entity type.
        const string source = """
            using Disruptor.Surface.Annotations;
            using Disruptor.Surface.Runtime;
            using System.Collections.Generic;
            namespace M;

            [Table, AggregateRoot] public partial class Design {
                [Id] public partial DesignId Id { get; set; }
                [Property] public partial string Description { get; set; }
                [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
            }

            [Table] public partial class Constraint {
                [Id] public partial ConstraintId Id { get; set; }
                [Parent] public partial Design Design { get; set; }
                [Property] public partial string Description { get; set; }
            }

            [CompositionRoot] public partial class Workspace { }
            """;
        var assembly = GeneratorHarness.CompileAndLoad(source);

        // Workspace.Query.Designs returns Query<Design>. Pull the static accessor via
        // reflection.
        var workspaceType = assembly.GetType("M.Workspace")!;
        var queryRoot = workspaceType.GetProperty("Query", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var designQuery = queryRoot.GetType().GetProperty("Designs")!.GetValue(queryRoot)!;

        // Call DesignQueryIncludes.IncludeConstraints(query, configure) — extension
        // method, so it's static on the includes class.
        var includesClass = assembly.GetType("M.DesignQueryIncludes")!;
        var includeMethod = includesClass.GetMethod("IncludeConstraints")!;

        // Configure action: invoke ConstraintTraversalBuilder.Where with a filter so we
        // verify both the children projection AND the user filter compose correctly.
        var constraintQType = assembly.GetType("M.ConstraintQ")!;
        var descriptionExpr = constraintQType.GetField("Description")!.GetValue(null)!;
        var eqMethod = descriptionExpr.GetType().GetMethod("Eq")!;
        var predicate = eqMethod.Invoke(descriptionExpr, new object?[] { "filtered" });

        var configureType = includeMethod.GetParameters()[1].ParameterType;
        var configureLambda = MakeConfigure(configureType, builderInstance =>
        {
            builderInstance.GetType().GetMethod("Where")!.Invoke(builderInstance, new[] { predicate });
        });

        var queryWithIncludes = includeMethod.Invoke(null, new[] { designQuery, configureLambda })!;

        // Drive ExecuteAsync against a recording transport that responds with one root
        // row (a Design) and one nested constraint row. Use real Ulids — RecordIdFormat
        // validates the value at typed-id construction time.
        var designUlid = Ulid.NewUlid().ToString();
        var constraintUlid = Ulid.NewUlid().ToString();
        var scriptedResponse = $$"""
            [{"result":[
                {
                    "id":"designs:{{designUlid}}",
                    "description":"design-desc",
                    "constraints":[
                        {
                            "id":"constraints:{{constraintUlid}}",
                            "description":"filtered",
                            "design":"designs:{{designUlid}}"
                        }
                    ]
                }
            ],"status":"OK"}]
            """;
        var transport = new ScriptedTransport(scriptedResponse);

        var executeMethod = queryWithIncludes.GetType().GetMethod("ExecuteAsync")!;
        var task = (Task)executeMethod.Invoke(queryWithIncludes, new object?[] { transport, default(CancellationToken) })!;
        await task;
        var resultProp = task.GetType().GetProperty("Result")!;
        var resultList = (System.Collections.IList)resultProp.GetValue(task)!;

        // Wire-side check: nested SELECT uses $parent.id to scope to the design row.
        Assert.Single(transport.SqlSeen);
        var sql = transport.SqlSeen[0];
        Assert.Contains("SELECT *, (SELECT * FROM constraints WHERE design = $parent.id AND description = $p0) AS constraints FROM designs;", sql);

        // Result-side check: one design, with one navigable constraint child. The
        // [Children] accessor walks the session's parents dict — proves the per-include
        // hydrator delegate fired for the constraint row.
        Assert.Single(resultList);
        var design = resultList[0]!;
        var constraintsCol = (System.Collections.IEnumerable)design.GetType().GetProperty("Constraints")!.GetValue(design)!;
        var constraints = constraintsCol.Cast<object>().ToList();
        Assert.Single(constraints);
        Assert.Equal("filtered", constraints[0].GetType().GetProperty("Description")!.GetValue(constraints[0]));
    }

    /// <summary>
    /// Build an Action&lt;TBuilder&gt; delegate from a runtime-known builder type without
    /// needing a generic helper. Used to feed the configure lambda into a generated
    /// IncludeX extension whose signature is only known via reflection.
    /// </summary>
    private static Delegate MakeConfigure(Type actionType, Action<object> body)
    {
        var builderType = actionType.GetGenericArguments()[0];
        var param = System.Linq.Expressions.Expression.Parameter(builderType, "b");
        var bodyConst = System.Linq.Expressions.Expression.Constant(body);
        var invoke = System.Linq.Expressions.Expression.Invoke(
            bodyConst,
            System.Linq.Expressions.Expression.Convert(param, typeof(object)));
        var lambda = System.Linq.Expressions.Expression.Lambda(actionType, invoke, param);
        return lambda.Compile();
    }

    private sealed class ScriptedTransport : ISurrealTransport
    {
        private readonly string responseJson;
        public List<string> SqlSeen { get; } = new();
        public ScriptedTransport(string responseJson) => this.responseJson = responseJson;
        public Task<JsonDocument> ExecuteAsync(string sql, object? vars = null, CancellationToken ct = default)
        {
            SqlSeen.Add(sql);
            return Task.FromResult(JsonDocument.Parse(responseJson));
        }
        public ValueTask DisposeAsync() => default;
    }

[Fact]
    public async Task LoadAsync_EmptyIncludes_DelegatesToAggregateLoader()
    {
        // PR5 v1: Query<TRoot>.LoadAsync(transport, lease) with no Include* calls fires
        // exactly the same SurrealQL the legacy Workspace.LoadDesignAsync(transport, id)
        // path uses — the new entry is a thin shim during the migration period. Behaviour
        // drift between the two paths is the regression this test guards against.
        var assembly = GeneratorHarness.CompileAndLoad(LoadFixture);
        var query = BuildPinnedDesignQuery(assembly, out var designUlid);

        var loadClass = assembly.GetType("M.DesignQueryLoad")!;
        var loadAsync = loadClass.GetMethod("LoadAsync")!;

        var transport = new RecordingLoadTransport(BuildEmptyLoadResponse(designUlid));
        var lease = await WriterLease.AcquireAsync(transport, "design", default);

        var task = (Task)loadAsync.Invoke(null, new object?[] { query, transport, lease, default(CancellationToken) })!;
        await task;
        var session = (SurrealSession)task.GetType().GetProperty("Result")!.GetValue(task)!;

        Assert.NotNull(session);

        // The aggregate loader's nested SELECT has a stable shape — pin a couple of
        // fragments to flag drift. We don't snapshot the whole SQL because the loader
        // already has its own dedicated tests.
        var loadSql = transport.NonLeaseSqlSeen.Single();
        Assert.Contains("SELECT *", loadSql);
        Assert.Contains($"FROM designs:{designUlid}", loadSql);
    }

    [Fact]
    public async Task LoadAsync_WithIncludes_Throws_NotImplementedException()
    {
        var assembly = GeneratorHarness.CompileAndLoad(LoadFixture);
        var query = BuildPinnedDesignQuery(assembly, out _);

        // Add an Include — the compiler-driven LoadAsync lands in PR6, so for now the
        // user-facing surface throws with a clear pointer at ExecuteAsync.
        var withIncludes = AppendIncludeConstraints(assembly, query);

        var loadAsync = assembly.GetType("M.DesignQueryLoad")!.GetMethod("LoadAsync")!;
        var transport = new RecordingLoadTransport("[]");
        var lease = await WriterLease.AcquireAsync(transport, "design", default);

        var ex = await Assert.ThrowsAsync<NotImplementedException>(async () =>
        {
            var task = (Task)loadAsync.Invoke(null, new object?[] { withIncludes, transport, lease, default(CancellationToken) })!;
            await task;
        });
        Assert.Contains("Filtered LoadAsync", ex.Message);
        Assert.Contains("ExecuteAsync", ex.Message);
    }

    [Fact]
    public async Task LoadAsync_WithoutPinnedId_Throws_InvalidOperationException()
    {
        var assembly = GeneratorHarness.CompileAndLoad(LoadFixture);

        // Skip WithId — the query has no pinned root id; LoadAsync must reject it loudly
        // because it has no way to issue the loader's `FROM designs:<id>` clause.
        var queryRoot = assembly.GetType("M.Workspace")!
            .GetProperty("Query", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var query = queryRoot.GetType().GetProperty("Designs")!.GetValue(queryRoot)!;

        var loadAsync = assembly.GetType("M.DesignQueryLoad")!.GetMethod("LoadAsync")!;
        var transport = new RecordingLoadTransport("[]");
        var lease = await WriterLease.AcquireAsync(transport, "design", default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var task = (Task)loadAsync.Invoke(null, new object?[] { query, transport, lease, default(CancellationToken) })!;
            await task;
        });
        Assert.Contains("WithId", ex.Message);
    }

    private const string LoadFixture = """
        using Disruptor.Surface.Annotations;
        using Disruptor.Surface.Runtime;
        using System.Collections.Generic;
        namespace M;

        [Table, AggregateRoot] public partial class Design {
            [Id] public partial DesignId Id { get; set; }
            [Property] public partial string Description { get; set; }
            [Children] public partial IReadOnlyCollection<Constraint> Constraints { get; }
        }

        [Table] public partial class Constraint {
            [Id] public partial ConstraintId Id { get; set; }
            [Parent] public partial Design Design { get; set; }
            [Property] public partial string Description { get; set; }
        }

        [CompositionRoot] public partial class Workspace { }
        """;

    private static object BuildPinnedDesignQuery(Assembly assembly, out string designUlid)
    {
        designUlid = Ulid.NewUlid().ToString();

        var workspaceType = assembly.GetType("M.Workspace")!;
        var queryRoot = workspaceType.GetProperty("Query", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var query = queryRoot.GetType().GetProperty("Designs")!.GetValue(queryRoot)!;

        // Construct DesignId from a real Ulid and pin via WithId.
        var designIdCtor = assembly.GetType("M.DesignId")!.GetConstructor(new[] { typeof(string) })!;
        var designId = designIdCtor.Invoke(new object?[] { designUlid });

        // Query<Design>.WithId takes IRecordId — DesignId implements it.
        var withId = query.GetType().GetMethod("WithId")!;
        return withId.Invoke(query, new[] { designId })!;
    }

    private static object AppendIncludeConstraints(Assembly assembly, object query)
    {
        var includesClass = assembly.GetType("M.DesignQueryIncludes")!;
        var includeMethod = includesClass.GetMethod("IncludeConstraints")!;
        var configureType = includeMethod.GetParameters()[1].ParameterType;
        // No-op configure delegate; we just need a non-empty Includes list to trip the throw.
        var noopAction = MakeConfigure(configureType, _ => { });
        return includeMethod.Invoke(null, new[] { query, noopAction })!;
    }

    private static string BuildEmptyLoadResponse(string designUlid)
    {
        // Aggregate loader unwraps a single-element `result` array; we hand back a
        // matching shape with one design row and empty children/edges arrays so the
        // hydrate path runs to completion without errors.
        return $$"""
            [{"result":[
                {
                    "id":"designs:{{designUlid}}",
                    "description":"",
                    "constraints":[]
                }
            ],"status":"OK"}]
            """;
    }

    /// <summary>
    /// Recording transport that fans the WriterLease seq-fetch (a different SurrealQL
    /// shape) away from the actual load query. <see cref="NonLeaseSqlSeen"/> filters out
    /// the lease's <c>SELECT seq FROM writer_lease:design</c> so assertions about the
    /// load SQL don't have to step around it.
    /// </summary>
    private sealed class RecordingLoadTransport : ISurrealTransport
    {
        private readonly string loadResponse;
        public List<string> SqlSeen { get; } = new();
        public RecordingLoadTransport(string loadResponse) => this.loadResponse = loadResponse;

        public List<string> NonLeaseSqlSeen
            => SqlSeen.Where(s => !s.Contains("writer_lease", StringComparison.Ordinal)).ToList();

        public Task<JsonDocument> ExecuteAsync(string sql, object? vars = null, CancellationToken ct = default)
        {
            SqlSeen.Add(sql);
            // Lease acquisition: return a `seq:0` row so AcquireAsync doesn't fail.
            if (sql.Contains("writer_lease", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonDocument.Parse("[{\"result\":[],\"status\":\"OK\"}]"));
            }
            return Task.FromResult(JsonDocument.Parse(loadResponse));
        }
        public ValueTask DisposeAsync() => default;
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
