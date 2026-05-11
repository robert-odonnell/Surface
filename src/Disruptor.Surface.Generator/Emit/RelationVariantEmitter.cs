using System.Text;
using Disruptor.Surface.Generator.Model;
using Disruptor.Surface.Generator.Pipeline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

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

        var sb = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine();

        var hasNamespace = !string.IsNullOrEmpty(variant.Namespace);
        if (hasNamespace)
        {
            sb.Append("namespace ").Append(variant.Namespace).AppendLine();
            sb.AppendLine("{");
        }

        var indent = hasNamespace ? "    " : string.Empty;
        var memberIndent = $"{indent}    ";

        sb.Append(indent)
          .Append(FormatAccessibility(variant.DeclaredAccessibility))
          .Append(" partial class ")
          .Append(variant.Name)
          .Append(" : ")
          .Append(EntityEmitterCommon.EntityInterface)
          // Mark every variant with IRelationVariant so SurrealSession.SaveContext.MarkSaved
          // and CleanupLocalState can branch on edge-shaped vs table-shaped entities — the
          // marker is methodless because variants expose everything Session needs via
          // IEntity (Id.Table for edge name, EnumerateReferences() for endpoint ids).
          .Append(", ").Append(EntityEmitterCommon.RelationVariantInterface);

        // Multi-variant kinds also implement the per-kind I{KindName}Variant marker so
        // session APIs can talk about "any variant of this kind" generically. Single-
        // variant kinds skip the marker (the variant class itself is the discriminator).
        if (totalVariantsInKind >= 2)
        {
            var variantInterfaceFqn = string.IsNullOrEmpty(forward.Namespace)
                ? $"global::I{markerName}Variant"
                : $"global::{forward.Namespace}.I{markerName}Variant";
            sb.Append(", ").Append(variantInterfaceFqn);
        }
        sb.AppendLine();

        sb.Append(indent).AppendLine("{");

        EntityEmitterCommon.WriteSessionPlumbing(sb, memberIndent);

        // Per-variant id anchor — uses the per-kind {MarkerName}Id type (shared across
        // every variant of this kind, since they all live on the same edge table). New()
        // mints a Ulid; Hydrate parses the loaded edge id back into the typed wrapper.
        EmitIdAnchor(sb, memberIndent, idTypeFqn);

        // Endpoint + payload properties — backing fields + property bodies. [In] / [Out]
        // are required endpoints (one each); [Property] members carry the typed payload.
        sb.AppendLine();
        EmitEndpointProperty(sb, memberIndent, variant.In, typedIdNamespaces);
        sb.AppendLine();
        EmitEndpointProperty(sb, memberIndent, variant.Out, typedIdNamespaces);

        foreach (var p in variant.PayloadProperties)
        {
            sb.AppendLine();
            EmitPayloadProperty(sb, memberIndent, p);
        }

        // Hydrate — parses the loaded edge row (id / in / out / payload fields) into
        // backing fields. Per-variant Hydrate doesn't discriminate; the per-kind dispatcher
        // picks the right variant class first based on (in.tb, out.tb).
        sb.AppendLine();
        EmitHydrate(sb, memberIndent, variant, idTypeFqn, typedIdNamespaces);

        // EnumerateReferences — yields ("in", inId) and ("out", outId). Variants don't
        // need IReferenceRegistry registration: the substrate's TYPE RELATION ENFORCED
        // handles edge cleanup on endpoint delete, and library-side cleanup of
        // state.Edges with the deleted endpoint already happens in CleanupLocalState's
        // existing endpoint walk. SurrealSession.MarkSaved + CleanupLocalState use this
        // method (via the IRelationVariant marker) to mirror the variant's own edge
        // tuple in/out of the read-side index.
        sb.AppendLine();
        EmitEnumerateReferences(sb, memberIndent, variant);

        // SetReferenceTo — only meaningful for nullable [In] / [Out] (rare; non-nullable
        // endpoints can't be unset). Empty switch when nothing is nullable.
        sb.AppendLine();
        EmitSetReferenceTo(sb, memberIndent, variant);

        // SaveAsync — dispatches INSERT RELATION INTO {edge} $_content [ON DUPLICATE KEY
        // UPDATE …] against the user's transaction. Forward-deps walk for entity-typed
        // endpoints; pure pass-through for typed-id endpoints.
        sb.AppendLine();
        EmitSaveAsync(sb, memberIndent, variant, forward);

        // Initialize / OnDeleting / MarkAllSlicesLoaded — IEntity contract completion.
        // Variants have no mandatory-reference seeding (endpoints are required at
        // construction), no slices (state is the row itself), and may opt into delete
        // hooks via the partial method.
        sb.AppendLine();
        EmitInitializeAndDeletingAndSlices(sb, memberIndent);

        sb.Append(indent).AppendLine("}");

        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        var hint = $"{variant.FullName}.RelationVariant.g.cs";
        spc.AddSource(hint, SourceText.From(sb.ToString(), Encoding.UTF8));
    }

    private static void EmitIdAnchor(StringBuilder sb, string indent, string idTypeFqn)
    {
        sb.AppendLine()
          .Append(indent).Append("private ").Append(idTypeFqn).AppendLine("? _id;")
          .Append(indent).Append("global::Disruptor.Surface.Runtime.RecordId ")
          .Append(EntityEmitterCommon.EntityInterface)
          .Append(".Id => _id ??= ").Append(idTypeFqn).AppendLine(".New();");
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
        StringBuilder sb, string indent, RelationVariantPropertyModel p,
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

            sb.Append(indent).Append("private ").Append(typeArg).Append("? ").Append(backing).AppendLine(";")
              .Append(indent).Append("private global::Disruptor.Surface.Runtime.RecordId? ").Append(idBacking).AppendLine(";")
              .Append(indent).Append(access).Append(" partial ").Append(declared).Append(' ').AppendLine(p.Name)
              .Append(indent).AppendLine("{");

            if (!nullable)
            {
                sb.Append(indent).AppendLine("    get")
                  .Append(indent).AppendLine("    {")
                  .Append(indent).Append("        var __resolved = ").Append(backing).Append(" ?? (").Append(idBacking)
                      .Append(" is { } __id ? _session?.Get<").Append(typeArg).AppendLine(">(__id) : null);")
                  .Append(indent).Append("        return __resolved ?? throw new global::System.InvalidOperationException(\"Endpoint '")
                      .Append(p.Name).AppendLine("' is not set.\");")
                  .Append(indent).AppendLine("    }");
            }
            else
            {
                sb.Append(indent).AppendLine("    get")
                  .Append(indent).AppendLine("    {")
                  .Append(indent).Append("        return ").Append(backing).Append(" ?? (").Append(idBacking)
                      .Append(" is { } __id ? _session?.Get<").Append(typeArg).AppendLine(">(__id) : null);")
                  .Append(indent).AppendLine("    }");
            }

            if (p.HasSetter || p.HasInitOnlySetter)
            {
                sb.Append(indent).Append("    ").AppendLine(p.HasInitOnlySetter ? "init" : "set")
                  .Append(indent).AppendLine("    {")
                  .Append(indent).Append("        ").Append(backing).AppendLine(" = value;")
                  .Append(indent).Append("        ").Append(idBacking).AppendLine(nullable
                      ? $" = value is null ? null : (({EntityEmitterCommon.EntityInterface})value).Id;"
                      : $" = (({EntityEmitterCommon.EntityInterface})value).Id;")
                  .Append(indent).AppendLine("    }");
            }

            sb.Append(indent).AppendLine("}");
        }
        else
        {
            // Typed-id endpoint (cross-aggregate): single backing field of the id type.
            // Pure pass-through — no Session interaction, no entity ref cache. Caller
            // resolves the entity separately if needed (e.g. via Session.Get).
            sb.Append(indent).Append("#pragma warning disable CS0649").AppendLine()
              .Append(indent).Append("private ").Append(declared).Append(' ').Append(backing).AppendLine(" = default!;")
              .Append(indent).Append("#pragma warning restore CS0649").AppendLine();

            if (!p.HasSetter && !p.HasInitOnlySetter)
            {
                sb.Append(indent).Append(access).Append(" partial ").Append(declared).Append(' ').Append(p.Name)
                  .Append(" => ").Append(backing).AppendLine(";");
                return;
            }

            sb.Append(indent).Append(access).Append(" partial ").Append(declared).Append(' ').AppendLine(p.Name)
              .Append(indent).AppendLine("{")
              .Append(indent).Append("    get => ").Append(backing).AppendLine(";")
              .Append(indent).Append("    ").Append(p.HasInitOnlySetter ? "init" : "set").Append(" => ").Append(backing).AppendLine(" = value;")
              .Append(indent).AppendLine("}");
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
        StringBuilder sb, string indent, RelationVariantModel variant, string idTypeFqn,
        IReadOnlyDictionary<string, string> typedIdNamespaces)
    {
        sb.Append(indent).Append("void ").Append(EntityEmitterCommon.EntityInterface)
          .Append(".Hydrate(global::Disruptor.Surreal.Values.SurrealValue row, ")
          .Append(EntityEmitterCommon.HydrationSinkType).AppendLine(" sink)")
          .Append(indent).AppendLine("{")
          .Append(indent).AppendLine("    if (row is not global::Disruptor.Surreal.Values.SurrealObjectValue __obj) return;")
          .Append(indent).AppendLine("    if (__obj.Object.TryGetValue(\"id\", out var __idVal))")
          .Append(indent).Append("        _id = new ").Append(idTypeFqn).AppendLine("(global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__idVal).Value);")
          .Append(indent).AppendLine("    sink.Track(this);");

        // [In] / [Out] endpoints: the edge row's "in" / "out" fields carry the endpoint
        // ids. Entity-typed endpoints set the cached id backing field (resolved via
        // Session.Get on read); typed-id endpoints wrap into the typed id struct.
        EmitHydrateEndpoint(sb, indent, variant.In, fieldName: "in", typedIdNamespaces);
        EmitHydrateEndpoint(sb, indent, variant.Out, fieldName: "out", typedIdNamespaces);

        // [Property] payload members: same scalar-read shape as PartialEmitter.
        foreach (var p in variant.PayloadProperties)
        {
            EmitHydratePayload(sb, indent, p);
        }

        sb.Append(indent).AppendLine("}");
    }

    private static void EmitHydrateEndpoint(
        StringBuilder sb, string indent, RelationVariantPropertyModel p, string fieldName,
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
            sb.Append(indent).Append("    ").Append(idBacking)
              .Append(" = global::Disruptor.Surface.Runtime.HydrationValue.TryReadReferenceId(__obj, ")
              .Append(fieldLit).AppendLine(");");
        }
        else
        {
            // Typed-id endpoint: parse the id and wrap into the declared id type. The
            // generated {Name}Id struct's primary ctor validates via RecordIdFormat,
            // so a malformed id throws at hydration time. Resolve the bare type-name to
            // its FQN — the variant `.g.cs` lives in a different namespace from the id
            // struct (cross-aggregate case) and has no using directives of its own.
            var declared = ResolveEndpointTypeFqn(p.Type.FullyQualifiedName, typedIdNamespaces);
            sb.Append(indent).AppendLine("    {")
              .Append(indent).Append("        if (__obj.Object.TryGetValue(").Append(fieldLit).AppendLine(", out var __ev))")
              .Append(indent).Append("            ").Append(backing).Append(" = new ").Append(StripNullable(declared))
              .AppendLine("(global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__ev).Value);")
              .Append(indent).AppendLine("    }");
        }
    }

    private static void EmitHydratePayload(StringBuilder sb, string indent, RelationVariantPropertyModel p)
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
            sb.Append(indent).Append("    ").Append(backing)
              .Append(" = global::Disruptor.Surface.Runtime.HydrationValue.ReadString(__obj, ")
              .Append(fieldLit).AppendLine(");");
        }
        else
        {
            var deserialiseAs = nullable ? typeFqn : StripNullable(typeFqn);
            sb.Append(indent).Append("    ").Append(backing)
              .Append(" = global::Disruptor.Surface.Runtime.HydrationValue.ReadOrDefault<")
              .Append(deserialiseAs).Append(">(__obj, ").Append(fieldLit).AppendLine(");");
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
    private static void EmitEnumerateReferences(StringBuilder sb, string indent, RelationVariantModel variant)
    {
        sb.Append(indent)
          .Append("global::System.Collections.Generic.IEnumerable<(string FieldName, global::Disruptor.Surface.Runtime.RecordId? Target)> ")
          .Append(EntityEmitterCommon.EntityInterface)
          .AppendLine(".EnumerateReferences()")
          .Append(indent).AppendLine("{");

        EmitEnumerateReferenceEntry(sb, indent, variant.In, fieldName: "in");
        EmitEnumerateReferenceEntry(sb, indent, variant.Out, fieldName: "out");

        sb.Append(indent).AppendLine("}");
    }

    private static void EmitEnumerateReferenceEntry(StringBuilder sb, string indent, RelationVariantPropertyModel p, string fieldName)
    {
        var fieldLit = Quote(fieldName);
        if (p.Type.IsTableType)
        {
            // Entity-typed: the cached id backing field is already RecordId?.
            var idBacking = $"_{ToCamel(p.Name)}Id";
            sb.Append(indent).Append("    yield return (").Append(fieldLit).Append(", ").Append(idBacking).AppendLine(");");
        }
        else
        {
            // Typed-id: cast through (RecordId?) using the implicit operator on {Name}Id.
            // Default-valued (unset) typed ids would convert to a RecordId with empty
            // Value — but EnumerateReferences is only called on hydrated/saved variants,
            // by which point both endpoints are required to be set.
            var backing = $"_{ToCamel(p.Name)}";
            sb.Append(indent).Append("    yield return (").Append(fieldLit)
              .Append(", (global::Disruptor.Surface.Runtime.RecordId?)(global::Disruptor.Surface.Runtime.RecordId)")
              .Append(backing).AppendLine(");");
        }
    }

    /// <summary>
    /// Emits <c>void IEntity.SetReferenceTo(string, RecordId?)</c>. Only nullable
    /// <c>[In]</c> / <c>[Out]</c> endpoints contribute switch cases; non-nullable
    /// endpoints are mandatory and aren't part of the Unset phase. Most variants get an
    /// empty switch since both endpoints are required.
    /// </summary>
    private static void EmitSetReferenceTo(StringBuilder sb, string indent, RelationVariantModel variant)
    {
        var hasNullableEndpoint = variant.In.Type.IsNullable || variant.Out.Type.IsNullable;

        sb.Append(indent).Append("void ").Append(EntityEmitterCommon.EntityInterface)
          .AppendLine(".SetReferenceTo(string fieldName, global::Disruptor.Surface.Runtime.RecordId? value)");

        if (!hasNullableEndpoint)
        {
            // Skip the switch when no case will be emitted — both endpoints non-nullable
            // means there's nothing to unset, and an empty switch would trip CS1522.
            sb.Append(indent).AppendLine("{")
              .Append(indent).AppendLine("    // Both [In] and [Out] are non-nullable; nothing to unset.")
              .Append(indent).AppendLine("}");
            return;
        }

        sb.Append(indent).AppendLine("{")
          .Append(indent).AppendLine("    switch (fieldName)")
          .Append(indent).AppendLine("    {");

        EmitSetReferenceCase(sb, indent, variant.In, fieldName: "in");
        EmitSetReferenceCase(sb, indent, variant.Out, fieldName: "out");

        sb.Append(indent).AppendLine("    }")
          .Append(indent).AppendLine("}");
    }

    private static void EmitSetReferenceCase(StringBuilder sb, string indent, RelationVariantPropertyModel p, string fieldName)
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
            sb.Append(indent).Append("        case ").Append(fieldLit).AppendLine(":")
              .Append(indent).Append("            ").Append(idBacking).AppendLine(" = value;")
              .Append(indent).Append("            ").Append(entityBacking).AppendLine(" = null;")
              .Append(indent).AppendLine("            break;");
        }
        else
        {
            // Typed-id endpoint: clear the backing field. Nullable typed-id is uncommon
            // but legal — the user's `[In] partial OwnerId? Source { get; set; }`.
            var backing = $"_{ToCamel(p.Name)}";
            sb.Append(indent).Append("        case ").Append(fieldLit).AppendLine(":")
              .Append(indent).Append("            ").Append(backing).AppendLine(" = null;")
              .Append(indent).AppendLine("            break;");
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
    private static void EmitSaveAsync(StringBuilder sb, string indent, RelationVariantModel variant, RelationKindModel forward)
    {
        var edgeName = SurrealNaming.ToEdgeName(forward.Name);
        var hasPayload = variant.PayloadProperties.Count > 0;

        sb.Append(indent)
          .Append("async global::System.Threading.Tasks.Task ")
          .Append(EntityEmitterCommon.EntityInterface)
          .AppendLine(".SaveAsync(global::Disruptor.Surface.Runtime.ISaveContext ctx, global::System.Threading.CancellationToken ct)")
          .Append(indent).AppendLine("{")
          .Append(indent).AppendLine("    var __id = ((global::Disruptor.Surface.Runtime.IEntity)this).Id;")
          .AppendLine();

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
            sb.Append(indent).Append("    if (").Append(backing).Append(" is not null && !ctx.IsTracked(((")
              .Append(EntityEmitterCommon.EntityInterface).Append(')').Append(backing).AppendLine(").Id))")
              .Append(indent).Append("        await ctx.SaveAsync(").Append(backing).AppendLine(", ct);");
        }
        if (hasForwardDep)
        {
            sb.AppendLine();
        }

        var inEndpointId = EmitEndpointIdResolution(sb, indent, variant.In);
        var outEndpointId = EmitEndpointIdResolution(sb, indent, variant.Out);
        if (variant.In.Type.IsTableType || variant.Out.Type.IsTableType)
        {
            sb.AppendLine();
        }

        // Build the wire content: id + in + out + payload fields.
        sb.Append(indent).AppendLine("    var __content = new global::Disruptor.Surreal.Values.SurrealObject")
          .Append(indent).AppendLine("    {")
          .Append(indent).AppendLine("        [\"id\"] = new global::Disruptor.Surreal.Values.SurrealRecordIdValue(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk(__id)),");

        EmitContentEndpoint(sb, indent, variant.In, "in", inEndpointId);
        EmitContentEndpoint(sb, indent, variant.Out, "out", outEndpointId);

        sb.Append(indent).AppendLine("    };");

        // Per-payload field: same ContentValue.Set helper as the typed-CBOR scalar path
        // on entity SaveAsync. Nullable values are omitted when null so the schema
        // DEFAULT applies.
        foreach (var p in variant.PayloadProperties)
        {
            var backing = $"_{ToCamel(p.Name)}";
            var fieldLit = Quote(p.FieldName);
            sb.Append(indent).Append("    global::Disruptor.Surface.Runtime.ContentValue.Set(__content, ")
              .Append(fieldLit).Append(", ").Append(backing).AppendLine(");");
        }

        sb.AppendLine();
        sb.Append(indent).AppendLine("    var __bindings = new global::Disruptor.Surreal.Values.SurrealObject")
          .Append(indent).AppendLine("    {")
          .Append(indent).AppendLine("        [\"_content\"] = new global::Disruptor.Surreal.Values.SurrealObjectValue(__content),")
          .Append(indent).AppendLine("    };");

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
                sb.Append(indent).Append("    global::Disruptor.Surface.Runtime.ContentValue.Set(__bindings, ")
                  .Append(bindLit).Append(", ").Append(backing).AppendLine(");");
            }
        }

        sb.AppendLine();

        // SQL — baked at codegen time. Edge name is snake_case lower (validated by
        // SurrealNaming.ToEdgeName); payload field names are snake_case lower
        // (validated by SurrealNaming.ToFieldName). Both are safe to inline in SurrealQL.
        sb.Append(indent).Append("    const string __sql = ");
        if (hasPayload)
        {
            sb.Append('"').Append("INSERT RELATION INTO ").Append(edgeName)
              .Append(" $_content ON DUPLICATE KEY UPDATE ");
            for (var i = 0; i < variant.PayloadProperties.Count; i++)
            {
                var pf = variant.PayloadProperties[i];
                if (i > 0)
                {
                    sb.Append(", ");
                }
                sb.Append(pf.FieldName).Append(" = $_p_").Append(pf.FieldName);
            }
            sb.Append(';').Append('"').AppendLine(";");
        }
        else
        {
            sb.Append('"').Append("INSERT RELATION IGNORE INTO ").Append(edgeName).Append(" $_content;\";").AppendLine();
        }

        sb.Append(indent).AppendLine("    var __response = await ctx.Transaction.QueryAsync(__sql, __bindings, ct).ConfigureAwait(false);")
          .Append(indent).AppendLine("    __response.EnsureSuccess();")
          .Append(indent).AppendLine("    ctx.MarkSaved(this);")
          .Append(indent).AppendLine("}");
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
    private static void EmitInitializeAndDeletingAndSlices(StringBuilder sb, string indent)
    {
        sb.Append(indent).Append("void ").Append(EntityEmitterCommon.EntityInterface)
          .Append(".Initialize(").Append(EntityEmitterCommon.SessionType).AppendLine(" session) { }")
          .AppendLine()
          .Append(indent).AppendLine("partial void OnDeleting();")
          .Append(indent).Append("void ").Append(EntityEmitterCommon.EntityInterface).AppendLine(".OnDeleting() => OnDeleting();")
          .AppendLine()
          .Append(indent).Append("void ").Append(EntityEmitterCommon.EntityInterface)
          .Append(".MarkAllSlicesLoaded(").Append(EntityEmitterCommon.HydrationSinkType).AppendLine(" sink) { }");
    }

    private static string? EmitEndpointIdResolution(StringBuilder sb, string indent, RelationVariantPropertyModel p)
    {
        if (!p.Type.IsTableType)
        {
            return null;
        }

        var entityBacking = $"_{ToCamel(p.Name)}";
        var idBacking = $"_{ToCamel(p.Name)}Id";
        var idLocal = $"__{ToCamel(p.Name)}Id";
        var entityLocal = $"__{ToCamel(p.Name)}Entity";

        sb.Append(indent).Append("    var ").Append(idLocal).Append(" = ").Append(idBacking)
          .Append(" ?? (").Append(entityBacking).Append(" is { } ").Append(entityLocal)
          .Append(" ? ((").Append(EntityEmitterCommon.EntityInterface).Append(')').Append(entityLocal).Append(").Id")
          .Append(" : throw new global::System.InvalidOperationException(\"Endpoint '").Append(p.Name).AppendLine("' is not set.\"));");

        return idLocal;
    }

    private static void EmitContentEndpoint(StringBuilder sb, string indent, RelationVariantPropertyModel p, string fieldName, string? resolvedEndpointId)
    {
        var fieldLit = Quote(fieldName);
        if (p.Type.IsTableType)
        {
            // Entity-typed endpoints were resolved into explicit locals above so a
            // missing endpoint produces a clear error instead of a nullable dereference.
            sb.Append(indent).Append("        [").Append(fieldLit)
              .Append("] = new global::Disruptor.Surreal.Values.SurrealRecordIdValue(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk(")
              .Append(resolvedEndpointId).AppendLine(")),");
        }
        else
        {
            // Typed-id: the backing field is itself an IRecordId.
            var backing = $"_{ToCamel(p.Name)}";
            sb.Append(indent).Append("        [").Append(fieldLit)
              .Append("] = new global::Disruptor.Surreal.Values.SurrealRecordIdValue(global::Disruptor.Surface.Runtime.RecordIdSdkBridge.ToSdk((global::Disruptor.Surface.Runtime.IRecordId)")
              .Append(backing).AppendLine(")),");
        }
    }

    /// <summary>
    /// Emits a pure backing-field <c>[Property]</c> payload member — same shape as
    /// <see cref="PartialEmitter"/>'s <c>EmitDataProperty</c>. No Session interaction,
    /// no slice tracking. Save reads the backing field at dispatch time.
    /// </summary>
    private static void EmitPayloadProperty(StringBuilder sb, string indent, RelationVariantPropertyModel p)
    {
        var type = p.Type.FullyQualifiedName;
        var access = FormatAccessibility(p.DeclaredAccessibility);
        var backing = $"_{ToCamel(p.Name)}";

        sb.Append(indent).AppendLine("#pragma warning disable CS0649")
          .Append(indent).Append("private ").Append(type).Append(' ').Append(backing).AppendLine(" = default!;")
          .Append(indent).AppendLine("#pragma warning restore CS0649");

        if (!p.HasSetter && !p.HasInitOnlySetter)
        {
            sb.Append(indent).Append(access).Append(" partial ").Append(type).Append(' ').Append(p.Name)
              .Append(" => ").Append(backing).AppendLine(";");
            return;
        }

        sb.Append(indent).Append(access).Append(" partial ").Append(type).Append(' ').AppendLine(p.Name)
          .Append(indent).AppendLine("{")
          .Append(indent).Append("    get => ").Append(backing).AppendLine(";")
          .Append(indent).Append("    ").Append(p.HasInitOnlySetter ? "init" : "set").Append(" => ").Append(backing).AppendLine(" = value;")
          .Append(indent).AppendLine("}");
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

        var sb = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine();

        var hasNamespace = !string.IsNullOrEmpty(forward.Namespace);
        if (hasNamespace)
        {
            sb.Append("namespace ").Append(forward.Namespace).AppendLine();
            sb.AppendLine("{");
        }

        var indent = hasNamespace ? "    " : string.Empty;

        sb.Append(indent).Append("/// <summary>Generated marker for any variant of the relation kind <c>")
          .Append(forward.FullName).AppendLine("</c>.</summary>");
        sb.Append(indent).Append("public interface ").Append(interfaceName)
          .Append(" : ").Append(EntityEmitterCommon.EntityInterface).AppendLine(" { }");

        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        var hint = string.IsNullOrEmpty(forward.Namespace)
            ? $"{interfaceName}.g.cs"
            : $"{forward.Namespace}.{interfaceName}.g.cs";
        spc.AddSource(hint, SourceText.From(sb.ToString(), Encoding.UTF8));
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

        var sb = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine();

        var hasNamespace = !string.IsNullOrEmpty(forward.Namespace);
        if (hasNamespace)
        {
            sb.Append("namespace ").Append(forward.Namespace).AppendLine();
            sb.AppendLine("{");
        }

        var indent = hasNamespace ? "    " : string.Empty;
        var memberIndent = $"{indent}    ";
        var bodyIndent = $"{memberIndent}    ";

        sb.Append(indent).Append("/// <summary>Per-kind variant hydration dispatcher for the relation kind <c>")
          .Append(forward.FullName).AppendLine("</c>.</summary>");
        sb.Append(indent).Append("internal static class ").AppendLine(dispatcherName);
        sb.Append(indent).AppendLine("{");

        sb.Append(memberIndent).Append("/// <summary>")
          .AppendLine("Discriminates the loaded edge row by <c>(in.tb, out.tb)</c> and instantiates the matching variant.</summary>");
        sb.Append(memberIndent).Append("public static ").Append(EntityEmitterCommon.EntityInterface)
          .Append("? HydrateVariant(global::Disruptor.Surreal.Values.SurrealValue row, ")
          .Append(EntityEmitterCommon.HydrationSinkType).AppendLine(" sink)");
        sb.Append(memberIndent).AppendLine("{");

        sb.Append(bodyIndent).AppendLine("if (row is not global::Disruptor.Surreal.Values.SurrealObjectValue __obj) return null;");
        sb.Append(bodyIndent).AppendLine("if (!__obj.Object.TryGetValue(\"in\", out var __inV)) return null;");
        sb.Append(bodyIndent).AppendLine("if (!__obj.Object.TryGetValue(\"out\", out var __outV)) return null;");
        sb.Append(bodyIndent).AppendLine("var __inTb = global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__inV).Table;");
        sb.Append(bodyIndent).AppendLine("var __outTb = global::Disruptor.Surface.Runtime.HydrationValue.ReadRecordId(__outV).Table;");
        sb.Append(bodyIndent).Append(EntityEmitterCommon.EntityInterface).AppendLine("? __variant = (__inTb, __outTb) switch");
        sb.Append(bodyIndent).AppendLine("{");
        foreach (var (inTable, outTable, variant) in pairs)
        {
            sb.Append(bodyIndent).Append("    (").Append(Quote(inTable)).Append(", ").Append(Quote(outTable))
              .Append(") => new global::").Append(variant.FullName).AppendLine("(),");
        }
        sb.Append(bodyIndent).AppendLine("    _ => null,");
        sb.Append(bodyIndent).AppendLine("};");
        sb.Append(bodyIndent).AppendLine("if (__variant is null) return null;");
        sb.Append(bodyIndent).AppendLine("__variant.Hydrate(row, sink);");
        sb.Append(bodyIndent).AppendLine("return __variant;");

        sb.Append(memberIndent).AppendLine("}");
        sb.Append(indent).AppendLine("}");

        if (hasNamespace)
        {
            sb.AppendLine("}");
        }

        var hint = string.IsNullOrEmpty(forward.Namespace)
            ? $"{dispatcherName}.g.cs"
            : $"{forward.Namespace}.{dispatcherName}.g.cs";
        spc.AddSource(hint, SourceText.From(sb.ToString(), Encoding.UTF8));
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
