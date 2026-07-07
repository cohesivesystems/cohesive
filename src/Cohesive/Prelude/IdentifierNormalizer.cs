namespace Cohesive.Prelude;

/// <summary>
/// Character set used when deciding which characters are valid identifier body characters.
/// </summary>
public enum IdentifierCharacterSet
{
    /// <summary>
    /// Allows letters and digits recognized by <see cref="char.IsLetterOrDigit(char)"/>.
    /// </summary>
    UnicodeLettersOrDigits,

    /// <summary>
    /// Allows only ASCII letters and digits.
    /// </summary>
    AsciiLettersOrDigits
}

/// <summary>
/// Casing transformation applied to valid identifier characters.
/// </summary>
public enum IdentifierCasing
{
    /// <summary>
    /// Preserves the original character casing.
    /// </summary>
    Preserve,

    /// <summary>
    /// Converts letters to lower invariant casing.
    /// </summary>
    LowerInvariant,

    /// <summary>
    /// Converts letters to upper invariant casing.
    /// </summary>
    UpperInvariant
}

/// <summary>
/// Behavior applied when an input character is not valid for the target identifier.
/// </summary>
public enum IdentifierInvalidCharacterBehavior
{
    /// <summary>
    /// Drops invalid characters.
    /// </summary>
    Omit,

    /// <summary>
    /// Replaces invalid characters with the configured separator when one is configured.
    /// </summary>
    ReplaceWithSeparator
}

/// <summary>
/// Options for normalizing human- or runtime-supplied values into stable identifiers.
/// </summary>
public sealed record IdentifierNormalizationOptions
{
    /// <summary>
    /// Default lowercase slug options using Unicode letters/digits and '-' as the separator.
    /// </summary>
    public static readonly IdentifierNormalizationOptions Slug = new();

    /// <summary>
    /// Lowercase ASCII slug options using '-' as the separator.
    /// </summary>
    public static readonly IdentifierNormalizationOptions AsciiSlug = new()
    {
        CharacterSet = IdentifierCharacterSet.AsciiLettersOrDigits
    };

    /// <summary>
    /// Lowercase compact resource-name options that drop invalid characters.
    /// </summary>
    public static readonly IdentifierNormalizationOptions CompactResourceName = new()
    {
        InvalidCharacterBehavior = IdentifierInvalidCharacterBehavior.Omit,
        Separator = null,
        RequireLeadingLetter = true,
        MinimumLength = 3,
        PaddingCharacter = 'x'
    };

    /// <summary>
    /// Character set used for primary letter/digit matching.
    /// </summary>
    public IdentifierCharacterSet CharacterSet { get; init; } = IdentifierCharacterSet.UnicodeLettersOrDigits;

    /// <summary>
    /// Casing transformation applied to letters.
    /// </summary>
    public IdentifierCasing Casing { get; init; } = IdentifierCasing.LowerInvariant;

    /// <summary>
    /// Separator used when replacing invalid characters. Set to <c>null</c> to omit invalid characters.
    /// </summary>
    public char? Separator { get; init; } = '-';

    /// <summary>
    /// Additional characters that should be preserved as valid identifier characters.
    /// </summary>
    public string? AdditionalAllowedCharacters { get; init; }

    /// <summary>
    /// Behavior used for invalid characters.
    /// </summary>
    public IdentifierInvalidCharacterBehavior InvalidCharacterBehavior { get; init; } = IdentifierInvalidCharacterBehavior.ReplaceWithSeparator;

    /// <summary>
    /// Indicates whether consecutive replacement separators should be collapsed into one separator.
    /// </summary>
    public bool CollapseSeparators { get; init; } = true;

    /// <summary>
    /// Indicates whether leading and trailing replacement separators should be removed.
    /// </summary>
    public bool TrimSeparators { get; init; } = true;

    /// <summary>
    /// Indicates whether the normalized identifier must start with a letter when it is non-empty.
    /// </summary>
    public bool RequireLeadingLetter { get; init; }

    /// <summary>
    /// Letter inserted when <see cref="RequireLeadingLetter"/> is true and the normalized identifier starts with a non-letter.
    /// </summary>
    public char LeadingLetterPrefix { get; init; } = 'a';

    /// <summary>
    /// Optional maximum normalized identifier length.
    /// </summary>
    public int? MaximumLength { get; init; }

    /// <summary>
    /// Optional minimum normalized identifier length. Empty results are not padded.
    /// </summary>
    public int? MinimumLength { get; init; }

    /// <summary>
    /// Character appended when the normalized identifier is shorter than <see cref="MinimumLength"/>.
    /// </summary>
    public char PaddingCharacter { get; init; } = 'x';

    /// <summary>
    /// Optional fallback value normalized when the input normalizes to an empty identifier.
    /// </summary>
    public string? EmptyFallback { get; init; }
}

/// <summary>
/// Normalizes strings into stable identifiers using configurable runtime and infrastructure constraints.
/// </summary>
public static class IdentifierNormalizer
{
    /// <summary>
    /// Normalizes <paramref name="value"/> with the supplied <paramref name="options"/>.
    /// </summary>
    public static string Normalize(string? value, IdentifierNormalizationOptions? options = null) =>
        Normalize(value.AsSpan(), options);

    /// <summary>
    /// Normalizes <paramref name="value"/> with the supplied <paramref name="options"/>.
    /// </summary>
    public static string Normalize(ReadOnlySpan<char> value, IdentifierNormalizationOptions? options = null)
    {
        var effectiveOptions = options ?? IdentifierNormalizationOptions.Slug;
        Validate(effectiveOptions);

        var result = NormalizeCore(value, effectiveOptions);
        result = TrimSeparators(result, effectiveOptions);

        if (result.Length == 0 && effectiveOptions.EmptyFallback is { Length: > 0 } fallback)
        {
            result = NormalizeCore(fallback.AsSpan(), effectiveOptions with { EmptyFallback = null });
            result = TrimSeparators(result, effectiveOptions);
        }

        if (result.Length == 0)
            return string.Empty;

        result = EnsureLeadingLetter(result, effectiveOptions);
        result = Truncate(result, effectiveOptions);
        result = TrimSeparators(result, effectiveOptions);
        result = Pad(result, effectiveOptions);
        return result;
    }

    static string NormalizeCore(ReadOnlySpan<char> value, IdentifierNormalizationOptions options)
    {
        if (value.Length == 0)
            return string.Empty;

        Span<char> initial = stackalloc char[Math.Min(value.Length, 128)];
        var builder = new ValueStringBuilder(initial);
        try
        {
            foreach (var ch in value)
            {
                if (IsLetterOrDigit(ch, options.CharacterSet))
                {
                    builder.Append(ApplyCasing(ch, options.Casing));
                    continue;
                }

                if (IsAdditionalAllowedCharacter(ch, options))
                {
                    builder.Append(ch);
                    continue;
                }

                if (options.InvalidCharacterBehavior != IdentifierInvalidCharacterBehavior.ReplaceWithSeparator ||
                    options.Separator is not { } separator)
                {
                    continue;
                }

                if (options.CollapseSeparators &&
                    builder.Length > 0 &&
                    builder.AsSpan()[^1] == separator)
                {
                    continue;
                }

                builder.Append(separator);
            }

            return builder.ToString();
        }
        finally
        {
            builder.Dispose();
        }
    }

    static void Validate(IdentifierNormalizationOptions options)
    {
        if (options.MaximumLength is <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum identifier length must be greater than zero.");

        if (options.MinimumLength is < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum identifier length cannot be negative.");

        if (options.MaximumLength is { } maximumLength &&
            options.MinimumLength is { } minimumLength &&
            minimumLength > maximumLength)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Minimum identifier length cannot exceed maximum identifier length.");
        }

        if (options.RequireLeadingLetter && !char.IsLetter(options.LeadingLetterPrefix))
            throw new ArgumentException("Leading letter prefix must be a letter.", nameof(options));
    }

    static bool IsLetterOrDigit(char ch, IdentifierCharacterSet characterSet) => characterSet switch
    {
        IdentifierCharacterSet.AsciiLettersOrDigits => char.IsAsciiLetterOrDigit(ch),
        _ => char.IsLetterOrDigit(ch)
    };

    static bool IsAdditionalAllowedCharacter(char ch, IdentifierNormalizationOptions options) =>
        options.AdditionalAllowedCharacters.AsSpan().Contains(ch);

    static char ApplyCasing(char ch, IdentifierCasing casing) => casing switch
    {
        IdentifierCasing.LowerInvariant => char.ToLowerInvariant(ch),
        IdentifierCasing.UpperInvariant => char.ToUpperInvariant(ch),
        _ => ch
    };

    static string EnsureLeadingLetter(string value, IdentifierNormalizationOptions options)
    {
        if (!options.RequireLeadingLetter || char.IsLetter(value[0]))
            return value;

        return string.Concat(options.LeadingLetterPrefix, value);
    }

    static string Truncate(string value, IdentifierNormalizationOptions options) =>
        options.MaximumLength is { } maximumLength && value.Length > maximumLength
            ? value[..maximumLength]
            : value;

    static string TrimSeparators(string value, IdentifierNormalizationOptions options) =>
        options.TrimSeparators && options.Separator is { } separator
            ? value.Trim(separator)
            : value;

    static string Pad(string value, IdentifierNormalizationOptions options)
    {
        if (options.MinimumLength is not { } minimumLength || value.Length >= minimumLength)
            return value;

        return value.PadRight(minimumLength, options.PaddingCharacter);
    }
}
