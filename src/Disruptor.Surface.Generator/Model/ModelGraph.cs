namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// The fully-linked model handed to the emit stage. Tables have had their <c>TypeRef</c>s
/// flagged with <see cref="TypeRef.IsTableType"/> where applicable, each relation kind
/// knows about its paired counterpart, and per-kind union sets have been computed.
/// </summary>
/// <remarks>
/// Keep this record body free of mutable fields — the synthesised record equality includes
/// every instance field, so a lazy cache here would bust the incremental generator's
/// deduplication. Lookups are small (tens of entries in practice) and cheap to recompute
/// at the emit site; use <see cref="BuildTableIndex"/> / <see cref="BuildKindIndex"/>
/// there instead of caching on the graph.
/// </remarks>
public sealed record ModelGraph(
    EquatableArray<TableModel> Tables,
    EquatableArray<RelationKindModel> RelationKinds,
    EquatableArray<RelationUnion> Unions,
    EquatableArray<AggregateModel> Aggregates,
    EquatableArray<string> AggregateConflicts,
    EquatableArray<string> CascadeCycles,
    EquatableArray<CompositionRootModel> CompositionRoots)
{
    public static readonly ModelGraph Empty = new(
        EquatableArray<TableModel>.Empty,
        EquatableArray<RelationKindModel>.Empty,
        EquatableArray<RelationUnion>.Empty,
        EquatableArray<AggregateModel>.Empty,
        EquatableArray<string>.Empty,
        EquatableArray<string>.Empty,
        EquatableArray<CompositionRootModel>.Empty);

    public IReadOnlyDictionary<string, TableModel> BuildTableIndex()
        => Tables.ToDictionary(t => t.FullName);

    public IReadOnlyDictionary<string, RelationKindModel> BuildKindIndex()
        => RelationKinds.ToDictionary(k => k.FullName);

    public RelationKindModel? FindKind(string? fullName)
    {
        if (fullName is null)
        {
            return null;
        }

        foreach (var kind in RelationKinds)
        {
            if (kind.FullName == fullName)
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>Given a forward kind, returns its paired inverse (if any).</summary>
    public RelationKindModel? PairedInverse(RelationKindModel forward)
        => RelationKinds.FirstOrDefault(k =>
            k.Direction == RelationDirection.Inverse && k.PairedForwardFullName == forward.FullName);

    /// <summary>All union interfaces this table is a member of (target side, source side, or both).</summary>
    public IEnumerable<RelationUnion> UnionsForTable(string tableFullName)
    {
        foreach (var union in Unions)
        {
            if (union.MemberFullNames.Any(m => m == tableFullName))
            {
                yield return union;
            }
        }
    }

    /// <summary>
    /// Resolves the C# type the source side of a relation should accept on its
    /// auto-emitted <c>Add{X}</c>/<c>Remove{X}</c> methods. Returns the union interface
    /// FQN when there are 2+ targets, the single target's FQN when there is exactly one,
    /// or <c>null</c> when the relation has no target. All returns are <c>global::</c>-prefixed.
    /// </summary>
    public string? TargetTypeFor(string forwardKindFullName)
    {
        foreach (var union in Unions)
        {
            if (union.Direction == UnionDirection.Target && union.ForwardKindFullName == forwardKindFullName)
            {
                return $"global::{union.InterfaceFullName}";
            }
        }

        var inverseKind = RelationKinds.FirstOrDefault(k =>
            k.Direction == RelationDirection.Inverse && k.PairedForwardFullName == forwardKindFullName);
        if (inverseKind is null)
        {
            return null;
        }

        return SingleTableWith(t => HasInverseAttribute(t, inverseKind.FullName));
    }

    /// <summary>
    /// Resolves the C# type the target side of a relation should accept on its
    /// auto-emitted <c>Add{X}</c>/<c>Remove{X}</c> methods (parameter is the source
    /// entity). Mirrors <see cref="TargetTypeFor"/>.
    /// </summary>
    public string? SourceTypeFor(string forwardKindFullName)
    {
        foreach (var union in Unions)
        {
            if (union.Direction == UnionDirection.Source && union.ForwardKindFullName == forwardKindFullName)
            {
                return $"global::{union.InterfaceFullName}";
            }
        }

        return SingleTableWith(t => HasForwardAttribute(t, forwardKindFullName));
    }

    private string? SingleTableWith(Func<TableModel, bool> predicate)
    {
        string? single = null;
        foreach (var table in Tables)
        {
            if (!predicate(table))
            {
                continue;
            }

            if (single is not null)
            {
                return null;
            }

            single = $"global::{table.FullName}";
        }
        return single;
    }

    private static bool HasForwardAttribute(TableModel table, string forwardKindFullName)
    {
        foreach (var p in table.Properties)
        {
            if (p.RelationRole == RelationRole.ForwardRelation && p.RelationKindFullName == forwardKindFullName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasInverseAttribute(TableModel table, string inverseKindFullName)
    {
        foreach (var p in table.Properties)
        {
            if (p.RelationRole == RelationRole.InverseRelation && p.RelationKindFullName == inverseKindFullName)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the aggregate root that owns <paramref name="tableFullName"/>, or null if
    /// the table is in no aggregate (e.g. a shared <c>[Reference]</c>-only entity like
    /// <c>Details</c>). The CG011 conflict check guarantees at most one root per entity.
    /// </summary>
    public string? AggregateRootOf(string tableFullName)
    {
        foreach (var agg in Aggregates)
        {
            if (agg.MemberFullNames.Any(m => m == tableFullName))
            {
                return agg.RootFullName;
            }
        }

        return null;
    }

    /// <summary>
    /// True iff the relation kind crosses aggregate boundaries — any pair of (source,
    /// target) where the two entities belong to different aggregate roots. Used to pick
    /// between the entity-typed and id-typed code paths in the generator.
    /// </summary>
    public bool IsCrossAggregate(string forwardKindFullName)
    {
        var sourceAggs = new HashSet<string>(StringComparer.Ordinal);
        var targetAggs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Tables)
        {
            if (HasForwardAttribute(t, forwardKindFullName))
            {
                var agg = AggregateRootOf(t.FullName);
                if (agg is not null)
                {
                    sourceAggs.Add(agg);
                }
            }
        }

        var inverseKind = RelationKinds.FirstOrDefault(k =>
            k.Direction == RelationDirection.Inverse && k.PairedForwardFullName == forwardKindFullName);
        if (inverseKind is not null)
        {
            foreach (var t in Tables)
            {
                if (HasInverseAttribute(t, inverseKind.FullName))
                {
                    var agg = AggregateRootOf(t.FullName);
                    if (agg is not null)
                    {
                        targetAggs.Add(agg);
                    }
                }
            }
        }

        // Cross when the two sides have any disjoint aggregates. If either side is empty
        // (no aggregate-owned participants) we treat as within — id-typing requires both
        // sides to be aggregate-owned to make sense.
        if (sourceAggs.Count == 0 || targetAggs.Count == 0)
        {
            return false;
        }

        foreach (var s in sourceAggs)
        {
            foreach (var t in targetAggs)
            {
                if (s != t)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
