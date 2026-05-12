using System.Collections.Immutable;
using Disruptor.Surface.Generator.Emit;
using Disruptor.Surface.Generator.Model;

namespace Disruptor.Surface.Generator.Pipeline;

/// <summary>
/// Final pipeline stage. Takes the raw collected sets of tables and relation kinds from
/// the per-symbol extractors and produces a <see cref="ModelGraph"/>:
/// <list type="bullet">
///   <item>Rewrites every <see cref="TypeRef"/> so <see cref="TypeRef.IsTableType"/> is
///         populated against the full table set (catches forward references the per-symbol
///         extractor couldn't see).</item>
///   <item>Computes per-relation-kind <see cref="RelationUnion"/> sets — the source set
///         (forward attribute holders) and target set (inverse attribute holders) for
///         each forward kind. Multi-member unions become marker interfaces emitted by
///         <see cref="Emit.UnionInterfaceEmitter"/>.</item>
///   <item>Leaves forward kinds alone — the <see cref="ModelGraph"/> computes pairs on
///         demand from the inverse kinds' <c>PairedForwardFullName</c> field.</item>
/// </list>
/// </summary>
internal static class RelationLinker
{
    public static ModelGraph Build(
        ImmutableArray<TableModel> tables,
        ImmutableArray<RelationKindModel> forwardKinds,
        ImmutableArray<RelationKindModel> inverseKinds,
        ImmutableArray<CompositionRootModel> compositionRoots,
        ImmutableArray<RelationVariantModel> relationVariants,
        ImmutableArray<UnionInterfaceCandidate> unionInterfaceCandidates,
        ImmutableArray<UnionMembershipCandidate> unionMembershipCandidates,
        ImmutableArray<SharedShapeInterfaceCandidate> sharedShapeCandidates)
    {
        var tableFullNames = new HashSet<string>();
        foreach (var t in tables)
        {
            tableFullNames.Add(t.FullName);
        }

        var linkedTables = ImmutableArray.CreateBuilder<TableModel>(tables.Length);
        foreach (var table in tables)
        {
            linkedTables.Add(RewriteTable(table, tableFullNames));
        }

        var linked = linkedTables.ToImmutable();

        var combinedKinds = ImmutableArray.CreateBuilder<RelationKindModel>(forwardKinds.Length + inverseKinds.Length);
        combinedKinds.AddRange(forwardKinds);
        combinedKinds.AddRange(inverseKinds);

        var linkedVariants = ImmutableArray.CreateBuilder<RelationVariantModel>(relationVariants.Length);
        foreach (var variant in relationVariants)
        {
            linkedVariants.Add(RewriteVariant(variant, tableFullNames));
        }
        var rewrittenVariants = linkedVariants.ToImmutable();

        // Variants whose own annotated members are empty get In / Out / Id / Payload
        // lifted from a matching annotated shared-shape interface (preview.56). Variants
        // whose In or Out remain null after the lift attempt — either because no matching
        // shared-shape interface is annotated, or the variant implements multiple
        // annotated shared-shape interfaces — are dropped silently here, the same fail-
        // soft contract the extractor used to enforce by returning null pre-lift.
        var liftedVariants = LiftVariantsFromSharedShape(rewrittenVariants, sharedShapeCandidates, tableFullNames);

        var unions = ComputeUnions(linked, forwardKinds, inverseKinds);
        var unionEndpoints = ComputeUnionEndpoints(linked, unionInterfaceCandidates, unionMembershipCandidates);
        var sharedShapes = ComputeSharedShapes(sharedShapeCandidates, liftedVariants, forwardKinds, inverseKinds);
        var (aggregates, conflicts) = ComputeAggregates(linked);
        var cascadeCycles = ComputeCascadeCycles(linked);

        return new ModelGraph(
            Tables: linked,
            RelationKinds: combinedKinds.ToImmutable(),
            RelationVariants: new EquatableArray<RelationVariantModel>(liftedVariants),
            Unions: new EquatableArray<RelationUnion>(unions),
            UnionEndpoints: new EquatableArray<UnionEndpointModel>(unionEndpoints),
            SharedShapes: new EquatableArray<SharedShapeModel>(sharedShapes),
            Aggregates: new EquatableArray<AggregateModel>(aggregates),
            AggregateConflicts: new EquatableArray<string>(conflicts),
            CascadeCycles: new EquatableArray<string>(cascadeCycles),
            CompositionRoots: new EquatableArray<CompositionRootModel>(compositionRoots));
    }

    /// <summary>
    /// preview.56 — fills in <see cref="RelationVariantModel.In"/> / <see cref="RelationVariantModel.Out"/> /
    /// <see cref="RelationVariantModel.Id"/> / <see cref="RelationVariantModel.PayloadProperties"/>
    /// from a matching annotated shared-shape interface candidate when the variant
    /// declares zero own annotated members. The variant's
    /// <see cref="RelationVariantModel.ImplementedInterfaceFullNames"/> are matched against
    /// candidates carrying lifted props (i.e. interfaces whose own members carry
    /// <c>[In]</c> / <c>[Out]</c> / <c>[Property]</c> / <c>[Id]</c>); a single match wins
    /// and contributes its lifted shape. Multiple annotated matches make the lift
    /// ambiguous; the variant is dropped to keep the contract one-source-of-truth.
    /// <para>
    /// Variants that don't need a lift (their own annotated members already produced a
    /// non-null In/Out at extraction time) pass through unchanged. The result is
    /// emit-ready: every returned variant is guaranteed to have non-null In and Out.
    /// </para>
    /// </summary>
    private static ImmutableArray<RelationVariantModel> LiftVariantsFromSharedShape(
        ImmutableArray<RelationVariantModel> variants,
        ImmutableArray<SharedShapeInterfaceCandidate> candidates,
        HashSet<string> tableFullNames)
    {
        if (variants.Length == 0)
        {
            return variants;
        }

        // Index candidates by FQN so the per-variant interface walk is O(1) per base.
        // Only annotated candidates participate — a candidate whose interface members
        // carry no model attributes can't supply a lift, and falsely "matching" against
        // it would make the per-variant ambiguity check fire spuriously.
        var annotatedByFqn = new Dictionary<string, SharedShapeInterfaceCandidate>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (c.LiftedIn is not null || c.LiftedOut is not null || c.LiftedId is not null || c.LiftedPayload.Count > 0)
            {
                annotatedByFqn[c.InterfaceFullName] = c;
            }
        }

        var builder = ImmutableArray.CreateBuilder<RelationVariantModel>(variants.Length);
        foreach (var variant in variants)
        {
            // Already populated — variant declared its own [In]/[Out]/[Property] members
            // and the extractor produced a fully-shaped model. Pass through; the lift
            // path is opt-in via "empty body, base list points at an annotated interface".
            if (variant.In is not null && variant.Out is not null)
            {
                builder.Add(variant);
                continue;
            }

            // Find every annotated candidate the variant lists in its base chain. Multiple
            // annotated matches make the lift ambiguous (which interface's payload wins?);
            // drop the variant rather than picking one arbitrarily. A single match is the
            // contract: that interface contributes In/Out/Id/Payload.
            SharedShapeInterfaceCandidate? lift = null;
            var ambiguous = false;
            foreach (var ifaceFqn in variant.ImplementedInterfaceFullNames)
            {
                if (!annotatedByFqn.TryGetValue(ifaceFqn, out var candidate))
                {
                    continue;
                }
                if (lift is null)
                {
                    lift = candidate;
                }
                else
                {
                    ambiguous = true;
                    break;
                }
            }

            if (ambiguous || lift is null)
            {
                // No clear lift source — silently drop, matching the existing fail-soft
                // contract the extractor used when In/Out couldn't be resolved.
                continue;
            }

            // Lifted props originate on interface members that don't see the same
            // type-rewrite pass linkedTables got. Re-run RewriteType so any TypeRef
            // pointing at a [Table] gets IsTableType=true (the rewrite knows the table
            // catalogue we built earlier in Build).
            var rewrittenIn = lift.LiftedIn is null
                ? null
                : lift.LiftedIn with { Type = RewriteType(lift.LiftedIn.Type, tableFullNames) };
            var rewrittenOut = lift.LiftedOut is null
                ? null
                : lift.LiftedOut with { Type = RewriteType(lift.LiftedOut.Type, tableFullNames) };
            var rewrittenId = lift.LiftedId is null
                ? null
                : lift.LiftedId with { Type = RewriteType(lift.LiftedId.Type, tableFullNames) };

            var rewrittenPayload = ImmutableArray.CreateBuilder<RelationVariantPropertyModel>(lift.LiftedPayload.Count);
            foreach (var p in lift.LiftedPayload)
            {
                rewrittenPayload.Add(p with { Type = RewriteType(p.Type, tableFullNames) });
            }

            // Lift wins only when both endpoints are present. A half-annotated interface
            // (e.g. [In] declared but [Out] missing) leaves the variant unsalvageable.
            if (rewrittenIn is null || rewrittenOut is null)
            {
                continue;
            }

            builder.Add(variant with
            {
                In = rewrittenIn,
                Out = rewrittenOut,
                Id = rewrittenId ?? variant.Id,
                PayloadProperties = new EquatableArray<RelationVariantPropertyModel>(rewrittenPayload.ToImmutable()),
            });
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Per shared-shape interface candidate, collect every variant that lists the
    /// interface in its <see cref="RelationVariantModel.ImplementedInterfaceFullNames"/>.
    /// Each enrolled variant contributes a <see cref="SharedShapeVariantBinding"/>
    /// carrying the variant FQN, the per-kind marker class FQN (the <c>TKind</c>
    /// argument the emitted factory will switch on), and the edge name (for SurrealQL
    /// generation in the query terminals).
    /// <para>
    /// Source / target endpoint type FQNs are the common type across all member variants.
    /// When variants disagree, both fields are left <c>null</c>; the query terminals are
    /// then suppressed and CG034 surfaces the disagreement to the user.
    /// </para>
    /// </summary>
    private static List<SharedShapeModel> ComputeSharedShapes(
        ImmutableArray<SharedShapeInterfaceCandidate> candidates,
        ImmutableArray<RelationVariantModel> variants,
        ImmutableArray<RelationKindModel> forwardKinds,
        ImmutableArray<RelationKindModel> inverseKinds)
    {
        if (candidates.Length == 0)
        {
            return [];
        }

        // Index forward kinds by FQN — we look up the variant's kind attribute (which
        // may be the forward attribute or the inverse) and walk to the forward to get
        // the kind marker name + edge name.
        var forwardByFqn = new Dictionary<string, RelationKindModel>(StringComparer.Ordinal);
        foreach (var k in forwardKinds)
        {
            forwardByFqn[k.FullName] = k;
        }
        var forwardForInverseByFqn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var inv in inverseKinds)
        {
            if (inv.PairedForwardFullName is { } paired)
            {
                forwardForInverseByFqn[inv.FullName] = paired;
            }
        }

        var result = new List<SharedShapeModel>();
        foreach (var candidate in candidates)
        {
            var enrolled = new List<SharedShapeVariantBinding>();
            string? commonSource = null;
            string? commonTarget = null;
            var sourceConsistent = true;
            var targetConsistent = true;

            foreach (var variant in variants)
            {
                if (!variant.ImplementedInterfaceFullNames.Any(i => i == candidate.InterfaceFullName))
                {
                    continue;
                }

                // Resolve to the forward kind so we know the marker class FQN + edge name.
                string? forwardFqn = forwardByFqn.ContainsKey(variant.KindAttributeFqn)
                    ? variant.KindAttributeFqn
                    : forwardForInverseByFqn.TryGetValue(variant.KindAttributeFqn, out var pf) ? pf : null;
                if (forwardFqn is null || !forwardByFqn.TryGetValue(forwardFqn, out var forward))
                {
                    continue;
                }

                var markerName = SurrealNaming.StripAttributeSuffix(forward.Name);
                var markerFqn = string.IsNullOrEmpty(forward.Namespace)
                    ? $"global::{markerName}"
                    : $"global::{forward.Namespace}.{markerName}";
                var edgeName = SurrealNaming.ToEdgeName(forward.Name);

                enrolled.Add(new SharedShapeVariantBinding(
                    VariantFullName: variant.FullName,
                    KindMarkerFullName: markerFqn,
                    EdgeName: edgeName));

                // Track common endpoint types — null sentinel means "no expectation
                // yet", first variant pins, subsequent variants must agree. The
                // LiftVariantsFromSharedShape pass guarantees a non-null In/Out by the
                // time we reach this point; the defensive guard skips any variant that
                // somehow slipped through with null endpoints rather than NRE-ing here.
                if (variant.In is null || variant.Out is null)
                {
                    continue;
                }
                var srcFqn = variant.In.Type.FullyQualifiedName;
                var tgtFqn = variant.Out.Type.FullyQualifiedName;

                if (commonSource is null && sourceConsistent && enrolled.Count == 1)
                {
                    commonSource = srcFqn;
                }
                else if (commonSource is not null && !string.Equals(commonSource, srcFqn, StringComparison.Ordinal))
                {
                    sourceConsistent = false;
                    commonSource = null;
                }

                if (commonTarget is null && targetConsistent && enrolled.Count == 1)
                {
                    commonTarget = tgtFqn;
                }
                else if (commonTarget is not null && !string.Equals(commonTarget, tgtFqn, StringComparison.Ordinal))
                {
                    targetConsistent = false;
                    commonTarget = null;
                }
            }

            enrolled.Sort((a, b) => StringComparer.Ordinal.Compare(a.VariantFullName, b.VariantFullName));

            result.Add(new SharedShapeModel(
                InterfaceFullName: candidate.InterfaceFullName,
                Namespace: candidate.Namespace,
                Name: candidate.Name,
                IsPartial: candidate.IsPartial,
                DeclaredAccessibility: candidate.DeclaredAccessibility,
                SourceTypeFullyQualifiedName: sourceConsistent ? commonSource : null,
                TargetTypeFullyQualifiedName: targetConsistent ? commonTarget : null,
                Variants: new EquatableArray<SharedShapeVariantBinding>(enrolled)));
        }

        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.InterfaceFullName, b.InterfaceFullName));
        return result;
    }

    /// <summary>
    /// Combines union-interface candidates (interfaces attributed with anything deriving
    /// from <c>In&lt;TKind&gt;</c> / <c>Out&lt;TKind&gt;</c>) with membership candidates
    /// (partial <c>I{Name}RecordId</c> decls extending other interfaces) into final
    /// <see cref="UnionEndpointModel"/>s. For each membership candidate, each base FQN is
    /// checked against the known union interfaces; matches enrol the marker's table in
    /// that union. The marker→table resolution strips the <c>I</c>-prefix and
    /// <c>RecordId</c>-suffix and looks up the resulting simple name against the table
    /// catalogue, preferring same-namespace matches.
    /// </summary>
    private static List<UnionEndpointModel> ComputeUnionEndpoints(
        ImmutableArray<TableModel> tables,
        ImmutableArray<UnionInterfaceCandidate> unionInterfaceCandidates,
        ImmutableArray<UnionMembershipCandidate> unionMembershipCandidates)
    {
        if (unionInterfaceCandidates.Length == 0)
        {
            return [];
        }

        // Per-interface accumulator. Keyed by interface FQN; value is the interface
        // candidate + sorted set of member table FQNs we've matched so far.
        var byInterface = new Dictionary<string, (UnionInterfaceCandidate Candidate, SortedSet<string> Members)>(StringComparer.Ordinal);
        foreach (var c in unionInterfaceCandidates)
        {
            // Dedupe: same interface attributed twice (rare; harmless) collapses to one
            // entry. The first wins; later ones contribute nothing new.
            if (!byInterface.ContainsKey(c.InterfaceFullName))
            {
                byInterface[c.InterfaceFullName] = (c, new SortedSet<string>(StringComparer.Ordinal));
            }
        }

        foreach (var membership in unionMembershipCandidates)
        {
            var tableFqn = ResolveMarkerToTable(membership.MarkerInterfaceFullName, tables);
            if (tableFqn is null)
            {
                continue;
            }

            foreach (var baseFqn in membership.BaseFullNames)
            {
                if (byInterface.TryGetValue(baseFqn, out var entry))
                {
                    entry.Members.Add(tableFqn);
                }
            }
        }

        var result = new List<UnionEndpointModel>();
        foreach (var entry in byInterface.Values)
        {
            result.Add(new UnionEndpointModel(
                InterfaceFullName: entry.Candidate.InterfaceFullName,
                KindFullName: entry.Candidate.KindFullName,
                Direction: entry.Candidate.Direction,
                MemberTableFullNames: new EquatableArray<string>(entry.Members.ToList())));
        }

        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.InterfaceFullName, b.InterfaceFullName));
        return result;
    }

    /// <summary>
    /// Resolves a per-table marker interface FQN (e.g. <c>M.IConstraintRecordId</c>) to
    /// the FQN of the <c>[Table]</c> it markers. Strips the leading <c>I</c> and trailing
    /// <c>RecordId</c> from the simple name and prefers a same-namespace table match;
    /// falls back to the first cross-namespace match by simple name. Returns null when
    /// no table matches the stripped name.
    /// </summary>
    private static string? ResolveMarkerToTable(string markerFullName, ImmutableArray<TableModel> tables)
    {
        var lastDot = markerFullName.LastIndexOf('.');
        var markerNamespace = lastDot >= 0 ? markerFullName[..lastDot] : string.Empty;
        var markerSimpleName = lastDot >= 0 ? markerFullName[(lastDot + 1)..] : markerFullName;

        if (!markerSimpleName.StartsWith("I")
            || !markerSimpleName.EndsWith("RecordId")
            || markerSimpleName.Length <= "IRecordId".Length)
        {
            return null;
        }

        var tableSimpleName = markerSimpleName[1..^"RecordId".Length];

        TableModel? sameNamespaceMatch = null;
        TableModel? anyMatch = null;
        foreach (var t in tables)
        {
            if (t.Name != tableSimpleName)
            {
                continue;
            }

            if (string.Equals(t.Namespace, markerNamespace, StringComparison.Ordinal))
            {
                sameNamespaceMatch = t;
                break;
            }

            anyMatch ??= t;
        }

        return (sameNamespaceMatch ?? anyMatch)?.FullName;
    }

    private static RelationVariantModel RewriteVariant(RelationVariantModel variant, HashSet<string> tableFullNames)
    {
        var rewrittenPayload = ImmutableArray.CreateBuilder<RelationVariantPropertyModel>(variant.PayloadProperties.Count);
        foreach (var p in variant.PayloadProperties)
        {
            rewrittenPayload.Add(p with { Type = RewriteType(p.Type, tableFullNames) });
        }

        // In / Out are nullable for variants awaiting an interface lift (preview.56).
        // Rewrite type-refs only when populated; the lift pass fills the null slots from
        // a matching annotated shared-shape interface candidate before emit. Once lifted,
        // the lifted props get their own type-rewrite pass via RewriteLiftedProperty.
        return variant with
        {
            In = variant.In is null ? null : variant.In with { Type = RewriteType(variant.In.Type, tableFullNames) },
            Out = variant.Out is null ? null : variant.Out with { Type = RewriteType(variant.Out.Type, tableFullNames) },
            Id = variant.Id is null ? null : variant.Id with { Type = RewriteType(variant.Id.Type, tableFullNames) },
            PayloadProperties = new EquatableArray<RelationVariantPropertyModel>(rewrittenPayload.ToImmutable()),
        };
    }

    /// <summary>
    /// Detects cycles in the <c>[Reference, Cascade]</c> graph. Each table is a node;
    /// an edge runs from a referencer table to a referenced table whenever the reference
    /// is declared with cascade behavior. A cycle composed entirely of cascade edges is
    /// invalid (CG014) — cascading a delete around the loop would loop forever.
    /// </summary>
    private static List<string> ComputeCascadeCycles(ImmutableArray<TableModel> tables)
    {
        var byFullName = new Dictionary<string, TableModel>(StringComparer.Ordinal);
        foreach (var t in tables)
        {
            byFullName[t.FullName] = t;
        }

        var adjacency = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in tables)
        {
            var edges = new List<string>();
            foreach (var p in t.Properties)
            {
                if (!p.Kinds.HasFlag(PropertyKind.Reference))
                {
                    continue;
                }

                if (p.ReferenceDelete != ReferenceDeletePolicy.Cascade)
                {
                    continue;
                }

                var targetFullName = StripGlobalAndNullable(p.Type.FullyQualifiedName);
                if (byFullName.ContainsKey(targetFullName))
                {
                    edges.Add(targetFullName);
                }
            }
            if (edges.Count > 0)
            {
                adjacency[t.FullName] = edges;
            }
        }

        var cycles = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack   = new HashSet<string>(StringComparer.Ordinal);
        var path    = new List<string>();

        foreach (var start in adjacency.Keys)
        {
            if (visited.Contains(start))
            {
                continue;
            }

            Dfs(start);
        }

        return cycles;

        void Dfs(string node)
        {
            visited.Add(node);
            stack.Add(node);
            path.Add(node);

            if (adjacency.TryGetValue(node, out var edges))
            {
                foreach (var next in edges)
                {
                    if (stack.Contains(next))
                    {
                        var startIdx = path.IndexOf(next);
                        var loop = path.GetRange(startIdx, path.Count - startIdx);
                        loop.Add(next);
                        cycles.Add(string.Join(" → ", loop));
                    }
                    else if (!visited.Contains(next))
                    {
                        Dfs(next);
                    }
                }
            }

            stack.Remove(node);
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>
    /// For each <c>[AggregateRoot]</c> table, walks <c>[Children]</c> reachability to
    /// compute the aggregate's member set. Detects entities reachable from 2+ roots and
    /// returns them as conflict descriptors of the form
    /// <c>"Member|Root1,Root2,..."</c> (rendered as CG011 by <see cref="ModelGenerator"/>).
    /// <c>[Reference]</c> targets are intentionally NOT walked — they're shared records
    /// loaded with whatever needs them, not aggregate-owned (see Details).
    /// </summary>
    private static (List<AggregateModel> Aggregates, List<string> Conflicts) ComputeAggregates(ImmutableArray<TableModel> tables)
    {
        var byFullName = new Dictionary<string, TableModel>();
        foreach (var t in tables)
        {
            byFullName[t.FullName] = t;
        }

        var aggregates = new List<AggregateModel>();
        var membershipReverse = new Dictionary<string, List<string>>(); // member → list of roots

        foreach (var root in tables)
        {
            if (!root.IsAggregateRoot)
            {
                continue;
            }

            var members = new SortedSet<string>(StringComparer.Ordinal);
            WalkChildren(root, byFullName, members);
            aggregates.Add(new AggregateModel(root.FullName, new EquatableArray<string>(members.ToList())));

            foreach (var m in members)
            {
                if (!membershipReverse.TryGetValue(m, out var roots))
                {
                    roots = [];
                    membershipReverse[m] = roots;
                }
                roots.Add(root.FullName);
            }
        }

        var conflicts = new List<string>();
        foreach (var kv in membershipReverse)
        {
            if (kv.Value.Count >= 2)
            {
                kv.Value.Sort(StringComparer.Ordinal);
                conflicts.Add($"{kv.Key}|{string.Join(",", kv.Value)}");
            }
        }
        conflicts.Sort(StringComparer.Ordinal);

        return (aggregates, conflicts);
    }

    private static void WalkChildren(TableModel current, Dictionary<string, TableModel> byFullName, SortedSet<string> accumulator)
    {
        if (!accumulator.Add(current.FullName))
        {
            return;
        }

        foreach (var p in current.Properties)
        {
            if (!p.Kinds.HasFlag(PropertyKind.Children))
            {
                continue;
            }

            VisitTypeChildren(p.Type, byFullName, accumulator);
        }
    }

    private static void VisitTypeChildren(TypeRef type, Dictionary<string, TableModel> byFullName, SortedSet<string> accumulator)
    {
        var elementType = type.ElementType ?? type;
        if (!elementType.IsTableType)
        {
            return;
        }

        var childFullName = StripGlobalAndNullable(elementType.FullyQualifiedName);
        if (byFullName.TryGetValue(childFullName, out var childTable))
        {
            WalkChildren(childTable, byFullName, accumulator);
        }
    }

    private static string StripGlobalAndNullable(string fqn)
    {
        const string prefix = "global::";
        if (fqn.StartsWith(prefix))
        {
            fqn = fqn[prefix.Length..];
        }

        if (fqn.EndsWith("?"))
        {
            fqn = fqn[..^1];
        }

        return fqn;
    }

    private static TableModel RewriteTable(TableModel table, HashSet<string> tableFullNames)
    {
        var props = ImmutableArray.CreateBuilder<PropertyModel>(table.Properties.Count);
        foreach (var p in table.Properties)
        {
            props.Add(p with { Type = RewriteType(p.Type, tableFullNames) });
        }

        return table with
        {
            Properties = props.ToImmutable(),
        };
    }

    private static TypeRef RewriteType(TypeRef type, HashSet<string> tableFullNames)
    {
        var naked = Strip(type.FullyQualifiedName);
        var isTable = type.IsTableType || tableFullNames.Contains(naked);

        var rewrittenArgs = new List<TypeRef>(type.TypeArguments.Count);
        foreach (var ta in type.TypeArguments)
        {
            rewrittenArgs.Add(RewriteType(ta, tableFullNames));
        }

        var rewrittenElement = type.ElementType is null
            ? null
            : RewriteType(type.ElementType, tableFullNames);

        return type with
        {
            IsTableType = isTable,
            ElementType = rewrittenElement,
            TypeArguments = new EquatableArray<TypeRef>(rewrittenArgs),
        };
    }

    private static string Strip(string fqn)
    {
        const string prefix = "global::";
        if (fqn.StartsWith(prefix))
        {
            fqn = fqn[prefix.Length..];
        }

        var angle = fqn.IndexOf('<');
        if (angle >= 0)
        {
            fqn = fqn[..angle];
        }

        var question = fqn.IndexOf('?');
        if (question >= 0)
        {
            fqn = fqn[..question];
        }

        return fqn;
    }

    /// <summary>
    /// Walks every table's relation-bearing members to populate per-forward-kind source
    /// (forward attribute holders) and target (inverse attribute holders) sets. Each set
    /// of size &gt;= 2 becomes a <see cref="RelationUnion"/>; single-member sides are
    /// dropped because the concrete entity type covers them with no extra interface noise.
    /// </summary>
    private static List<RelationUnion> ComputeUnions(
        ImmutableArray<TableModel> tables,
        ImmutableArray<RelationKindModel> forwardKinds,
        ImmutableArray<RelationKindModel> inverseKinds)
    {
        var unions = new List<RelationUnion>();
        var inverseByFull = new Dictionary<string, RelationKindModel>();
        foreach (var inv in inverseKinds)
        {
            inverseByFull[inv.FullName] = inv;
        }

        foreach (var fwd in forwardKinds)
        {
            var sources = new SortedSet<string>(StringComparer.Ordinal);
            var targets = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var table in tables)
            {
                foreach (var p in table.Properties)
                {
                    if (p.RelationRole == RelationRole.ForwardRelation && p.RelationKindFullName == fwd.FullName)
                    {
                        sources.Add(table.FullName);
                    }
                    else if (p.RelationRole == RelationRole.InverseRelation
                             && p.RelationKindFullName is { } pfn
                             && inverseByFull.TryGetValue(pfn, out var pInv)
                             && pInv.PairedForwardFullName == fwd.FullName)
                    {
                        targets.Add(table.FullName);
                    }
                }
            }

            if (sources.Count >= 2)
            {
                // Forward attribute names are 3rd-person verbs (Restricts, References, …);
                // singularising before prefixing yields the noun-ish marker the user wants
                // (IRestrict, not IRestricts). Id interface mirrors the entity interface
                // with a trailing Id (IRestrict + IRestrictId).
                var baseName = SurrealNaming.Singularize(StripAttribute(fwd.Name));
                var ifaceName = $"I{baseName}";
                var idIfaceName = $"I{baseName}Id";
                unions.Add(new RelationUnion(
                    ForwardKindFullName: fwd.FullName,
                    InterfaceName: ifaceName,
                    InterfaceFullName: QualifyName(fwd.Namespace, ifaceName),
                    IdInterfaceName: idIfaceName,
                    IdInterfaceFullName: QualifyName(fwd.Namespace, idIfaceName),
                    Namespace: fwd.Namespace,
                    Direction: UnionDirection.Source,
                    MemberFullNames: new EquatableArray<string>(sources.ToList())));
            }

            if (targets.Count >= 2)
            {
                // Target interface is named after the inverse attribute — "Design IS RestrictedBy"
                // reads naturally and matches the schema's own relation language. Id interface
                // appends Id (IRestrictedBy + IRestrictedById).
                var pairedInverse = inverseKinds.FirstOrDefault(k => k.PairedForwardFullName == fwd.FullName);
                if (pairedInverse is null)
                {
                    continue;
                }

                var baseName = StripAttribute(pairedInverse.Name);
                var ifaceName = $"I{baseName}";
                var idIfaceName = $"I{baseName}Id";
                unions.Add(new RelationUnion(
                    ForwardKindFullName: fwd.FullName,
                    InterfaceName: ifaceName,
                    InterfaceFullName: QualifyName(pairedInverse.Namespace, ifaceName),
                    IdInterfaceName: idIfaceName,
                    IdInterfaceFullName: QualifyName(pairedInverse.Namespace, idIfaceName),
                    Namespace: pairedInverse.Namespace,
                    Direction: UnionDirection.Target,
                    MemberFullNames: new EquatableArray<string>(targets.ToList())));
            }
        }

        return unions;
    }

    private static string StripAttribute(string name)
        => name.EndsWith("Attribute") ? name[..^"Attribute".Length] : name;

    private static string QualifyName(string ns, string name)
        => string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
}
