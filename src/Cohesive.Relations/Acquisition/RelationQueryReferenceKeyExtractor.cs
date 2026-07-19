using System.Collections.Immutable;
using Cohesive.Model;

namespace Cohesive.Relations.Acquisition;

/// <summary>Outcome of normalizing one relationship-reference value into canonical lookup keys.</summary>
public enum RelationQueryReferenceKeyExtractionState
{
    /// <summary>The reference was a valid string or string collection within the configured boundary.</summary>
    Success,

    /// <summary>The reference had an unsupported value kind or contained an empty or non-string key.</summary>
    Invalid,

    /// <summary>The configured key boundary was invalid or the reference contained too many keys.</summary>
    BoundaryExceeded
}

/// <summary>Shared canonical normalization for scalar and collection-valued relationship references.</summary>
public static class RelationQueryReferenceKeyExtractor
{
    /// <summary>Extracts distinct, ordinally ordered relationship keys from one observed reference value.</summary>
    /// <param name="value">String or string-array relationship-reference value.</param>
    /// <param name="maximumKeys">Positive maximum number of keys admitted from the reference.</param>
    /// <param name="cancellationToken">Token that cancels collection validation and normalization.</param>
    /// <param name="keys">Distinct keys in deterministic ordinal order on success; otherwise an empty array.</param>
    /// <returns>The successful, invalid-value, or boundary-exceeded extraction state.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static RelationQueryReferenceKeyExtractionState Extract(
        ObservationValue value,
        long maximumKeys,
        CancellationToken cancellationToken,
        out ImmutableArray<string> keys
        )
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

        if (value.Kind != ObservationValueKind.Array || value.Array.IsDefault)
        {
            keys = [];
            return RelationQueryReferenceKeyExtractionState.Invalid;
        }
        var values = value.Array;

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
