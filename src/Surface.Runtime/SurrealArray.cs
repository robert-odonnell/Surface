#nullable enable
using System.Collections;

namespace Surface.Runtime;

/// <summary>
/// A mutable, ordered collection that lives on an entity field and routes every mutation
/// through a writer callback supplied by the generator. Backs SurrealDB's
/// <c>array&lt;object&gt;</c> inline-collection columns (e.g.
/// <c>acceptance_criteria.scenarios</c>, <c>tests.facts</c>) — Surreal preserves
/// insertion order natively, so reordering operations like <see cref="Move"/> just
/// shuffle the underlying list and re-record.
/// <para>
/// The writer callback is the generator-emitted entity's <c>__WriteField</c>: same lifecycle
/// as scalar / reference / parent setters, so pre-bind mutations buffer alongside object-
/// initializer values and replay through <see cref="IEntity.Flush"/> when the owner is
/// tracked. The dirty batch holds the live <see cref="List{T}"/> reference, so subsequent
/// mutations on the same list are automatically picked up at commit time.
/// </para>
/// </summary>
public sealed class SurrealArray<T> : IList<T>, IReadOnlyList<T>
{
    private readonly List<T> items;
    private readonly Action<List<T>> writer;

    /// <summary>
    /// Construct a tracked list with a writer callback. Generator-emitted property getters
    /// call this with no initial items and a lambda that routes through the entity's
    /// <c>__WriteField</c>; the loader's <see cref="IEntity.Hydrate"/> path calls it with
    /// the hydrated payload (and the same writer).
    /// </summary>
    public SurrealArray(IEnumerable<T>? initial, Action<List<T>> writer)
    {
        this.writer = writer;
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

    private void Notify() => writer(items);
}
