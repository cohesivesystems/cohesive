using System.Collections.Immutable;

namespace Cohesive.Model;

/// <summary>Physical object-field storage recognized by observation traversal, ordering and retention.</summary>
internal enum ObservationFieldStorageKind : byte
{
    /// <summary>Caller or framework storage with no known ordering or ownership guarantee.</summary>
    Other,

    /// <summary>Bounded inline storage, already ordinally sorted.</summary>
    Small,

    /// <summary>Owned dictionary storage in insertion order.</summary>
    Owned,

    /// <summary>Dense layout-ordinal value vector.</summary>
    Ordinal,

    /// <summary>Packed layout-ordinal values with a presence bitmap.</summary>
    CompactOrdinal,

    /// <summary>Immutable sorted dictionary using the ordinal comparer.</summary>
    SortedOrdinal,

    /// <summary>Immutable sorted dictionary using another comparer.</summary>
    Sorted,

    /// <summary>Immutable hash dictionary.</summary>
    Immutable,

    /// <summary>Mutable framework dictionary.</summary>
    Dictionary
}

// The single classification of object-field storage. Nested views are unwrapped here, so retention, traversal,
// validation and canonical ordering cannot drift apart when a storage type is added: extend Of and the
// canonical enumerator together.
internal readonly struct ObservationFieldStorage
{
    ObservationFieldStorage(IReadOnlyDictionary<string, ObservationValue>? fields, ObservationFieldStorageKind kind)
    {
        Fields = fields;
        Kind = kind;
    }

    /// <summary>The unwrapped physical storage, or <see langword="null"/>.</summary>
    internal IReadOnlyDictionary<string, ObservationValue>? Fields { get; }

    /// <summary>The recognized storage kind.</summary>
    internal ObservationFieldStorageKind Kind { get; }

    /// <summary>Immutable storage owned by an observation that may be retained without a snapshot copy.</summary>
    internal bool IsOwnedImmutable => Kind is ObservationFieldStorageKind.Small or ObservationFieldStorageKind.Owned
        or ObservationFieldStorageKind.Ordinal or ObservationFieldStorageKind.CompactOrdinal;

    /// <summary>Storage whose <see cref="GetCanonicalEnumerator"/> yields ordinal key order without copying or sorting.</summary>
    internal bool IsCanonicallyOrdered => Kind is ObservationFieldStorageKind.Small or ObservationFieldStorageKind.Ordinal
        or ObservationFieldStorageKind.CompactOrdinal or ObservationFieldStorageKind.SortedOrdinal;

    /// <summary>Classifies storage, unwrapping a nested view to the storage it exposes.</summary>
    /// <param name="fields">Object fields to classify, or <see langword="null"/>.</param>
    /// <returns>The unwrapped storage and its kind.</returns>
    internal static ObservationFieldStorage Of(IReadOnlyDictionary<string, ObservationValue>? fields)
    {
        if (fields is ObservationNestedFields nested) fields = nested.Storage;
        return new(fields, fields switch
        {
            SmallObservationFields => ObservationFieldStorageKind.Small,
            OwnedObservationFields => ObservationFieldStorageKind.Owned,
            OrdinalObservationFields => ObservationFieldStorageKind.Ordinal,
            CompactOrdinalObservationFields => ObservationFieldStorageKind.CompactOrdinal,
            ImmutableSortedDictionary<string, ObservationValue> sorted => ReferenceEquals(sorted.KeyComparer, StringComparer.Ordinal)
                ? ObservationFieldStorageKind.SortedOrdinal
                : ObservationFieldStorageKind.Sorted,
            ImmutableDictionary<string, ObservationValue> => ObservationFieldStorageKind.Immutable,
            Dictionary<string, ObservationValue> => ObservationFieldStorageKind.Dictionary,
            _ => ObservationFieldStorageKind.Other
        });
    }

    /// <summary>Enumerates canonically ordered storage in ordinal key order without allocation.</summary>
    /// <returns>A borrowed enumerator; dispose it to release a sorted-dictionary traversal stack.</returns>
    /// <exception cref="InvalidOperationException">The storage is not <see cref="IsCanonicallyOrdered"/>.</exception>
    internal CanonicalEnumerator GetCanonicalEnumerator() => IsCanonicallyOrdered
        ? new(this)
        : throw new InvalidOperationException($"Storage kind '{Kind}' has no canonical enumeration.");

    internal struct CanonicalEnumerator : IEnumerator<KeyValuePair<string, ObservationValue>>
    {
        readonly ObservationFieldStorageKind kind;
        readonly SmallObservationFields? small;
        OrdinalObservationFields.Enumerator ordinal;
        CompactOrdinalObservationFields.Enumerator compact;
        ImmutableSortedDictionary<string, ObservationValue>.Enumerator sorted;
        int index;

        internal CanonicalEnumerator(ObservationFieldStorage storage)
        {
            kind = storage.Kind;
            small = storage.Fields as SmallObservationFields;
            ordinal = storage.Fields is OrdinalObservationFields dense ? dense.GetCanonicalEnumerator() : default;
            compact = storage.Fields is CompactOrdinalObservationFields packed ? packed.GetCanonicalEnumerator() : default;
            sorted = storage.Fields is ImmutableSortedDictionary<string, ObservationValue> ordered ? ordered.GetEnumerator() : default;
            index = -1;
        }

        public readonly KeyValuePair<string, ObservationValue> Current => kind switch
        {
            ObservationFieldStorageKind.Small => small!.At(index),
            ObservationFieldStorageKind.Ordinal => ordinal.Current,
            ObservationFieldStorageKind.CompactOrdinal => compact.Current,
            _ => sorted.Current
        };

        readonly object System.Collections.IEnumerator.Current => Current;

        public bool MoveNext() => kind switch
        {
            ObservationFieldStorageKind.Small => ++index < small!.Count,
            ObservationFieldStorageKind.Ordinal => ordinal.MoveNext(),
            ObservationFieldStorageKind.CompactOrdinal => compact.MoveNext(),
            _ => sorted.MoveNext()
        };

        public void Dispose()
        {
            if (kind == ObservationFieldStorageKind.SortedOrdinal) sorted.Dispose();
        }

        public readonly void Reset() => throw new NotSupportedException();
    }
}
