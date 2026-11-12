using System.Text.Json;
using System.Text.Json.Serialization;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model;
using Microsoft.AspNetCore.Mvc;

namespace Cohesive.Semantics.Tests;

public sealed class ApiWireCompatibilityTests
{
    static JsonSerializerOptions Options(bool requiredConstructors = false) => new(JsonSerializerDefaults.Web)
        { RespectRequiredConstructorParameters = requiredConstructors };
    static ApiOperation Operation<T>() => new("wire", ApiOperationKind.Query, typeof(void), typeof(T), new HttpBinding("GET", "/wire", [], null));
    static ApiContractDocument Contract<T>(JsonSerializerOptions options) => AspNetApiContract.Lower(Operation<T>(), options, "tests/web/problem-details-v1");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedDefaultAcceptsOldPayloadAndOldReadersIgnoreNewField(bool requiredConstructors)
    {
        var options = Options(requiredConstructors);
        var old = new LegacySummary("reservation", "sku", 2, 7);
        var oldJson = JsonSerializer.Serialize(old, options);
        var restored = JsonSerializer.Deserialize<ReservationSummary>(oldJson, options)!;
        Assert.False(restored.Released);
        Assert.Equal(old.Id, restored.Id);
        foreach (var released in new[] { false, true })
        {
            var current = new ReservationSummary(old.Id, old.Sku, old.Quantity, old.AvailableStock, released);
            var currentJson = JsonSerializer.Serialize(current, options);
            Assert.Equal(old, JsonSerializer.Deserialize<LegacySummary>(currentJson, options));
            Assert.Equal(released, JsonSerializer.Deserialize<ReservationSummary>(currentJson, options)!.Released);
        }
        Assert.False(JsonSerializer.Deserialize<OptionalInitSummary>(oldJson, options)!.Released);
        Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(Contract<LegacySummary>(options),
            Contract<OptionalInitSummary>(options), ApiCompatibilityDirection.ProposedClientToExistingServer).Verdict);
        var before = Contract<LegacySummary>(options);
        var after = Contract<ReservationSummary>(options);
        foreach (var direction in Enum.GetValues<ApiCompatibilityDirection>())
            Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(before, after, direction).Verdict);
    }

    [Fact]
    public void GeneratedOrderReadersAndSkuReadersAcceptLegacyResponses()
    {
        var legacy = new LegacyOrderDetails("order", OrderStatus.Submitted, [new("r", "s", 2, 7)]);
        var oldJson = JsonSerializer.Serialize(legacy, WireRegressionContext.Default.LegacyOrderDetails);
        var current = JsonSerializer.Deserialize(oldJson, WireRegressionContext.Default.OrderDetails)!;
        Assert.False(Assert.Single(current.Reservations).Released);
        var newJson = JsonSerializer.Serialize(new OrderDetails("order", OrderStatus.Submitted,
            [new("r", "s", 2, 7, Released: true)]), WireRegressionContext.Default.OrderDetails);
        var oldReader = JsonSerializer.Deserialize(newJson, WireRegressionContext.Default.LegacyOrderDetails)!;
        Assert.Equal("r", Assert.Single(oldReader.Reservations).Id);
        var options = Options(requiredConstructors: true);
        var sku = new LegacySku("r", "order", "s", 2);
        Assert.False(JsonSerializer.Deserialize<ReservationBySku>(JsonSerializer.Serialize(sku, options), options)!.Released);
        Assert.Equal(sku, JsonSerializer.Deserialize<LegacySku>(JsonSerializer.Serialize(new ReservationBySku("r", "order", "s", 2, true), options), options));
        foreach (var direction in Enum.GetValues<ApiCompatibilityDirection>())
        {
            Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(Contract<LegacyOrderDetails>(options), Contract<OrderDetails>(options), direction).Verdict);
            Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(Contract<LegacySku>(options), Contract<ReservationBySku>(options), direction).Verdict);
        }
    }

    [Fact]
    public void RequiredFieldsRemainBreakingAndRejectMissingWireValues()
    {
        var options = Options(requiredConstructors: true);
        var payload = JsonSerializer.Serialize(new LegacySummary("r", "s", 1, 3), options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RequiredReleased>(payload, options));
        Assert.Equal(ApiCompatibilityVerdict.Breaking, ApiContractCompatibility.Compare(Contract<LegacySummary>(options),
            Contract<RequiredReleased>(options), ApiCompatibilityDirection.ProposedClientToExistingServer).Verdict);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AttributedRequired>("{}", Options()));
        Assert.Equal(ApiCompatibilityVerdict.Breaking, ApiContractCompatibility.Compare(Contract<Empty>(Options()),
            Contract<AttributedRequired>(Options()), ApiCompatibilityDirection.ProposedClientToExistingServer).Verdict);
    }

    [Fact]
    public void KnownIdentifiersAndAdmissionResultsHaveConformantWireRepresentations()
    {
        var options = Options();
        var ids = new Ids(new("definition"), new("revision"), new("instance"), new("attempt"), new("1"));
        var json = JsonSerializer.Serialize(ids, options);
        using var parsed = JsonDocument.Parse(json);
        Assert.All(parsed.RootElement.EnumerateObject(), p => Assert.Equal(JsonValueKind.String, p.Value.ValueKind));
        Assert.Equal(ids, JsonSerializer.Deserialize<Ids>(json, options));
        var conflict = ProcessStartResult.Conflict(ProcessStartDisposition.InstanceConflict);
        var resultJson = JsonSerializer.Serialize(conflict, options);
        Assert.Equal(conflict, JsonSerializer.Deserialize<ProcessStartResult>(resultJson, options));
        Assert.DoesNotContain("isConflict", resultJson);
        var definition = new FulfillmentProcess(new FulfillmentDomain()).Definition.Document.Reference;
        var accepted = new ProcessStartResult(ProcessStartDisposition.Accepted,
            new(definition, new(new("instance"), new("attempt")), ProcessControlRevision.Initial,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(accepted, JsonSerializer.Deserialize<ProcessStartResult>(JsonSerializer.Serialize(accepted, options), options));
        foreach (var contract in new[] { Contract<Ids>(options), Contract<ProcessStartResult>(options) })
            Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(contract, contract).Verdict);
    }

    [Fact]
    public void StringKeyedExtensionsAreObjectsAndValueChangesRemainBreaking()
    {
        var options = Options();
        var problem = new ApiProblem("conflict", "rejected", Extensions: new Dictionary<string, string[]> { ["a/b~c"] = ["one", "two"], ["0"] = [] });
        var json = JsonSerializer.Serialize(problem, options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.GetProperty("extensions").ValueKind);
        Assert.Equal(problem.Extensions!["a/b~c"], JsonSerializer.Deserialize<ApiProblem>(json, options)!.Extensions!["a/b~c"]);
        Assert.Equal(ApiCompatibilityVerdict.Compatible, ApiContractCompatibility.Compare(Contract<ApiProblem>(options), Contract<ApiProblem>(options)).Verdict);
        Assert.Equal(ApiCompatibilityVerdict.Breaking, ApiContractCompatibility.Compare(Contract<StringMap>(options), Contract<IntMap>(options)).Verdict);
    }

    [Fact]
    public void AspNetProblemDetailsSupportsStandardMembersAndOpenExtensionData()
    {
        var options = Options();
        var problem = new ProblemDetails { Type = "urn:conflict", Title = "Conflict", Status = 409, Detail = "state", Instance = "/orders/1" };
        problem.Extensions["trace"] = new[] { "a", "b" };
        var json = JsonSerializer.Serialize(problem, options);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(409, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("extensions", out _));
        var restored = JsonSerializer.Deserialize<ProblemDetails>(json, options)!;
        Assert.Equal(problem.Status, restored.Status);
        Assert.Equal(problem.Title, restored.Title);
        Assert.True(restored.Extensions.ContainsKey("trace"));
        Assert.NotNull(JsonSerializer.Deserialize<ProblemDetails>("{}", options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProblemDetails>("{\"status\":\"not-a-number\"}", options));
        var contract = Contract<ProblemDetails>(options);
        Assert.True(ApiContractCompatibility.Compare(contract, contract).Verdict == ApiCompatibilityVerdict.Compatible, JsonSerializer.Serialize(contract));
    }

    [Fact]
    public void EveryCurrentOrdersApiIsCoveredByTheQualifiedWireProfile()
    {
        var domain = new FulfillmentDomain();
        var process = new FulfillmentProcess(domain);
        var queries = new FulfillmentQueries(domain);
        var cancellation = new CancellationProcess(domain, queries);
        var execution = new FulfillmentExecutionPolicy { Lifetime = ProcessExecutionLifetime.Durable };
        var plan = new OrderEndpoints(new OrderApi(domain, process, execution, cancellation), domain, queries, execution: execution).Plan;
        Assert.Equal(10, plan.Implementations.Length);
        foreach (var endpoint in plan.Implementations)
        {
            var contract = AspNetApiContract.Lower(endpoint.Endpoint, Options(), "tests/web/problem-details-v1");
            var result = ApiContractCompatibility.Compare(contract, contract);
            Assert.True(result.Verdict == ApiCompatibilityVerdict.Compatible,
                endpoint.Endpoint.Id + ": " + JsonSerializer.Serialize(result.Diagnostics));
        }
    }

    [Fact]
    public void ArbitraryConvertersRemainUnknown()
    {
        var options = Options();
        options.Converters.Add(new ArbitraryConverter());
        var contract = Contract<Custom>(options);
        Assert.Equal(ApiCompatibilityVerdict.Unknown, ApiContractCompatibility.Compare(contract, contract).Verdict);
    }

    [Fact]
    public void MalformedReaderEvidenceRemainsUnknown()
    {
        var contract = ApiContractDocument.Lower(Operation<LegacySummary>(), Options(), "tests/invalid",
            wireMetadata: new InvalidReaderEvidence());
        Assert.Equal(ApiCompatibilityVerdict.Unknown, ApiContractCompatibility.Compare(contract, contract).Verdict);
    }

    sealed class InvalidReaderEvidence : IClrShapeMetadataProvider
    {
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context) => context.Target == ClrShapeMetadataTarget.Field
            ? new() { Annotations = AnnotationMap.Create("api.json.readerRequired/v1", "invalid") } : ClrShapeMetadata.Empty;
    }

    public sealed record LegacyOrderDetails(string Id, OrderStatus Status, IReadOnlyList<LegacySummary> Reservations);
    public sealed record LegacySku(string Id, string OrderId, string Sku, int Quantity);
    public sealed record LegacySummary(string Id, string Sku, int Quantity, int AvailableStock);
    public sealed record OptionalInitSummary(string Id, string Sku, int Quantity, int AvailableStock)
    {
        public bool Released { get; init; }
    }
    public sealed record RequiredReleased(string Id, string Sku, int Quantity, int AvailableStock, bool Released);
    public sealed record AttributedRequired([property: JsonRequired] bool Released);
    public sealed record Empty(string? Unrelated = null);
    public sealed record Ids(ExecutionDefinitionId Definition, ExecutionRevisionId Revision, ProcessInstanceId Instance, ProcessAttemptId Attempt, ProcessControlRevision Control);
    public sealed record StringMap(IReadOnlyDictionary<string, string[]> Values);
    public sealed record IntMap(IReadOnlyDictionary<string, int[]> Values);
    public sealed record Custom(int Value);
    sealed class ArbitraryConverter : JsonConverter<Custom>
    {
        public override Custom Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt32());
        public override void Write(Utf8JsonWriter writer, Custom value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Value);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ApiWireCompatibilityTests.LegacyOrderDetails))]
[JsonSerializable(typeof(OrderDetails))]
internal partial class WireRegressionContext : JsonSerializerContext;
