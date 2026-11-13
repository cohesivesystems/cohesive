namespace Cohesive.Model;

/// <summary>Shared unambiguous text encoding for algorithm, canonicalization profile and digest triples.</summary>
public static class FingerprintText
{
    /// <summary>Formats a fingerprint as three colon-delimited components.</summary>
    /// <param name="algorithm">Hash-algorithm text; the owning domain validates its identity.</param>
    /// <param name="canonicalization">Canonicalization-profile text; the owning domain validates its identity.</param>
    /// <param name="value">Digest text supplied and validated by the owning domain.</param>
    /// <returns>A caller-owned string; percent and colon are encoded as %25 and %3A within components. Other characters are retained.</returns>
    /// <remarks>This is fingerprint text, not a URI encoder or a hash algorithm. Domain types retain their validation and hash authority.</remarks>
    /// <exception cref="ArgumentNullException">A component is null.</exception>
    public static string Format(string algorithm, string canonicalization, string value)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(canonicalization);
        ArgumentNullException.ThrowIfNull(value);
        return $"{Escape(algorithm)}:{Escape(canonicalization)}:{Escape(value)}";
    }

    static string Escape(string component) => component.AsSpan().IndexOfAny('%', ':') < 0
        ? component : component.Replace("%", "%25", StringComparison.Ordinal)
        .Replace(":", "%3A", StringComparison.Ordinal);
}
