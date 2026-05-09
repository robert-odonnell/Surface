using Disruptor.Surface.Runtime;
using Disruptor.Surface.Runtime.Query;
using Disruptor.Surface.Sample;
using Disruptor.Surface.Sample.Models;
using Disruptor.Surface.Sample.Relations;
using Disruptor.Surreal.Connection;
using SdkSurreal = Disruptor.Surreal.Surreal;

// Mirror of: surreal start --bind 127.0.0.1:8000 --default-namespace project-brain
//                          --default-database workspace --username root --password secret
await using var db = await SdkSurreal.ConnectAsync(SurrealOptions.Parse(
    "Url=ws://127.0.0.1:8000;Namespace=project-brain;Database=workspace;User=root;Password=secret"));

// SurrealSdkTransport bridges Disruptor.Surreal to the legacy ISurrealTransport surface
// the runtime's hydration still consumes. Goes away when hydration migrates to Value
// (alongside Step 4 of the unpainting plan).
await using var transport = new SurrealSdkTransport(db);

var workspace = new Workspace();

Console.WriteLine("=== Disruptor.Surface.Sample harness ===");
Console.WriteLine($"Connected: ws://127.0.0.1:8000  ns=project-brain  db=workspace\n");

// ── 1. Apply schema. One db call per chunk so a failure pinpoints which one
//    broke. Iterate `Workspace.Schema` directly when you want to filter / log /
//    transact per chunk.
Console.WriteLine($"Applying schema ({Workspace.Schema.Count} chunks)...");
await Workspace.ApplySchemaAsync(transport);
Console.WriteLine("  schema ready.\n");

// ── 2. Seed three Design aggregates. Each commit opens its own server-side
//    transaction; concurrent writers conflict natively at COMMIT.
var seededDesignIds = new List<DesignId>();
for (var i = 0; i < 10; i++)
{
    seededDesignIds.Add(await SeedAndCommitDesign($"seed-{i}", db));
}

// ── 3. Seed a Review aggregate that assesses the third Design. Exercises cross-
//    aggregate edges (Review.Assesses → Design, Issue.Concerns → Constraint), plus
//    within-aggregate edges that aren't `restricts` (Finding.Informs → Issue,
//    Finding.Cites → Observation, DesignChange.Resolves → Issue).
var reviewId = await SeedAndCommitReview(seededDesignIds[2], db);

// ── 4. Reload + print. Verifies the round-trip including SurrealArray<Scenario>
//    contents, cross-aggregate id collections, and within-aggregate edge reads.
await ReloadAndPrintDesign(seededDesignIds[2], db);
await ReloadAndPrintReview(reviewId, db);

// ── 5. Query layer demo — exercises the unified read+load surface end-to-end:
//    flat predicates, traversal projections, edge queries, filtered LoadAsync,
//    LoadShapeViolationException on unloaded reads, and FetchAsync top-up.
await DemoQueryLayer(seededDesignIds, db, transport);

// ── 6. Relation traversal demo — the four flavors of [Forward]/[Inverse] relation
//    includes: forward outgoing single-target, inverse incoming single-target, forward
//    outgoing multi-target leaf, and cross-aggregate id-only.
await DemoRelationTraversal(transport);

return 0;

async Task<DesignId> SeedAndCommitDesign(string text, SdkSurreal db)
{
    Console.WriteLine($"--- Seeding design '{text}' ---");

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

    for (var i = 0; i < 3; i++)
    {
        var epic = session.Track(new Epic
        {
            Design = design,
            Details = MintDetails($"design.epic.{i}"),
            Description = $"design.epic.{i}.description: {text}"
        });

        for (var j = 0; j < 3; j++)
        {
            var feature = session.Track(new Feature
            {
                Design = design,
                Epic = epic,
                Details = MintDetails($"design.epic.{i}.feature.{j}"),
                Description = $"design.epic.{i}.feature.{j}.description: {text}"
            });

            for (var k = 0; k < 3; k++)
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

                for (var l = 0; l < 3; l++)
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
    await using var tx = await db.BeginTransactionAsync();
    await session.SaveAsync(tx);
    await tx.CommitAsync();
    Console.WriteLine($"  committed; design id = {design.Id}\n");
    return design.Id;

    Details MintDetails(string prefix) => new()
    {
        Header = $"{prefix}.details.header: {text}",
        Summary = $"{prefix}.details.summary: {text}",
        Text = $"{prefix}.details.text: {text}"
    };
}

async Task<ReviewId> SeedAndCommitReview(DesignId targetDesignId, SdkSurreal db)
{
    Console.WriteLine($"--- Seeding review of {targetDesignId} ---");

    // Pre-load the target design so we can pick a real Constraint id to link `Concerns`
    // against (cross-aggregate edges are id-typed; the link is just an id, not a typed
    // entity). This is the canonical "load other aggregate to discover its ids" pattern.
    var preload = await workspace.LoadDesignAsync(db, targetDesignId);
    var design = preload.Get<Design>(targetDesignId)
        ?? throw new InvalidOperationException($"design {targetDesignId} did not hydrate");
    var someConstraintId = design.Constraints.First().Id;

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
    await using var tx = await db.BeginTransactionAsync();
    await session.SaveAsync(tx);
    await tx.CommitAsync();
    Console.WriteLine($"  committed; review id = {review.Id}\n");
    return review.Id;
}

async Task ReloadAndPrintDesign(DesignId designId, SdkSurreal db)
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

async Task ReloadAndPrintReview(ReviewId reloadId, SdkSurreal db)
{
    Console.WriteLine($"--- Reloading Review {reloadId} ---");
    var session = await workspace.LoadReviewAsync(db, reloadId);

    var review = session.Get<Review>(reloadId)
                 ?? throw new InvalidOperationException($"Loader didn't hydrate {reloadId}.");

    Console.WriteLine($"  outcome: '{review.Outcome}'  mode: '{review.Mode}'  state: '{review.State}'");
    Console.WriteLine($"  details: header='{review.Details?.Header}'");
    Console.WriteLine($"  findings: {review.Findings.Count}, observations: {review.Observations.Count}, issues: {review.Issues.Count}, design_changes: {review.DesignChanges.Count}");

    Console.WriteLine($"  cross-agg assesses ({review.Assessments.Count}):");
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

async Task DemoQueryLayer(IReadOnlyList<DesignId> designIds, SdkSurreal sdk, ISurrealTransport db)
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
    Console.WriteLine($"  traversal read: design '{queriedDesign.Description}' has {queriedDesign.Constraints.Count} constraint(s)");
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
        Console.WriteLine($"    - {pair.Source} :: {pair.Target}");
    }

    // (d) Compiler-driven LoadAsync (filtered slice): only Constraints get loaded.
    //     Same query AST as (b) — only the terminal verb differs. The session it returns
    //     is mutable / committable; we don't commit here.
    var slicedSession = await Workspace.Query.Designs
        .WithId(targetId)
        .IncludeDetails()
        .IncludeConstraints(c => c.IncludeDetails())
        .LoadAsync(db);
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

static async Task DemoRelationTraversal(ISurrealTransport db)
{
    Console.WriteLine("--- Relation traversal demo ---");

    // (a) Forward outgoing, single-target, within-aggregate. Each Finding declares
    //     [Informs]→Issue and [Cites]→Observation. Both are within the Review
    //     aggregate, so the include projects target rows (not just ids) and supports
    //     nested traversal — here we top up each target's [Reference, Inline] details.
    //     After ExecuteAsync, finding.InformedIssues and finding.Citations resolve
    //     against the implicit hydrated session — no extra round trip.
    var findings = await Workspace.Query.Findings
        .IncludeDetails()
        .IncludeInformedIssues(i => i.IncludeDetails())
        .IncludeCitations(o => o.IncludeDetails())
        .ExecuteAsync(db);
    Console.WriteLine($"  forward outgoing single-target: {findings.Count} finding(s)");
    foreach (var f in findings.Take(2))
    {
        Console.WriteLine($"    - finding {((IEntity)f).Id}  recommendation='{f.Recommendation}'");
        foreach (var iss in f.InformedIssues)
        {
            Console.WriteLine($"        informs : {iss.Id}  severity='{iss.Severity}'  details.header='{iss.Details?.Header}'");
        }
        foreach (var obs in f.Citations)
        {
            Console.WriteLine($"        cites   : {obs.Id}  description='{obs.Description}'");
        }
    }

    // (b) Inverse incoming, single-target, within-aggregate. Issue declares
    //     [InformedBy]→Finding — same `informs` edge as (a), opposite direction.
    //     The traversal walks the edge backwards and hydrates the source-side
    //     Finding rows.
    var issues = await Workspace.Query.Issues
        .IncludeDetails()
        .IncludeInformingFindings(f => f.IncludeDetails())
        .ExecuteAsync(db);
    Console.WriteLine($"  inverse incoming single-target: {issues.Count} issue(s)");
    foreach (var iss in issues.Take(2))
    {
        Console.WriteLine($"    - issue {iss.Id}  severity='{iss.Severity}'");
        foreach (var f in iss.InformingFindings)
        {
            Console.WriteLine($"        informed-by: {((IEntity)f).Id}  recommendation='{f.Recommendation}'");
        }
    }

    // (c) Forward outgoing, multi-target, leaf. Constraint.Restricts targets a
    //     heterogeneous union (UserStory / AcceptanceCriteria / Test), so the
    //     include is a leaf — no nested configure-action, no per-target predicate.
    //     The hydrator dispatches each row to the right entity type by record-id
    //     prefix, materialising Constraint.Restrictions across all union members.
    var constraints = await Workspace.Query.Constraints
        .Where(ConstraintQ.Description.Contains("seed-2"))
        .IncludeRestrictions()
        .ExecuteAsync(db);
    Console.WriteLine($"  forward outgoing multi-target leaf: {constraints.Count} constraint(s)");
    foreach (var c in constraints.Take(1))
    {
        Console.WriteLine($"    - constraint {c.Id}  Restrictions.Count={c.Restrictions.Count}");
        foreach (var target in c.Restrictions.Take(4))
        {
            Console.WriteLine($"        restricts: {target.Id}  ({target.GetType().Name})");
        }
    }

    // (d) Cross-aggregate inverse, id-only. `concerns` runs from Issue (Review
    //     aggregate) → Constraint (Design aggregate). Crossing the boundary collapses
    //     the include to ids — load the other aggregate explicitly when you need the
    //     entity. The forward side (Issue.IncludeConcerns) is symmetric.
    foreach (var c in constraints.Take(1))
    {
        var withConcerns = (await Workspace.Query.Constraints
            .WithId(c.Id)
            .IncludeConcerns()
            .ExecuteAsync(db)).Single();
        Console.WriteLine($"  cross-aggregate inverse id-only: constraint {withConcerns.Id} concerned by {withConcerns.Concerns.Count} issue id(s)");
        foreach (var id in withConcerns.Concerns.Take(3))
        {
            Console.WriteLine($"        concerned-by: {id}");
        }
    }

    // (e) Forward side of the same cross-aggregate edge. Issue.IncludeConcerns is
    //     also id-only — the include sits on the source side of `concerns`, but the
    //     target lives in a different aggregate, so the include collapses identically.
    var issuesWithConcerns = await Workspace.Query.Issues
        .IncludeConcerns()
        .ExecuteAsync(db);
    Console.WriteLine($"  cross-aggregate forward id-only: {issuesWithConcerns.Count} issue(s)");
    foreach (var iss in issuesWithConcerns.Take(2))
    {
        Console.WriteLine($"    - issue {iss.Id} concerns {iss.Concerns.Count} constraint id(s)");
        foreach (var id in iss.Concerns.Take(3))
        {
            Console.WriteLine($"        concerns: {id}");
        }
    }

    Console.WriteLine();
}

