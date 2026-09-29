using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessStorageDepthTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(40)]
    public void NestedTaggedValue_RetainsCanonicalBytesAndFingerprintThroughRecovery(int levels)
    {
        var value = Nested(levels);
        var options = ProcessDurableCheckpointJsonSerializer.CreateOptions();
        var bytes = StrictDocumentJson.GetCanonicalBytes(value, options);
        Assert.True(StrictDocumentJson.TryReadCanonicalObject<PortableValue>(
            Encoding.UTF8.GetString(bytes), options, "retained value", out var restored, out var error), error.Message);
        Assert.Equal(bytes, StrictDocumentJson.GetCanonicalBytes(restored!, options));
        Assert.Equal(ProcessStorageContentFingerprints.Value(value), ProcessStorageContentFingerprints.Value(restored!));
        if (levels == 2)
            Assert.Equal(StrictDocumentJson.GetCanonicalBytes(value, StrictDocumentJson.CreateOptions()), bytes);
        else
            Assert.Throws<JsonException>(() => StrictDocumentJson.GetCanonicalBytes(value, StrictDocumentJson.CreateOptions()));
    }

    [Fact]
    public void BeyondStorageDepth_IsRejectedOnWriteAndRead()
    {
        var value = Nested(ProcessDurableCheckpointJsonSerializer.MaximumJsonDepth);
        var options = ProcessDurableCheckpointJsonSerializer.CreateOptions();
        Assert.Throws<JsonException>(() => ProcessStorageContentFingerprints.Value(value));
        var expanded = new JsonSerializerOptions(options) { MaxDepth = 1024 };
        var json = JsonSerializer.Serialize(value, expanded);
        Assert.False(StrictDocumentJson.TryReadCanonicalObject<PortableValue>(
            json, options, "retained value", out var restored, out var error));
        Assert.Null(restored);
        Assert.Equal(StrictDocumentJsonReadFailure.InvalidJson, error.Failure);
    }

    static PortableValue Nested(int levels)
    {
        var value = ObservationValue.FromString("leaf");
        for (var index = 0; index < levels; index++)
            value = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["child"] = value });
        return PortableValue.Concrete(new ValueContract(new JsonTypeRef(JsonTypeKind.Object)), value);
    }
}
