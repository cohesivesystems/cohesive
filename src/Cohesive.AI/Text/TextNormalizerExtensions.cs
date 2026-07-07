using System.Buffers;

namespace Cohesive.AI.Text;

/// <summary>
/// Helpers for projecting normalized text into managed strings.
/// </summary>
public static class TextNormalizerExtensions
{
    /// <param name="normalizer">Normalizer instance to run.</param>
    extension(ITextNormalizer normalizer)
    {
        /// <summary>
        /// Normalizes text into an <see cref="ArrayBufferWriter{T}"/> and returns the resulting string.
        /// </summary>
        /// <param name="input">Input text to normalize.</param>
        /// <returns>Normalized text materialized as a string.</returns>
        public string NormalizeToString(ReadOnlySpan<char> input)
        {
            ArgumentNullException.ThrowIfNull(normalizer);
            var output = new ArrayBufferWriter<char>(Math.Max(input.Length, 16));
            normalizer.Normalize(input, output);
            return new string(output.WrittenSpan);
        }
    }
}
