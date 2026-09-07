using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Tests;

internal static class GenerationCatalogFingerprintAssertions
{
    const string ExpectedAlgorithm = "sha256";
    const string ExpectedCanonicalization = "cohesive-simulation-generation-catalog/v2-c14n/v1";

    public static void EqualPinnedWireIdentity(
        GenerationCatalogDocument catalog,
        Func<string, string> normalizeReleaseCoordinates,
        string expectedNormalizedDefinitionSha256)
    {
        Assert.Equal(ExpectedAlgorithm, catalog.Fingerprint.Algorithm);
        Assert.Equal(ExpectedCanonicalization, catalog.Fingerprint.Canonicalization);
        using var document = JsonDocument.Parse(GenerationCatalogJsonSerializer.GetCanonicalBytes(catalog));
        var definitionJson = document.RootElement.GetProperty("definition").GetRawText();
        var normalizedDefinitionJson = normalizeReleaseCoordinates(definitionJson);

        Assert.NotEqual(definitionJson, normalizedDefinitionJson);
        var normalizedDefinitionSha256 = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDefinitionJson)));
        Assert.Equal(
            expectedNormalizedDefinitionSha256,
            normalizedDefinitionSha256);
        Assert.Equal(ComputeFingerprint(definitionJson), catalog.Fingerprint.Value);
    }

    static string ComputeFingerprint(string definitionJson)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes(ExpectedCanonicalization));
        Append(hash, Encoding.UTF8.GetBytes(definitionJson));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static void Append(IncrementalHash hash, byte[] value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
