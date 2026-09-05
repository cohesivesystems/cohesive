using System.Text;

namespace Cohesive.Adapters.Sql;

/// <summary>Strict UTF-8 validation and encoding shared by SQL construction and adapter I/O.</summary>
public static class SqlUtf8
{
    static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Creates a stateful encoder for segmented text without a byte-order mark.</summary>
    /// <returns>A caller-owned encoder that rejects invalid Unicode; flush it on the final segment.</returns>
    public static Encoder CreateEncoder() => Strict.GetEncoder();

    /// <summary>Calculates a conservative encoding buffer size.</summary>
    /// <param name="characterCount">Nonnegative number of UTF-16 code units to encode.</param>
    /// <returns>Maximum required bytes, including possible pending encoder state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is negative or the maximum exceeds Int32.</exception>
    public static int GetMaximumByteCount(int characterCount) => Strict.GetMaxByteCount(characterCount);

    /// <summary>Counts the exact UTF-8 bytes after validating Unicode.</summary>
    /// <param name="value">Text to validate and measure; zero characters are permitted.</param>
    /// <param name="parameterName">Caller parameter name to attribute a validation failure to.</param>
    /// <returns>Number of encoded bytes without a byte-order mark.</returns>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">The value contains invalid Unicode.</exception>
    public static int GetByteCount(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        try
        {
            return Strict.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("A SQL UTF-8 value must contain valid Unicode.", parameterName, exception);
        }
    }

    /// <summary>Validates the shared SQL text domain without allocating an encoded copy.</summary>
    /// <param name="value">Text to validate; empty text is permitted.</param>
    /// <param name="parameterName">Caller parameter name to attribute a validation failure to.</param>
    /// <returns>The unchanged input string.</returns>
    /// <exception cref="ArgumentNullException">The value is null.</exception>
    /// <exception cref="ArgumentException">The value contains invalid Unicode or a zero character.</exception>
    public static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("A SQL text value cannot contain a zero character.", parameterName);
        _ = GetByteCount(value, parameterName);
        return value;
    }
}
