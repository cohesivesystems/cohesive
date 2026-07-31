using System.Security.Cryptography;

namespace Cohesive.Storage.Materialization;

/// <summary>
/// Encodes and authenticates bounded opaque materialization values without interpreting their payload schema.
/// </summary>
/// <remarks>
/// The codec frames bytes as canonical unpadded base64url plus an HMAC-SHA-256 tag. Callers own payload
/// serialization, schema validation, and canonical re-encoding. Format prefixes and authentication domains must be
/// versioned whenever their wire semantics change. The codec copies the authentication key and domain.
/// </remarks>
public sealed class MaterializationAuthenticatedValueCodec
{
    /// <summary>Minimum accepted authentication-key size in bytes.</summary>
    public const int MinimumAuthenticationKeyBytes = 32;

    const int AuthenticationTagBytes = 32;
    const int EncodedAuthenticationTagCharacters = 43;

    readonly string formatPrefix;
    readonly byte[] authenticationDomain;
    readonly byte[] authenticationKey;
    readonly int maximumValueCharacters;

    /// <summary>Creates one versioned authenticated opaque-value codec.</summary>
    /// <param name="formatPrefix">Non-empty versioned prefix prepended to every encoded value.</param>
    /// <param name="authenticationDomain">Non-empty domain-separation bytes included before every payload.</param>
    /// <param name="authenticationKey">Caller-owned secret of at least 32 bytes; the codec copies the value.</param>
    /// <param name="maximumValueCharacters">Positive maximum encoded characters accepted or produced.</param>
    /// <exception cref="ArgumentNullException"><paramref name="formatPrefix"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="formatPrefix"/> is empty, <paramref name="authenticationDomain"/> is empty, or
    /// <paramref name="authenticationKey"/> contains fewer than 32 bytes.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumValueCharacters"/> is not positive.</exception>
    public MaterializationAuthenticatedValueCodec(
        string formatPrefix,
        ReadOnlySpan<byte> authenticationDomain,
        ReadOnlySpan<byte> authenticationKey,
        int maximumValueCharacters)
    {
        this.formatPrefix = Guard.RequireNotNullOrWhiteSpace(formatPrefix);
        if (authenticationDomain.IsEmpty)
            throw new ArgumentException("An authenticated value requires a non-empty domain.", nameof(authenticationDomain));
        if (authenticationKey.Length < MinimumAuthenticationKeyBytes)
        {
            throw new ArgumentException(
                $"An authenticated value key requires at least {MinimumAuthenticationKeyBytes} bytes.",
                nameof(authenticationKey));
        }
        if (maximumValueCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumValueCharacters),
                maximumValueCharacters,
                "An authenticated value character bound must be positive.");
        }

        this.authenticationDomain = authenticationDomain.ToArray();
        this.authenticationKey = authenticationKey.ToArray();
        this.maximumValueCharacters = maximumValueCharacters;
    }

    /// <summary>Authenticates and frames one opaque payload.</summary>
    /// <param name="payload">Opaque payload bytes owned by the caller.</param>
    /// <returns>The versioned bounded authenticated value.</returns>
    /// <exception cref="InvalidOperationException">The encoded value exceeds its declared character bound.</exception>
    public string Encode(ReadOnlySpan<byte> payload)
    {
        var tag = Authenticate(payload);
        var value = string.Concat(
            formatPrefix,
            ToBase64Url(Convert.ToBase64String(payload)),
            ".",
            ToBase64Url(Convert.ToBase64String(tag)));
        if (value.Length > maximumValueCharacters)
            throw new InvalidOperationException("The authenticated materialization value exceeded its declared size bound.");
        return value;
    }

    /// <summary>Authenticates and extracts one opaque payload without interpreting its schema.</summary>
    /// <param name="value">Encoded value to authenticate.</param>
    /// <param name="parameterName">Caller-facing parameter name used by validation exceptions.</param>
    /// <param name="valueKind">Non-empty caller-facing kind such as <c>continuation</c> or <c>position</c>.</param>
    /// <returns>A newly allocated authenticated payload byte array.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="value"/>, <paramref name="parameterName"/>, or <paramref name="valueKind"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The value is oversized, has the wrong versioned prefix, is malformed, or fails authentication; or a textual
    /// parameter is empty or white space.
    /// </exception>
    public byte[] Decode(string value, string parameterName, string valueKind)
    {
        ArgumentNullException.ThrowIfNull(value);
        parameterName = Guard.RequireNotNullOrWhiteSpace(parameterName);
        valueKind = Guard.RequireNotNullOrWhiteSpace(valueKind);
        if (value.Length > maximumValueCharacters || !value.StartsWith(formatPrefix, StringComparison.Ordinal))
            throw new ArgumentException($"The authenticated {valueKind} format or size is unsupported.", parameterName);

        try
        {
            var framed = value.AsSpan(formatPrefix.Length);
            var separator = framed.IndexOf('.');
            if (separator <= 0
                || framed[(separator + 1)..].Length != EncodedAuthenticationTagCharacters
                || framed[(separator + 1)..].IndexOf('.') >= 0)
            {
                throw new FormatException("Invalid authenticated value framing.");
            }

            var payload = Convert.FromBase64String(FromBase64Url(framed[..separator]));
            var suppliedTag = Convert.FromBase64String(FromBase64Url(framed[(separator + 1)..]));
            if (suppliedTag.Length != AuthenticationTagBytes
                || !CryptographicOperations.FixedTimeEquals(suppliedTag, Authenticate(payload)))
            {
                throw new CryptographicException("Invalid authenticated value tag.");
            }
            return payload;
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException($"The authenticated {valueKind} failed authentication.", parameterName, exception);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException($"The authenticated {valueKind} is malformed.", parameterName, exception);
        }
    }

    byte[] Authenticate(ReadOnlySpan<byte> payload)
    {
        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, authenticationKey);
        hash.AppendData(authenticationDomain);
        hash.AppendData(payload);
        return hash.GetHashAndReset();
    }

    static string ToBase64Url(string value) => value
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    static string FromBase64Url(ReadOnlySpan<char> value)
    {
        var text = value.ToString().Replace('-', '+').Replace('_', '/');
        return (text.Length % 4) switch
        {
            0 => text,
            2 => string.Concat(text, "=="),
            3 => string.Concat(text, "="),
            _ => throw new FormatException("Invalid base64url length.")
        };
    }
}
