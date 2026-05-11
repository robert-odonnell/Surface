using Disruptor.Surface.Generator.Model;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Emits the implementing half of every partial property and method on a <c>[Table]</c>
/// class. Property setters are pure backing-field assignments — no Session interaction,
/// no per-field dirty tracking. Save reads the entity's current state at dispatch time.
/// <list type="bullet">
///   <item>[Id] — lazy-cached field; defaults to <c>{Name}Id.New()</c> on first access.</item>
///   <item>[Property] — getter-only or get/set partial property; setter writes the
///         backing field, nothing else.</item>
///   <item>[Reference] — sync property over <c>_{name}</c> entity ref + <c>_{name}Id</c>
///         record id backing fields. Getter falls back to <c>Session.Get&lt;T&gt;(_id)</c>
///         when the entity ref isn't locally cached (covers "loaded as id only" + "user
///         loaded other aggregate separately"). Mandatory get-only references emit an
///         <c>OnCreate{Name}</c> partial hook plus an entry in <c>IEntity.Initialize</c>
///         that mints + assigns directly.</item>
///   <item>[Parent] — sync property; same shape as Reference, plus the setter cascade-
///         tracks the child into the parent's session via
///         <c>parent.Session.AdoptIfUnbound(this)</c> so <c>parent.Children</c> sees the
///         new child at Save time.</item>
///   <item>[Children] — sync getter-only partial property over
///         <c>Session.QueryChildren</c>.</item>
///   <item>Forward/inverse relation property — sync collection from
///         <c>Session.QueryOutgoing</c> / <c>QueryIncoming</c> for same-aggregate edges,
///         or <c>QueryRelatedIds</c> / <c>QueryInverseRelatedIds</c> for cross-aggregate
///         edges. Relation writes go through per-variant relation classes dispatched via
///         <c>Session.SaveAsync(variantInstance, tx)</c>.</item>
/// </list>
/// </summary>
internal static class PartialEmitter
{
    /// <summary>
    /// True when <paramref name="t"/> is one of the recognised element-collection shapes
    /// for a <c>[Property]</c> column: <c>IReadOnlyList&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>,
    /// or <c>List&lt;T&gt;</c>. Detection mirrors <c>TableExtractor.ResolveInlineMembers</c>.
    /// </summary>
    private static bool IsElementCollection(TypeRef t) =>
        t.MetadataName is "System.Collections.Generic.IReadOnlyList`1"
                       or "System.Collections.Generic.IList`1"
                       or "System.Collections.Generic.List`1";

    public static void Emit(SourceProductionContext spc, TableModel table, ModelGraph graph)
    {
        if (!table.IsPartial)
        {
            return;
        }

        // Empty annotated tables are legal — the id anchor is always emitted, so a
        // [Table] with no partial members still gets IEntity scaffolding (Bind, Initialize,
        // Hydrate, OnDeleting) and is fully Track-able / Load-able. Just an entity that
        // carries nothing but its identity.
        var partialProps = table.Properties.Where(p => p.IsPartial).ToArray();

        var writer = new CodeWriter().Header();
        using (writer.Namespace(table.Namespace))
        {
            var declarationParts = new List<string>();
            var access = FormatAccessibility(table.DeclaredAccessibility);
            if (!string.IsNullOrEmpty(access))
            {
                declarationParts.Add(access);
            }

            if (table.IsAbstract)
            {
                declarationParts.Add("abstract");
            }

            if (table.IsSealed)
            {
                declarationParts.Add("sealed");
            }

            var typeParameters = table.TypeParameters.Count > 0
                ? $"<{string.Join(", ", table.TypeParameters)}>"
                : string.Empty;
            declarationParts.Add($"partial class {table.Name}{typeParameters}");

            var baseTypes = new List<string> {
                Namespaces.EntityInterface };
            baseTypes.AddRange(graph.UnionsForTable(table.FullName).Select(union => $"global::{union.InterfaceFullName}"));
            var declaration = $"{string.Join(" ", declarationParts)} : {string.Join(", ", baseTypes)}";

            using (writer.Block(declaration))
            {
                WriteSessionPlumbing(writer);

                // The id anchor is emitted unconditionally — the runtime needs IEntity.Id to read
                // every entity's identity, and the typed {Name}Id struct is always emitted by
                // IdEmitter regardless of whether the user opted into a public-facing [Id] property.
                EmitIdAnchor(writer, table);

                var mandatoryRefs = new List<PropertyModel>();
                PropertyModel? parentProp = null;

                foreach (var t in partialProps)
                {
                    EmitProperty(writer, t, graph);

                    if (IsMandatoryReference(t))
                    {
                        mandatoryRefs.Add(t);
                    }
                    if (t.Kinds.HasFlag(PropertyKind.Parent))
                    {
                        parentProp = t;
                    }
                }

                // Initialize hook always emitted, even when empty — IEntity demands it and a
                // no-op for entities without mandatory refs is the cleanest path.
                EmitInitialize(writer, mandatoryRefs);

                // Hydrate hook — invoked by the per-aggregate loader on each loaded row.
                EmitHydrate(writer, table);

                // OnDeleting hook — invoked by Session.DeleteAsync(IEntity) before the entity's
                // own DELETE is dispatched. User can implement the simple-form partial method to
                // queue child deletes / clears.
                EmitOnDeleting(writer);

                // MarkAllSlicesLoaded — used by the legacy aggregate loader (after Hydrate) and by
                // SurrealSession.Track<T> (fresh-entity path) to declare every slice on this entity
                // is "loaded". The compiler-driven path marks slices selectively via the include
                // AST instead.
                EmitMarkAllSlicesLoaded(writer, table);

                // GetParentId — emitted iff the entity has a [Parent] property. Default-interface
                // returns null for tables without a parent. Used by SurrealSession.QueryChildren
                // to match a child against its parent owner.
                if (parentProp is not null)
                {
                    EmitGetParentId(writer, parentProp);
                }

                // EnumerateReferences + SetReferenceTo — used by DeleteAsync's pre-flight cascade
                // resolve. EnumerateReferences yields (snake_case field name, current target id)
                // per [Reference] / [Parent]. SetReferenceTo writes the matching nullable
                // [Reference] backing field for the Unset phase (non-nullable refs and [Parent]
                // are skipped since the schema emits REJECT for those). Default-interface
                // implementations on IEntity cover entities that have neither.
                var refLikeProps = table.Properties.Where(p =>
                    p.Kinds.HasFlag(PropertyKind.Reference) || p.Kinds.HasFlag(PropertyKind.Parent)).ToList();
                if (refLikeProps.Count > 0)
                {
                    EmitEnumerateReferences(writer, refLikeProps);

                    var unsetableProps = refLikeProps.Where(p =>
                        p.Kinds.HasFlag(PropertyKind.Reference) && p.Type.IsNullable).ToList();
                    if (unsetableProps.Count > 0)
                    {
                        EmitSetReferenceTo(writer, unsetableProps);
                    }
                }

                // SaveAsync — generator-emitted per-entity Save dispatch. Walks forward dependencies
                // (Reference / Parent) via backing fields, dispatches CREATE/UPDATE-with-CONTENT,
                // recurses into new children, dispatches new outgoing relations.
                EmitSaveAsync(writer, table);
            }
        }

        spc.AddSource(table.FileHintName, writer.ToSourceText());
    }

    // ──────────────────────────── per-table preamble ─────────────────────────

    /// <summary>
    /// Emits per-entity session plumbing: a private <c>_session</c> field, the explicit
    /// <c>IEntity.Session</c> getter (nullable), <c>IEntity.Bind</c> (one-shot setter for
    /// <c>_session</c>), the protected <c>Session</c> property for entity-body reads
    /// (throws when unbound — reading from an unbound entity is a programmer error), and
    /// the <c>__EnsureSliceLoaded</c> guard the navigable read paths use to enforce
    /// strict-with-escape. Delegates to <see cref="EntityEmitterCommon.WriteSessionPlumbing"/>
    /// so <see cref="RelationVariantEmitter"/> can reuse the same shape without drift.
    /// </summary>
    private static void WriteSessionPlumbing(CodeWriter writer)
        => EntityEmitterCommon.WriteSessionPlumbing(writer);

    // ──────────────────────────── property emission ──────────────────────────

    private static void EmitProperty(CodeWriter writer, PropertyModel p, ModelGraph graph)
    {
        if (p.RelationRole is RelationRole.ForwardRelation or RelationRole.InverseRelation)
        {
            EmitRelationProperty(writer, p, graph);
            return;
        }

        if (p.Kinds.HasFlag(PropertyKind.Id))
        {
            EmitIdProperty(writer, p);
        }
        else if (p.Kinds.HasFlag(PropertyKind.Property))
        {
            EmitPropertyMember(writer, p);
        }
        else if (p.Kinds.HasFlag(PropertyKind.Parent))
        {
            EmitParentProperty(writer, p);
        }
        else if (p.Kinds.HasFlag(PropertyKind.Children))
        {
            EmitChildrenProperty(writer, p);
        }
        else if (p.Kinds.HasFlag(PropertyKind.Reference))
        {
            EmitReferenceProperty(writer, p);
        }
        else
        {
            EmitFallbackProperty(writer, p);
        }
    }

    /// <summary>
    /// <c>[Property]</c> on a property declaration: element-collection columns
    /// (<c>IReadOnlyList&lt;T&gt;</c> / <c>IList&lt;T&gt;</c> / <c>List&lt;T&gt;</c>) go
    /// through <see cref="EmitElementCollectionProperty"/> (backing <c>List&lt;T&gt;</c>
    /// + Add/Remove/Clear helpers); plain scalars go through
    /// <see cref="EmitDataProperty"/> (pure backing field).
    /// </summary>
    private static void EmitPropertyMember(CodeWriter writer, PropertyModel p)
    {
        if (IsElementCollection(p.Type))
        {
            EmitElementCollectionProperty(writer, p);
        }
        else
        {
            EmitDataProperty(writer, p);
        }
    }

    /// <summary>
    /// Emits the backing <c>List&lt;T&gt;</c> field plus the partial property surface and
    /// generator-emitted Add/Remove/Clear helpers for an element-collection column.
    /// The user-facing property is exposed exactly as declared
    /// (<c>IReadOnlyList&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, or <c>List&lt;T&gt;</c>);
    /// the helpers mirror IList semantics regardless so the user has a uniform mutation
    /// surface even when the property is read-only.
    /// </summary>
    private static void EmitElementCollectionProperty(CodeWriter writer, PropertyModel p)
    {
        var declaredType = p.Type.FullyQualifiedName;
        var elementType = p.Type.TypeArguments.Count > 0
            ? p.Type.TypeArguments[0].FullyQualifiedName
            : "object";
        var listType = $"global::System.Collections.Generic.List<{elementType}>";
        var access = FormatAccessibility(p.DeclaredAccessibility);
        var backing = $"_{ToCamel(p.Name)}";
        var singular = SurrealNaming.Singularize(p.Name);

        writer.Line($"private readonly {listType} {backing} = new();");
        writer.Line($"{access} partial {declaredType} {p.Name} => {backing};");
        writer.Line($"public void Add{singular}({elementType} item) => {backing}.Add(item);");
        writer.Line($"public bool Remove{singular}({elementType} item) => {backing}.Remove(item);");
        writer.Line($"public void Clear{p.Name}() => {backing}.Clear();");
    }

    /// <summary>
    /// Emits the always-present id anchor: the private <c>_id</c> backing field plus the
    /// explicit <c>IEntity.Id</c> accessor. Both lazy-mint via <c>{Name}Id.New()</c> on
    /// first read; <c>Hydrate</c> writes the field directly when loading from the DB.
    /// The user's optional <c>[Id]</c>-tagged partial property (if declared) is emitted
    /// separately by <see cref="EmitIdProperty"/> as a delegate to this anchor.
    /// </summary>
    private static void EmitIdAnchor(CodeWriter writer, TableModel table)
    {
        var idType = $"global::{(string.IsNullOrEmpty(table.Namespace) ? table.Name : $"{table.Namespace}.{table.Name}")}Id";
        writer.Line($"private {idType}? _id;");
        writer.Line($"global::Disruptor.Surface.Runtime.RecordId {Namespaces.EntityInterface}.Id => _id ??= {idType}.New();");
    }

    /// <summary>
    /// Emits the user-facing <c>[Id]</c> partial property — a get/set delegate to the
    /// id anchor. Only called when the user has declared an <c>[Id]</c>-tagged partial
    /// property; without one, the entity simply has no public-facing Id surface (the
    /// anchor still exists internally).
    /// <para>
    /// The setter (when declared) refuses to mutate the anchor once the entity is bound
    /// to a session — by that point the entity lives in the session's identity map keyed
    /// on the current id, and silently overwriting it would corrupt every dict.
    /// </para>
    /// </summary>
    private static void EmitIdProperty(CodeWriter writer, PropertyModel p)
    {
        var idType = p.Type.FullyQualifiedName;
        var idArg = StripNullable(idType);
        var access = FormatAccessibility(p.DeclaredAccessibility);

        using (writer.Block($"{access} partial {idType} {p.Name}"))
        {
            writer.Line($"get => _id ??= {idArg}.New();");
            if (p.HasSetter)
            {
                using (writer.Block("set"))
                {
                    writer.Line("if (_session is not null)");
                    using (writer.Indent())
                    {
                        writer.Line("throw new global::System.InvalidOperationException(\"Cannot mutate Id after the entity is bound to a session.\");");
                    }

                    writer.Line("_id = value;");
                }
            }
        }
    }

    /// <summary>
    /// Pure backing-field property: <c>get => _name; set => _name = value;</c>. No
    /// Session call, no buffer, no command log entry. Save reads the backing field at
    /// dispatch time.
    /// </summary>
    private static void EmitDataProperty(CodeWriter writer, PropertyModel p)
    {
        var type = p.Type.FullyQualifiedName;
        var access = FormatAccessibility(p.DeclaredAccessibility);
        var backing = $"_{ToCamel(p.Name)}";

        // = default! silences CS8618 for non-nullable types; CS0649 covers the case where
        // the user declares no setter (read-only [Property] is legal). Reading before any
        // setter runs returns the type's default — the schema's DEFAULT clause is the
        // contract for what the database hands back, not something we synthesise.
        writer.Line("#pragma warning disable CS0649");
        writer.Line($"private {type} {backing} = default!;");
        writer.Line("#pragma warning restore CS0649");

        if (!p.HasSetter && !p.HasInitOnlySetter)
        {
            writer.Line($"{access} partial {type} {p.Name} => {backing};");
            return;
        }

        using (writer.Block($"{access} partial {type} {p.Name}"))
        {
            writer.Line($"get => {backing};");
            writer.Line($"{(p.HasInitOnlySetter ? "init" : "set")} => {backing} = value;");
        }
    }

    /// <summary>
    /// Sync property body for <c>[Parent]</c>:
    /// <list type="bullet">
    ///   <item>Two backing fields: <c>_{name}</c> caches the parent entity ref;
    ///         <c>_{name}Id</c> caches the parent's record id.</item>
    ///   <item>Getter: returns <c>_{name}</c> if cached; otherwise falls back to
    ///         <c>Session?.Get&lt;TParent&gt;(_{name}Id)</c> when an id is known.</item>
    ///   <item>Setter: stores both backing fields; if the assigned parent has a session
    ///         and <c>this</c> is unbound, calls
    ///         <c>parent.Session.AdoptIfUnbound(this)</c> so this entity joins the
    ///         parent's session — that's how a freshly constructed
    ///         <c>new Constraint { Design = design }</c> shows up in
    ///         <c>design.Constraints</c> when Save walks children.</item>
    /// </list>
    /// </summary>
    private static void EmitParentProperty(CodeWriter writer, PropertyModel p)
    {
        var declared = p.Type.FullyQualifiedName;
        var typeArg = StripNullable(declared);
        var access = FormatAccessibility(p.DeclaredAccessibility);
        var nullable = p.Type.IsNullable;
        var sliceKey = SurrealNaming.ToFieldName(p.Name);
        var sliceKeyLit = Quote(sliceKey);
        var fetchHintLit = Quote($".Include{p.Name}() on the parent query");
        var backing = $"_{ToCamel(p.Name)}";
        var idBacking = $"_{ToCamel(p.Name)}Id";

        writer.Line($"private {typeArg}? {backing};");
        writer.Line($"private global::Disruptor.Surface.Runtime.RecordId? {idBacking};");

        using (writer.Block($"{access} partial {declared} {p.Name}"))
        {
            if (nullable)
            {
                using (writer.Block("get"))
                {
                    writer.Line($"__EnsureSliceLoaded({sliceKeyLit}, {fetchHintLit});");
                    writer.Line($"return {backing} ?? ({idBacking} is {{ }} __id ? _session?.Get<{typeArg}>(__id) : null);");
                }
            }
            else
            {
                // Non-nullable [Parent]: a parent should always exist for a tracked child.
                // Throw if neither the entity ref nor the id is set, mirroring the mandatory
                // [Reference] shape.
                using (writer.Block("get"))
                {
                    writer.Line($"__EnsureSliceLoaded({sliceKeyLit}, {fetchHintLit});");
                    writer.Line($"var __resolved = {backing} ?? ({idBacking} is {{ }} __id ? _session?.Get<{typeArg}>(__id) : null);");
                    writer.Line($"return __resolved ?? throw new global::System.InvalidOperationException(\"Parent '{p.Name}' is not set.\");");
                }
            }

            if (p.HasSetter || p.HasInitOnlySetter)
            {
                using (writer.Block(p.HasInitOnlySetter ? "init" : "set"))
                {
                    writer.Line($"{backing} = value;");
                    writer.Line(nullable
                        ? $"{idBacking} = value is null ? null : (({Namespaces.EntityInterface})value).Id;"
                        : $"{idBacking} = (({Namespaces.EntityInterface})value).Id;");
                    // Cascade-track: assigning a parent that's bound to a session should pull
                    // this child into that session, so the parent's [Children] sees it at Save
                    // time. AdoptIfUnbound is a no-op when this entity already has a session.
                    // IEntity.Session is the explicit-impl public accessor; the parent's own
                    // `protected Session` would be inaccessible from this site.
                    writer.Line($"if (value is not null && (({Namespaces.EntityInterface})value).Session is {{ }} __ps) __ps.AdoptIfUnbound(this);");
                }
            }
        }
    }

    private static void EmitChildrenProperty(CodeWriter writer, PropertyModel p)
    {
        var declared = p.Type.FullyQualifiedName;
        var element = p.Type.ElementType ?? p.Type;
        var (elementTypeArg, elementNameExpr) = ResolveElement(element);
        var access = FormatAccessibility(p.DeclaredAccessibility);
        var sliceKey = SurrealNaming.ToFieldName(p.Name);
        var sliceKeyLit = Quote(sliceKey);
        var fetchHintLit = Quote($".Include{p.Name}(...) on the parent query");

        using (writer.Block($"{access} partial {declared} {p.Name}"))
        {
            using (writer.Block("get"))
            {
                writer.Line($"__EnsureSliceLoaded({sliceKeyLit}, {fetchHintLit});");
                writer.Line($"return Session.QueryChildren<{elementTypeArg}>(this, {elementNameExpr});");
            }
        }
    }

    /// <summary>
    /// Sync property body for <c>[Reference]</c>:
    /// <list type="bullet">
    ///   <item>Two backing fields: <c>_{name}</c> caches the reference target entity;
    ///         <c>_{name}Id</c> caches its record id.</item>
    ///   <item>Mandatory get-only (non-nullable): getter returns <c>_{name}</c> directly,
    ///         throwing if it isn't set. <c>Initialize</c> mints the target via the
    ///         <c>OnCreate{Name}</c> hook for fresh-construct entities; loaders set it
    ///         via inline expansion (<c>field.*</c>).</item>
    ///   <item>Optional with set/init: setter stores both backing fields. Getter falls
    ///         back to <c>Session?.Get&lt;T&gt;(_{name}Id)</c> when the entity ref isn't
    ///         locally cached (covers "loaded as id only" + "user later loaded other
    ///         aggregate separately and the entity is now in the identity map").</item>
    /// </list>
    /// </summary>
    private static void EmitReferenceProperty(CodeWriter writer, PropertyModel p)
    {
        var declared = p.Type.FullyQualifiedName;
        var typeArg = StripNullable(declared);
        var access = FormatAccessibility(p.DeclaredAccessibility);
        var sliceKey = SurrealNaming.ToFieldName(p.Name);
        var sliceKeyLit = Quote(sliceKey);
        var fetchHintLit = Quote($".Include{p.Name}() on the parent query");
        var nullable = p.Type.IsNullable;
        var backing = $"_{ToCamel(p.Name)}";
        var idBacking = $"_{ToCamel(p.Name)}Id";

        writer.Line($"private {typeArg}? {backing};");
        writer.Line($"private global::Disruptor.Surface.Runtime.RecordId? {idBacking};");

        using (writer.Block($"{access} partial {declared} {p.Name}"))
        {
            // Getter
            if (!nullable)
            {
                // Mandatory: throw if neither the entity ref nor the id is set; otherwise
                // resolve. Initialize mints + sets _{name}; inline loaders set both fields.
                using (writer.Block("get"))
                {
                    writer.Line($"__EnsureSliceLoaded({sliceKeyLit}, {fetchHintLit});");
                    writer.Line($"var __resolved = {backing} ?? ({idBacking} is {{ }} __id ? _session?.Get<{typeArg}>(__id) : null);");
                    writer.Line($"return __resolved ?? throw new global::System.InvalidOperationException(\"Mandatory reference '{p.Name}' is not set.\");");
                }
            }
            else
            {
                using (writer.Block("get"))
                {
                    writer.Line($"__EnsureSliceLoaded({sliceKeyLit}, {fetchHintLit});");
                    writer.Line($"return {backing} ?? ({idBacking} is {{ }} __id ? _session?.Get<{typeArg}>(__id) : null);");
                }
            }

            if (p.HasSetter || p.HasInitOnlySetter)
            {
                using (writer.Block(p.HasInitOnlySetter ? "init" : "set"))
                {
                    writer.Line($"{backing} = value;");
                    writer.Line(nullable
                        ? $"{idBacking} = value is null ? null : (({Namespaces.EntityInterface})value).Id;"
                        : $"{idBacking} = (({Namespaces.EntityInterface})value).Id;");
                }
            }
        }
    }

    /// <summary>
    /// A getter-only collection property carrying a forward/inverse relation attribute
    /// maps to either:
    /// <list type="bullet">
    ///   <item>Within-aggregate → <c>Session.QueryOutgoing&lt;TKind, TElement&gt;(this)</c>
    ///         on the forward side, <c>Session.QueryIncoming&lt;TKind, TElement&gt;(this)</c>
    ///         on the inverse side. Direction is explicit so same-table relations and
    ///         overlapping role types don't accidentally read both endpoints.</item>
    ///   <item>Cross-aggregate → <c>Session.QueryRelatedIds&lt;TKind&gt;(this)</c> on the
    ///         forward side / <c>Session.QueryInverseRelatedIds&lt;TKind&gt;(this)</c> on
    ///         the inverse side (id-typed, since target/source entities live in a
    ///         different aggregate snapshot).</item>
    /// </list>
    /// </summary>
    private static void EmitRelationProperty(CodeWriter writer, PropertyModel p, ModelGraph graph)
    {
        var declared = p.Type.FullyQualifiedName;
        var access = FormatAccessibility(p.DeclaredAccessibility);
        var kindMarker = ResolveKindMarkerFqn(p.RelationKindFullName, graph);

        var forwardKindFullName = ForwardKindFullNameFor(p.RelationKindFullName, p.RelationRole, graph);
        var crossAggregate = forwardKindFullName is not null && graph.IsCrossAggregate(forwardKindFullName);
        var sliceKey = SurrealNaming.ToFieldName(p.Name);
        var sliceKeyLit = Quote(sliceKey);
        var fetchHintLit = Quote($"a Workspace.Query.Edges.* query covering {p.Name}");

        if (crossAggregate)
        {
            var method = p.RelationRole == RelationRole.ForwardRelation ? "QueryRelatedIds" : "QueryInverseRelatedIds";
            using (writer.Block($"{access} partial {declared} {p.Name}"))
            {
                using (writer.Block("get"))
                {
                    writer.Line($"__EnsureSliceLoaded({sliceKeyLit}, {fetchHintLit});");
                    writer.Line($"return Session.{method}<{kindMarker}>(this);");
                }
            }

            return;
        }

        var elementType = p.Type.ElementType ?? p.Type;
        var elementArg = elementType.IsTypeParameter
            ? elementType.DisplayName
            : StripNullable(elementType.FullyQualifiedName);

        var directionalMethod = p.RelationRole == RelationRole.ForwardRelation
            ? "QueryOutgoing"
            : "QueryIncoming";

        using (writer.Block($"{access} partial {declared} {p.Name}"))
        {
            using (writer.Block("get"))
            {
                writer.Line($"__EnsureSliceLoaded({sliceKeyLit}, {fetchHintLit});");
                writer.Line($"return Session.{directionalMethod}<{kindMarker}, {elementArg}>(this);");
            }
        }
    }

    /// <summary>
    /// Resolves the FQN of the typed-kind marker class emitted alongside a forward
    /// relation attribute. <paramref name="memberKindFullName"/> may be either side —
    /// inverse kinds resolve to their paired forward's marker.
    /// </summary>
    private static string ResolveKindMarkerFqn(string? memberKindFullName, ModelGraph graph)
    {
        var kind = graph.FindKind(memberKindFullName);
        if (kind is null)
        {
            return "global::Disruptor.Surface.Runtime.IRelationKind";
        }

        var forward = kind.Direction == RelationDirection.Forward
            ? kind
            : graph.FindKind(kind.PairedForwardFullName);
        if (forward is null)
        {
            return "global::Disruptor.Surface.Runtime.IRelationKind";
        }

        var markerName = SurrealNaming.StripAttributeSuffix(forward.Name);
        return string.IsNullOrEmpty(forward.Namespace)
            ? $"global::{markerName}"
            : $"global::{forward.Namespace}.{markerName}";
    }

    /// <summary>
    /// Resolves the forward kind FQN whether the member sits on the forward or inverse
    /// side. Forward-side members carry the forward kind directly; inverse-side members
    /// carry the inverse kind and we walk to the paired forward.
    /// </summary>
    private static string? ForwardKindFullNameFor(string? memberKindFullName, RelationRole role, ModelGraph graph)
    {
        if (memberKindFullName is null)
        {
            return null;
        }

        if (role == RelationRole.ForwardRelation)
        {
            return memberKindFullName;
        }

        var kind = graph.FindKind(memberKindFullName);
        return kind?.PairedForwardFullName;
    }

    private static void EmitFallbackProperty(CodeWriter writer, PropertyModel p)
    {
        var type = p.Type.FullyQualifiedName;
        var access = FormatAccessibility(p.DeclaredAccessibility);
        writer.Line($"{access} partial {type} {p.Name} => throw new global::System.NotImplementedException();");
    }

    private static string ToCamel(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>
    /// Resolves element info for a TypeRef. The <c>NameExpr</c> is the Surreal table-name
    /// string the runtime keys off (lower_snake_case + pluralised). For concrete types we
    /// bake the literal at codegen time; for type parameters we fall back to
    /// <c>typeof(T).Name</c> — only ever hit by generic <c>[Children]</c>, which CG009
    /// rejects, so the unresolved-name path is effectively dead.
    /// </summary>
    private static (string TypeArg, string NameExpr) ResolveElement(TypeRef t)
    {
        if (t.IsTypeParameter)
        {
            return (t.DisplayName, $"typeof({t.DisplayName}).Name");
        }

        var stripped = StripNullable(t.FullyQualifiedName);
        return (stripped, Quote(SurrealNaming.ToTableName(SurrealNaming.SimpleName(stripped))));
    }

    // ──────────────────────────── Initialize emission ────────────────────────

    private static bool IsMandatoryReference(PropertyModel p)
        => p.Kinds.HasFlag(PropertyKind.Reference)
           && !p.Type.IsNullable
           && p.HasGetter
           && !p.HasSetter
           && !p.HasInitOnlySetter;

    /// <summary>
    /// Emits the <c>void IEntity.Initialize(SurrealSession)</c> impl that
    /// <c>Session.Track</c> (and <c>EnsureBoundForSave</c>) invokes. For each mandatory
    /// <c>[Reference]</c> we emit the <c>OnCreate{Name}</c> partial declaration so the
    /// user supplies the seed logic; the body then mints the target, runs the hook, and
    /// assigns it directly into the backing fields. Idempotent — guards each mint with
    /// <c>if (_{name} is null)</c> so re-invocation (Track followed by SaveAsync's
    /// auto-bind) doesn't re-mint already-set references. Entities without mandatory
    /// refs get an empty body.
    /// </summary>
    private static void EmitInitialize(CodeWriter writer, List<PropertyModel> mandatoryRefs)
    {
        foreach (var p in mandatoryRefs)
        {
            var typeArg = StripNullable(p.Type.FullyQualifiedName);
            // Simple-form partial — no accessibility, no body required. If the user
            // doesn't implement it, the call below is elided at compile time.
            writer.Line($"partial void OnCreate{p.Name}({typeArg} entity);");
        }

        if (mandatoryRefs.Count == 0)
        {
            writer.Line($"void {Namespaces.EntityInterface}.Initialize({Namespaces.SessionType} session) {{ }}");
            return;
        }

        using (writer.Block($"void {Namespaces.EntityInterface}.Initialize({Namespaces.SessionType} session)"))
        {
            foreach (var p in mandatoryRefs)
            {
                var typeArg = StripNullable(p.Type.FullyQualifiedName);
                var backing = $"_{ToCamel(p.Name)}";
                var idBacking = $"_{ToCamel(p.Name)}Id";

                using (writer.Block($"if ({backing} is null)"))
                {
                    writer.Line($"{backing} = new {typeArg}();");
                    writer.Line($"OnCreate{p.Name}({backing});");
                    writer.Line($"{idBacking} = (({Namespaces.EntityInterface}){backing}).Id;");
                }
            }
        }
    }

    // ──────────────────────────── OnDeleting emission ───────────────────────

    /// <summary>
    /// Emits a simple-form <c>partial void OnDeleting()</c> hook the user may optionally
    /// implement to queue dependent-delete / reference-clear commands, plus the explicit
    /// <see>
    ///     <cref>IEntity.OnDeleting</cref>
    /// </see>
    /// impl that the workspace dispatches to. If the
    /// user doesn't implement the partial, the call inside the dispatch is elided at
    /// compile time and Delete just queues the entity's own DELETE command.
    /// </summary>
    private static void EmitOnDeleting(CodeWriter writer)
    {
        writer.Line("partial void OnDeleting();");
        writer.Line($"void {Namespaces.EntityInterface}.OnDeleting() => OnDeleting();");
    }

    /// <summary>
    /// Emits <c>IEntity.MarkAllSlicesLoaded</c> — one <see cref="IHydrationSink.MarkSliceLoaded"/>
    /// call per navigable slice on this entity. Slice keys are snake-cased C# property names
    /// (matches the keys the read paths look up via <see cref="SurrealSession.IsSliceLoaded"/>).
    /// Skipped: <c>[Id]</c> (the entity itself) and scalar <c>[Property]</c> (always loaded
    /// with the row's <c>*</c> projection — no slice check on the read path).
    /// </summary>
    private static void EmitMarkAllSlicesLoaded(CodeWriter writer, TableModel table)
    {
        using (writer.Block($"void {Namespaces.EntityInterface}.MarkAllSlicesLoaded({Namespaces.HydrationSinkType} sink)"))
        {
            writer.Line($"var __id = (({Namespaces.EntityInterface})this).Id;");

            foreach (var p in table.Properties)
            {
                // Skip [Id] (the entity itself) and scalar [Property] (no slice check).
                if (p.Kinds.HasFlag(PropertyKind.Id))
                {
                    continue;
                }

                if (p.Kinds.HasFlag(PropertyKind.Property) && p.RelationRole == RelationRole.None)
                {
                    continue;
                }

                // [Parent] / [Reference] / [Children] / forward+inverse relation kinds — all
                // get a slice mark keyed on the snake-cased property name.
                var sliceKey = SurrealNaming.ToFieldName(p.Name);
                writer.Line($"sink.MarkSliceLoaded(__id, {Quote(sliceKey)});");
            }
        }
    }

    /// <summary>
    /// Emits <c>RecordId? IEntity.GetParentId()</c> for entities with a <c>[Parent]</c>
    /// property. Returns the parent's record id (if set) or <c>null</c>. Used by
    /// <see cref="SurrealSession.QueryChildren{T}"/> to match a candidate child against
    /// its parent owner. Tables without a <c>[Parent]</c> get the default-interface
    /// no-op return null defined on <c>IEntity</c>.
    /// </summary>
    private static void EmitGetParentId(CodeWriter writer, PropertyModel parentProp)
    {
        var idBacking = $"_{ToCamel(parentProp.Name)}Id";
        writer.Line($"global::Disruptor.Surface.Runtime.RecordId? {Namespaces.EntityInterface}.GetParentId() => {idBacking};");
    }

    /// <summary>
    /// Emits <c>IEnumerable&lt;(string, RecordId?)&gt; IEntity.EnumerateReferences()</c>
    /// yielding one entry per <c>[Reference]</c> / <c>[Parent]</c> field. Snake-cased
    /// field name + the <c>_{name}Id</c> backing-field id. Used by
    /// <see cref="SurrealSession"/>'s pre-flight cascade resolve to find which entities
    /// in the snapshot point at a delete target.
    /// </summary>
    private static void EmitEnumerateReferences(CodeWriter writer, IReadOnlyList<PropertyModel> refLikeProps)
    {
        using (writer.Block($"global::System.Collections.Generic.IEnumerable<(string FieldName, global::Disruptor.Surface.Runtime.RecordId? Target)> {Namespaces.EntityInterface}.EnumerateReferences()"))
        {
            foreach (var p in refLikeProps)
            {
                var idBacking = $"_{ToCamel(p.Name)}Id";
                var fieldNameLit = Quote(SurrealNaming.ToFieldName(p.Name));
                writer.Line($"yield return ({fieldNameLit}, {idBacking});");
            }
        }
    }

    /// <summary>
    /// Emits <c>void IEntity.SetReferenceTo(string, RecordId?)</c> for entities with at
    /// least one nullable <c>[Reference]</c>. Switches on the snake-cased field name and
    /// writes both the id backing field and the cached entity ref (clears the cache so a
    /// subsequent read doesn't hand back the cascaded-away target). Used by
    /// <see cref="SurrealSession.DeleteAsync"/>'s Unset phase to mirror the substrate's
    /// <c>REFERENCE ON DELETE UNSET</c> into the in-memory snapshot. Non-nullable
    /// references and <c>[Parent]</c>s are NOT in the switch — schema emits REJECT for
    /// those, so they never enter the Unset phase.
    /// </summary>
    private static void EmitSetReferenceTo(CodeWriter writer, IReadOnlyList<PropertyModel> unsetableProps)
    {
        using (writer.Block($"void {Namespaces.EntityInterface}.SetReferenceTo(string fieldName, global::Disruptor.Surface.Runtime.RecordId? value)"))
        {
            using (writer.Block("switch (fieldName)"))
            {
                foreach (var p in unsetableProps)
                {
                    var idBacking = $"_{ToCamel(p.Name)}Id";
                    var entityBacking = $"_{ToCamel(p.Name)}";
                    var fieldNameLit = Quote(SurrealNaming.ToFieldName(p.Name));
                    writer.Line($"case {fieldNameLit}:");
                    using (writer.Indent())
                    {
                        writer.Line($"{idBacking} = value;");
                        writer.Line($"{entityBacking} = null;");
                        writer.Line("break;");
                    }
                }
            }

        }
    }

    // ──────────────────────────── SaveAsync emission ────────────────────────

    /// <summary>
    /// Emits <c>IEntity.SaveAsync(ISaveContext, CancellationToken)</c> per <c>[Table]</c>.
    /// Walks forward dependencies (<c>[Reference]</c> / <c>[Parent]</c> targets that
    /// aren't tracked yet → recurse via <c>ctx.SaveAsync</c>); builds a content dict
    /// from <c>[Property]</c> + <c>[Reference]</c> + <c>[Parent]</c> backing fields;
    /// dispatches <c>CREATE</c> or <c>UPDATE</c> via the SDK's typed methods (CBOR, no SurrealQL); recurses
    /// into new children via the <c>[Children]</c> property accessor. Edges are out
    /// of scope — domain code constructs a relation-variant entity and dispatches it
    /// through <see cref="SurrealSession.SaveAsync(IEntity, Disruptor.Surreal.SurrealTransaction, System.Threading.CancellationToken)"/>
    /// against the same transaction.
    /// </summary>
    private static void EmitSaveAsync(CodeWriter writer, TableModel table)
    {
        using (writer.Block($"async global::System.Threading.Tasks.Task {Namespaces.EntityInterface}.SaveAsync(global::Disruptor.Surface.Runtime.ISaveContext ctx, global::System.Threading.CancellationToken ct)"))
        {
            writer.Line($"var __id = (({Namespaces.EntityInterface})this).Id;");
            writer.Line("var __isNew = !ctx.IsTracked(__id);");

            // Forward dependency walk: [Reference] + [Parent], skip relation-role properties.
            // Backing fields hold the entity ref directly under the new pure-setter model, so
            // the walk reads `_{name}` (the entity ref) rather than going through Session.
            foreach (var p in table.Properties)
            {
                var isFwdDep = (p.Kinds.HasFlag(PropertyKind.Reference) || p.Kinds.HasFlag(PropertyKind.Parent))
                    && p.RelationRole == RelationRole.None;
                if (!isFwdDep)
                {
                    continue;
                }

                var backing = $"_{ToCamel(p.Name)}";
                writer.Line($"if ({backing} is not null && !ctx.IsTracked((({Namespaces.EntityInterface}){backing}).Id))");
                using (writer.Indent())
                {
                    writer.Line($"await ctx.SaveAsync({backing}, ct);");
                }
            }
            // Typed CBOR content — SurrealObject built via ContentValue.Set helpers (the
            // mirror of HydrationValue's read side). Each scalar wraps into the right
            // SurrealValue variant; nullable values that are null get omitted so the
            // schema's DEFAULT applies. No SurrealQL string formatting, no JSON, no
            // reflection — pure typed CBOR.
            writer.Line("var __content = new global::Disruptor.Surreal.Values.SurrealObject();");
            foreach (var p in table.Properties)
            {
                if (p.Kinds.HasFlag(PropertyKind.Id))
                {
                    continue;
                }

                if (p.Kinds.HasFlag(PropertyKind.Children))
                {
                    continue;
                }

                if (p.RelationRole != RelationRole.None)
                {
                    continue;
                }

                var fieldLit = Quote(SurrealNaming.ToFieldName(p.Name));

                if (p.Kinds.HasFlag(PropertyKind.Property))
                {
                    var backing = $"_{ToCamel(p.Name)}";
                    if (IsElementCollection(p.Type) && p.InlineMembers.Count > 0)
                    {
                        // Element collection of records — typed per-element SurrealObject
                        // construction; the outer SurrealList wraps as SurrealListValue.
                        var elemLocal = $"__elem_{ToCamel(p.Name)}";
                        var objLocal = $"__obj_{ToCamel(p.Name)}";
                        using (writer.BracedBlock())
                        {
                            writer.Line($"var __list = new global::Disruptor.Surreal.Values.SurrealList({backing}.Count);");
                            using (writer.Block($"foreach (var {elemLocal} in {backing})"))
                            {
                                writer.Line($"var {objLocal} = new global::Disruptor.Surreal.Values.SurrealObject();");
                                foreach (var im in p.InlineMembers)
                                {
                                    var subLit = Quote(SurrealNaming.ToFieldName(im.Name));
                                    writer.Line($"global::Disruptor.Surface.Runtime.ContentValue.Set({objLocal}, {subLit}, {elemLocal}.{im.Name});");
                                }

                                writer.Line($"__list.Add(new global::Disruptor.Surreal.Values.SurrealObjectValue({objLocal}));");
                            }

                            writer.Line($"__content[{fieldLit}] = new global::Disruptor.Surreal.Values.SurrealListValue(__list);");
                        }
                    }
                    else
                    {
                        // Scalar [Property] — typed Set picks the right SurrealValue variant
                        // based on the C# type. Nullable scalars omit null values (schema
                        // DEFAULT applies).
                        writer.Line($"global::Disruptor.Surface.Runtime.ContentValue.Set(__content, {fieldLit}, {backing});");
                    }
                }
                else if (p.Kinds.HasFlag(PropertyKind.Reference) || p.Kinds.HasFlag(PropertyKind.Parent))
                {
                    // FK rendered from the cached id (or the entity's id if only the entity
                    // ref is set — user assigned but Save hasn't yet rebuilt the id). Emits
                    // as a typed SurrealRecordIdValue, preserving Thing typing through CBOR.
                    var backing = $"_{ToCamel(p.Name)}";
                    var idBacking = $"_{ToCamel(p.Name)}Id";
                    writer.Line($"global::Disruptor.Surface.Runtime.ContentValue.SetRef(__content, {fieldLit}, {idBacking} ?? ({backing} is null ? (global::Disruptor.Surface.Runtime.RecordId?)null : (({Namespaces.EntityInterface}){backing}).Id));");
                }
            }

            // Typed CBOR dispatch — SDK methods accept ISurrealRecordId + SurrealObject and
            // CBOR-encode end-to-end. No SurrealQL string, no escape rules.
            writer.Line("if (__isNew)");
            using (writer.Indent())
            {
                writer.Line("await ctx.Transaction.CreateAsync(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk(__id), __content, ct);");
            }

            writer.Line("else");
            using (writer.Indent())
            {
                writer.Line("await ctx.Transaction.UpsertAsync(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk(__id), __content, ct);");
            }

            writer.Line("ctx.MarkSaved(this);");

            // Children walk: AFTER self-dispatch (children's [Parent] FK needs this row to
            // exist in the txn before their CREATE lands). For each [Children] collection,
            // iterate via the property accessor; recurse into any not-yet-saved entries.
            foreach (var p in table.Properties)
            {
                if (!p.Kinds.HasFlag(PropertyKind.Children))
                {
                    continue;
                }

                var elemLocal = $"__child_{ToCamel(p.Name)}";
                using (writer.Block($"foreach (var {elemLocal} in this.{p.Name})"))
                {
                    writer.Line($"if (!ctx.IsTracked((({Namespaces.EntityInterface}){elemLocal}).Id))");
                    using (writer.Indent())
                    {
                        writer.Line($"await ctx.SaveAsync({elemLocal}, ct);");
                    }
                }
            }

            // Relations are no longer dispatched from SaveAsync — sync Relate (and the
            // snapshot-diff that drained it) was deleted in preview.45. Edge mutations
            // flow through per-variant relation classes dispatched via
            // Session.SaveAsync(variantInstance, tx) (or through UnrelateAsync<TKind> for
            // bulk drops). SaveAsync is entity-only: scalar fields, owned [Reference, Inline]
            // sidecars, [Children] recursion. Same shape the SurrealDB substrate would
            // expect from any client that doesn't re-implement a write buffer in front of it.
        }
    }

    // ──────────────────────────── Hydrate emission ──────────────────────────

    /// <summary>
    /// Emits <c>IEntity.Hydrate(SurrealValue, IHydrationSink)</c> for the table. Each
    /// declared property writes directly into the entity's backing field — no
    /// <c>sink.Parent</c> / <c>sink.Reference</c> calls; the entity owns its own state.
    /// <list type="bullet">
    ///   <item>[Id] → parses the record id and sets the typed <c>_id</c> field.</item>
    ///   <item>scalar [Property] → reads the value and sets <c>_{name}</c>.</item>
    ///   <item>[Reference] non-Inline → reads the id, sets <c>_{name}Id</c>.</item>
    ///   <item>[Reference, Inline] → constructs the target via
    ///         <see cref="HydrationValue.HydrateInlineReference{T}"/>, sets both
    ///         <c>_{name}</c> and <c>_{name}Id</c>; falls back to id-only when the
    ///         target was already tracked (multi-owner inline expansion).</item>
    ///   <item>[Parent] → reads the id, sets <c>_{name}Id</c>.</item>
    /// </list>
    /// Element-collection properties hydrate inline via <see cref="EmitHydrateElementCollection"/>.
    /// Forward/inverse relation edges are loaded separately by the per-aggregate loader.
    /// </summary>
    private static void EmitHydrate(CodeWriter writer, TableModel table)
    {
        using (writer.Block($"void {Namespaces.EntityInterface}.Hydrate(global::Disruptor.Surreal.Values.SurrealValue row, {Namespaces.HydrationSinkType} sink)"))
        {
            writer.Line("if (row is not global::Disruptor.Surreal.Values.SurrealObjectValue __obj) return;");

            var idType = $"global::{(string.IsNullOrEmpty(table.Namespace) ? table.Name : $"{table.Namespace}.{table.Name}")}Id";
            writer.Line("if (__obj.Object.TryGetValue(\"id\", out var __idVal))");
            using (writer.Indent())
            {
                writer.Line($"_id = new {idType}(global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__idVal).Value);");
            }

            writer.Line("sink.Track(this);");

            foreach (var p in table.Properties)
            {
                if (p.Kinds.HasFlag(PropertyKind.Property))
                {
                    if (IsElementCollection(p.Type) && p.InlineMembers.Count > 0)
                    {
                        EmitHydrateElementCollection(writer, p);
                    }
                    else
                    {
                        EmitHydrateValueProperty(writer, p);
                    }
                }
                else if (p.Kinds.HasFlag(PropertyKind.Reference))
                {
                    EmitHydrateReference(writer, p);
                }
                else if (p.Kinds.HasFlag(PropertyKind.Parent))
                {
                    EmitHydrateParent(writer, p);
                }
                // [Children] is computed via parentByChild reverse lookup — nothing to read here.
                // Forward/inverse relation properties are populated by the per-aggregate loader's
                // edge query, not by per-row Hydrate.
            }
        }
    }

    /// <summary>
    /// Hydrates any non-element-collection [Property] — scalar (int, bool, DateTime, …),
    /// array (<c>int[]</c>, <c>List&lt;T&gt;</c>), record. String stays special-cased
    /// so its empty-string fallback matches the schema's <c>DEFAULT ""</c> clause without
    /// a round-trip through the converter.
    /// </summary>
    private static void EmitHydrateValueProperty(CodeWriter writer, PropertyModel p)
    {
        var backing = $"_{ToCamel(p.Name)}";
        var fieldLit = Quote(SurrealNaming.ToFieldName(p.Name));
        var typeFqn = p.Type.FullyQualifiedName;
        var nullable = p.Type.IsNullable;

        // Non-nullable string special-cased through ReadString — empty-string fallback
        // matches the schema's DEFAULT "" clause for a missing/null column.
        // Nullable string and every nullable value type goes through ReadOrDefault<T>
        // with the DECLARED type (not stripped), so SurrealNullValue / SurrealNoneValue
        // round-trip as null instead of being squashed to 0/""/MinValue/false.
        // Non-nullable value types stay on the stripped path — they have a schema-level
        // DEFAULT and the convertor's `default` matches it.
        if (!nullable && typeFqn is "string" or "global::System.String")
        {
            writer.Line($"{backing} = global::Disruptor.Surface.Runtime.HydrationValue.ReadString(__obj, {fieldLit});");
        }
        else
        {
            // For nullable: deserialise as the declared type (e.g. ReadOrDefault<int?>)
            // so default!/null is preserved. For non-nullable value types: strip nullable
            // (no-op) and use the value-type form; default is the type's natural zero.
            var deserialiseAs = nullable ? typeFqn : StripNullable(typeFqn);
            writer.Line($"{backing} = global::Disruptor.Surface.Runtime.HydrationValue.ReadOrDefault<{deserialiseAs}>(__obj, {fieldLit});");
        }
    }

    /// <summary>
    /// Hydrates an element-collection [Property] of records (the
    /// <c>IReadOnlyList&lt;Scenario&gt;</c> shape): clears the backing list, walks the
    /// SurrealListValue elements, constructs each <c>T</c> via its primary constructor
    /// using the public scalar properties discovered at codegen time. Pure typed code,
    /// no reflection. Primitive-element collections take the
    /// <see cref="EmitHydrateValueProperty"/> path instead (HydrationValue's typed
    /// converter handles primitive element types).
    /// </summary>
    private static void EmitHydrateElementCollection(CodeWriter writer, PropertyModel p)
    {
        var backing = $"_{ToCamel(p.Name)}";
        var fieldLit = Quote(SurrealNaming.ToFieldName(p.Name));
        if (p.Type.TypeArguments.Count == 0)
        {
            writer.Line($"throw new global::System.NotSupportedException(\"Hydrate: collection element type for property '{p.Name}' could not be resolved at codegen time.\");");
            return;
        }

        var elementType = p.Type.TypeArguments[0].FullyQualifiedName;
        var arrLocal = $"__sl_{ToCamel(p.Name)}";
        var elemLocal = $"__el_{ToCamel(p.Name)}";
        var elemObjLocal = $"__eo_{ToCamel(p.Name)}";

        using (writer.Block($"if (__obj.Object.TryGetValue({fieldLit}, out var {arrLocal}) && {arrLocal} is global::Disruptor.Surreal.Values.SurrealListValue {arrLocal}Cast)"))
        {
            writer.Line($"{backing}.Clear();");
            using (writer.Block($"foreach (var {elemLocal} in {arrLocal}Cast.List)"))
            {
                writer.Line($"if ({elemLocal} is not global::Disruptor.Surreal.Values.SurrealObjectValue {elemObjLocal}) continue;");
                writer.Line($"{backing}.Add(new {elementType}(");
                using (writer.Indent())
                {
                    for (var i = 0; i < p.InlineMembers.Count; i++)
                    {
                        var im = p.InlineMembers[i];
                        var subLit = Quote(SurrealNaming.ToFieldName(im.Name));
                        var typeFqn = im.Type.FullyQualifiedName;
                        // String fast-path mirrors EmitHydrateValueProperty's optimisation:
                        // only non-nullable strings use the empty-string fallback.
                        var trailing = i == p.InlineMembers.Count - 1 ? "" : ",";
                        var nullable = im.Type.IsNullable;
                        var isString = typeFqn is "string" or "global::System.String" or "string?" or "global::System.String?";
                        if (!nullable && isString)
                        {
                            writer.Line($"{im.Name}: global::Disruptor.Surface.Runtime.HydrationValue.ReadString({elemObjLocal}, {subLit}){trailing}");
                        }
                        else
                        {
                            var deserialiseAs = nullable && !isString ? typeFqn : StripNullable(typeFqn);
                            writer.Line($"{im.Name}: global::Disruptor.Surface.Runtime.HydrationValue.ReadOrDefault<{deserialiseAs}>({elemObjLocal}, {subLit}){trailing}");
                        }
                    }
                }

                writer.Line("));");
            }
        }
    }

    /// <summary>
    /// Hydrates a <c>[Reference]</c> field. Inline form populates both the entity ref
    /// and id backing fields; non-inline (id-only) populates just the id backing field.
    /// The runtime's reference resolution falls back to <c>Session.Get&lt;T&gt;(id)</c>
    /// on read when only the id is set.
    /// </summary>
    private static void EmitHydrateReference(CodeWriter writer, PropertyModel p)
    {
        var backing = $"_{ToCamel(p.Name)}";
        var idBacking = $"_{ToCamel(p.Name)}Id";
        var fieldLit = Quote(SurrealNaming.ToFieldName(p.Name));
        var typeArg = StripNullable(p.Type.FullyQualifiedName);

        // Try inline first (returns the hydrated entity if the field is an inline object);
        // fall back to id-only if it isn't inline. The if-let-then-else avoids two
        // dictionary lookups for the common case where neither path matches.
        var inlineLocal = $"__inline_{ToCamel(p.Name)}";
        writer.Line($"var {inlineLocal} = global::Disruptor.Surface.Runtime.HydrationValue.HydrateInlineReference<{typeArg}>(__obj, {fieldLit}, sink);");
        using (writer.Block($"if ({inlineLocal} is not null)"))
        {
            writer.Line($"{backing} = {inlineLocal};");
            writer.Line($"{idBacking} = (({Namespaces.EntityInterface}){inlineLocal}).Id;");
        }

        writer.Line("else");
        using (writer.BracedBlock())
        {
            writer.Line($"{idBacking} = global::Disruptor.Surface.Runtime.HydrationValue.TryReadReferenceId(__obj, {fieldLit});");
        }
    }

    private static void EmitHydrateParent(CodeWriter writer, PropertyModel p)
    {
        var idBacking = $"_{ToCamel(p.Name)}Id";
        var fieldLit = Quote(SurrealNaming.ToFieldName(p.Name));
        writer.Line($"{idBacking} = global::Disruptor.Surface.Runtime.HydrationValue.TryReadReferenceId(__obj, {fieldLit});");
    }

    // ──────────────────────────── helpers ────────────────────────────────────

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";

    private static string StripNullable(string typeName)
        => typeName.EndsWith("?") ? typeName[..^1] : typeName;

    private static string FormatAccessibility(string raw) => raw switch
    {
        "Public" => "public",
        "Internal" => "internal",
        "Private" => "private",
        "Protected" => "protected",
        "ProtectedOrInternal" => "protected internal",
        "ProtectedAndInternal" => "private protected",
        "NotApplicable" => string.Empty,
        _ => raw.ToLowerInvariant(),
    };
}
