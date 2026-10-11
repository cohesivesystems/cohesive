using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class HostedQueryValueAdapterProjectionTests
{
    static readonly ValueContract Contract = new(new JsonTypeRef());
    static readonly JsonSerializerOptions Options = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        options.PropertyNamingPolicy = null;
        options.Converters.Insert(0, new DeclaredJsonValueConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    [Fact]
    public void LargeTypedDecodeAvoidsTheClonedElementAllocation()
    {
        var payload = new Payload(Enumerable.Range(0, 1024).Select(i => new Row(i, "é<>&🙂", 1.2300m)).ToArray());
        var input = PortableValue.Concrete(Contract, ObservationValue.FromObject(payload));
        for (var warm = 0; warm < 4; warm++)
        {
            Previous(input);
            HostedQueryValueAdapter.Decode<Payload>(input, Contract);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var expected = Previous(input);
        var previousAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var actual = HostedQueryValueAdapter.Decode<Payload>(input, Contract);
        var allocation = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(ResultType.Success, actual.Type);
        Assert.Equal(expected.Rows, actual.Success!.Rows);
        var payloadSize = JsonSerializer.SerializeToUtf8Bytes(payload, Options).Length;
        Assert.True(previousAllocation - allocation > payloadSize,
            $"Cloned-element decode allocated {previousAllocation}; scoped decode allocated {allocation}; JSON {payloadSize} bytes.");
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

    static Payload Previous(PortableValue input)
    {
        Assert.True(PortableExecutionValidator.Validate(input).IsValid);
        var element = JsonSerializer.SerializeToElement(input.Value!.Value, Options);
        return element.Deserialize<Payload>(Options)!;
    }

    public sealed record Payload(Row[] Rows);
    public sealed record Row(long Number, string Text, decimal Amount);
    public sealed record JsonPayload(JsonElement Content);
    public sealed record NumericPayload(long Number);
}
