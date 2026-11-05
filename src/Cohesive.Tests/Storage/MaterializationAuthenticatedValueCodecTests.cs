using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationAuthenticatedValueCodecTests
{
    const string FormatPrefix = "test/v1/";
    const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    readonly MaterializationAuthenticatedValueCodec codec = new(
        formatPrefix: FormatPrefix, authenticationDomain: "test-domain"u8,
        authenticationKey: new byte[MaterializationAuthenticatedValueCodec.MinimumAuthenticationKeyBytes],
        maximumValueCharacters: 1024);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CanonicalValuesRoundTripAndRejectAlteredPayloads(int length)
    {
        var payload = Enumerable.Repeat((byte)255, length).ToArray();
        var value = codec.Encode(payload);
        Assert.Equal(payload, codec.Decode(value, "position", "position"));
        var start = FormatPrefix.Length;
        var altered = value[..start] + "A" + value[(start + 1)..];
        Assert.Throws<ArgumentException>(() => codec.Decode(altered, "position", "position"));
        var tagStart = value.IndexOf('.') + 1;
        var alteredTag = value[..tagStart] + (value[tagStart] == 'A' ? 'B' : 'A') + value[(tagStart + 1)..];
        Assert.Throws<ArgumentException>(() => codec.Decode(alteredTag, "position", "position"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void RejectsEveryPayloadPaddingBitAliasEvenWithAnAuthenticTag(int length)
    {
        var payload = Enumerable.Repeat((byte)255, length).ToArray();
        var value = codec.Encode(payload);
        var separator = value.IndexOf('.');
        var canonical = Alphabet.IndexOf(value[separator - 1]);
        var aliasCount = length == 1 ? 16 : 4;
        for (var lowBits = 1; lowBits < aliasCount; lowBits++)
        {
            var alias = value[..(separator - 1)] + Alphabet[canonical + lowBits] + value[separator..];
            var error = Assert.Throws<ArgumentException>(() => codec.Decode(alias, "position", "position"));
            Assert.Equal("position", error.ParamName);
        }
    }

    [Fact]
    public void RejectsEveryTagPaddingBitAliasEvenWhenItDecodesToTheSameTag()
    {
        var value = codec.Encode([255]);
        var canonical = Alphabet.IndexOf(value[^1]);
        for (var lowBits = 1; lowBits < 4; lowBits++)
        {
            var alias = value[..^1] + Alphabet[canonical + lowBits];
            Assert.Throws<ArgumentException>(() => codec.Decode(alias, "position", "position"));
        }
    }

    [Theory]
    [InlineData(1, "_w==")]
    [InlineData(3, "////")]
    [InlineData(3, "____    ")]
    public void RejectsPaddingStandardAlphabetAndWhitespace(int length, string encodedPayload)
    {
        var value = codec.Encode(Enumerable.Repeat((byte)255, length).ToArray());
        var separator = value.IndexOf('.');
        var altered = FormatPrefix + encodedPayload + value[separator..];
        Assert.Throws<ArgumentException>(() => codec.Decode(altered, "position", "position"));
    }
}
