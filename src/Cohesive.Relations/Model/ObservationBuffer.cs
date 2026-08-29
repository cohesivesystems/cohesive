using System.Runtime.CompilerServices;

namespace Cohesive.Relations.Model;

/// <summary>
/// Owned ordinal-aligned observation values with inline presence storage for layouts of at most 64 fields.
/// </summary>
internal readonly struct ObservationBuffer
{
    readonly ObservationValue[] valuesByOrdinal;
    readonly ulong inlinePresence;
    readonly ulong[]? presenceWords;

    /// <summary>Creates a buffer by taking ownership of storage produced inside the physical interpretation.</summary>
    /// <param name="ownedValuesByOrdinal">Exclusively owned value slots that will not be mutated after construction.</param>
    /// <param name="inlinePresence">Packed presence bits when the field count is at most 64.</param>
    /// <param name="ownedPresenceWords">
    /// Exclusively owned packed presence words for layouts larger than 64 fields; otherwise <see langword="null"/>.
    /// </param>
    internal ObservationBuffer(
        ObservationValue[] ownedValuesByOrdinal,
        ulong inlinePresence,
        ulong[]? ownedPresenceWords)
    {
        ArgumentNullException.ThrowIfNull(ownedValuesByOrdinal);

        var fieldCount = ownedValuesByOrdinal.Length;
        var requiredWords = RequiredWordCount(fieldCount);
        if (requiredWords <= 1)
        {
            if (ownedPresenceWords is not null)
            {
                throw new ArgumentException(
                    "Inline observation presence must not supply external presence words.",
                    nameof(ownedPresenceWords));
            }
        }
        else if (ownedPresenceWords?.Length != requiredWords)
        {
            throw new ArgumentException(
                "Observation presence word length does not match the field count.",
                nameof(ownedPresenceWords));
        }

        RequireNoPresenceOutsideFieldCount(
            fieldCount,
            requiredWords <= 1 ? inlinePresence : ownedPresenceWords![^1]);
        valuesByOrdinal = ownedValuesByOrdinal;
        this.inlinePresence = requiredWords <= 1 ? inlinePresence : 0;
        presenceWords = ownedPresenceWords;
    }

    /// <summary>Returns true if an ordinal has a materialized value.</summary>
    /// <param name="ordinal">Zero-based field ordinal.</param>
    /// <returns><see langword="true"/> when the ordinal is valid and present; otherwise <see langword="false"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool HasValue(int ordinal)
    {
        if ((uint)ordinal >= (uint)valuesByOrdinal.Length)
            return false;

        var word = ordinal >> 6;
        var bits = presenceWords is null ? inlinePresence : presenceWords[word];
        return (bits & (1UL << (ordinal & 63))) != 0;
    }

    /// <summary>Returns the value for an ordinal.</summary>
    /// <param name="ordinal">Zero-based field ordinal.</param>
    /// <returns>The retained value slot.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is outside the buffer.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ObservationValue GetValue(int ordinal)
    {
        if ((uint)ordinal >= (uint)valuesByOrdinal.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        return valuesByOrdinal[ordinal];
    }

    /// <summary>Allocates external presence storage only when more than one 64-bit word is required.</summary>
    /// <param name="fieldCount">Number of represented ordinals.</param>
    /// <returns>External presence words for large layouts; otherwise <see langword="null"/>.</returns>
    internal static ulong[]? CreatePresenceWords(int fieldCount) =>
        fieldCount > 64 ? new ulong[RequiredWordCount(fieldCount)] : null;

    /// <summary>Marks an ordinal present in inline or external owned storage.</summary>
    /// <param name="inlinePresence">Inline presence word for layouts of at most 64 fields.</param>
    /// <param name="presenceWords">External presence words for larger layouts.</param>
    /// <param name="fieldCount">Number of represented ordinals.</param>
    /// <param name="ordinal">Ordinal to mark present.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is outside the field count.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetHasValue(
        ref ulong inlinePresence,
        ulong[]? presenceWords,
        int fieldCount,
        int ordinal)
    {
        if ((uint)ordinal >= (uint)fieldCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        if (presenceWords is null)
            inlinePresence |= 1UL << ordinal;
        else
            presenceWords[ordinal >> 6] |= 1UL << (ordinal & 63);
    }

    /// <summary>Calculates the required number of 64-bit words to store one presence bit per field.</summary>
    /// <param name="fieldCount">Number of represented fields.</param>
    /// <returns>Number of packed presence words.</returns>
    internal static int RequiredWordCount(int fieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldCount);
        return fieldCount == 0 ? 0 : ((fieldCount - 1) >> 6) + 1;
    }

    /// <summary>Marks an ordinal as present in a caller-owned mutable bit mask.</summary>
    /// <param name="hasValueBitMask">Packed presence words to update.</param>
    /// <param name="ordinal">Ordinal to mark present.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ordinal"/> is negative or cannot be represented by <paramref name="hasValueBitMask"/>.
    /// </exception>
    internal static void SetHasValue(Span<ulong> hasValueBitMask, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        var word = ordinal >> 6;
        if ((uint)word >= (uint)hasValueBitMask.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        hasValueBitMask[word] |= 1UL << (ordinal & 63);
    }

    static void RequireNoPresenceOutsideFieldCount(int fieldCount, ulong finalWord)
    {
        var remainder = fieldCount & 63;
        if (fieldCount == 0)
        {
            if (finalWord != 0)
                throw new ArgumentException("An empty observation buffer cannot contain presence bits.");
            return;
        }
        if (remainder == 0)
            return;

        var allowed = (1UL << remainder) - 1UL;
        if ((finalWord & ~allowed) != 0)
            throw new ArgumentException("Observation presence contains bits outside the field count.");
    }
}
