using System.Collections.Immutable;
using Cohesive.Model;

namespace Cohesive.Relations.Acquisition;

enum RelationQueryReferenceKeyExtractionState
{
    Success,
    Invalid,
    BoundaryExceeded
}

static class RelationQueryReferenceKeyExtractor
{
    public static RelationQueryReferenceKeyExtractionState Extract(
        ObservationValue value,
        long maximumKeys,
        CancellationToken cancellationToken,
        out ImmutableArray<string> keys)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (maximumKeys <= 0)
        {
            keys = [];
            return RelationQueryReferenceKeyExtractionState.BoundaryExceeded;
        }

        if (value.Kind == ObservationValueKind.String)
        {
            if (string.IsNullOrWhiteSpace(value.String))
            {
                keys = [];
                return RelationQueryReferenceKeyExtractionState.Invalid;
            }

            keys = [value.String];
            return RelationQueryReferenceKeyExtractionState.Success;
        }

        if (value.Kind != ObservationValueKind.Array || value.Array is not { } values)
        {
            keys = [];
            return RelationQueryReferenceKeyExtractionState.Invalid;
        }

        if ((long)values.Length > maximumKeys)
        {
            keys = [];
            return RelationQueryReferenceKeyExtractionState.BoundaryExceeded;
        }

        HashSet<string> unique = new(values.Length, StringComparer.Ordinal);
        foreach (var item in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Kind != ObservationValueKind.String || string.IsNullOrWhiteSpace(item.String))
            {
                keys = [];
                return RelationQueryReferenceKeyExtractionState.Invalid;
            }
            unique.Add(item.String);
        }

        cancellationToken.ThrowIfCancellationRequested();
        keys = [.. unique.Order(StringComparer.Ordinal)];
        cancellationToken.ThrowIfCancellationRequested();
        return RelationQueryReferenceKeyExtractionState.Success;
    }
}
