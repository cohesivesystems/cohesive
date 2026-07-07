namespace Cohesive.Relations.Model;

/// <summary>
/// Ordinal-aligned observation value storage backed by read-only memory and packed presence bits.
/// </summary>
public readonly struct ObservationBuffer
{
    readonly ReadOnlyMemory<ObservationValue> valuesByOrdinal;
    readonly ReadOnlyMemory<ulong> hasValueBitMask;

    /// <summary>
    /// Creates a buffer from ordinal-aligned values and packed presence bits.
    /// </summary>
    public ObservationBuffer(
        ReadOnlyMemory<ObservationValue> valuesByOrdinal,
        ReadOnlyMemory<ulong> hasValueBitMask,
        int fieldCount
        )
    {
        if (fieldCount < 0)
            throw new ArgumentOutOfRangeException(nameof(fieldCount));

        if (valuesByOrdinal.Length != fieldCount)
            throw new ArgumentException("Values length must match field count.", nameof(valuesByOrdinal));

        var requiredWords = RequiredWordCount(fieldCount);
        if (hasValueBitMask.Length != requiredWords)
            throw new ArgumentException("Bitmask length does not match field count.", nameof(hasValueBitMask));

        this.valuesByOrdinal = valuesByOrdinal;
        this.hasValueBitMask = hasValueBitMask;
        FieldCount = fieldCount;
    }

    /// <summary>
    /// Number of ordinals represented by this buffer.
    /// </summary>
    public int FieldCount { get; }

    /// <summary>
    /// Ordinal-aligned values.
    /// </summary>
    public ReadOnlyMemory<ObservationValue> ValuesByOrdinal => valuesByOrdinal;

    /// <summary>
    /// Packed value-presence bit mask.
    /// </summary>
    public ReadOnlyMemory<ulong> HasValueBitMask => hasValueBitMask;

    /// <summary>
    /// Returns true if an ordinal has a materialized value.
    /// </summary>
    public bool HasValue(int ordinal)
    {
        if ((uint)ordinal >= (uint)FieldCount)
            return false;

        var word = ordinal >> 6;
        var bit = ordinal & 63;
        return (hasValueBitMask.Span[word] & (1UL << bit)) != 0;
    }

    /// <summary>
    /// Returns the value for an ordinal.
    /// </summary>
    public ObservationValue GetValue(int ordinal)
    {
        if ((uint)ordinal >= (uint)FieldCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        return valuesByOrdinal.Span[ordinal];
    }

    /// <summary>
    /// Creates a packed buffer from dense value and boolean-presence arrays.
    /// </summary>
    public static ObservationBuffer FromDense(ObservationValue[] valuesByOrdinal, bool[] hasValueByOrdinal)
    {
        ArgumentNullException.ThrowIfNull(valuesByOrdinal);
        ArgumentNullException.ThrowIfNull(hasValueByOrdinal);
        return FromDense(valuesByOrdinal.AsMemory(), hasValueByOrdinal.AsMemory());
    }

    /// <summary>
    /// Creates a packed buffer from dense value and boolean-presence memory blocks.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public static ObservationBuffer FromDense(
        ReadOnlyMemory<ObservationValue> valuesByOrdinal,
        ReadOnlyMemory<bool> hasValueByOrdinal)
    {
        if (valuesByOrdinal.Length != hasValueByOrdinal.Length)
            throw new ArgumentException("Values and presence lengths must match.");

        var fieldCount = valuesByOrdinal.Length;
        var bitMask = new ulong[RequiredWordCount(fieldCount)];
        var hasValue = hasValueByOrdinal.Span;
        for (var i = 0; i < hasValue.Length; i++)
        {
            if (hasValue[i])
                SetHasValue(bitMask, i);
        }

        return new(valuesByOrdinal, bitMask, fieldCount);
    }

    /// <summary>
    /// Calculates the required number of 64-bit words to store a bit per field ordinal.
    /// </summary>
    public static int RequiredWordCount(int fieldCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldCount);
        if (fieldCount == 0)
            return 0;

        return ((fieldCount - 1) >> 6) + 1;
    }

    /// <summary>
    /// Marks an ordinal as present in a mutable bit mask.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"></exception>
    public static void SetHasValue(Span<ulong> hasValueBitMask, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);

        var word = ordinal >> 6;
        if ((uint)word >= (uint)hasValueBitMask.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));

        var bit = ordinal & 63;
        hasValueBitMask[word] |= 1UL << bit;
    }
}
