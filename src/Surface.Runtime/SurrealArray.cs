#nullable enable
using System.Collections;

namespace Surface.Runtime;

/// <summary>
/// A mutable, ordered collection that lives on an entity field and notifies the owner's
/// bound <see cref="SurrealSession"/> on every mutation. Backs SurrealDB's
/// <c>array&lt;object&gt;</c> inline-collection columns (e.g. <c>acceptance_criteria.scenarios</c>,
/// <c>tests.facts</c>) — Surreal preserves insertion order natively, so reordering
/// operations like <see cref="Move"/> just shuffle the underlying list and re-record.
/// <para>
/// Each mutation call routes the underlying <see cref="List{T}"/> reference through
/// <see cref="SurrealSession.SetField"/>; the dirty batch holds the live reference, so
/// subsequent mutations are automatically picked up at commit time without re-recording.
/// Mutations made before the owner is tracked (object-initializer time) silently update
/// the in-memory list — the owner's <see cref="IEntity.Flush"/> picks them up via the
/// final SetField when Track binds it.
/// </para>
/// </summary>
public sealed class SurrealArray<T> : IList<T>, IReadOnlyList<T>
{
    private readonly List<T> items;
    private readonly IEntity owner;
    private readonly string fieldName;

    /// <summary>
    /// Construct a tracked list bound to <paramref name="owner"/>'s
    /// <paramref name="fieldName"/>. Generator-emitted property getters call this with no
    /// initial items; the loader (when wired) calls it with the hydrated payload.
    /// </summary>
    public SurrealArray(IEntity owner, string fieldName, IEnumerable<T>? initial = null)
    {
        this.owner = owner;
        this.fieldName = fieldName;
        items = initial is null ? [] : [..initial];
    }

    public int Count => items.Count;
    public bool IsReadOnly => false;

    public T this[int index]
    {
        get => items[index];
        set
        {
            items[index] = value;
            Notify();
        }
    }

    public void Add(T item)
    {
        items.Add(item);
        Notify();
    }

    public void Insert(int index, T item)
    {
        items.Insert(index, item);
        Notify();
    }

    public bool Remove(T item)
    {
        var removed = items.Remove(item);
        if (removed)
        {
            Notify();
        }

        return removed;
    }

    public void RemoveAt(int index)
    {
        items.RemoveAt(index);
        Notify();
    }

    public void Clear()
    {
        if (items.Count == 0)
        {
            return;
        }

        items.Clear();
        Notify();
    }

    /// <summary>Reorder: take the item at <paramref name="from"/> and place it at <paramref name="to"/>.</summary>
    public void Move(int from, int to)
    {
        if (from == to)
        {
            return;
        }

        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        Notify();
    }

    public int IndexOf(T item) => items.IndexOf(item);
    public bool Contains(T item) => items.Contains(item);
    public void CopyTo(T[] array, int arrayIndex) => items.CopyTo(array, arrayIndex);

    public List<T>.Enumerator GetEnumerator() => items.GetEnumerator();
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();

    /// <summary>
    /// Mutation goes via the owner's currently-bound session. If the owner hasn't been
    /// tracked yet, the call is a silent no-op — the underlying list is still updated, and
    /// the owner's <see cref="IEntity.Flush"/> will queue the final state when it's
    /// tracked.
    /// </summary>
    private void Notify() => owner.Session?.SetField(owner.Id, fieldName, items);
}
