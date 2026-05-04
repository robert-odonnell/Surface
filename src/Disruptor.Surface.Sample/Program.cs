using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Disruptor.Surface.Sample;
using Disruptor.Surface.Sample.Models;
using Disruptor.Surface.Sample.Relations;

const string designAggregate = "design";
const string reviewAggregate = "review";

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

Console.WriteLine("=== Disruptor.Surface.Sample harness ===");
Console.WriteLine($"Connected: {config.Url}  ns={config.Namespace}  db={config.Database}\n");

// ── 1. Apply schema. One db call per chunk so a failure pinpoints which one
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
for (var i = 0; i < 10; i++)
{
    seededDesignIds.Add(await SeedAndCommitDesign($"seed-{i}", transport));
}

// ── 3. Seed a Review aggregate that assesses the third Design. Exercises cross-
//    aggregate edges (Review.Assesses → Design, Issue.Concerns → Constraint), plus
//    within-aggregate edges that aren't `restricts` (Finding.Informs → Issue,
//    Finding.Cites → Observation, DesignChange.Resolves → Issue).
var reviewId = await SeedAndCommitReview(seededDesignIds[2], transport);

// ── 4. Reload + print. Verifies the round-trip including SurrealArray<Scenario>
//    contents, cross-aggregate id collections, and within-aggregate edge reads.
await ReloadAndPrintDesign(seededDesignIds[2], transport);
await ReloadAndPrintReview(reviewId, transport);

// ── 5. Query layer demo — exercises the unified read+load surface end-to-end:
//    flat predicates, traversal projections, edge queries, filtered LoadAsync,
//    LoadShapeViolationException on unloaded reads, and FetchAsync top-up.
await DemoQueryLayer(seededDesignIds, transport);

// ── 6. Lease theft demo — acquire a lease, simulate theft via direct DELETE, then
//    attempt commit and observe the typed exception.
await DemoLeaseTheft(seededDesignIds[2], transport);

return 0;

async Task<DesignId> SeedAndCommitDesign(string text, SurrealHttpClient db)
{
    Console.WriteLine($"--- Seeding design '{text}' ---");
    await using var lease = await WriterLease.AcquireAsync(db, designAggregate);

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
        Details = MintDetails("design.constraint"),
        Description = $"design.constraint.description: {text}"
    });

    for (var i = 0; i < 5; i++)
    {
        var epic = session.Track(new Epic
        {
            Design = design,
            Details = MintDetails($"design.epic.{i}"),
            Description = $"design.epic.{i}.description: {text}"
        });

        for (var j = 0; j < 5; j++)
        {
            var feature = session.Track(new Feature
            {
                Design = design,
                Epic = epic,
                Details = MintDetails($"design.epic.{i}.feature.{j}"),
                Description = $"design.epic.{i}.feature.{j}.description: {text}"
            });

            for (var k = 0; k < 5; k++)
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

                for (var l = 0; l < 5; l++)
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

                    for (var m = 0; m < 5; m++)
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
    await session.CommitAsync(db, lease);
    Console.WriteLine($"  committed; design id = {design.Id}\n");
    return design.Id;

    Details MintDetails(string prefix) => new()
    {
        Header = $"{prefix}.details.header: {text}",
        Summary = $"{prefix}.details.summary: {text}",
        Text = $"{prefix}.details.text: {text}"
    };
}

async Task<ReviewId> SeedAndCommitReview(DesignId targetDesignId, SurrealHttpClient db)
{
    Console.WriteLine($"--- Seeding review of {targetDesignId} ---");

    // Pre-load the target design so we can pick a real Constraint id to link `Concerns`
    // against (cross-aggregate edges are id-typed; the link is just an id, not a typed
    // entity). This is the canonical "load other aggregate to discover its ids" pattern.
    var preload = await workspace.LoadDesignAsync(db, targetDesignId);
    var design = preload.Get<Design>(targetDesignId)
        ?? throw new InvalidOperationException($"design {targetDesignId} did not hydrate");
    var someConstraintId = design.Constraints.First().Id;

    await using var lease = await WriterLease.AcquireAsync(db, reviewAggregate);
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
    session.Relate<References>(observation.Id, targetDesignId);
    session.Relate<Concerns>(issue.Id, someConstraintId);
    session.Relate<Revises>(change.Id, targetDesignId);

    Console.WriteLine($"  pending: {session.Pending.Records.Count} records, {session.Log.Count} commands");
    await session.CommitAsync(db, lease);
    Console.WriteLine($"  committed; review id = {review.Id}\n");
    return review.Id;
}

async Task ReloadAndPrintDesign(DesignId designId, SurrealHttpClient db)
{
    Console.WriteLine($"--- Reloading Design {designId} ---");
    var session = await workspace.LoadDesignAsync(db, designId);

    var design = session.Get<Design>(designId)
                 ?? throw new InvalidOperationException($"Loader didn't hydrate {designId}.");

    Console.WriteLine($"  description:     '{design.Description}'");
    Console.WriteLine($"  repository_root: '{design.RepositoryRoot}'");
    Console.WriteLine(
        $"  details:         header='{design.Details?.Header}'");
    Console.WriteLine($"  epics ({design.Epics.Count}):");
    foreach (var e in design.Epics)
    {
        Console.WriteLine($"    - '{e.Description}'");
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
        Console.WriteLine($"  acceptance_criteria sample: scenarios.Count={firstAc.Scenarios.Count}");
        if (firstAc.Scenarios.Count > 0)
        {
            var s = firstAc.Scenarios[0];
            Console.WriteLine($"    scenario[0]: kind='{s.Kind}'  desc='{s.Description}'");
        }
    }

    Console.WriteLine();
}

async Task ReloadAndPrintReview(ReviewId reloadId, SurrealHttpClient db)
{
    Console.WriteLine($"--- Reloading Review {reloadId} ---");
    var session = await workspace.LoadReviewAsync(db, reloadId);

    var review = session.Get<Review>(reloadId)
                 ?? throw new InvalidOperationException($"Loader didn't hydrate {reloadId}.");

    Console.WriteLine($"  outcome: '{review.Outcome}'  mode: '{review.Mode}'  state: '{review.State}'");
    Console.WriteLine($"  details: header='{review.Details?.Header}'");
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

async Task DemoQueryLayer(IReadOnlyList<DesignId> designIds, SurrealHttpClient db)
{
    Console.WriteLine("--- Query layer demo ---");

    // (a) Flat read-mode query: predicate factory + ExecuteAsync. No session, no lease.
    //     Constraint.Description was seeded with the seed name; pin to one batch.
    var seedTwoConstraints = await Workspace.Query.Constraints
        .Where(ConstraintQ.Description.Contains("seed-2"))
        .ExecuteAsync(db);
    Console.WriteLine($"  flat predicate: {seedTwoConstraints.Count} constraints with 'seed-2' in description");
    foreach (var c in seedTwoConstraints.Take(2))
    {
        Console.WriteLine($"    - {c.Id}  '{c.Description}'");
    }

    // (b) Read-mode traversal: pin a single design, include its details + constraints.
    //     Returns root entities; navigation through .Constraints walks the implicit
    //     internal session populated during hydration.
    var targetId = designIds[2];
    var designsWithConstraints = await Workspace.Query.Designs
        .WithId(targetId)
        .IncludeDetails()
        .IncludeConstraints(c => c.IncludeDetails())
        .ExecuteAsync(db);
    var queriedDesign = designsWithConstraints.Single();
    Console.WriteLine($"  traversal read: design '{queriedDesign.Description}' → {queriedDesign.Constraints.Count} constraint(s)");
    foreach (var c in queriedDesign.Constraints.Take(2))
    {
        Console.WriteLine($"    - {c.Id}  details.header='{c.Details?.Header}'");
    }

    // (c) Edge query: enumerate `restricts` edges originating at the constraints we just
    //     read. Returns flat (source, target) pairs — useful for UI listings without
    //     materialising either side's entity.
    var constraintIds = queriedDesign.Constraints.Select(c => c.Id).ToList();
    var restrictPairs = await Workspace.Query.Edges.Restricts
        .WhereIn(constraintIds)
        .ExecuteAsync(db);
    Console.WriteLine($"  edge query: {restrictPairs.Count} restricts edges from these constraints");
    foreach (var pair in restrictPairs.Take(3))
    {
        Console.WriteLine($"    - {pair.Source} → {pair.Target}");
    }

    // (d) Compiler-driven LoadAsync (filtered slice): only Constraints get loaded.
    //     Same query AST as (b) — only the terminal verb differs. The session it returns
    //     is mutable / committable; we don't commit here.
    await using var lease = await WriterLease.AcquireAsync(db, designAggregate);
    var slicedSession = await Workspace.Query.Designs
        .WithId(targetId)
        .IncludeDetails()
        .IncludeConstraints(c => c.IncludeDetails())
        .LoadAsync(db, lease);
    var slicedDesign = slicedSession.Get<Design>(targetId)!;
    Console.WriteLine($"  filtered LoadAsync: {slicedDesign.Constraints.Count} constraints loaded into a write-mode session");

    // (e) LoadShapeViolationException: reading a slice that wasn't included throws.
    //     Strict-with-escape — the message points at FetchAsync as the way out.
    try
    {
        _ = slicedDesign.Epics.Count;
        Console.WriteLine("  ⚠  reading Epics succeeded — unexpected");
    }
    catch (LoadShapeViolationException ex)
    {
        Console.WriteLine($"  caught LoadShapeViolationException for slice '{ex.Field}'");
    }

    // (f) FetchAsync top-up: extend the loaded slice to include Epics. After this,
    //     reads of design.Epics succeed against the same session.
    await slicedSession.FetchAsync(
        Workspace.Query.Designs.WithId(targetId).IncludeEpics(e => e.IncludeDetails()),
        db);
    Console.WriteLine($"  FetchAsync top-up: design now sees {slicedDesign.Epics.Count} epic(s)");
    foreach (var e in slicedDesign.Epics.Take(2))
    {
        // Epic doesn't declare a public [Id] partial; the IEntity.Id is explicit-impl,
        // reach it through the interface for diagnostics.
        Console.WriteLine($"    - {((IEntity)e).Id}  '{e.Description}'");
    }

    Console.WriteLine();
}

async Task DemoLeaseTheft(DesignId designId, SurrealHttpClient db)
{
    Console.WriteLine("--- Lease theft recovery demo ---");
    await using var lease = await WriterLease.AcquireAsync(db, designAggregate);
    var session = await workspace.LoadDesignAsync(db, designId);
    var design = session.Get<Design>(designId)!;
    // Mutate, but don't commit yet.
    var beforeReload = design.Description;
    Console.WriteLine($"  loaded; current description: '{beforeReload}'");

    // Simulate another writer winning the race — direct UPSERT advances the seq past
    // the value our lease captured, so our CAS check at commit time will fail. Real
    // life: another process completed an Acquire+CommitAsync cycle in this gap.
    await db.ExecuteAsync($"UPSERT writer_lease:{designAggregate} CONTENT {{ seq: {lease.ExpectedSequence + 99} }};");
    Console.WriteLine($"  simulated theft: another writer advanced seq past {lease.ExpectedSequence}");

    // The (now-stale) writer attempts to commit. The CAS check spliced into the
    // commit script detects the seq mismatch and aborts the whole transaction.
    var rwSession = new SurrealSession(Workspace.ReferenceRegistry);
    rwSession.Track(new Design { Id = designId, Description = beforeReload + " [edit]" });
    try
    {
        await rwSession.CommitAsync(db, lease);
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

