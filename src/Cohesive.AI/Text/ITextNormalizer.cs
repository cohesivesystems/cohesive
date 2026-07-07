using System.Buffers;

namespace Cohesive.AI.Text;

/// <summary>
/// Normalizes text into a canonical representation.
/// </summary>
public interface ITextNormalizer
{
    /// <summary>
    /// Writes a normalized form of the input text into the output buffer.
    /// </summary>
    /// <param name="input">Input text to normalize.</param>
    /// <param name="output">Output buffer receiving normalized text.</param>
    void Normalize(ReadOnlySpan<char> input, IBufferWriter<char> output);
}
