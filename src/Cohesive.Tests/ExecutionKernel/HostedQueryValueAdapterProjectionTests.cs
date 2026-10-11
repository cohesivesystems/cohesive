using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class HostedQueryValueAdapterProjectionTests
{
    static readonly ValueContract Contract = new(new JsonTypeRef());
    [Fact]
    public void WarmLargeTypedDecodeHasABoundedAllocationPerRow()
    {
        var payload = new Payload(Enumerable.Range(0, 1024).Select(i => new Row(i, "é<>&🙂", 1.2300m)).ToArray());
        var input = PortableValue.Concrete(Contract, ObservationValue.FromObject(payload));
        for (var warm = 0; warm < 4; warm++) HostedQueryValueAdapter.Decode<Payload>(input, Contract);
        var samples = new long[7];
        Result<Payload, DocumentValidationDiagnostic> actual = default;
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            actual = HostedQueryValueAdapter.Decode<Payload>(input, Contract);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.Equal(ResultType.Success, actual.Type);
        Assert.Equal(payload.Rows, actual.Success!.Rows);
        Assert.True(samples[3] <= 304L * 1024, $"Median hosted decode allocation {samples[3]} exceeds 304 bytes per row.");
    }

    [Fact]
    public void DecodedJsonElementsOwnTheirStorageAfterTemporaryDocumentDisposal()
    {
        using var source = JsonDocument.Parse("{\"Content\":{\"number\":42,\"text\":\"retained\"}}");
        var input = PortableValue.Concrete(Contract, ObservationValue.FromJsonElement(source.RootElement));
        var actual = HostedQueryValueAdapter.Decode<JsonPayload>(input, Contract);
        Assert.Equal(ResultType.Success, actual.Type);
        for (var i = 0; i < 64; i++) HostedQueryValueAdapter.Decode<JsonPayload>(input, Contract);
        Assert.Equal(42, actual.Success!.Content.GetProperty("number").GetInt32());
        Assert.Equal("retained", actual.Success.Content.GetProperty("text").GetString());
    }

    [Fact]
    public void DecodeKeepsAdmissionAndConversionDiagnostics()
    {
        var malformed = PortableValue.Concrete(Contract, ObservationValue.FromObject(new { Number = "bad" }));
        var conversion = HostedQueryValueAdapter.Decode<NumericPayload>(malformed, Contract);
        Assert.Equal(HostedQueryValueDiagnosticCodes.ValueConversionFailed, conversion.Failure!.Code);
        var mismatch = HostedQueryValueAdapter.Decode<NumericPayload>(malformed, new(new ScalarTypeRef(ScalarTypeKind.String)));
        Assert.Equal(HostedQueryValueDiagnosticCodes.InputContractMismatch, mismatch.Failure!.Code);
        var missing = HostedQueryValueAdapter.Decode<NumericPayload>(PortableValue.Missing(Contract), Contract);
        Assert.Equal(HostedQueryValueDiagnosticCodes.InputValueInvalid, missing.Failure!.Code);
    }

    public sealed record Payload(Row[] Rows);
    public sealed record Row(long Number, string Text, decimal Amount);
    public sealed record JsonPayload(JsonElement Content);
    public sealed record NumericPayload(long Number);
}
