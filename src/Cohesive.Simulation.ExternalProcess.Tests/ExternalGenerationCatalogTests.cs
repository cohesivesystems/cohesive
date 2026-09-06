using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Simulation.ExternalProcess;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.ExternalProcess.Tests;

public sealed class ExternalGenerationCatalogTests
{
    const string ExpectedRequestId =
        "csimcatalogrequest1_539821d7ce98d659955a01108cc7579965b54e5d5a45a41c26a1542a5dac0052";
    const string ExpectedRequestJson =
        "{\"catalogId\":\"catalog/external-profiles\",\"catalogRevision\":\"r1\","
        + "\"configuration\":{\"prefix\":\"fixture\"},\"count\":2,"
        + "\"dateTimeReferenceUtc\":\"1970-01-01T00:00:00+00:00\",\"locale\":\"en\","
        + $"\"requestId\":\"{ExpectedRequestId}\","
        + "\"schemaVersion\":\"cohesive-simulation-generation-catalog-provider/v1\","
        + "\"seed\":\"9223372036854775807\",\"valueType\":{\"$type\":\"object\",\"fields\":["
        + "{\"annotations\":{},\"cardinality\":\"Single\",\"name\":\"Name\","
        + "\"nullability\":\"NonNullable\",\"presence\":\"Required\","
        + "\"type\":{\"$type\":\"scalar\",\"format\":\"None\",\"kind\":\"String\"}},"
        + "{\"annotations\":{},\"cardinality\":\"Single\",\"name\":\"Region\","
        + "\"nullability\":\"NonNullable\",\"presence\":\"Required\","
        + "\"type\":{\"$type\":\"scalar\",\"format\":\"None\",\"kind\":\"String\"}}]}}";
    const string ExpectedResponseJson =
        "{\"provider\":\"fixture-provider\",\"providerVersion\":\"9.1.0\","
        + $"\"requestId\":\"{ExpectedRequestId}\","
        + "\"schemaVersion\":\"cohesive-simulation-generation-catalog-provider/v1\","
        + "\"values\":[\"Ada\",\"Grace\"]}";

    static readonly DefaultClrTypeRefMapper TypeMapper = new();
    static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Protocol_RoundTripsOneCanonicalCorrelatedRequest()
    {
        var request = ExternalGenerationCatalogProtocol.CreateRequest(
            catalogId: "catalog/external-profiles",
            catalogRevision: "r1",
            count: 2,
            seed: long.MaxValue,
            valueType: TypeMapper.Map(typeof(FixtureProfile), nullability: null),
            configuration: Configuration(),
            locale: "en",
            dateTimeReferenceUtc: DateTimeOffset.UnixEpoch);

        var json = ExternalGenerationCatalogProtocol.SerializeRequest(request);
        Assert.Equal(ExpectedRequestId, request.RequestId);
        Assert.Equal(ExpectedRequestJson, json);
        var restored = ExternalGenerationCatalogProtocol.DeserializeRequest(json);

        Assert.StartsWith("csimcatalogrequest1_", restored.RequestId, StringComparison.Ordinal);
        Assert.Equal(long.MaxValue, restored.Seed);
        Assert.Equal("fixture", restored.Configuration.GetProperty("prefix").GetString());
        Assert.Equal(json, ExternalGenerationCatalogProtocol.SerializeRequest(restored));
        Assert.Contains($"\"seed\":\"{long.MaxValue.ToString(CultureInfo.InvariantCulture)}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Protocol_RejectsTamperedIdentityAndOpenWireContent()
    {
        var request = Request();
        var json = ExternalGenerationCatalogProtocol.SerializeRequest(request);
        var tamperedIdentity = json.Replace(
            request.RequestId,
            "csimcatalogrequest1_0000000000000000000000000000000000000000000000000000000000000000",
            StringComparison.Ordinal);
        var open = json.Replace("\"configuration\":", "\"unknown\":true,\"configuration\":", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => ExternalGenerationCatalogProtocol.DeserializeRequest(tamperedIdentity));
        Assert.Throws<JsonException>(() => ExternalGenerationCatalogProtocol.DeserializeRequest(open));

        using var duplicateConfiguration = JsonDocument.Parse("{\"field\":1,\"field\":2}");
        Assert.Throws<ArgumentException>(() => ExternalGenerationCatalogProtocol.CreateRequest(
            catalogId: "catalog/duplicate-configuration",
            catalogRevision: "r1",
            count: 1,
            seed: 1,
            valueType: TypeMapper.Map(typeof(string), nullability: null),
            configuration: duplicateConfiguration.RootElement));
    }

    [Fact]
    public void Protocol_RoundTripsOneCanonicalCorrelatedResponse()
    {
        var response = new ExternalGenerationCatalogResponse(
            schemaVersion: ExternalGenerationCatalogProtocol.CurrentSchemaVersion,
            requestId: ExpectedRequestId,
            provider: "fixture-provider",
            providerVersion: "9.1.0",
            values: [ObservationValue.FromString("Ada"), ObservationValue.FromString("Grace")]);

        var json = ExternalGenerationCatalogProtocol.SerializeResponse(response);
        var restored = ExternalGenerationCatalogProtocol.DeserializeResponse(json);

        Assert.Equal(ExpectedResponseJson, json);
        Assert.Equal(ExpectedRequestId, restored.RequestId);
        Assert.Equal(["Ada", "Grace"], restored.Values.Select(static value => value.String));
    }

    [Fact]
    public async Task ImportAsync_RetainsExactValuesAndProviderEvidence()
    {
        var provider = Provider("success");
        var options = Options();

        var first = await ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(provider, options);
        var second = await ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(provider, options);

        Assert.Equal(
            GenerationCatalogJsonSerializer.Serialize(first),
            GenerationCatalogJsonSerializer.Serialize(second));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(["sample/00000000", "sample/00000001"], first.Definition.Entries.Select(static entry => entry.Id));
        Assert.Equal("fixture-42-0", first.Definition.Entries[0].Value.Fields!["Name"].String);
        Assert.Equal("en", first.Definition.Entries[0].Value.Fields!["Region"].String);
        Assert.Equal(ExternalGenerationCatalogImporter.AdapterIdentity, first.Definition.Provenance.Adapter);
        Assert.Equal("fixture-provider", first.Definition.Provenance.Provider);
        Assert.Equal("9.1.0", first.Definition.Provenance.ProviderVersion);
        Assert.Equal("fixture-random/v1", first.Definition.Provenance.RandomAlgorithm);
        Assert.Equal("42", first.Definition.Provenance.Seed);
        Assert.Equal(DateTimeOffset.UnixEpoch, first.Definition.Provenance.DateTimeReferenceUtc);
        Assert.Equal(Profile(), first.Definition.Provenance.CapabilityProfile);
        Assert.Equal(
            [
                "csimcatalogrequest://csimcatalogrequest1_9e4f781082c0f0ca72053afc384eacd78b47bf86b61a00ec9a4218d940e8efd5",
                "repo://src/Cohesive.Simulation.ExternalProcess.Tests/ExternalGenerationCatalogTests.cs"
            ],
            first.Definition.Provenance.SourceReferences.Select(static source => source.Value));
    }

    [Fact]
    public async Task ImportAsync_FingerprintsConfigurationEvenWhenProducedValuesAreEqual()
    {
        var provider = Provider("success");
        var baseline = await ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(provider, Options());
        var changedConfiguration = JsonSerializer.SerializeToElement(
            new ExtendedFixtureConfiguration("fixture", "changed-but-ignored-by-provider"),
            WebJson);
        var changed = await ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(
            provider,
            Options(changedConfiguration));

        Assert.Equal(
            baseline.Definition.Entries.Select(static entry => entry.Value),
            changed.Definition.Entries.Select(static entry => entry.Value));
        Assert.NotEqual(baseline.Fingerprint, changed.Fingerprint);
        Assert.NotEqual(
            baseline.Definition.Provenance.SourceReferences[0],
            changed.Definition.Provenance.SourceReferences[0]);
    }

    [Theory]
    [InlineData("wrong-request", ExternalGenerationCatalogFailure.ResponseMismatch)]
    [InlineData("wrong-version", ExternalGenerationCatalogFailure.ResponseMismatch)]
    [InlineData("wrong-value", ExternalGenerationCatalogFailure.ResponseInvalid)]
    [InlineData("invalid", ExternalGenerationCatalogFailure.ResponseInvalid)]
    [InlineData("oversize", ExternalGenerationCatalogFailure.ResponseTooLarge)]
    public async Task ImportAsync_FailsClosedForInvalidProviderOutput(
        string mode,
        ExternalGenerationCatalogFailure expectedFailure)
    {
        var exception = await Assert.ThrowsAsync<ExternalGenerationCatalogException>(() =>
            ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(
                Provider(mode, maximumMessageBytes: 1024),
                Options()));

        Assert.Equal(expectedFailure, exception.Failure);
    }

    [Fact]
    public async Task ImportAsync_RejectsOversizedRequestsBeforeProcessLaunch()
    {
        var exception = await Assert.ThrowsAsync<ExternalGenerationCatalogException>(() =>
            ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(
                Provider("success", maximumMessageBytes: 64),
                Options()));

        Assert.Equal(ExternalGenerationCatalogFailure.RequestTooLarge, exception.Failure);
    }

    [Fact]
    public async Task ImportAsync_ClassifiesExecutableStartFailure()
    {
        var provider = new ExternalGenerationCatalogProvider(
            executable: "cohesive-intentionally-missing-provider-executable",
            arguments: [],
            provider: "fixture-provider",
            providerVersion: "9.1.0",
            randomAlgorithm: "fixture-random/v1",
            capabilityProfile: Profile());

        var exception = await Assert.ThrowsAsync<ExternalGenerationCatalogException>(() =>
            ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(provider, Options()));

        Assert.Equal(ExternalGenerationCatalogFailure.StartFailed, exception.Failure);
    }

    [Fact]
    public async Task ImportAsync_RetainsBoundedFailureDiagnostics()
    {
        var exception = await Assert.ThrowsAsync<ExternalGenerationCatalogException>(() =>
            ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(
                Provider("fail", maximumStandardErrorBytes: 12),
                Options()));

        Assert.Equal(ExternalGenerationCatalogFailure.ProcessFailed, exception.Failure);
        Assert.Equal(17, exception.ExitCode);
        Assert.Equal("fixture prov", exception.StandardError);
        Assert.True(exception.StandardErrorTruncated);
    }

    [Fact]
    public async Task ImportAsync_TerminatesAProviderThatExceedsItsLimit()
    {
        var exception = await Assert.ThrowsAsync<ExternalGenerationCatalogException>(() =>
            ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(
                Provider("hang", timeout: TimeSpan.FromMilliseconds(200)),
                Options()));

        Assert.Equal(ExternalGenerationCatalogFailure.TimedOut, exception.Failure);
    }

    [Fact]
    public async Task ImportAsync_PreservesCallerCancellationWhileTerminatingProvider()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(
                Provider("hang", timeout: TimeSpan.FromSeconds(5)),
                Options(),
                cancellation.Token));
    }

    [Fact]
    public async Task ImportAsync_ValidatesCapabilityCoordinatesBeforeProcessLaunch()
    {
        var finiteOnly = new GenerationCatalogCapabilityProfile(
            id: "fixture-provider/finite/v1",
            capabilities: [GenerationCatalogProducerCapability.FiniteSnapshot],
            sourceReferences: [SourceReference.Repository(new("provider.py"))]);
        var provider = new ExternalGenerationCatalogProvider(
            executable: "does-not-exist",
            arguments: [],
            provider: "fixture-provider",
            providerVersion: "9.1.0",
            randomAlgorithm: "fixture-random/v1",
            capabilityProfile: finiteOnly);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            ExternalGenerationCatalogImporter.ImportAsync<FixtureProfile>(provider, Options()));
    }

    static ExternalGenerationCatalogRequest Request() => ExternalGenerationCatalogProtocol.CreateRequest(
        catalogId: "catalog/external-profiles",
        catalogRevision: "r1",
        count: 2,
        seed: 42,
        valueType: TypeMapper.Map(typeof(FixtureProfile), nullability: null),
        configuration: Configuration(),
        locale: "en",
        dateTimeReferenceUtc: DateTimeOffset.UnixEpoch);

    static ExternalGenerationCatalogProvider Provider(
        string mode,
        TimeSpan? timeout = null,
        int maximumMessageBytes = ExternalGenerationCatalogProvider.DefaultMaximumMessageBytes,
        int maximumStandardErrorBytes = ExternalGenerationCatalogProvider.DefaultMaximumStandardErrorBytes) => new(
        executable: Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
        arguments: [typeof(ExternalGenerationCatalogTests).Assembly.Location, mode],
        provider: "fixture-provider",
        providerVersion: "9.1.0",
        randomAlgorithm: "fixture-random/v1",
        capabilityProfile: Profile(),
        timeout: timeout,
        maximumMessageBytes: maximumMessageBytes,
        maximumStandardErrorBytes: maximumStandardErrorBytes);

    static ExternalGenerationCatalogImportOptions Options(JsonElement? configuration = null) => new(
        id: "catalog/external-profiles",
        revision: "r1",
        count: 2,
        seed: 42,
        configuration: configuration ?? Configuration(),
        sourceReferences:
        [
            SourceReference.Repository(
                new("src/Cohesive.Simulation.ExternalProcess.Tests/ExternalGenerationCatalogTests.cs"))
        ],
        locale: "en",
        dateTimeReferenceUtc: DateTimeOffset.UnixEpoch);

    static GenerationCatalogCapabilityProfile Profile() => new(
        id: "fixture-provider/process-snapshot/v1",
        capabilities:
        [
            GenerationCatalogProducerCapability.FiniteSnapshot,
            GenerationCatalogProducerCapability.StructuredValues,
            GenerationCatalogProducerCapability.LocaleSelection,
            GenerationCatalogProducerCapability.LocalSeed,
            GenerationCatalogProducerCapability.FixedUtcDateTimeReference
        ],
        sourceReferences: [SourceReference.Repository(new("provider.py"))]);

    static JsonElement Configuration() =>
        JsonSerializer.SerializeToElement(new FixtureConfiguration("fixture"), WebJson);

    sealed record FixtureConfiguration(string Prefix);

    sealed record ExtendedFixtureConfiguration(string Prefix, string Evidence);

    public sealed record FixtureProfile(string Name, string Region);
}

public static class ExternalGenerationCatalogTestProviderProgram
{
    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length != 1)
            return 64;

        var mode = arguments[0];
        if (string.Equals(mode, "hang", StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return 0;
        }
        if (string.Equals(mode, "fail", StringComparison.Ordinal))
        {
            await Console.Error.WriteAsync("fixture provider failed with intentionally long diagnostics");
            return 17;
        }
        if (string.Equals(mode, "oversize", StringComparison.Ordinal))
        {
            await Console.Out.WriteAsync(new string('x', 4096));
            return 0;
        }
        if (string.Equals(mode, "invalid", StringComparison.Ordinal))
        {
            await Console.Out.WriteAsync("{\"unknown\":true}");
            return 0;
        }

        var request = ExternalGenerationCatalogProtocol.DeserializeRequest(await Console.In.ReadToEndAsync());
        var values = ImmutableArray.CreateBuilder<ObservationValue>(request.Count);
        var prefix = request.Configuration.GetProperty("prefix").GetString()
                     ?? throw new InvalidOperationException("Fixture prefix is required.");
        for (var index = 0; index < request.Count; index++)
        {
            values.Add(string.Equals(mode, "wrong-value", StringComparison.Ordinal)
                ? ObservationValue.FromString("not-an-object")
                : ObservationValue.FromObject(new ExternalGenerationCatalogTests.FixtureProfile(
                    $"{prefix}-{request.Seed.ToString(CultureInfo.InvariantCulture)}-{index.ToString(CultureInfo.InvariantCulture)}",
                    request.Locale ?? "none")));
        }

        var responseRequestId = string.Equals(mode, "wrong-request", StringComparison.Ordinal)
            ? "csimcatalogrequest1_0000000000000000000000000000000000000000000000000000000000000000"
            : request.RequestId;
        var response = new ExternalGenerationCatalogResponse(
            schemaVersion: ExternalGenerationCatalogProtocol.CurrentSchemaVersion,
            requestId: responseRequestId,
            provider: "fixture-provider",
            providerVersion: string.Equals(mode, "wrong-version", StringComparison.Ordinal) ? "9.2.0" : "9.1.0",
            values: values.MoveToImmutable());
        await Console.Out.WriteAsync(ExternalGenerationCatalogProtocol.SerializeResponse(response));
        return 0;
    }
}
