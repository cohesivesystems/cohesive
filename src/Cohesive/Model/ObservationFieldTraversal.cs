using System.Collections.Immutable;

namespace Cohesive.Model;

internal interface IObservationFieldVisitor
{
    void Visit<TEnumerator>(TEnumerator fields, bool canonical)
        where TEnumerator : IEnumerator<KeyValuePair<string, ObservationValue>>;
}

// Dispatch once without boxing the built-in storage enumerators.
internal static class ObservationFieldTraversal
{
    internal static void Visit<TVisitor>(IReadOnlyDictionary<string, ObservationValue> fields, ref TVisitor visitor)
        where TVisitor : struct, IObservationFieldVisitor
    {
        switch (fields)
        {
            case OwnedObservationFields owned: visitor.Visit(owned.GetEnumerator(), false); break;
            case OrdinalObservationFields ordinal: visitor.Visit(ordinal.GetCanonicalEnumerator(), true); break;
            case ImmutableDictionary<string, ObservationValue> immutable: visitor.Visit(immutable.GetEnumerator(), false); break;
            case ImmutableSortedDictionary<string, ObservationValue> sorted: visitor.Visit(sorted.GetEnumerator(), ReferenceEquals(sorted.KeyComparer, StringComparer.Ordinal)); break;
            case Dictionary<string, ObservationValue> dictionary: visitor.Visit(dictionary.GetEnumerator(), false); break;
            default: visitor.Visit(fields.GetEnumerator(), false); break;
        }
    }
}
