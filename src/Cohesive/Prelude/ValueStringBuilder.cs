namespace Cohesive.Prelude;

using System;
using System.Buffers;

/// <summary>
/// A stack-first, pooled fallback string builder.
/// Designed for high-performance, low-allocation scenarios.
/// </summary>
/// <param name="initialBuffer">Initial stack or caller-owned character buffer.</param>
/// <param name="arrayPool">Optional pool used when the builder grows beyond <paramref name="initialBuffer"/>.</param>
public ref struct ValueStringBuilder(Span<char> initialBuffer, ArrayPool<char>? arrayPool = null)
{
    readonly ArrayPool<char> arrayPool = arrayPool ?? ArrayPool<char>.Shared;
    
    Span<char> buffer = initialBuffer;
    char[]? pooledArray = null;
    int pos = 0;

    /// <summary>
    /// Gets the number of characters currently written into the builder.
    /// </summary>
    public int Length => pos;

    /// <summary>
    /// Appends one character to the builder.
    /// </summary>
    /// <param name="c">Character to append.</param>
    public void Append(char c)
    {
        if (pos < buffer.Length)
        {
            buffer[pos++] = c;
        }
        else
        {
            Grow(1);
            buffer[pos++] = c;
        }
    }

    /// <summary>
    /// Appends a span of characters to the builder.
    /// </summary>
    /// <param name="value">Characters to append.</param>
    public void Append(ReadOnlySpan<char> value)
    {
        var required = pos + value.Length;
        if (required > buffer.Length)
        {
            Grow(value.Length);
        }

        value.CopyTo(buffer[pos..]);
        pos += value.Length;
    }

    /// <summary>
    /// Appends a string to the builder when it is non-empty.
    /// </summary>
    /// <param name="value">String value to append.</param>
    public void Append(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        Append(value.AsSpan());
    }

    /// <summary>
    /// Resets the builder length to zero without releasing any rented buffer.
    /// </summary>
    public void Clear() => pos = 0;

    /// <summary>
    /// Returns the written portion of the builder as a span.
    /// </summary>
    /// <returns>The characters currently written into the builder.</returns>
    public ReadOnlySpan<char> AsSpan() => buffer[..pos];

    /// <summary>
    /// Materializes the written content as a string and disposes the builder.
    /// </summary>
    /// <returns>A new string containing the written characters.</returns>
    public override string ToString()
    {
        var result = new string(buffer[..pos]);
        Dispose();
        return result;
    }

    /// <summary>
    /// Copies the written characters into the supplied destination span.
    /// </summary>
    /// <param name="destination">Destination span that receives the builder contents.</param>
    public void CopyTo(Span<char> destination) => 
        AsSpan().CopyTo(destination);

    void Grow(int additionalCapacity)
    {
        var newSize = Math.Max(buffer.Length * 2, pos + additionalCapacity);
        var newArray = arrayPool.Rent(newSize);

        buffer[..pos].CopyTo(newArray);

        var oldArray = pooledArray;
        buffer = pooledArray = newArray;

        if (oldArray != null)
        {
            arrayPool.Return(oldArray);
        }
    }

    /// <summary>
    /// Returns any rented buffer to the pool and resets the builder to its default state.
    /// </summary>
    public void Dispose()
    {
        var pool = arrayPool;
        var toReturn = pooledArray;
        this = default;
        if (toReturn != null)
        {
            pool.Return(toReturn);
        }
    }
}
