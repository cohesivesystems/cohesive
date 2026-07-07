using System.Diagnostics.Contracts;
using System.Text;

namespace Cohesive.Prelude;

/// <summary>
/// String helper extensions.
/// </summary>
public static class StringExtensions
{
    extension(ReadOnlySpan<char> value)
    {
        /// <summary>
        /// Normalizes a character string such that all letters and digits are converted to lowercase and all other characters are separated by the specified separator.
        /// </summary>
        /// <param name="separator">The separator to substitute for non-alphanumeric characters.</param>
        /// <param name="lowerCase">Whether to convert letters to lowercase.</param>
        /// <returns>The original string with its letters and digits, and with non-alphanumeric characters replaced with the specified separator.</returns>
        public string ToLettersOrDigitsWithSeparator(char separator = '-', bool lowerCase = true) => IdentifierNormalizer.Normalize(
            value,
            IdentifierNormalizationOptions.Slug with
            {
                Casing = lowerCase ? IdentifierCasing.LowerInvariant : IdentifierCasing.Preserve,
                Separator = separator
            });
    }
    
    extension(string? value)
    {
        /// <summary>
        /// Normalizes the string by trimming whitespace, and returns <paramref name="fallback"/> when the result is empty or whitespace.
        /// </summary>
        /// <param name="fallback">The string to fall back to if the given string is null or whitespace.</param>
        /// <returns>The whitespace trimmed string if not null or empty, otherwise <paramref name="fallback"/></returns>
        [Pure]
        public string? TrimmedEmptyOrWhiteSpaceAs(string? fallback = null)
        {
            var normalized = value?.Trim();
            return string.IsNullOrEmpty(normalized) ? fallback : normalized;
        }
        
        /// <summary>
        /// Returns <c>null</c> when the input is null, empty, or whitespace; otherwise returns the original value.
        /// </summary>
        /// <param name="fallback">Fallback value to use when the input is null, empty, or whitespace.</param>
        [Pure]
        public string? EmptyOrWhiteSpaceAs(string? fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

        /// <summary>
        /// Returns <c>null</c> when the input is null, empty, or whitespace; otherwise returns the original value.
        /// </summary>
        [Pure]
        public string? EmptyOrWhiteSpaceAsNull() => value.EmptyOrWhiteSpaceAs(fallback: null);
        
        /// <summary>
        /// Converts the string to a UTF8 byte array.
        /// </summary>
        /// <returns></returns>
        [Pure]
        public ReadOnlyMemory<byte> ToUtf8ReadOnlyMemory() => value is null ? [] : Encoding.UTF8.GetBytes(value);
    }

    extension(IEnumerable<string?> values)
    {
        /// <summary>
        /// Joins the strings using the specified separator.
        /// </summary>
        /// <param name="separator">The separator to join the strings with.</param>
        /// <param name="emptyFallback">The optional fallback value to use when the result is empty.</param>
        /// <returns></returns>
        [Pure]
        public string JoinToString(string separator, string? emptyFallback = null)
        {
            var result = string.Join(separator, values);
            if (string.IsNullOrEmpty(result) && !string.IsNullOrEmpty(emptyFallback))
                return emptyFallback;
            return result;
        }
        
        /// <summary>
        /// Joins the strings using the specified separator.
        /// </summary>
        /// <param name="separator">The separator to join the strings with.</param>
        /// <param name="emptyFallback">The optional fallback value to use when the result is empty.</param>
        /// <returns></returns>
        [Pure]
        public string JoinToString(char separator, string? emptyFallback = null)
        {
            var result = string.Join(separator, values);
            if (string.IsNullOrEmpty(result) && !string.IsNullOrEmpty(emptyFallback))
                return emptyFallback;
            return result;
        }
    }
}
