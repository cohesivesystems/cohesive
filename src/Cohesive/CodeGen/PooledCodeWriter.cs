using System.Buffers;

namespace Cohesive.CodeGen;

/// <summary>
/// Pooled character writer for source emission.
/// </summary>
public struct PooledCodeWriter : ICodeWriter, IDisposable
{
    const int DefaultInitialCapacity = 256;
    const int DefaultIndentSize = 4;

    ArrayPool<char>? arrayPool;
    char[]? buffer;
    int position;
    int indentLevel;
    int indentSize;
    bool lineStart;
    string? newLine;

    /// <summary>
    /// Creates a pooled writer.
    /// </summary>
    public PooledCodeWriter(
        ArrayPool<char>? arrayPool = null,
        int initialCapacity = DefaultInitialCapacity,
        int indentSize = DefaultIndentSize,
        string? newLine = null
        )
    {
        this.arrayPool = arrayPool ?? ArrayPool<char>.Shared;
        buffer = this.arrayPool.Rent(Math.Max(1, initialCapacity));
        position = 0;
        indentLevel = 0;
        this.indentSize = Math.Max(0, indentSize);
        lineStart = true;
        this.newLine = string.IsNullOrEmpty(newLine) ? Environment.NewLine : newLine;
    }

    /// <summary>
    /// Gets the number of characters written.
    /// </summary>
    public readonly int Length => position;

    /// <inheritdoc />
    public readonly int IndentLevel => indentLevel;

    /// <summary>
    /// Returns the written content as a span.
    /// </summary>
    public readonly ReadOnlySpan<char> AsSpan()
    {
        return buffer is null
            ? ReadOnlySpan<char>.Empty
            : buffer.AsSpan(0, position);
    }

    /// <summary>
    /// Materializes the writer content as a string.
    /// </summary>
    public readonly override string ToString() => new(AsSpan());

    /// <inheritdoc />
    public void PushIndent() => indentLevel++;

    /// <inheritdoc />
    public void PopIndent()
    {
        if (indentLevel > 0)
            indentLevel--;
    }

    /// <inheritdoc />
    public void Write(char value)
    {
        EnsureInitialized();
        WriteIndentIfNeeded();
        EnsureCapacity(1);
        buffer![position++] = value;
    }

    /// <inheritdoc />
    public void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        Write(value.AsSpan());
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return;

        EnsureInitialized();
        WriteIndentIfNeeded();
        EnsureCapacity(value.Length);
        value.CopyTo(buffer.AsSpan(position));
        position += value.Length;
    }

    /// <inheritdoc />
    public void WriteLine()
    {
        EnsureInitialized();
        var lineEnding = newLine.AsSpan();
        EnsureCapacity(lineEnding.Length);
        lineEnding.CopyTo(buffer.AsSpan(position));
        position += lineEnding.Length;
        lineStart = true;
    }

    /// <inheritdoc />
    public void WriteLine(string? value)
    {
        Write(value);
        WriteLine();
    }

    /// <inheritdoc />
    public void WriteLine(ReadOnlySpan<char> value)
    {
        Write(value);
        WriteLine();
    }

    void WriteIndentIfNeeded()
    {
        if (!lineStart)
            return;

        lineStart = false;
        var indentWidth = indentLevel * indentSize;
        if (indentWidth <= 0)
            return;

        EnsureCapacity(indentWidth);
        buffer.AsSpan(position, indentWidth).Fill(' ');
        position += indentWidth;
    }

    void EnsureInitialized()
    {
        if (buffer is not null)
            return;

        arrayPool ??= ArrayPool<char>.Shared;
        indentSize = indentSize <= 0 ? DefaultIndentSize : indentSize;
        newLine ??= Environment.NewLine;
        buffer = arrayPool.Rent(DefaultInitialCapacity);
        lineStart = true;
    }

    void EnsureCapacity(int additionalCapacity)
    {
        EnsureInitialized();
        var currentBuffer = buffer!;
        var required = position + additionalCapacity;
        if (required <= currentBuffer.Length)
            return;

        var newLength = currentBuffer.Length * 2;
        if (newLength < required)
            newLength = required;

        var pool = arrayPool!;
        var replacement = pool.Rent(newLength);
        currentBuffer.AsSpan(0, position).CopyTo(replacement);
        buffer = replacement;
        pool.Return(currentBuffer);
    }

    /// <summary>
    /// Returns the rented buffer to the pool.
    /// </summary>
    public void Dispose()
    {
        if (buffer is not null)
        {
            arrayPool!.Return(buffer);
        }

        this = default;
    }
}
