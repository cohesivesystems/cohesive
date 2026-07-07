namespace Cohesive.CodeGen;

/// <summary>
/// Span-friendly abstraction for emitting source text.
/// </summary>
public interface ICodeWriter
{
    /// <summary>
    /// Gets the current indentation level.
    /// </summary>
    int IndentLevel { get; }

    /// <summary>
    /// Increases indentation by one level.
    /// </summary>
    void PushIndent();

    /// <summary>
    /// Decreases indentation by one level.
    /// </summary>
    void PopIndent();

    /// <summary>
    /// Writes a single character.
    /// </summary>
    void Write(char value);

    /// <summary>
    /// Writes a string when it is non-empty.
    /// </summary>
    void Write(string? value);

    /// <summary>
    /// Writes a span when it is non-empty.
    /// </summary>
    void Write(ReadOnlySpan<char> value);

    /// <summary>
    /// Writes the current line terminator.
    /// </summary>
    void WriteLine();

    /// <summary>
    /// Writes text followed by the current line terminator.
    /// </summary>
    void WriteLine(string? value);

    /// <summary>
    /// Writes text followed by the current line terminator.
    /// </summary>
    void WriteLine(ReadOnlySpan<char> value);
}
