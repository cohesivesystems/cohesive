namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Projects transition results into field-level patches for storage backends.
/// </summary>
public static class TransitionPatchProjector
{
    /// <summary>
    /// Projects a patch from declared transition write fields.
    /// </summary>
    /// <remarks>
    /// This is optimal when writes are explicit and complete for the backend representation.
    /// </remarks>
    public static IReadOnlyDictionary<string, ObservationValue> ProjectDeclaredWritePatch(
        TransitionResult result,
        bool includeUnchangedWrites = false
        )
    {
        ArgumentNullException.ThrowIfNull(result);
        return ProjectPatchFromFields(
            result: result,
            fieldNames: result.WriteFields,
            includeUnchanged: includeUnchangedWrites);
    }

    /// <summary>
    /// Projects a patch from actual field changes observed in the state delta.
    /// </summary>
    /// <remarks>
    /// This includes implicit updates (for example computed fields) even when they are not in the declared write set.
    /// </remarks>
    public static IReadOnlyDictionary<string, ObservationValue> ProjectChangedFieldPatch(TransitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return ProjectPatchFromFields(
            result: result,
            fieldNames: result.ChangedFields,
            includeUnchanged: false);
    }

    static IReadOnlyDictionary<string, ObservationValue> ProjectPatchFromFields(
        TransitionResult result,
        IReadOnlyList<string> fieldNames,
        bool includeUnchanged
        )
    {
        if (fieldNames.Count == 0)
            return new Dictionary<string, ObservationValue>(StringComparer.Ordinal);

        Dictionary<string, ObservationValue> patch = new(StringComparer.Ordinal);
        foreach (var fieldName in fieldNames)
        {
            if (!result.NewState.Fields.TryGetValue(fieldName, out var newValue))
                continue;

            var hasOld = result.OldState.Fields.TryGetValue(fieldName, out var oldValue);
            if (!includeUnchanged && hasOld && ObservationValue.DeepEquals(oldValue, newValue))
                continue;

            patch[fieldName] = newValue;
        }

        return patch;
    }
}
