using System.Collections.Immutable;

namespace Cohesive.Model;

internal interface IObservationFieldVisitor
{
    void Visit<TEnumerator>(TEnumerator fields, bool canonical)
        where TEnumerator : IEnumerator<KeyValuePair<string, ObservationValue>>;
}

// Dispatch once, on the shared storage classification, without boxing the built-in storage enumerators.
internal static class ObservationFieldTraversal
{
    internal static void Visit<TVisitor>(IReadOnlyDictionary<string, ObservationValue> fields, ref TVisitor visitor)
        where TVisitor : struct, IObservationFieldVisitor
    {
        var storage = ObservationFieldStorage.Of(fields);
        switch (storage.Kind)
        {
            case ObservationFieldStorageKind.Small: visitor.Visit(((SmallObservationFields)storage.Fields!).GetEnumerator(), true); break;
            case ObservationFieldStorageKind.Owned: visitor.Visit(((OwnedObservationFields)storage.Fields!).GetEnumerator(), false); break;
            case ObservationFieldStorageKind.Ordinal: visitor.Visit(((OrdinalObservationFields)storage.Fields!).GetCanonicalEnumerator(), true); break;
            case ObservationFieldStorageKind.CompactOrdinal: visitor.Visit(((CompactOrdinalObservationFields)storage.Fields!).GetCanonicalEnumerator(), true); break;
            case ObservationFieldStorageKind.SortedOrdinal or ObservationFieldStorageKind.Sorted:
                visitor.Visit(((ImmutableSortedDictionary<string, ObservationValue>)storage.Fields!).GetEnumerator(),
                    storage.Kind == ObservationFieldStorageKind.SortedOrdinal); break;
            case ObservationFieldStorageKind.Immutable: visitor.Visit(((ImmutableDictionary<string, ObservationValue>)storage.Fields!).GetEnumerator(), false); break;
            case ObservationFieldStorageKind.Dictionary: visitor.Visit(((Dictionary<string, ObservationValue>)storage.Fields!).GetEnumerator(), false); break;
            default: visitor.Visit(storage.Fields!.GetEnumerator(), false); break;
        }
    }
}
