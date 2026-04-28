using Surface.Runtime;
using Surface.Sample;
using Surface.Sample.Models;

const string AggregateName = "design";

// Mirror of: surreal start --bind 127.0.0.1:8000 --default-namespace project-brain
//                          --default-database workspace --username root --password secret
var config = new SurrealConfig(
    Url: new Uri("http://127.0.0.1:8000"),
    Namespace: "project-brain",
    Database: "workspace",
    User: "root",
    Password: "secret",
    Timeout: TimeSpan.FromSeconds(30));

using var http = new HttpClient();
await using var transport = new SurrealHttpClient(config, http);

Console.WriteLine("=== Surface.Sample harness ===");
Console.WriteLine($"Connected: {config.Url}  ns={config.Namespace}  db={config.Database}\n");

// ── 1. Apply schema (generator-emitted, idempotent). One transport call per chunk
//    so a failure pinpoints which one broke. Iterate `Workspace.Schema` directly if
//    you need to filter / log / transact per chunk.
Console.WriteLine($"Applying schema ({Workspace.Schema.Count} chunks)...");
await Workspace.ApplySchemaAsync(transport);
Console.WriteLine("  schema ready.\n");

// Dev-time hygiene: clear any stale writer_lease record so reruns don't trip on a
// half-released lease from a crashed previous attempt. Production code wouldn't do this.
await transport.ExecuteAsync("DELETE writer_lease;");

// ── 2. Acquire the per-aggregate writer lease. Held for the duration of this scope;
//    released on dispose. CommitAsync renews it before flushing so a stolen lease
//    aborts the write cleanly via WriterLeaseStolenException.
Console.WriteLine($"Acquiring writer lease '{AggregateName}'...");
await using var lease = await WriterLease.AcquireAsync(transport, AggregateName);
Console.WriteLine($"  held by {lease.HolderId}\n");

// ── 3. Mint a fresh Design aggregate (no preceding load — we're seeding).
//var designId = new DesignId(Ulid.Parse("01KQ7AF5MDP9VPBMJFEYHZ32VH"));//) await SeedAndCommit();

var seededIds = new List<DesignId>();
for (var i = 0; i < 3; i++)
{
    seededIds.Add(await SeedAndCommit($"seed-{i}"));
}

// ── 4. Reload one of the just-seeded designs. The loader must scope by parent path
//    back to the root — if it doesn't, we'd see all 10 designs' children show up here.
await ReloadAndPrint(seededIds[2]);

return 0;

async Task<DesignId> SeedAndCommit(string text)
{
    var session = new SurrealSession();
    {
        var design = session.Track(new Design
        {
            RepositoryRoot = $"design.repository_root: {text}",
            Description = $"design.description: {text}",
            Details = new Details
            {
                Header = $"design.details.header: {text}",
                Summary = $"design.details.summary: {text}",
                Text = $"design.details.text: {text}"
            }
        });


        //var constraint0 = new Constraint
        //{
        //    Design = design,
        //    Details = CreateDetails("design.constraint.0"),
        //    Description = $"design.constraint.0.description: {text}"
        //};
        //var constraint1 = new Constraint
        //{
        //    Design = design,
        //    Details = CreateDetails("design.constraint.1"),
        //    Description = $"design.constraint.1.description: {text}"
        //};
        //var constraint2 = new Constraint
        //{
        //    Design = design,
        //    Details = CreateDetails("design.constraint.2"),
        //    Description = $"design.constraint.2.description: {text}"
        //};
        var constraint3 = session.Track(new Constraint
        {
            Design = design,
            Details = CreateDetails("design.constraint.3"),
            Description = $"design.constraint.3.description: {text}"
        });

        for (int i = 0; i < 2; i++)
        {
            var epic = session.Track(new Epic
            {
                Design = design,
                Details = CreateDetails($"design.epic.{i}"),
                Description = $"design.epic.{i}.description: {text}"
            });

            for (int j = 0; j < 2; j++)
            {
                var feature = session.Track(new Feature
                {
                    Design = design,
                    Epic = epic,
                    Details = CreateDetails($"design.epic.{i}.feature.{j}"),
                    Description = $"design.epic.{i}.feature.{j}.description: {text}"
                });

                for (int k = 0; k < 2; k++)
                {
                    var userStory = session.Track(new UserStory
                    {
                        Design = design,
                        Feature = feature,
                        Details = CreateDetails($"design.epic.{i}.feature.{j}.user_story.{k}"),
                        AsA = $"design.epic.{i}.feature.{j}.user_story.{k}.as_a: {text}",
                        IWant = $"design.epic.{i}.feature.{j}.user_story.{k}.i_want: {text}",
                        SoThat = $"design.epic.{i}.feature.{j}.user_story.{k}.so_that: {text}"
                    });

                    for (var l = 0; l < 2; l++)
                    {
                        var acceptanceCriteria = session.Track(new AcceptanceCriteria
                        {
                            Design = design,
                            UserStory = userStory,
                            Details = CreateDetails(
                                $"design.epic.{i}.feature.{j}.user_story.{k}.acceptance_criteria.{l}"
                            ),
                        });
                        var test = session.Track(new Test
                        {
                            Design = design,
                            UserStory = userStory,
                            Details = CreateDetails($"design.epic.{i}.feature.{j}.user_story.{k}.test.{l}"),
                        });
                        //constraint0.Restricts(userStory);
                        //constraint1.Restricts(acceptanceCriteria);
                        //constraint2.Restricts(test);
                        constraint3.Restricts(userStory);
                        constraint3.Restricts(acceptanceCriteria);
                        constraint3.Restricts(test);
                        for (var m = 0; m < 3; m++)
                        {
                            acceptanceCriteria.Scenarios.Add(
                                new Scenario(
                                    $"design.epic.{i}.feature.{j}.user_story.{k}.acceptance_criteria.{l}.scenario.{m}.kind: {text}",
                                    $"design.epic.{i}.feature.{j}.user_story.{k}.acceptance_criteria.{l}.scenario.{m}.description: {text}"
                                )
                            );
                            test.Facts.Add(
                                new Fact(
                                    $"design.epic.{i}.feature.{j}.user_story.{k}.test.{l}.fact.{m}.kind: {text}",
                                    $"design.epic.{i}.feature.{j}.user_story.{k}.test.{l}.fact.{m}.arrange: {text}",
                                    $"design.epic.{i}.feature.{j}.user_story.{k}.test.{l}.fact.{m}.act: {text}",
                                    $"design.epic.{i}.feature.{j}.user_story.{k}.test.{l}.fact.{m}.assert: {text}"
                                )
                            );
                        }
                    }
                }
            }
        }


        Console.WriteLine($"Seeded design {design.Id}");
        Console.WriteLine($"  pending: {session.Pending.Records.Count} records, {session.Log.Count} commands queued");
        Console.WriteLine();

        Console.WriteLine("Committing via SurrealHttpClient...");
        await session.CommitAsync(transport, lease);
        Console.WriteLine($"  committed; pending now: {session.Pending.Records.Count} records\n");

    return design.Id;

    Details CreateDetails(string prefix)
        => new()
        {
            Header = $"{prefix}.details.header: {text}",
            Summary = $"{prefix}.details.summary: {text}",
            Text = $"{prefix}.details.text: {text}"
        };
    }
}

async Task ReloadAndPrint(DesignId designId)
{
    Console.WriteLine($"Reloading Design aggregate {designId}...");
    var workspace = new Workspace();
    var reloaded = await workspace.LoadDesignAsync(transport, designId);

    var design = reloaded.Get<Design>(designId)
                 ?? throw new InvalidOperationException($"Loader didn't hydrate {designId}.");

    Console.WriteLine($"  description:     '{design.Description}'");
    Console.WriteLine($"  repository_root: '{design.RepositoryRoot}'");
    Console.WriteLine(
        $"  details:         {design.Details?.Id}  header='{design.Details?.Header}'  summary='{design.Details?.Summary}'"
    );
    Console.WriteLine($"  epics ({design.Epics.Count}):");
    foreach (var e in design.Epics)
    {
        Console.WriteLine($"    - {e.Id}  description='{e.Description}'  details={e.Details?.Id}");
    }

    Console.WriteLine($"  constraints ({design.Constraints.Count}):");
    foreach (var c in design.Constraints)
    {
        Console.WriteLine($"    - {c.Id}  description='{c.Description}'  details={c.Details?.Id}");
    }
}
