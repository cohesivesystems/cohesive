namespace Cohesive.Model;

// Layout-ordinal value storage that can back a dictionary view. Implemented only by the dense and packed
// stores; requiring it at construction keeps a nested view from wrapping storage it cannot enumerate.
internal interface IOrdinalObservationStorage : IReadOnlyDictionary<string, ObservationValue>, IOrdinalObservationFieldReader
{
    /// <summary>Finds the next present field in layout-ordinal order.</summary>
    /// <param name="after">Ordinal after which to search; -1 starts at the first ordinal.</param>
    /// <returns>The next present ordinal, or -1 when none remains.</returns>
    int NextPresentOrdinal(int after);
}
