using Cohesive.Adapters.Sql;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cohesive.Adapters.Postgres;

/// <summary>
/// Allocates deterministic, human-readable PostgreSQL aliases within one SQL identifier namespace.
/// </summary>
internal sealed class PostgresSqlAliasAllocator
{
    const int DigestLength = 8;
    readonly Dictionary<string, string> semanticKeyByAlias = new(StringComparer.Ordinal);

    /// <summary>Allocates one safe, unique alias from semantic display and identity inputs.</summary>
    /// <param name="preferredName">Human-readable name preferred when it is safe and unique.</param>
    /// <param name="semanticKey">Stable semantic identity used for shortening and collision suffixes.</param>
    /// <param name="fallback">Readable fallback used when the preferred name contains no usable characters.</param>
    /// <returns>A unique PostgreSQL identifier no longer than the standard 63-byte limit.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fallback"/> is empty or white space.</exception>
    public string Allocate(string preferredName, string semanticKey, string fallback)
    {
        ArgumentNullException.ThrowIfNull(preferredName);
        ArgumentNullException.ThrowIfNull(semanticKey);
        ArgumentNullException.ThrowIfNull(fallback);

        var normalized = Normalize(preferredName, fallback);
        var digest = Digest(semanticKey);
        var baseCandidate = Fit(normalized, suffix: null, digest);
        if (semanticKeyByAlias.TryAdd(baseCandidate, semanticKey))
        {
            return baseCandidate;
        }

        if (string.Equals(semanticKeyByAlias[baseCandidate], semanticKey, StringComparison.Ordinal))
        {
            for (var ordinal = 2; ; ordinal++)
            {
                var candidate = Fit(
                    normalized,
                    $"__{ordinal.ToString(CultureInfo.InvariantCulture)}",
                    digest);
                if (semanticKeyByAlias.TryAdd(candidate, semanticKey))
                {
                    return candidate;
                }
            }
        }

        var collisionSuffix = $"__{digest}";
        var collisionCandidate = Fit(normalized, collisionSuffix, digest);
        if (semanticKeyByAlias.TryAdd(collisionCandidate, semanticKey))
        {
            return collisionCandidate;
        }

        for (var ordinal = 2; ; ordinal++)
        {
            var suffix = $"{collisionSuffix}_{ordinal.ToString(CultureInfo.InvariantCulture)}";
            collisionCandidate = Fit(normalized, suffix, digest);
            if (semanticKeyByAlias.TryAdd(collisionCandidate, semanticKey))
            {
                return collisionCandidate;
            }
        }
    }

    static string Normalize(string preferredName, string fallback)
    {
        var source = preferredName;
        try
        {
            source = source.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Invalid UTF-16 is replaced by separators below instead of reaching SQL rendering.
        }

        StringBuilder builder = new(source.Length);
        foreach (var rune in source.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || rune.Value == '_')
            {
                builder.Append(rune);
            }
            else if (builder.Length != 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var normalized = builder.ToString().TrimEnd('_');
        if (normalized.Length != 0)
        {
            return normalized;
        }

        if (string.IsNullOrWhiteSpace(fallback))
        {
            throw new ArgumentException("A PostgreSQL alias fallback cannot be empty.", nameof(fallback));
        }

        return Normalize(fallback, "alias");
    }

    static string Fit(string normalized, string? suffix, string digest)
    {
        if (suffix is null
            && SqlUtf8.GetByteCount(normalized, nameof(normalized))
            <= PostgresSqlDialect.StandardMaxUtf8ByteLength)
        {
            return PostgresSqlDialect.Identifier(normalized).Value;
        }

        var effectiveSuffix = suffix ?? $"__{digest}";
        var suffixLength = SqlUtf8.GetByteCount(effectiveSuffix, nameof(suffix));
        var prefixBudget = PostgresSqlDialect.StandardMaxUtf8ByteLength - suffixLength;
        if (prefixBudget <= 0)
        {
            throw new InvalidOperationException("PostgreSQL alias suffix exceeds the identifier limit.");
        }

        StringBuilder prefix = new();
        var prefixLength = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            var runeLength = rune.Utf8SequenceLength;
            if (prefixLength + runeLength > prefixBudget)
            {
                break;
            }

            prefix.Append(rune);
            prefixLength += runeLength;
        }

        while (prefix.Length != 0 && prefix[^1] == '_')
        {
            prefix.Length--;
        }

        if (prefix.Length == 0)
        {
            prefix.Append("alias");
        }

        return PostgresSqlDialect.Identifier($"{prefix}{effectiveSuffix}").Value;
    }

    static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant()[..DigestLength];
}
