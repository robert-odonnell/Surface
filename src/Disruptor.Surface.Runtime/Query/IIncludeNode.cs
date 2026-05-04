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
public interface IIncludeNode { }

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
    Action<System.Text.Json.JsonElement, IHydrationSink>? Hydrator = null) : IIncludeNode;
