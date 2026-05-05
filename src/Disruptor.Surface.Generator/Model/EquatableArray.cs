using System.Collections;
using System.Collections.Immutable;

namespace Disruptor.Surface.Generator.Model;

/// <summary>
/// Value-equality wrapper around <see cref="ImmutableArray{T}"/>.
/// Required by <c>IIncrementalGenerator</c>: model types returned from pipeline
/// stages must compare by value so Roslyn can skip downstream work when nothing changed.
/// </summary>
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    public static readonly EquatableArray<T> Empty = new([]);

    private readonly ImmutableArray<T> items;

    public EquatableArray(ImmutableArray<T> items) => this.items = items;
    public EquatableArray(IEnumerable<T> items) => this.items = [..items];

    public ImmutableArray<T> AsImmutableArray() => items.IsDefault ? [] : items;

    public int Count => items.IsDefault ? 0 : items.Length;
    public T this[int index] => items[index];

    public bool Equals(EquatableArray<T> other)
    {
        var a = AsImmutableArray();
        var b = other.AsImmutableArray();
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i]))
            {
                return false;
            }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            foreach (var item in AsImmutableArray())
            {
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            }

            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)AsImmutableArray()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new(items);
}

public static class EquatableArrayExtensions
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> source)
        where T : IEquatable<T> => new(source);
}
