using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Cohesive.Prelude;

/// <summary>
/// Read-only slice view over an <see cref="IReadOnlyList{T}"/> without copying values.
/// </summary>
public readonly struct ReadOnlyListSlice<T> : IReadOnlyList<T>
{
    /// <summary>
    /// Creates a slice over <paramref name="source"/>.
    /// </summary>
    public ReadOnlyListSlice(IReadOnlyList<T> source, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        if ((uint)start > (uint)source.Count)
            throw new ArgumentOutOfRangeException(nameof(start));
        if ((uint)count > (uint)(source.Count - start))
            throw new ArgumentOutOfRangeException(nameof(count));

        Source = source;
        Start = start;
        Count = count;
    }

    /// <summary>
    /// Source list for this view.
    /// </summary>
    public IReadOnlyList<T> Source { get; }

    /// <summary>
    /// Zero-based offset in <see cref="Source"/>.
    /// </summary>
    public int Start { get; }

    /// <summary>
    /// Number of visible elements.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Gets an element in the slice.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return Source[Start + index];
        }
    }

    /// <summary>
    /// Creates a nested slice that still views the same backing source.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public ReadOnlyListSlice<T> Slice(int start, int length)
    {
        if ((uint)start > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(start));
        if ((uint)length > (uint)(Count - start))
            throw new ArgumentOutOfRangeException(nameof(length));

        return new ReadOnlyListSlice<T>(Source, Start + start, length);
    }

    /// <summary>
    /// Gets an allocation-free enumerator for foreach over the concrete slice type.
    /// </summary>
    public Enumerator GetEnumerator() => new(Source, Start, Count);

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(Source, Start, Count);

    IEnumerator IEnumerable.GetEnumerator() => new Enumerator(Source, Start, Count);

    /// <summary>
    /// Enumerator for <see cref="ReadOnlyListSlice{T}"/>.
    /// </summary>
    public struct Enumerator : IEnumerator<T>
    {
        readonly IReadOnlyList<T> source;
        readonly int start;
        readonly int endExclusive;
        int index;

        internal Enumerator(IReadOnlyList<T> source, int start, int count)
        {
            this.source = source;
            this.start = start;
            endExclusive = start + count;
            index = start - 1;
        }

        public T Current => source[index];

        object IEnumerator.Current => Current!;

        public bool MoveNext()
        {
            var next = index + 1;
            if (next >= endExclusive)
            {
                index = endExclusive;
                return false;
            }

            index = next;
            return true;
        }

        public void Reset() => index = start - 1;

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Extensions for efficient read-only list slicing.
/// </summary>
public static class ReadOnlyListExtensions
{
    extension<T>([NotNullWhen(false)] IReadOnlyList<T>? source)
    {
        /// <summary>
        /// Indicates whether the list is null or empty.
        /// </summary>
        public bool IsDefaultOrEmpty => source is null || source.Count == 0;
    }

    extension<T>(IReadOnlyList<T> source)
    {
        /// <summary>
        /// Creates a read-only view over the specified range of <paramref name="source"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public ReadOnlyListSlice<T> Slice(int start, int length)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source is ReadOnlyListSlice<T> existing)
                return existing.Slice(start, length);
            return new ReadOnlyListSlice<T>(source, start, length);
        }

        /// <summary>
        /// Skips <paramref name="count"/> items using a non-copying slice view.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public ReadOnlyListSlice<T> Skip(int count)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (count <= 0)
                return source.Slice(0, source.Count);

            var start = Math.Min(count, source.Count);
            return source.Slice(start, source.Count - start);
        }

        /// <summary>
        /// Takes at most <paramref name="count"/> items using a non-copying slice view.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public ReadOnlyListSlice<T> Take(int count)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (count <= 0)
                return source.Slice(0, 0);

            return source.Slice(0, Math.Min(count, source.Count));
        }

        /// <summary>
        /// Same as <see cref="IReadOnlyList{T}.Count"/>.
        /// </summary>
        public int Length => source.Count;
        
        /// <summary>
        /// Indicates whether the list is empty.
        /// </summary>
        public bool IsEmpty => source.Count == 0;
    }
}
