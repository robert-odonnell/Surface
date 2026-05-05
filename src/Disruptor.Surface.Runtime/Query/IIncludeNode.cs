namespace Disruptor.Surface.Runtime.Query;

/// <summary>
/// Marker for a node in the traversal AST. A query carries a list of these per level —
/// each instance becomes a projection element in the rendered SurrealQL <c>SELECT</c>.
/// Two shapes today:
/// <list type="bullet">
///   <item>
///     <see cref="IncludeInlineRefNode"/> — adds <c>field.*</c> to the projection,
///     pulling back the inline-record payload for a <c>[Reference, Inline]</c> column.
///     No subselect, no filter, no nested traversal.
///   </item>
///   <item>
///     <see cref="IncludeChildrenNode"/> — opens a nested
///     <c>(SELECT … FROM child WHERE parent_field = $parent.id) AS alias</c>. Carries its
///     own predicate filter and its own nested include list for further descent.
///   </item>
/// </list>
/// <para>
/// Strict-with-escape: nothing is implicitly included. Every nested record / inline ref /
/// edge needs an explicit <c>Include*</c> call; otherwise the wire SQL only sees scalar
/// columns from <c>SELECT *</c>.
/// </para>
/// </summary>
public interface IIncludeNode;

/// <summary>
/// Inline-record projection: emits <c>field.*</c> alongside the level's <c>*</c>.
/// </summary>
public sealed record IncludeInlineRefNode(string Field) : IIncludeNode;

/// <summary>
/// Nested children traversal: emits
/// <c>(SELECT projection FROM <see cref="ChildTable"/> WHERE
/// <see cref="ParentField"/> = $parent.id [AND <see cref="Filter"/>]) AS <see cref="ChildTable"/></c>.
/// <see cref="Nested"/> describes further descent inside the subselect — which inline
/// refs to expand on the child rows and which grandchildren to pull through.
/// <para>
/// <see cref="ParentSliceKey"/> is the field name marked loaded on the enclosing parent
/// once this slice is hydrated — usually the snake-cased C# property name. Decoupled
/// from <see cref="ChildTable"/> because the user can rename the [Children] property
/// (e.g. <c>partial IReadOnlyCollection&lt;UserStory&gt; Stories</c>: child table is
/// <c>user_stories</c>, slice key is <c>stories</c>).
/// </para>
/// <para>
/// <see cref="Hydrator"/> is the generator-emitted callback that instantiates a fresh
/// entity of the right CLR type and runs <c>IEntity.Hydrate</c> against the row JSON.
/// Captured at codegen time so the runtime never needs reflection / type discovery to
/// dispatch on the table-name string. <c>null</c> when the node was constructed by hand
/// (tests, ad-hoc tooling) — in that case the runtime emits SQL but won't hydrate the
/// nested rows.
/// </para>
/// </summary>
public sealed record IncludeChildrenNode(
    string ChildTable,
    string ParentField,
    IPredicate? Filter,
    IReadOnlyList<IIncludeNode> Nested,
    Action<System.Text.Json.JsonElement, IHydrationSink>? Hydrator = null,
    string? ParentSliceKey = null) : IIncludeNode;

/// <summary>
/// Forward / inverse relation traversal — follows a typed edge kind from each owner row
/// and projects the targets (within-aggregate) or just edge ids (cross-aggregate).
/// <para>
/// Two emit shapes, picked by <see cref="IdsOnly"/>:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Within-aggregate</b> — graph-traversal projection. Without nested includes,
///     forward (outgoing) renders <c>(->edge->target[WHERE filter].*) AS slice_key</c>
///     for single-target relations and <c>(->edge->?.*) AS slice_key</c> for multi-target.
///     With nested includes, the compiler wraps the traversal in a <c>SELECT</c> so the
///     nested <c>field.*</c> / subselect list lands in the inner projection:
///     <c>(SELECT projection FROM ->edge->target [WHERE filter]) AS slice_key</c>.
///     Inverse (incoming) flips the arrows. Targets come back as full rows; the
///     <see cref="Hydrator"/> dispatches each row to the right concrete entity type
///     (single-target = direct construction; multi-target = switch on the row's
///     <c>id:&lt;table&gt;</c> prefix). Each hydrated target also gets an
///     <c>IHydrationSink.Edge(...)</c> call synthesised from the parent row's id +
///     <see cref="EdgeName"/>, so <c>SurrealSession.QueryOutgoing</c> /
///     <c>QueryIncoming</c> resolve the slice after load.
///   </item>
///   <item>
///     <b>Cross-aggregate</b> — edge subselect. Forward renders
///     <c>(SELECT id, in, out FROM edge WHERE in = $parent.id) AS slice_key</c>; inverse
///     swaps <c>in</c> for <c>out</c>. No target hydration — only the edges dict is
///     populated. Matches the existing entity-side surface where cross-aggregate
///     relation properties expose <c>IReadOnlyCollection&lt;IRecordId&gt;</c>.
///   </item>
/// </list>
/// <para>
/// Multi-target relation includes are leaves: they take no <c>configure</c> lambda and
/// have an empty <see cref="Nested"/>. Single-target includes can carry a target-side
/// <see cref="Filter"/> and further <see cref="Nested"/> traversals (passed to the
/// generated <c>{Target}TraversalBuilder</c>).
/// </para>
/// </summary>
public sealed record IncludeRelationNode(
    string EdgeName,
    bool IsOutgoing,
    string ParentSliceKey,
    bool IdsOnly,
    string? SingleTargetTable,
    IPredicate? Filter,
    IReadOnlyList<IIncludeNode> Nested,
    Action<System.Text.Json.JsonElement, IHydrationSink>? Hydrator = null) : IIncludeNode;
