using Cohesive.Prelude;

namespace Cohesive.Tests.Prelude;

public sealed class IdentifierNormalizerTests
{
    [Theory]
    [InlineData("Sample Test_Run!!", "sample-test-run")]
    [InlineData("__Sample---Run__", "sample-run")]
    [InlineData("éxample 12", "xample-12")]
    public void Normalize_WithAsciiSlugOptions_CreatesDelimitedAsciiIdentifier(string value, string expected)
    {
        var normalized = IdentifierNormalizer.Normalize(value, IdentifierNormalizationOptions.AsciiSlug);

        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("sample-training-dev", null, "sampletrainingdev")]
    [InlineData("9-", null, "a9x")]
    [InlineData("abc-def-ghi", 5, "abcde")]
    public void Normalize_WithCompactResourceNameOptions_CreatesStorageCompatibleIdentifier(
        string value,
        int? maximumLength,
        string expected)
    {
        var normalized = IdentifierNormalizer.Normalize(
            value,
            IdentifierNormalizationOptions.CompactResourceName with
            {
                MaximumLength = maximumLength
            });

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Normalize_UsesFallbackWhenInputNormalizesToEmpty()
    {
        var normalized = IdentifierNormalizer.Normalize(
            "!!!",
            IdentifierNormalizationOptions.CompactResourceName with
            {
                EmptyFallback = "fallback"
            });

        Assert.Equal("fallback", normalized);
    }

    [Fact]
    public void Normalize_CanPreserveAdditionalIdentifierCharacters()
    {
        var normalized = IdentifierNormalizer.Normalize(
            "Task Type__Tenant!",
            IdentifierNormalizationOptions.Slug with
            {
                AdditionalAllowedCharacters = "-_",
                CollapseSeparators = false,
                TrimSeparators = false
            });

        Assert.Equal("task-type__tenant-", normalized);
    }
}
