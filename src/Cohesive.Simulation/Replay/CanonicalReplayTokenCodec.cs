using System.Text;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation;

/// <summary>Shared strict encoding mechanism for versioned simulation replay evidence.</summary>
internal static class CanonicalReplayTokenCodec
{
    public static string Encode<T>(T evidence, string prefix)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var payload = StrictDocumentJson.GetCanonicalBytes(evidence, StrictDocumentJson.CreateOptions());
        return prefix + Convert.ToBase64String(payload)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static T Decode<T>(
        string token,
        string prefix,
        string tokenName,
        string evidenceContractName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!token.StartsWith(prefix, StringComparison.Ordinal) || token.Length == prefix.Length)
            throw new FormatException($"A {tokenName} must use the '{prefix}' format.");

        var encoded = token[prefix.Length..];
        byte[] payload;
        try
        {
            var paddingLength = (4 - encoded.Length % 4) % 4;
            payload = Convert.FromBase64String(encoded
                .Replace('-', '+')
                .Replace('_', '/')
                .PadRight(encoded.Length + paddingLength, '='));
        }
        catch (FormatException exception)
        {
            throw new FormatException(
                $"{StartSentence(tokenName)} payload is not URL-safe Base64.",
                exception);
        }

        var json = Encoding.UTF8.GetString(payload);
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                StrictDocumentJson.CreateOptions(),
                evidenceContractName,
                out T? evidence,
                out var error)
            || evidence is null)
        {
            throw new FormatException(
                $"{StartSentence(tokenName)} payload is invalid at '{error.Location}': {error.Message}");
        }

        if (!string.Equals(token, Encode(evidence, prefix), StringComparison.Ordinal))
            throw new FormatException($"{StartSentence(tokenName)} is not in canonical current-version form.");
        return evidence;
    }

    static string StartSentence(string value) =>
        char.ToUpperInvariant(value[0]) + value[1..];
}
