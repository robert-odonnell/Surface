That changes the emphasis: you don’t mainly want “table queries”; you want **typed graph/path queries with set operators**.

So I’d make the generated query layer **edge-aware first-class**, not bolt edges on afterwards.

The natural shape should match your vocalisation:

> “These features have these restrictions via constraints.”

That is a path:

```text id="1xj8o1"
Feature
  <- concerns/restricts/targets/etc
Constraint
  -> restricts
UserStory / Feature / etc
```

Or more generally:

```text id="lgw45h"
source set
  traverse edge(s)
  filter intermediate nodes
  filter target nodes
  project / group / count
```

## Recommended query API shape

I’d add a path query layer like this:

```csharp id="4ynoby"
var result = await workspace.Query.Features
    .Where(FeatureQ.Id.In(featureIds))
    .ViaIncoming<Restricts, Constraint>()
    .Where(ConstraintQ.Description.Contains("security"))
    .Project(FeatureRestrictionProjections.FeatureWithRestrictions)
    .ExecuteAsync(db, ct);
```

But I’d also generate named helpers, because they will read much better:

```csharp id="wn7ujv"
var result = await workspace.Query.Features
    .WhereIdIn(featureIds)
    .RestrictedByConstraints()
    .Select(FeatureProjections.WithRestrictions)
    .ExecuteAsync(db, ct);
```

For your “all user stories that may have ambiguity to resolve” example:

```csharp id="qc1i5s"
var stories = await workspace.Query.UserStories
    .WithOpenIssues()
    .InformedByFindings(FindingKind.Ambiguity)
    .Select(UserStoryProjections.AmbiguityResolutionListItem)
    .ExecuteAsync(db, ct);
```

That should emit real SurrealQL over the issue/finding/design-node graph. Not load a design, not load all issues, not walk dictionaries.

## Make edges queryable objects

The code generator should emit query roots for both **nodes** and **relations**.

Something like:

```csharp id="bjtqpf"
workspace.Query.UserStories
workspace.Query.Features
workspace.Query.Constraints
workspace.Query.Issues
workspace.Query.Findings

workspace.Query.Edges.Restricts
workspace.Query.Edges.Concerns
workspace.Query.Edges.InformedBy
workspace.Query.Edges.Cites
```

That lets you write edge-subset queries directly:

```csharp id="y4f47p"
var restrictions = await workspace.Query.Edges.Restricts
    .WhereIn<Constraint>(constraintIds)
    .WhereOut<UserStory>(storyIds)
    .Select(RestrictsEdgeProjections.Pair)
    .ExecuteAsync(db, ct);
```

This matters because sometimes the edge set is the actual thing you are querying.

For example:

```csharp id="r1tld7"
var restrictedPairs = await workspace.Query.Edges.Restricts
    .FromConstraints()
    .ToUserStories()
    .WhereOutIn(storyIds)
    .SelectPairs()
    .ExecuteAsync(db, ct);
```

Returns:

```csharp id="aa1w76"
public readonly record struct ConstraintRestrictionPair(
    ConstraintId ConstraintId,
    UserStoryId UserStoryId);
```

Then your UI can group however it wants.

## Add generated “semantic shortcuts”

Raw path APIs are powerful, but for Project Brain-style queries, generated semantic shortcuts will be worth their weight.

For example, generate these from relation metadata:

```csharp id="n15048"
UserStoryQueries.WithOpenIssues()
UserStoryQueries.WithIssues(IssueDisposition disposition)
UserStoryQueries.WithFindings(FindingKind kind)
UserStoryQueries.WithUnresolvedAmbiguity()
UserStoryQueries.ConstrainedBy()
FeatureQueries.RestrictedByConstraints()
FeatureQueries.WithOpenIssues()
ConstraintQueries.RestrictingUserStories()
IssueQueries.ConcerningUserStories()
FindingQueries.ConcerningDesignNodes()
```

Then the callsite becomes close to how you speak:

```csharp id="xq1xnz"
var stories = await workspace.Query.UserStories
    .WithUnresolvedAmbiguity()
    .ForFeature(featureId)
    .Select(UserStoryProjections.ReviewWorkItem)
    .ExecuteAsync(db, ct);
```

That is the sweet spot: **domain-fluent API over generated SurrealQL**, not generic ORM sludge.

## Projection shape for edge-subset queries

You probably want grouped projections often:

```csharp id="6g5z62"
public readonly record struct FeatureRestrictions(
    FeatureId FeatureId,
    string FeatureTitle,
    IReadOnlyList<RestrictionSummary> Restrictions);

public readonly record struct RestrictionSummary(
    ConstraintId ConstraintId,
    string Description);
```

Generated query could emit something shaped like:

```sql id="32qz50"
SELECT
    id.id() AS feature_id,
    title,
    (
        SELECT
            id.id() AS constraint_id,
            description
        FROM constraint
        WHERE ->restricts->feature CONTAINS $parent.id
    ) AS restrictions
FROM feature
WHERE id IN $feature_ids;
```

Or, depending on the actual stored direction, use edge table queries:

```sql id="rh3qx6"
SELECT
    out.id() AS feature_id,
    in.id() AS constraint_id
FROM restricts
WHERE out IN $feature_ids;
```

Then materialize/group in generated C# if that is simpler. Do not be religious here. If SurrealQL can return the shape cleanly, let it. If the query gets grotesque, return flat rows and group client-side. The rule is: **database filters, narrows, traverses; C# may shape small result sets.**

## I’d model this as three query types

### 1. Node query

```csharp id="ibcf4r"
workspace.Query.UserStories
    .Where(...)
    .Select(...)
```

For table-first reads.

### 2. Edge query

```csharp id="epmj26"
workspace.Query.Edges.Concerns
    .From<Issue>()
    .To<UserStory>()
    .Where(...)
    .SelectPairs()
```

For “subset of edges” reads.

### 3. Path query

```csharp id="sy2rtv"
workspace.Query.UserStories
    .Path()
    .Incoming<Concerns, Issue>()
    .Incoming<InformedBy, Finding>()
    .Where(FindingQ.Kind.Eq(FindingKind.Ambiguity))
    .ReturnStart()
```

For “things connected to things connected to things”.

That third one is the powerful one, but also easiest to overcomplicate. Start with generated named methods for your common paths, then expose raw path composition later.

## For “all user stories that may have ambiguity to resolve”

I’d want to be able to write one of these:

```csharp id="kb53qf"
var stories = await workspace.Query.UserStories
    .WithUnresolvedAmbiguity()
    .Select(UserStoryProjections.AmbiguityQueueItem)
    .ExecuteAsync(db, ct);
```

And maybe the explicit equivalent:

```csharp id="8xsd36"
var stories = await workspace.Query.UserStories
    .WhereExistsIncoming<Concerns, Issue>(
        IssueQ.Disposition.In(IssueDisposition.Open, IssueDisposition.Acknowledged)
            .AndExistsIncoming<InformedBy, Finding>(
                FindingQ.Kind.Eq(FindingKind.Ambiguity)))
    .Select(UserStoryProjections.AmbiguityQueueItem)
    .ExecuteAsync(db, ct);
```

The first is what you actually want to use. The second is the lower-level construct the generator/runtime can support.

## My strong recommendation

Make **edge subsets** a first-class generated concept:

```csharp id="gv7b1p"
workspace.Query.Edges.Restricts
workspace.Query.Edges.Concerns
workspace.Query.Edges.InformedBy
```

Then add fluent node shortcuts on top:

```csharp id="xsxgh9"
UserStories.WithUnresolvedAmbiguity()
Features.RestrictedByConstraints()
Issues.ForDesignNode(...)
```

That gives you both:

```text id="2cdy4t"
precise graph-query power
```

and

```text id="me5x4n"
nice domain language
```

The wrong move would be making everything start from `workspace.Query.TableName`. Your actual mental model is often **relationship-first**. Surface should respect that.
