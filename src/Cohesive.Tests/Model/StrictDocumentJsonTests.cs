using System.Text;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class StrictDocumentJsonTests
{
    [Fact]
    public void TypedObjectApi_WritesAndReadsOneCanonicalExactDecimalWire()
    {
        var document = new TestDocument(
            Name: "index-sync",
            Slots: [2, 1],
            BatchSize: 1.2300m,
            Description: null);
        var options = StrictDocumentJson.CreateOptions();

        var canonical = StrictDocumentJson.GetCanonicalBytes(document, options);

        Assert.Equal(
            """{"batchSize":1.23,"description":null,"name":"index-sync","slots":[2,1]}""",
            Encoding.UTF8.GetString(canonical));
        Assert.True(
            StrictDocumentJson.TryReadCanonicalObject<TestDocument>(
                Encoding.UTF8.GetString(canonical),
                options,
                "test document",
                out var restored,
                out var error));
        Assert.NotNull(restored);
        Assert.Equal(document.Name, restored.Name);
        Assert.Equal(document.Slots, restored.Slots);
        Assert.Equal(document.BatchSize, restored.BatchSize);
        Assert.Null(restored.Description);
        Assert.Equal(default, error);
        Assert.Equal(canonical, StrictDocumentJson.GetCanonicalBytes(restored, options));
    }

    [Theory]
    [InlineData("[]", StrictDocumentJsonReadFailure.RootInvalid, "$")]
    [InlineData(
        """{"batchSize":1.23,"description":null,"name":"first","name":"second","slots":[2,1]}""",
        StrictDocumentJsonReadFailure.DuplicateProperty,
        "/name")]
    [InlineData(
        """{"batchSize":1.23,"name":"index-sync","slots":[2,1]}""",
        StrictDocumentJsonReadFailure.WireNonCanonical,
        "$")]
    public void TypedObjectApi_ReportsStrictStructuredReadFailures(
        string json,
        StrictDocumentJsonReadFailure expectedFailure,
        string expectedLocation)
    {
        var success = StrictDocumentJson.TryReadCanonicalObject<TestDocument>(
            json,
            StrictDocumentJson.CreateOptions(),
            "test document",
            out var value,
            out var error);

        Assert.False(success);
        if (expectedFailure == StrictDocumentJsonReadFailure.WireNonCanonical)
        {
            Assert.NotNull(value);
        }
        else
        {
            Assert.Null(value);
        }
        Assert.Equal(expectedFailure, error.Failure);
        Assert.Equal(expectedLocation, error.Location);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    sealed record TestDocument(
        string Name,
        int[] Slots,
        decimal BatchSize,
        string? Description);
}
