using Disruptor.Surface.Generator.Model;
using Disruptor.Surface.Generator.Pipeline;
using Microsoft.CodeAnalysis;

namespace Disruptor.Surface.Generator.Emit;

/// <summary>
/// Per-variant emitter for relation classes — user-declared classes annotated with a
/// <c>ForwardRelation</c>- or <c>InverseRelation&lt;T&gt;</c>-derived attribute applied
/// to the class itself (e.g. <c>[Restricts] public partial class EpicRestriction</c>).
/// Emits the implementing half of every variant: <see cref="EntityEmitterCommon.WriteSessionPlumbing"/>
/// session plumbing, <c>[In]</c> / <c>[Out]</c> endpoint properties (entity-typed within
/// the same aggregate, typed-id across), <c>[Property]</c> payload members, the
/// <c>IEntity</c> hooks (<c>Initialize</c> / <c>Hydrate</c> / <c>OnDeleting</c> /
/// <c>MarkAllSlicesLoaded</c> / <c>EnumerateReferences</c>), and a
/// <c>SaveAsync</c> body that dispatches <c>INSERT RELATION INTO {edge} … ON DUPLICATE
/// KEY UPDATE …</c> against the user's transaction.
/// <para>
/// Per-kind sidecars are also emitted: a <c>I{KindName}Variant</c> marker interface (only
/// for kinds with 2+ variants) and a <c>{KindName}Hydration.HydrateVariant</c> dispatcher
/// that picks the right variant class by <c>(in.tb, out.tb)</c> at hydration time.
/// </para>
/// </summary>
internal static class RelationVariantEmitter
{
    public static void Emit(SourceProductionContext spc, ModelGraph graph)
    {
        if (graph.RelationVariants.Count == 0)
        {
            return;
        }

        // Same-pass type resolution gotcha: typed-id endpoints (e.g. `[Out] partial DesignId Target`)
        // reference generator-emitted structs that don't exist yet at extraction time. Roslyn
        // hands back error types whose FullyQualifiedName is the bare simple name (no
        // `global::` prefix, no namespace). Build a per-table catalog so the emitter can
        // re-resolve those bare names against the table that owns the matching `{Name}Id`.
        // Within-aggregate entity-typed endpoints don't hit this — `[Table]` types are real
        // user code the extractor sees as proper INamedTypeSymbols.
        var typedIdNamespaces = BuildTypedIdNamespaceLookup(graph);

        // Group variants by their kind attribute FQN (e.g. all [Restricts] variants
        // together). Per-kind sidecars (variant marker interface, hydration dispatcher)
        // emit once per kind.
        var byKind = graph.RelationVariants.ToLookup(v => v.KindAttributeFqn, StringComparer.Ordinal);

        foreach (var group in byKind)
        {
            var kind = graph.FindKind(group.Key);
            if (kind is null)
            {
                // Linker didn't find the attribute as a relation kind — should not happen
                // for well-formed inputs (TableExtractor's discovery of the same attribute
                // class would have produced a RelationKindModel). Skip silently rather
                // than fault the emit pass.
                continue;
            }

            // Variants only ever attach to forward kinds — the inverse-side attribute is
            // a read alias, not an edge declaration. Walk to the forward when we got the
            // inverse so per-kind id type / dispatcher live next to the forward.
            var forward = kind.Direction == RelationDirection.Forward
                ? kind
                : graph.FindKind(kind.PairedForwardFullName);
            if (forward is null)
            {
                continue;
            }

            var variants = group.OrderBy(v => v.FullName, StringComparer.Ordinal).ToList();

            foreach (var variant in variants)
            {
                if (!variant.IsPartial)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.VariantMustBePartial, Location.None, variant.FullName));
                    continue;
                }

                EmitVariant(spc, variant, forward, variants.Count, typedIdNamespaces);
            }

            // Per-kind variant marker interface — only for multi-variant kinds. Single-
            // variant kinds skip the marker (the variant class itself is the discriminator).
            if (variants.Count >= 2)
            {
                EmitVariantMarkerInterface(spc, forward, variants);
            }

            EmitHydrationDispatcher(spc, forward, variants);
        }
    }

    /// <summary>
    /// Builds <c>{TableSimpleName} → {TableNamespace}</c> map so the emitter can rewrite a
    /// bare typed-id endpoint name (e.g. <c>DesignId</c>) into <c>global::{ns}.DesignId</c>.
    /// Stripping the trailing <c>Id</c> off the bare name yields the table simple name;
    /// looking that up against the catalog gives us the namespace the generator-emitted
    /// <c>DesignId</c> struct lives in (always the same namespace as the table).
    /// </summary>
    private static Dictionary<string, string> BuildTypedIdNamespaceLookup(ModelGraph graph)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in graph.Tables)
        {
            // Multiple tables sharing a simple name across namespaces would be ambiguous,
            // but the generator's existing CG-checks would have rejected that earlier.
            // Last-write wins — the lookup is only consulted as a fallback when the FQN is
            // already bare, which itself is a same-pass-resolution edge case.
            lookup[t.Name] = t.Namespace;
        }
        return lookup;
    }

    /// <summary>
    /// Returns the rendered C# type for an endpoint property, resolving same-pass
    /// resolution errors on typed-id endpoints. If <paramref name="declared"/> is already
    /// fully qualified (<c>global::*</c>) it's returned unchanged. Otherwise we strip the
    /// trailing <c>Id</c> off the bare name and look the table up; if found we return
    /// <c>global::{ns}.{declared}</c>.
    /// </summary>
    /// <remarks>
    /// Only typed-id endpoints (<see cref="TypeRef.IsTableType"/> false) hit this path —
    /// entity-typed endpoints flow through Roslyn's normal symbol resolution and arrive
    /// already qualified. Bare names that don't match any table (e.g. a user typo) pass
    /// through unchanged so the downstream compile error points at the original signature.
    /// </remarks>
    private static string ResolveEndpointTypeFqn(string declared, IReadOnlyDictionary<string, string> typedIdNamespaces)
    {
        if (declared.StartsWith("global::", StringComparison.Ordinal))
        {
            return declared;
        }

        var bare = StripNullable(declared);
        if (!bare.EndsWith("Id", StringComparison.Ordinal) || bare.Length <= 2)
        {
            return declared;
        }

        var tableName = bare[..^"Id".Length];
        if (!typedIdNamespaces.TryGetValue(tableName, out var ns))
        {
            return declared;
        }

        var qualified = string.IsNullOrEmpty(ns)
            ? $"global::{bare}"
            : $"global::{ns}.{bare}";
        return declared.EndsWith("?", StringComparison.Ordinal) ? $"{qualified}?" : qualified;
    }

    // ──────────────────────── per-variant entity emission ──────────────────────

    private static void EmitVariant(
        SourceProductionContext spc,
        RelationVariantModel variant,
        RelationKindModel forward,
        int totalVariantsInKind,
        IReadOnlyDictionary<string, string> typedIdNamespaces)
    {
        var markerName = SurrealNaming.StripAttributeSuffix(forward.Name);
        var idTypeFqn = ResolveKindIdFqn(forward, markerName);

        var baseTypes = new List<string>
        {
            EntityEmitterCommon.EntityInterface,
            EntityEmitterCommon.RelationVariantInterface,
        };

        // Multi-variant kinds also implement the per-kind I{KindName}Variant marker so
        // session APIs can talk about "any variant of this kind" generically. Single-
        // variant kinds skip the marker (the variant class itself is the discriminator).
        if (totalVariantsInKind >= 2)
        {
            var variantInterfaceFqn = string.IsNullOrEmpty(forward.Namespace)
                ? $"global::I{markerName}Variant"
                : $"global::{forward.Namespace}.I{markerName}Variant";
            baseTypes.Add(variantInterfaceFqn);
        }

        var writer = new CodeWriter().Header();
        using (writer.Namespace(variant.Namespace))
        {
            var declaration = $"{FormatAccessibility(variant.DeclaredAccessibility)} partial class {variant.Name} : {string.Join(", ", baseTypes)}";
            using (writer.Block(declaration))
            {
                EntityEmitterCommon.WriteSessionPlumbing(writer);

                // Per-variant id anchor — uses the per-kind {MarkerName}Id type (shared across
                // every variant of this kind, since they all live on the same edge table). New()
                // mints a Ulid; Hydrate parses the loaded edge id back into the typed wrapper.
                EmitIdAnchor(writer, idTypeFqn);

                // Endpoint + payload properties — backing fields + property bodies. [In] / [Out]
                // are required endpoints (one each); [Property] members carry the typed payload.
                writer.Line();
                EmitEndpointProperty(writer, variant.In, typedIdNamespaces);
                writer.Line();
                EmitEndpointProperty(writer, variant.Out, typedIdNamespaces);

                foreach (var p in variant.PayloadProperties)
                {
                    writer.Line();
                    EmitPayloadProperty(writer, p);
                }

                // Hydrate — parses the loaded edge row (id / in / out / payload fields) into
                // backing fields. Per-variant Hydrate doesn't discriminate; the per-kind dispatcher
                // picks the right variant class first based on (in.tb, out.tb).
                writer.Line();
                EmitHydrate(writer, variant, idTypeFqn, typedIdNamespaces);

                // EnumerateReferences — yields ("in", inId) and ("out", outId). Variants don't
                // need IReferenceRegistry registration: the substrate's TYPE RELATION ENFORCED
                // handles edge cleanup on endpoint delete, and library-side cleanup of
                // state.Edges with the deleted endpoint already happens in CleanupLocalState's
                // existing endpoint walk. SurrealSession.MarkSaved + CleanupLocalState use this
                // method (via the IRelationVariant marker) to mirror the variant's own edge
                // tuple in/out of the read-side index.
                writer.Line();
                EmitEnumerateReferences(writer, variant);

                // SetReferenceTo — only meaningful for nullable [In] / [Out] (rare; non-nullable
                // endpoints can't be unset). Empty switch when nothing is nullable.
                writer.Line();
                EmitSetReferenceTo(writer, variant);

                // SaveAsync — dispatches INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY
                // UPDATE …] against the user's transaction. Forward-deps walk for entity-typed
                // endpoints; pure pass-through for typed-id endpoints.
                writer.Line();
                EmitSaveAsync(writer, variant, forward);

                // Initialize / OnDeleting / MarkAllSlicesLoaded — IEntity contract completion.
                // Variants have no mandatory-reference seeding (endpoints are required at
                // construction), no slices (state is the row itself), and may opt into delete
                // hooks via the partial method.
                writer.Line();
                EmitInitializeAndDeletingAndSlices(writer);
            }
        }

        var hint = $"{variant.FullName}.RelationVariant.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }

    private static void EmitIdAnchor(CodeWriter writer, string idTypeFqn)
    {
        writer.Line();
        writer.Line($"private {idTypeFqn}? _id;");
        writer.Line($"global::Disruptor.Surface.Runtime.RecordId {EntityEmitterCommon.EntityInterface}.Id => _id ??= {idTypeFqn}.New();");
    }

    /// <summary>
    /// Emits backing fields + property body for an <c>[In]</c> or <c>[Out]</c> endpoint.
    /// Two shapes:
    /// <list type="bullet">
    ///   <item><b>Entity-typed</b> (within-aggregate, e.g. <c>partial Constraint Source</c>):
    ///         dual backing fields <c>_{name}</c> (entity ref) + <c>_{name}Id</c> (record
    ///         id). Getter returns the cached entity ref, falling back to
    ///         <c>Session?.Get&lt;T&gt;(_{name}Id)</c> when only the id is set. Setter
    ///         stores both. No <c>__EnsureSliceLoaded</c> guard — endpoints are part of
    ///         the variant's identity, not lazy slices.</item>
    ///   <item><b>Typed-id-typed</b> (cross-aggregate, e.g. <c>partial EpicId Target</c>):
    ///         single <c>_{name}</c> backing field of the typed id. Pure pass-through —
    ///         no Session, no entity ref cache.</item>
    /// </list>
    /// Mandatory non-nullable endpoints with no setter throw on read if unset (mirrors
    /// the mandatory-reference shape in <see cref="PartialEmitter"/>).
    /// </summary>
    private static void EmitEndpointProperty(
        CodeWriter writer, RelationVariantPropertyModel p,
        IReadOnlyDictionary<string, string> typedIdNamespaces)
    {
        // For typed-id endpoints (cross-aggregate), the extractor's snapshot of the user's
        // signature came back as a bare error-type name (`ReviewId`) because Roslyn
        // hasn't seen the generator-emitted struct yet (same-pass resolution gotcha).
        // Re-qualify against the table catalog so the emitted partial declaration uses
        // the FQN — CS9255 ("partial member declarations must have the same type") still
        // passes because the user's bare-name decl resolves to the SAME type symbol at
        // body-compile time. Entity-typed endpoints already arrive fully qualified.
        var declared = ResolveEndpointTypeFqn(p.Type.FullyQualifiedName, typedIdNamespaces);
        var typeArg = StripNullable(declared);
        var access = FormatAccessibility(p.DeclaredAccessibility);
        var nullable = p.Type.IsNullable;
        var backing = $"_{ToCamel(p.Name)}";

        if (p.Type.IsTableType)
        {
            // Entity-typed endpoint (within-aggregate): dual backing fields. Mirrors
            // PartialEmitter.EmitReferenceProperty, but no slice guard — variants don't
            // have lazy slices; their endpoints are baked into the row.
            var idBacking = $"_{ToCamel(p.Name)}Id";

            writer.Line($"private {typeArg}? {backing};");
            writer.Line($"private global::Disruptor.Surface.Runtime.RecordId? {idBacking};");
            writer.Line($"{access} partial {declared} {p.Name}");
            using (writer.BracedBlock())
            {

                using (writer.Block("get"))
                {
                    if (!nullable)
                    {
                        writer.Line($"var __resolved = {backing} ?? ({idBacking} is {{ }} __id ? _session?.Get<{typeArg}>(__id) : null);");
                        writer.Line($"return __resolved ?? throw new global::System.InvalidOperationException(\"Endpoint '{p.Name}' is not set.\");");
                    }
                    else
                    {
                        writer.Line($"return {backing} ?? ({idBacking} is {{ }} __id ? _session?.Get<{typeArg}>(__id) : null);");
                    }
                }

                if (p.HasSetter || p.HasInitOnlySetter)
                {
                    using (writer.Block(p.HasInitOnlySetter ? "init" : "set"))
                    {
                        writer.Line($"{backing} = value;");
                        writer.Line(nullable
                            ? $"{idBacking} = value is null ? null : (({EntityEmitterCommon.EntityInterface})value).Id;"
                            : $"{idBacking} = (({EntityEmitterCommon.EntityInterface})value).Id;");
                    }
                }
            }
        }
        else
        {
            // Typed-id endpoint (cross-aggregate): single backing field of the id type.
            // Pure pass-through — no Session interaction, no entity ref cache. Caller
            // resolves the entity separately if needed (e.g. via Session.Get).
            writer.Line("#pragma warning disable CS0649");
            writer.Line($"private {declared} {backing} = default!;");
            writer.Line("#pragma warning restore CS0649");

            if (!p.HasSetter && !p.HasInitOnlySetter)
            {
                writer.Line($"{access} partial {declared} {p.Name} => {backing};");
                return;
            }

            writer.Line($"{access} partial {declared} {p.Name}");
            using (writer.BracedBlock())
            {
                writer.Line($"get => {backing};");
                writer.Line($"{(p.HasInitOnlySetter ? "init" : "set")} => {backing} = value;");
            }
        }
    }

    /// <summary>
    /// Emits the <c>IEntity.Hydrate(SurrealValue, IHydrationSink)</c> body for a variant.
    /// Reads the edge row's <c>id</c> field into the per-kind <c>{Marker}Id</c>, registers
    /// the variant on the sink, then per-property reads the matching field. Entity-typed
    /// endpoints land in the <c>_{name}Id</c> backing field (entity ref resolves lazily
    /// via Session); typed-id endpoints land in the single backing field directly.
    /// Payload properties go through <see cref="HydrationValue.ReadOrDefault{T}"/> /
    /// <c>ReadString</c> the same way <see cref="PartialEmitter"/>'s
    /// <c>EmitHydrateValueProperty</c> does.
    /// </summary>
    private static void EmitHydrate(
        CodeWriter writer, RelationVariantModel variant, string idTypeFqn,
        IReadOnlyDictionary<string, string> typedIdNamespaces)
    {
        using (writer.Block($"void {EntityEmitterCommon.EntityInterface}.Hydrate(global::Disruptor.Surreal.Values.SurrealValue row, {EntityEmitterCommon.HydrationSinkType} sink)"))
        {
            writer.Line("if (row is not global::Disruptor.Surreal.Values.SurrealObjectValue __obj) return;");
            writer.Line("if (__obj.Object.TryGetValue(\"id\", out var __idVal))");
            using (writer.Indent())
            {
                writer.Line($"_id = new {idTypeFqn}(global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__idVal).Value);");
            }
            writer.Line("sink.Track(this);");

            // [In] / [Out] endpoints: the edge row's "in" / "out" fields carry the endpoint
            // ids. Entity-typed endpoints set the cached id backing field (resolved via
            // Session.Get on read); typed-id endpoints wrap into the typed id struct.
            EmitHydrateEndpoint(writer, variant.In, fieldName: "in", typedIdNamespaces);
            EmitHydrateEndpoint(writer, variant.Out, fieldName: "out", typedIdNamespaces);

            // [Property] payload members: same scalar-read shape as PartialEmitter.
            foreach (var p in variant.PayloadProperties)
            {
                EmitHydratePayload(writer, p);
            }
        }
    }

    private static void EmitHydrateEndpoint(
        CodeWriter writer, RelationVariantPropertyModel p, string fieldName,
        IReadOnlyDictionary<string, string> typedIdNamespaces)
    {
        var backing = $"_{ToCamel(p.Name)}";
        var fieldLit = Quote(fieldName);

        if (p.Type.IsTableType)
        {
            // Entity-typed endpoint: write the cached id backing field. The entity ref
            // backing field stays null until first read (which falls through to
            // Session.Get<T>(id)); inline-expanded loaders may pre-populate _{name} too,
            // but the variant edge row never carries an inline-expanded endpoint (edges
            // ship endpoint ids, not nested entities).
            var idBacking = $"_{ToCamel(p.Name)}Id";
            writer.Line($"{idBacking} = global::Disruptor.Surface.Runtime.HydrationValue.TryReadReferenceId(__obj, {fieldLit});");
        }
        else
        {
            // Typed-id endpoint: parse the id and wrap into the declared id type. The
            // generated {Name}Id struct's primary ctor validates via RecordIdFormat,
            // so a malformed id throws at hydration time. Resolve the bare type-name to
            // its FQN — the variant `.g.cs` lives in a different namespace from the id
            // struct (cross-aggregate case) and has no using directives of its own.
            var declared = ResolveEndpointTypeFqn(p.Type.FullyQualifiedName, typedIdNamespaces);
            using (writer.BracedBlock())
            {
                writer.Line($"if (__obj.Object.TryGetValue({fieldLit}, out var __ev))");
                using (writer.Indent())
                {
                    writer.Line($"{backing} = new {StripNullable(declared)}(global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__ev).Value);");
                }
            }
        }
    }

    private static void EmitHydratePayload(CodeWriter writer, RelationVariantPropertyModel p)
    {
        var backing = $"_{ToCamel(p.Name)}";
        var fieldLit = Quote(p.FieldName);
        var typeFqn = p.Type.FullyQualifiedName;
        var nullable = p.Type.IsNullable;

        // Mirrors PartialEmitter.EmitHydrateValueProperty: non-nullable string reads via
        // ReadString (empty-string fallback matching schema DEFAULT ""); everything else
        // through ReadOrDefault<T> with the declared type so nullable values round-trip.
        if (!nullable && typeFqn is "string" or "global::System.String")
        {
            writer.Line($"{backing} = global::Disruptor.Surface.Runtime.HydrationValue.ReadString(__obj, {fieldLit});");
        }
        else
        {
            var deserialiseAs = nullable ? typeFqn : StripNullable(typeFqn);
            writer.Line($"{backing} = global::Disruptor.Surface.Runtime.HydrationValue.ReadOrDefault<{deserialiseAs}>(__obj, {fieldLit});");
        }
    }

    /// <summary>
    /// Emits <c>IEnumerable&lt;(string, RecordId?)&gt; IEntity.EnumerateReferences()</c>
    /// yielding the variant's two endpoints. Entity-typed endpoints expose the cached
    /// <c>_{name}Id</c> backing field directly; typed-id endpoints cast the typed-id
    /// backing field to <c>RecordId?</c> via the implicit conversion.
    /// <para>
    /// Variants don't need <c>IReferenceRegistry</c> registration — the substrate's
    /// <c>TYPE RELATION ENFORCED</c> handles edge cleanup on endpoint delete; library-side
    /// cleanup of <c>state.Edges</c> with the deleted endpoint already happens in
    /// <c>SurrealSession.CleanupLocalState</c>'s existing endpoint walk.
    /// <c>SurrealSession.MarkSaved</c> + <c>CleanupLocalState</c> additionally read
    /// these entries (via the <c>IRelationVariant</c> marker) to mirror the variant's
    /// own <c>(in, edge, out)</c> tuple in and out of the read-side edge index.
    /// </para>
    /// </summary>
    private static void EmitEnumerateReferences(CodeWriter writer, RelationVariantModel variant)
    {
        using (writer.Block($"global::System.Collections.Generic.IEnumerable<(string FieldName, global::Disruptor.Surface.Runtime.RecordId? Target)> {EntityEmitterCommon.EntityInterface}.EnumerateReferences()"))
        {
            EmitEnumerateReferenceEntry(writer, variant.In, fieldName: "in");
            EmitEnumerateReferenceEntry(writer, variant.Out, fieldName: "out");
        }
    }

    private static void EmitEnumerateReferenceEntry(CodeWriter writer, RelationVariantPropertyModel p, string fieldName)
    {
        var fieldLit = Quote(fieldName);
        if (p.Type.IsTableType)
        {
            // Entity-typed: the cached id backing field is already RecordId?.
            var idBacking = $"_{ToCamel(p.Name)}Id";
            writer.Line($"yield return ({fieldLit}, {idBacking});");
        }
        else
        {
            // Typed-id: cast through (RecordId?) using the implicit operator on {Name}Id.
            // Default-valued (unset) typed ids would convert to a RecordId with empty
            // Value — but EnumerateReferences is only called on hydrated/saved variants,
            // by which point both endpoints are required to be set.
            var backing = $"_{ToCamel(p.Name)}";
            writer.Line($"yield return ({fieldLit}, (global::Disruptor.Surface.Runtime.RecordId?)(global::Disruptor.Surface.Runtime.RecordId){backing});");
        }
    }

    /// <summary>
    /// Emits <c>void IEntity.SetReferenceTo(string, RecordId?)</c>. Only nullable
    /// <c>[In]</c> / <c>[Out]</c> endpoints contribute switch cases; non-nullable
    /// endpoints are mandatory and aren't part of the Unset phase. Most variants get an
    /// empty switch since both endpoints are required.
    /// </summary>
    private static void EmitSetReferenceTo(CodeWriter writer, RelationVariantModel variant)
    {
        var hasNullableEndpoint = variant.In.Type.IsNullable || variant.Out.Type.IsNullable;

        using (writer.Block($"void {EntityEmitterCommon.EntityInterface}.SetReferenceTo(string fieldName, global::Disruptor.Surface.Runtime.RecordId? value)"))
        {
            if (!hasNullableEndpoint)
            {
                // Skip the switch when no case will be emitted — both endpoints non-nullable
                // means there's nothing to unset, and an empty switch would trip CS1522.
                writer.Line("// Both [In] and [Out] are non-nullable; nothing to unset.");
                return;
            }

            writer.Line("switch (fieldName)");
            using (writer.BracedBlock())
            {
                EmitSetReferenceCase(writer, variant.In, fieldName: "in");
                EmitSetReferenceCase(writer, variant.Out, fieldName: "out");
            }
        }
    }

    private static void EmitSetReferenceCase(CodeWriter writer, RelationVariantPropertyModel p, string fieldName)
    {
        if (!p.Type.IsNullable)
        {
            // Non-nullable endpoint — bail out; the schema's REFERENCE ON DELETE clause
            // wouldn't be UNSET for a required edge endpoint anyway.
            return;
        }

        var fieldLit = Quote(fieldName);
        if (p.Type.IsTableType)
        {
            var idBacking = $"_{ToCamel(p.Name)}Id";
            var entityBacking = $"_{ToCamel(p.Name)}";
            writer.Line($"case {fieldLit}:");
            using (writer.Indent())
            {
                writer.Line($"{idBacking} = value;");
                writer.Line($"{entityBacking} = null;");
                writer.Line("break;");
            }
        }
        else
        {
            // Typed-id endpoint: clear the backing field. Nullable typed-id is uncommon
            // but legal — the user's `[In] partial OwnerId? Source { get; set; }`.
            var backing = $"_{ToCamel(p.Name)}";
            writer.Line($"case {fieldLit}:");
            using (writer.Indent())
            {
                writer.Line($"{backing} = null;");
                writer.Line("break;");
            }
        }
    }

    /// <summary>
    /// Emits the <c>IEntity.SaveAsync</c> body for a variant. Walks entity-typed [In]/[Out]
    /// endpoints as forward-deps (recurses into <c>ctx.SaveAsync</c> when the endpoint
    /// isn't yet tracked), then dispatches:
    /// <list type="bullet">
    ///   <item>Payload-bearing: <c>INSERT RELATION INTO {edge} $_content ON DUPLICATE KEY
    ///         UPDATE field1 = $_p_field1, …</c> — full-replace upsert.</item>
    ///   <item>Payload-less: <c>INSERT RELATION IGNORE INTO {edge} $_content;</c> —
    ///         first-call wins on duplicate (substrate-native idempotence).</item>
    /// </list>
    /// </summary>
    private static void EmitSaveAsync(CodeWriter writer, RelationVariantModel variant, RelationKindModel forward)
    {
        var edgeName = SurrealNaming.ToEdgeName(forward.Name);
        var hasPayload = variant.PayloadProperties.Count > 0;

        using (writer.Block($"async global::System.Threading.Tasks.Task {EntityEmitterCommon.EntityInterface}.SaveAsync(global::Disruptor.Surface.Runtime.ISaveContext ctx, global::System.Threading.CancellationToken ct)"))
        {
            writer.Line("var __id = ((global::Disruptor.Surface.Runtime.IEntity)this).Id;");
            writer.Line();

            // Forward-dep walk: entity-typed endpoints whose ref is set but not yet tracked
            // recurse so the endpoint row exists in the substrate before we INSERT RELATION
            // (the foreign key from the edge to the endpoint is checked at write time).
            // Typed-id endpoints skip this — caller is expected to have created the foreign
            // entity in a separate session/aggregate.
            var hasForwardDep = false;
            foreach (var endpoint in new[] { variant.In, variant.Out })
            {
                if (!endpoint.Type.IsTableType)
                {
                    continue;
                }
                hasForwardDep = true;
                var backing = $"_{ToCamel(endpoint.Name)}";
                writer.Line($"if ({backing} is not null && !ctx.IsTracked((({EntityEmitterCommon.EntityInterface}){backing}).Id))");
                using (writer.Indent())
                {
                    writer.Line($"await ctx.SaveAsync({backing}, ct);");
                }
            }

            if (hasForwardDep)
            {
                writer.Line();
            }

            var inEndpointId = EmitEndpointIdResolution(writer, variant.In);
            var outEndpointId = EmitEndpointIdResolution(writer, variant.Out);
            if (variant.In.Type.IsTableType || variant.Out.Type.IsTableType)
            {
                writer.Line();
            }

            // Build the wire content: id + in + out + payload fields.
            writer.Line("var __content = new global::Disruptor.Surreal.Values.SurrealObject");
            writer.Line("{");
            using (writer.Indent())
            {
                writer.Line("[\"id\"] = new global::Disruptor.Surreal.Values.SurrealRecordIdValue(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk(__id)),");
                EmitContentEndpoint(writer, variant.In, "in", inEndpointId);
                EmitContentEndpoint(writer, variant.Out, "out", outEndpointId);
            }
            writer.Line("};");

            // Per-payload field: same ContentValue.Set helper as the typed-CBOR scalar path
            // on entity SaveAsync. Nullable values are omitted when null so the schema
            // DEFAULT applies.
            foreach (var p in variant.PayloadProperties)
            {
                var backing = $"_{ToCamel(p.Name)}";
                var fieldLit = Quote(p.FieldName);
                writer.Line($"global::Disruptor.Surface.Runtime.ContentValue.Set(__content, {fieldLit}, {backing});");
            }

            writer.Line();
            writer.Line("var __bindings = new global::Disruptor.Surreal.Values.SurrealObject");
            writer.Line("{");
            using (writer.Indent())
            {
                writer.Line("[\"_content\"] = new global::Disruptor.Surreal.Values.SurrealObjectValue(__content),");
            }
            writer.Line("};");

            // For payload-bearing variants, bind each payload field separately as $_p_{field}
            // so the SET clause can reference them. ContentValue.Set picks the right
            // SurrealValue wrapping per scalar type (and omits null values for nullable
            // payloads).
            if (hasPayload)
            {
                foreach (var p in variant.PayloadProperties)
                {
                    var backing = $"_{ToCamel(p.Name)}";
                    var bindLit = Quote($"_p_{p.FieldName}");
                    writer.Line($"global::Disruptor.Surface.Runtime.ContentValue.Set(__bindings, {bindLit}, {backing});");
                }
            }

            writer.Line();

            // SQL — baked at codegen time. Edge name is snake_case lower (validated by
            // SurrealNaming.ToEdgeName); payload field names are snake_case lower
            // (validated by SurrealNaming.ToFieldName). Both are safe to inline in SurrealQL.
            if (hasPayload)
            {
                var updateClause = string.Join(", ", variant.PayloadProperties.Select(p => $"{p.FieldName} = $_p_{p.FieldName}"));
                writer.Line($"const string __sql = \"INSERT RELATION INTO {edgeName} $_content ON DUPLICATE KEY UPDATE {updateClause};\";");
            }
            else
            {
                writer.Line($"const string __sql = \"INSERT RELATION IGNORE INTO {edgeName} $_content;\";");
            }

            writer.Line("var __response = await ctx.Transaction.QueryAsync(__sql, __bindings, ct).ConfigureAwait(false);");
            writer.Line("__response.EnsureSuccess();");
            writer.Line("ctx.MarkSaved(this);");
        }
    }

    /// <summary>
    /// Emits the trio of IEntity hooks that variants don't otherwise contribute to:
    /// <list type="bullet">
    ///   <item><c>Initialize</c> — empty body. Variants have no mandatory-reference
    ///         seeding hooks (the [In] / [Out] endpoints are required at construction
    ///         and ALSO immutable-after-set; no analogue to <c>OnCreate{Name}</c>).</item>
    ///   <item><c>OnDeleting</c> — simple-form partial method + explicit dispatch, same
    ///         shape as <see cref="PartialEmitter"/>. User opts into cleanup by
    ///         implementing the partial.</item>
    ///   <item><c>MarkAllSlicesLoaded</c> — empty body. Variants have no slices: their
    ///         state IS the (id, in, out, payload) row. There's nothing to lazy-load
    ///         beyond what <see cref="IEntity.Hydrate"/> already wrote.</item>
    /// </list>
    /// </summary>
    private static void EmitInitializeAndDeletingAndSlices(CodeWriter writer)
    {
        writer.Line($"void {EntityEmitterCommon.EntityInterface}.Initialize({EntityEmitterCommon.SessionType} session) {{ }}");
        writer.Line();
        writer.Line("partial void OnDeleting();");
        writer.Line($"void {EntityEmitterCommon.EntityInterface}.OnDeleting() => OnDeleting();");
        writer.Line();
        writer.Line($"void {EntityEmitterCommon.EntityInterface}.MarkAllSlicesLoaded({EntityEmitterCommon.HydrationSinkType} sink) {{ }}");
    }

    private static string? EmitEndpointIdResolution(CodeWriter writer, RelationVariantPropertyModel p)
    {
        if (!p.Type.IsTableType)
        {
            return null;
        }

        var entityBacking = $"_{ToCamel(p.Name)}";
        var idBacking = $"_{ToCamel(p.Name)}Id";
        var idLocal = $"__{ToCamel(p.Name)}Id";
        var entityLocal = $"__{ToCamel(p.Name)}Entity";

        writer.Line($"var {idLocal} = {idBacking} ?? ({entityBacking} is {{ }} {entityLocal} ? (({EntityEmitterCommon.EntityInterface}){entityLocal}).Id : throw new global::System.InvalidOperationException(\"Endpoint '{p.Name}' is not set.\"));");

        return idLocal;
    }

    private static void EmitContentEndpoint(CodeWriter writer, RelationVariantPropertyModel p, string fieldName, string? resolvedEndpointId)
    {
        var fieldLit = Quote(fieldName);
        if (p.Type.IsTableType)
        {
            // Entity-typed endpoints were resolved into explicit locals above so a
            // missing endpoint produces a clear error instead of a nullable dereference.
            writer.Line($"[{fieldLit}] = new global::Disruptor.Surreal.Values.SurrealRecordIdValue(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk({resolvedEndpointId})),");
        }
        else
        {
            // Typed-id: the backing field is itself an IRecordId.
            var backing = $"_{ToCamel(p.Name)}";
            writer.Line($"[{fieldLit}] = new global::Disruptor.Surreal.Values.SurrealRecordIdValue(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk((global::Disruptor.Surface.Runtime.IRecordId){backing})),");
        }
    }

    /// <summary>
    /// Emits a pure backing-field <c>[Property]</c> payload member — same shape as
    /// <see cref="PartialEmitter"/>'s <c>EmitDataProperty</c>. No Session interaction,
    /// no slice tracking. Save reads the backing field at dispatch time.
    /// </summary>
    private static void EmitPayloadProperty(CodeWriter writer, RelationVariantPropertyModel p)
    {
        var type = p.Type.FullyQualifiedName;
        var access = FormatAccessibility(p.DeclaredAccessibility);
        var backing = $"_{ToCamel(p.Name)}";

        writer.Line("#pragma warning disable CS0649");
        writer.Line($"private {type} {backing} = default!;");
        writer.Line("#pragma warning restore CS0649");

        if (!p.HasSetter && !p.HasInitOnlySetter)
        {
            writer.Line($"{access} partial {type} {p.Name} => {backing};");
            return;
        }

        writer.Line($"{access} partial {type} {p.Name}");
        using (writer.BracedBlock())
        {
            writer.Line($"get => {backing};");
            writer.Line($"{(p.HasInitOnlySetter ? "init" : "set")} => {backing} = value;");
        }
    }

    /// <summary>
    /// Resolves the FQN of the per-kind <c>{MarkerName}Id</c> type emitted by
    /// <see cref="RelationKindEmitter"/>. Same namespace as the forward kind.
    /// </summary>
    private static string ResolveKindIdFqn(RelationKindModel forward, string markerName)
        => string.IsNullOrEmpty(forward.Namespace)
            ? $"global::{markerName}Id"
            : $"global::{forward.Namespace}.{markerName}Id";

    // ──────────────────────── helpers ──────────────────────────────────────────

    private static string ToCamel(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string StripNullable(string typeName)
        => typeName.EndsWith("?") ? typeName[..^1] : typeName;

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";

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

    // ──────────────────────── per-kind marker interface ────────────────────────

    /// <summary>
    /// Emits <c>public interface I{KindName}Variant : IEntity { }</c> — the per-kind
    /// marker that every variant of a multi-variant kind implements. Mirrors
    /// <see cref="UnionInterfaceEmitter"/>'s entity-side template. Single-variant kinds
    /// skip emission since the variant class itself is the discriminator (and the
    /// marker would be a single-member interface with no value).
    /// </summary>
    private static void EmitVariantMarkerInterface(
        SourceProductionContext spc,
        RelationKindModel forward,
        IReadOnlyList<RelationVariantModel> variants)
    {
        var markerName = SurrealNaming.StripAttributeSuffix(forward.Name);
        var interfaceName = $"I{markerName}Variant";

        var writer = new CodeWriter().Header();
        using (writer.Namespace(forward.Namespace))
        {
            writer.Line($"/// <summary>Generated marker for any variant of the relation kind <c>{forward.FullName}</c>.</summary>");
            writer.Line($"public interface {interfaceName} : {EntityEmitterCommon.EntityInterface} {{ }}");
        }

        var hint = string.IsNullOrEmpty(forward.Namespace)
            ? $"{interfaceName}.g.cs"
            : $"{forward.Namespace}.{interfaceName}.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }

    // ──────────────────────── per-kind hydration dispatcher ────────────────────

    /// <summary>
    /// Emits <c>{KindName}Hydration.HydrateVariant(SurrealValue, IHydrationSink)</c> —
    /// a static dispatcher that picks the right variant class to instantiate based on
    /// the loaded edge row's <c>(in.tb, out.tb)</c>. Even single-variant kinds get the
    /// dispatcher so the loader has a uniform call site.
    /// <para>
    /// Endpoint-pair collisions (two variants sharing the same <c>(InType, OutType)</c>)
    /// are reported via <see cref="Diagnostics.VariantEndpointPairCollision"/>; the
    /// dispatcher is suppressed for the affected kind so the emitted code doesn't
    /// silently pick one variant over the other.
    /// </para>
    /// </summary>
    private static void EmitHydrationDispatcher(
        SourceProductionContext spc,
        RelationKindModel forward,
        IReadOnlyList<RelationVariantModel> variants)
    {
        // Resolve each variant's (in-table, out-table) pair. Entity-typed endpoints use
        // the entity type's pluralised table name; typed-id endpoints strip the trailing
        // "Id" off the simple type name and pluralise.
        var pairs = new List<(string InTable, string OutTable, RelationVariantModel Variant)>();
        foreach (var variant in variants)
        {
            pairs.Add((
                ResolveEndpointTableName(variant.In),
                ResolveEndpointTableName(variant.Out),
                variant));
        }

        // Pair-collision detection — two variants with the same (in.tb, out.tb) would
        // make the dispatcher ambiguous. Group + report the offenders, then bail.
        var collisions = pairs
            .GroupBy(p => (p.InTable, p.OutTable))
            .Where(g => g.Count() > 1)
            .ToList();
        if (collisions.Count > 0)
        {
            foreach (var group in collisions)
            {
                var offenders = string.Join(", ", group.Select(g => g.Variant.FullName));
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.VariantEndpointPairCollision,
                    Location.None,
                    forward.FullName,
                    $"({group.Key.InTable}, {group.Key.OutTable}) shared by {offenders}"));
            }
            return;
        }

        var markerName = SurrealNaming.StripAttributeSuffix(forward.Name);
        var dispatcherName = $"{markerName}Hydration";

        var writer = new CodeWriter().Header();
        using (writer.Namespace(forward.Namespace))
        {
            writer.Line($"/// <summary>Per-kind variant hydration dispatcher for the relation kind <c>{forward.FullName}</c>.</summary>");
            using (writer.Block($"internal static class {dispatcherName}"))
            {
                writer.Line("/// <summary>Discriminates the loaded edge row by <c>(in.tb, out.tb)</c> and instantiates the matching variant.</summary>");
                using (writer.Block($"public static {EntityEmitterCommon.EntityInterface}? HydrateVariant(global::Disruptor.Surreal.Values.SurrealValue row, {EntityEmitterCommon.HydrationSinkType} sink)"))
                {
                    writer.Line("if (row is not global::Disruptor.Surreal.Values.SurrealObjectValue __obj) return null;");
                    writer.Line("if (!__obj.Object.TryGetValue(\"in\", out var __inV)) return null;");
                    writer.Line("if (!__obj.Object.TryGetValue(\"out\", out var __outV)) return null;");
                    writer.Line("var __inTb = global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__inV).Table;");
                    writer.Line("var __outTb = global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__outV).Table;");
                    writer.Line($"{EntityEmitterCommon.EntityInterface}? __variant = (__inTb, __outTb) switch");
                    writer.Line("{");
                    using (writer.Indent())
                    {
                        foreach (var (inTable, outTable, variant) in pairs)
                        {
                            writer.Line($"({Quote(inTable)}, {Quote(outTable)}) => new global::{variant.FullName}(),");
                        }

                        writer.Line("_ => null,");
                    }
                    writer.Line("};");
                    writer.Line("if (__variant is null) return null;");
                    writer.Line("__variant.Hydrate(row, sink);");
                    writer.Line("return __variant;");
                }
            }
        }

        var hint = string.IsNullOrEmpty(forward.Namespace)
            ? $"{dispatcherName}.g.cs"
            : $"{forward.Namespace}.{dispatcherName}.g.cs";
        spc.AddSource(hint, writer.ToSourceText());
    }

    /// <summary>
    /// Resolves the SurrealDB table name corresponding to an [In] / [Out] endpoint's
    /// declared type. Entity-typed endpoints (within-aggregate): pluralise the simple
    /// type name (<c>Constraint</c> → <c>constraints</c>). Typed-id endpoints
    /// (cross-aggregate): strip the trailing <c>Id</c> from the simple name first
    /// (<c>OwnerId</c> → <c>Owner</c> → <c>owners</c>).
    /// </summary>
    private static string ResolveEndpointTableName(RelationVariantPropertyModel p)
    {
        var simple = SurrealNaming.SimpleName(p.Type.FullyQualifiedName);
        if (!p.Type.IsTableType && simple.EndsWith("Id", StringComparison.Ordinal) && simple.Length > 2)
        {
            simple = simple[..^"Id".Length];
        }
        return SurrealNaming.ToTableName(simple);
    }
}
