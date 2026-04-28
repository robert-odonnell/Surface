namespace Surface.Generator.Model;

/// <summary>
/// A computed aggregate: the root entity and every entity reachable from it via
/// <c>[Children]</c>. Reference targets are NOT considered owned (they're loaded with
/// the aggregate but not aggregate-members) — that's what keeps shared records like
/// <c>Details</c> out of any specific aggregate.
/// <para>
/// Aggregates form the load and write-coordination units of the runtime. Conflicts
/// (an entity reachable from 2+ roots) surface as the CG011 diagnostic.
/// </para>
/// </summary>
public sealed record AggregateModel(
    string RootFullName,
    EquatableArray<string> MemberFullNames);
