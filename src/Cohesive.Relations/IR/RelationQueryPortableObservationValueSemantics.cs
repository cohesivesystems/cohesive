namespace Cohesive.Relations.IR;

/// <summary>Canonical relation/query persistence rules for observation values.</summary>
static class RelationQueryPortableObservationValueSemantics
{
    /// <summary>Finds the first reason an observation value cannot be persisted losslessly.</summary>
    /// <param name="value">Observation value to inspect recursively.</param>
    /// <param name="code">Stable relation/query diagnostic code when the value is unsupported.</param>
    /// <param name="message">Actionable explanation when the value is unsupported.</param>
    /// <returns><see langword="true"/> when the value cannot be encoded by canonical relation/query JSON.</returns>
    internal static bool TryGetCanonicalJsonIssue(
        ObservationValue value,
        out string? code,
        out string? message)
    {
        if (TryGetCurrentNodeIssue(value, out code, out message))
            return true;

        if (value.Kind == ObservationValueKind.Object && value.Fields is not null)
        {
            foreach (var child in value.Fields.Values)
            {
                if (TryGetCanonicalJsonIssue(child, out code, out message))
                    return true;
            }
        }
        else if (value.Kind == ObservationValueKind.Array && value.Array is not null)
        {
            foreach (var child in value.Array)
            {
                if (TryGetCanonicalJsonIssue(child, out code, out message))
                    return true;
            }
        }

        code = null;
        message = null;
        return false;
    }

    /// <summary>Finds a canonical JSON problem on the selected value without inspecting children.</summary>
    /// <param name="value">Observation value to inspect.</param>
    /// <param name="code">Stable relation/query diagnostic code when the value is unsupported.</param>
    /// <param name="message">Actionable explanation when the value is unsupported.</param>
    /// <returns><see langword="true"/> when this value node cannot be encoded canonically.</returns>
    internal static bool TryGetCurrentNodeIssue(
        ObservationValue value,
        out string? code,
        out string? message)
    {
        if (value.Kind is ObservationValueKind.Undefined
            or ObservationValueKind.Bytes
            or ObservationValueKind.DateTimeOffset
            or ObservationValueKind.DateOnly
            or ObservationValueKind.TimeOnly
            or ObservationValueKind.TimeSpan)
        {
            code = "relationQuery.value.kindUnsupported";
            message = $"Observation value kind '{value.Kind}' does not have a lossless canonical relation/query JSON encoding.";
            return true;
        }

        if (value.Kind == ObservationValueKind.Double && !double.IsFinite(value.Double))
        {
            code = "relationQuery.value.numberNonFinite";
            message = "Canonical relation/query JSON cannot represent non-finite numeric values.";
            return true;
        }

        code = null;
        message = null;
        return false;
    }
}
