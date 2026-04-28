using Surface.Runtime;
using Surface.Sample;
using Surface.Sample.Models;
using Surface.Sample.Relations;

const string DesignAggregate = "design";
const string ReviewAggregate = "review";

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
var workspace = new Workspace();

Console.WriteLine("=== Surface.Sample harness ===");
Console.WriteLine($"Connected: {config.Url}  ns={config.Namespace}  db={config.Database}\n");

// ── 1. Apply schema. One transport call per chunk so a failure pinpoints which one
//    broke. Iterate `Workspace.Schema` directly when you want to filter / log /
//    transact per chunk.
Console.WriteLine($"Applying schema ({Workspace.Schema.Count} chunks)...");
await Workspace.ApplySchemaAsync(transport);
Console.WriteLine("  schema ready.\n");

// Dev-time hygiene: clear any stale writer_lease record so reruns don't trip on a
// half-released lease from a crashed previous attempt. Production code wouldn't do this.
await transport.ExecuteAsync("DELETE writer_lease;");

// ── 2. Seed three Design aggregates. Each pass acquires + releases its own writer
//    lease so the per-aggregate granularity is exercised.
var seededDesignIds = new List<DesignId>();
for (var i = 0; i < 3; i++)
{
    seededDesignIds.Add(await SeedAndCommitDesign($"seed-{i}"));
}

// ── 3. Seed a Review aggregate that assesses the third Design. Exercises cross-
//    aggregate edges (Review.Assesses → Design, Issue.Concerns → Constraint), plus
//    within-aggregate edges that aren't `restricts` (Finding.Informs → Issue,
//    Finding.Cites → Observation, DesignChange.Resolves → Issue).
var reviewId = await SeedAndCommitReview(seededDesignIds[2]);

// ── 4. Reload + print. Verifies the round-trip including SurrealArray<Scenario>
//    contents, cross-aggregate id collections, and within-aggregate edge reads.
await ReloadAndPrintDesign(seededDesignIds[2]);
await ReloadAndPrintReview(reviewId);

// ── 5. Lease theft demo — acquire a lease, simulate theft via direct DELETE, then
//    attempt commit and observe the typed exception.
await DemoLeaseTheft(seededDesignIds[2]);

return 0;

async Task<DesignId> SeedAndCommitDesign(string text)
{
    Console.WriteLine($"--- Seeding design '{text}' ---");
    await using var lease = await WriterLease.AcquireAsync(transport, DesignAggregate);

    var session = new SurrealSession(Workspace.ReferenceRegistry);
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

    var constraint = session.Track(new Constraint
    {
        Design = design,
        Details = MintDetails($"design.constraint"),
        Description = $"design.constraint.description: {text}"
    });

    for (var i = 0; i < 2; i++)
    {
        var epic = session.Track(new Epic
        {
            Design = design,
            Details = MintDetails($"design.epic.{i}"),
            Description = $"design.epic.{i}.description: {text}"
        });

        for (var j = 0; j < 2; j++)
        {
            var feature = session.Track(new Feature
            {
                Design = design,
                Epic = epic,
                Details = MintDetails($"design.epic.{i}.feature.{j}"),
                Description = $"design.epic.{i}.feature.{j}.description: {text}"
            });

            for (var k = 0; k < 2; k++)
            {
                var userStory = session.Track(new UserStory
                {
                    Design = design,
                    Feature = feature,
                    Details = MintDetails($"design.epic.{i}.feature.{j}.user_story.{k}"),
                    AsA = $"as a user-{text}",
                    IWant = $"i want feature-{i}.{j}.{k}",
                    SoThat = $"so that {text}"
                });

                for (var l = 0; l < 2; l++)
                {
                    var ac = session.Track(new AcceptanceCriteria
                    {
                        Design = design,
                        UserStory = userStory,
                        Details = MintDetails($"design.epic.{i}.feature.{j}.user_story.{k}.acceptance_criteria.{l}"),
                    });
                    var test = session.Track(new Test
                    {
                        Design = design,
                        UserStory = userStory,
                        Details = MintDetails($"design.epic.{i}.feature.{j}.user_story.{k}.test.{l}"),
                    });

                    constraint.Restricts(userStory);
                    constraint.Restricts(ac);
                    constraint.Restricts(test);

                    // Within-aggregate `validates` edge: each Test validates its sibling
                    // AcceptanceCriteria. Exercised so the harness covers more than just
                    // `restricts` for the within-aggregate edge surface.
                    session.Relate<Validates>(test, ac);

                    for (var m = 0; m < 3; m++)
                    {
                        ac.Scenarios.Add(new Scenario(
                            $"scenario.{m}.kind",
                            $"scenario.{m}.description: {text}"));
                        test.Facts.Add(new Fact(
                            $"fact.{m}.kind",
                            $"fact.{m}.arrange",
                            $"fact.{m}.act",
                            $"fact.{m}.assert"));
                    }
                }
            }
        }
    }

    Console.WriteLine($"  pending: {session.Pending.Records.Count} records, {session.Log.Count} commands");
    await session.CommitAsync(transport, lease);
    Console.WriteLine($"  committed; design id = {design.Id}\n");
    return design.Id;

    Details MintDetails(string prefix) => new()
    {
        Header = $"{prefix}.details.header: {text}",
        Summary = $"{prefix}.details.summary: {text}",
        Text = $"{prefix}.details.text: {text}"
    };
}

async Task<ReviewId> SeedAndCommitReview(DesignId targetDesignId)
{
    Console.WriteLine($"--- Seeding review of {targetDesignId} ---");

    // Pre-load the target design so we can pick a real Constraint id to link `Concerns`
    // against (cross-aggregate edges are id-typed; the link is just an id, not a typed
    // entity). This is the canonical "load other aggregate to discover its ids" pattern.
    var preload = await workspace.LoadDesignAsync(transport, targetDesignId);
    var design = preload.Get<Design>(targetDesignId)
        ?? throw new InvalidOperationException($"design {targetDesignId} did not hydrate");
    var someConstraintId = design.Constraints.First().Id;

    await using var lease = await WriterLease.AcquireAsync(transport, ReviewAggregate);
    var session = new SurrealSession(Workspace.ReferenceRegistry);

    var review = session.Track(new Review
    {
        Outcome = "needs-work",
        Mode = "synchronous",
        State = "open",
        Details = new Details { Header = "review header", Summary = "review summary", Text = "review text" }
    });

    var observation = session.Track(new Observation
    {
        Review = review,
        Kind = "behavioural",
        Description = "design conflates concerns A and B",
        Excerpt = "see line 42",
        Confidence = "high",
        Details = new Details { Header = "obs header", Summary = "obs summary", Text = "obs text" }
    });

    var finding = session.Track(new Finding
    {
        Review = review,
        Kind = "structural",
        Recommendation = "split A and B into separate aggregates",
        Details = new Details { Header = "finding header", Summary = "finding summary", Text = "finding text" }
    });

    var issue = session.Track(new Issue
    {
        Review = review,
        Severity = "high",
        Disposition = "accepted",
        DispositionReason = "valid concern",
        Details = new Details { Header = "issue header", Summary = "issue summary", Text = "issue text" }
    });

    var change = session.Track(new DesignChange
    {
        Review = review,
        Details = new Details { Header = "change header", Summary = "change summary", Text = "change text" }
    });

    // Within-aggregate edges: Finding informs Issue, Finding cites Observation,
    // DesignChange resolves Issue.
    session.Relate<Informs>(finding, issue);
    session.Relate<Cites>(finding, observation);
    session.Relate<Resolves>(change, issue);

    // Cross-aggregate edges (Review → Design): Review assesses the Design, Observation
    // references it, Issue concerns one of its Constraints, DesignChange revises it.
    session.Relate<Assesses>(review.Id, targetDesignId);
    session.Relate<Surface.Sample.Relations.References>(observation.Id, targetDesignId);
    session.Relate<Concerns>(issue.Id, someConstraintId);
    session.Relate<Revises>(change.Id, targetDesignId);

    Console.WriteLine($"  pending: {session.Pending.Records.Count} records, {session.Log.Count} commands");
    await session.CommitAsync(transport, lease);
    Console.WriteLine($"  committed; review id = {review.Id}\n");
    return review.Id;
}

async Task ReloadAndPrintDesign(DesignId designId)
{
    Console.WriteLine($"--- Reloading Design {designId} ---");
    var session = await workspace.LoadDesignAsync(transport, designId);

    var design = session.Get<Design>(designId)
                 ?? throw new InvalidOperationException($"Loader didn't hydrate {designId}.");

    Console.WriteLine($"  description:     '{design.Description}'");
    Console.WriteLine($"  repository_root: '{design.RepositoryRoot}'");
    Console.WriteLine(
        $"  details:         {design.Details?.Id}  header='{design.Details?.Header}'");
    Console.WriteLine($"  epics ({design.Epics.Count}):");
    foreach (var e in design.Epics)
    {
        Console.WriteLine($"    - {e.Id}  '{e.Description}'");
    }

    Console.WriteLine($"  constraints ({design.Constraints.Count}):");
    foreach (var c in design.Constraints)
    {
        Console.WriteLine($"    - {c.Id}  '{c.Description}'");
    }

    // SurrealArray round-trip — drill into one acceptance criteria, print scenario count
    // and the first scenario's content. Tests survive the same way via Test.Facts.
    var firstAc = design.Epics
        .SelectMany(e => session.QueryChildren<Feature>(e, "features"))
        .SelectMany(f => session.QueryChildren<UserStory>(f, "user_stories"))
        .SelectMany(u => session.QueryChildren<AcceptanceCriteria>(u, "acceptance_criteria"))
        .FirstOrDefault();
    if (firstAc is not null)
    {
        Console.WriteLine($"  acceptance_criteria sample: {firstAc.Id}  scenarios.Count={firstAc.Scenarios.Count}");
        if (firstAc.Scenarios.Count > 0)
        {
            var s = firstAc.Scenarios[0];
            Console.WriteLine($"    scenario[0]: kind='{s.Kind}'  desc='{s.Description}'");
        }
    }

    Console.WriteLine();
}

async Task ReloadAndPrintReview(ReviewId reviewId)
{
    Console.WriteLine($"--- Reloading Review {reviewId} ---");
    var session = await workspace.LoadReviewAsync(transport, reviewId);

    var review = session.Get<Review>(reviewId)
                 ?? throw new InvalidOperationException($"Loader didn't hydrate {reviewId}.");

    Console.WriteLine($"  outcome: '{review.Outcome}'  mode: '{review.Mode}'  state: '{review.State}'");
    Console.WriteLine($"  details: {review.Details?.Id}  header='{review.Details?.Header}'");
    Console.WriteLine($"  findings: {review.Findings.Count}, observations: {review.Observations.Count}, issues: {review.Issues.Count}, design_changes: {review.DesignChanges.Count}");

    Console.WriteLine($"  cross-agg → assesses ({review.Assessments.Count}):");
    foreach (var id in review.Assessments)
    {
        Console.WriteLine($"    - {id}");
    }
    foreach (var obs in review.Observations)
    {
        Console.WriteLine($"  observation {obs.Id} references ({obs.References.Count}):");
        foreach (var id in obs.References)
        {
            Console.WriteLine($"    - {id}");
        }
    }
    foreach (var iss in review.Issues)
    {
        Console.WriteLine($"  issue {iss.Id} concerns ({iss.Concerns.Count}):");
        foreach (var id in iss.Concerns)
        {
            Console.WriteLine($"    - {id}");
        }
    }

    Console.WriteLine();
}

async Task DemoLeaseTheft(DesignId designId)
{
    Console.WriteLine("--- Lease theft recovery demo ---");
    await using var lease = await WriterLease.AcquireAsync(transport, DesignAggregate);
    var session = await workspace.LoadDesignAsync(transport, designId);
    var design = session.Get<Design>(designId)!;
    // Mutate, but don't commit yet.
    var beforeReload = design.Description;
    Console.WriteLine($"  loaded; current description: '{beforeReload}'");

    // Simulate another writer stealing the lease — direct DELETE wipes the lock row
    // out from under us. Real life would be a TTL expiring + another acquirer claiming.
    await transport.ExecuteAsync("DELETE writer_lease;");
    Console.WriteLine("  simulated lease theft (DELETE writer_lease;)");

    // The (now-invalid) writer attempts to commit. RenewAsync runs first inside
    // CommitAsync; it should detect the missing/stolen row and throw.
    var rwSession = new SurrealSession(Workspace.ReferenceRegistry);
    var rwTracked = rwSession.Track(new Design { Id = designId, Description = beforeReload + " [edit]" });
    try
    {
        await rwSession.CommitAsync(transport, lease);
        Console.WriteLine("  ⚠  commit succeeded — lease theft NOT detected (unexpected)");
    }
    catch (WriterLeaseStolenException ex)
    {
        Console.WriteLine($"  caught WriterLeaseStolenException: {ex.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  caught {ex.GetType().Name}: {ex.Message}");
    }

    Console.WriteLine();
}

