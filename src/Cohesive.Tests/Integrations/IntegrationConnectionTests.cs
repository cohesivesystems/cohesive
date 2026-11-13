using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Integrations.Authoring;
using Xunit;

namespace Cohesive.Tests.Integrations;

public sealed partial class IntegrationModelTests
{
    static readonly ConnectionReference MarketData = ConnectionReference.Named("market-data");
    static ProviderOperationProfile Profile(string operation = "prices") => ProviderOperationProfile
        .Define("test/" + operation, "v1", "tiingo", operation, "HTTPS")
        .AuthenticateWith("api-key").RequiresEntitlements("daily").Batching(defaultSize: 2, maximumSize: 10);
    static IntegrationConnection Connection(string revision = "v1", string provider = "tiingo", string medium = "HTTPS",
        string authentication = "api-key", string entitlement = "daily", int maximum = 10) => IntegrationConnection
        .Define("test/connection", revision, provider, "test/account", medium, "https://api.example.com")
        .AuthenticateWith(authentication, "env:TEST_API_KEY").EntitledTo(entitlement).LimitBatchSize(maximum);
    static IntegrationInstance<SelectionSource> Connected(ProviderOperationProfile profile)
    {
        var model = new TestModel();
        return model.Collect.For(Selection()).Use(model.Collect.Provider(profile).Provides(model.Coverage))
            .Through(MarketData).ForConsumer(new("research", "source", "store", "SPY"));
    }
    static IntegrationConnectionResolution Resolution(IntegrationCompilation compiled) => compiled.Documents
        .Single(d => d.Kind.Value == "integration.connection-resolution.v1").GetDefinition<IntegrationConnectionResolution>();

    [Fact]
    public void SharedConnectionServesDistinctOperationsAndReopensExactRuntimeBinding()
    {
        var connection = Connection();
        var bindings = new ConnectionBindings().Bind(MarketData, connection);
        var prices = Profile(); var metadata = Profile("metadata");
        var sharedClient = new object();
        var runtime = new IntegrationRuntime().Register(prices, connection, sharedClient).Register(metadata, connection, sharedClient);
        foreach (var profile in new[] { prices, metadata })
        {
            var compiled = Connected(profile).Compile(bindings);
            Valid(compiled);
            Assert.Equal(connection.Document.Reference, Resolution(compiled).Connection);
            Assert.Same(sharedClient, runtime.Resolve<object>(compiled));
            var reopened = compiled with { Documents = [.. compiled.Documents.Select(d =>
            {
                Assert.True(ExecutionDefinitionJsonSerializer.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(d), out var value).IsValid);
                return value!;
            })] };
            Assert.Same(sharedClient, runtime.Resolve<object>(reopened));
        }
    }

    [Fact]
    public void LogicalRebindingDoesNotChangeRetainedResolution()
    {
        var profile = Profile(); var old = Connection(); var current = Connection(revision: "v2");
        var instance = Connected(profile);
        var retained = instance.Compile(new ConnectionBindings().Bind(MarketData, old));
        var rebound = instance.Compile(new ConnectionBindings().Bind(MarketData, current));
        Valid(retained); Valid(rebound);
        Assert.NotEqual(retained.Binding!.Reference, rebound.Binding!.Reference);
        var handler = new object();
        var runtime = new IntegrationRuntime().Register(profile, current, handler);
        Assert.Throws<InvalidOperationException>(() => runtime.Resolve<object>(retained));
        Assert.Same(handler, runtime.Resolve<object>(rebound));
        Assert.Same(handler, runtime.Register(profile, old, handler).Resolve<object>(retained));
    }

    [Fact]
    public void DefaultAndExplicitBatchChoicesHaveDistinctProvenanceAndNeverWeakenLimits()
    {
        var instance = Connected(Profile());
        var bindings = new ConnectionBindings().Bind(MarketData, Connection(maximum: 3));
        var defaults = instance.Compile(bindings); var explicitChoice = instance.WithBatchSize(3).Compile(bindings);
        Valid(defaults); Valid(explicitChoice);
        Assert.Null(Resolution(defaults).RequestedBatchSize);
        Assert.Equal(2, Resolution(defaults).EffectiveBatchSize);
        Assert.Equal(3, Resolution(explicitChoice).RequestedBatchSize);
        foreach (var size in new[] { 0, 4, 11 })
            Assert.Contains(instance.WithBatchSize(size).Compile(bindings).Validation.Diagnostics, d => d.Code == "integrations.connection.batchSize");
        Assert.False(instance.Compile(new ConnectionBindings().Bind(MarketData, Connection(maximum: 1))).IsSuccessful);
        Assert.True(instance.WithBatchSize(1).Compile(new ConnectionBindings().Bind(MarketData, Connection(maximum: 1))).IsSuccessful);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("medium")]
    [InlineData("authentication")]
    [InlineData("entitlement")]
    public void IneligibleConnectionsHaveStructuredDiagnostics(string mismatch)
    {
        var connection = Connection(provider: mismatch == "provider" ? "other" : "tiingo",
            medium: mismatch == "medium" ? "FTP" : "HTTPS", authentication: mismatch == "authentication" ? "oauth" : "api-key",
            entitlement: mismatch == "entitlement" ? "other" : "daily");
        var compiled = Connected(Profile()).Compile(new ConnectionBindings().Bind(MarketData, connection));
        Assert.False(compiled.IsSuccessful);
        Assert.Contains(compiled.Validation.Diagnostics, d => d.Code == "integrations.connection." + mismatch);
    }

    [Fact]
    public void ExplicitResolutionRejectsUnboundAndAmbiguousNames()
    {
        Assert.Contains(Connected(Profile()).Compile(new ConnectionBindings()).Validation.Diagnostics,
            d => d.Code == "integrations.connection.unbound");
        var bindings = new ConnectionBindings().Bind(MarketData, Connection());
        Assert.Throws<ArgumentException>(() => bindings.Bind(MarketData, Connection()));
        Assert.Contains(Connected(Profile()).WithAccount("other").Compile(bindings).Validation.Diagnostics,
            d => d.Code == "integrations.connection.account");
    }

    [Fact]
    public void NormalizedDeclarationsHaveDeterministicFingerprints()
    {
        var first = Profile().RequiresEntitlements("daily", "metadata", "daily");
        var second = Profile().RequiresEntitlements("metadata", "daily");
        Assert.Equal(first.Document.Reference, second.Document.Reference);
        Assert.DoesNotContain("metadata", Profile().Document.Definition.GetRawText());
    }

    [Fact]
    public void ChangedEffectiveValuesAndMissingExactDependenciesAreRejected()
    {
        var compiled = Connected(Profile()).Compile(new ConnectionBindings().Bind(MarketData, Connection()));
        Valid(compiled);
        var original = compiled.Documents.Single(d => d.Kind.Value == "integration.connection-resolution.v1");
        var tampered = ExecutionDefinitionDocument.Create(original.Kind, original.Metadata.DefinitionId, original.Metadata.RevisionId,
            Resolution(compiled) with { EffectiveBatchSize = 9 }, original.Metadata.Provenance);
        Assert.True(ExecutionDefinitionDocumentCatalog.TryCreate(compiled.Documents, out var catalog).IsValid);
        Assert.Contains(IntegrationConnectionDocuments.Inspect(tampered, catalog!, out _).Diagnostics,
            d => d.Code == "integrations.connection.effective");
        Assert.True(ExecutionDefinitionDocumentCatalog.TryCreate(compiled.Documents.Where(d => d.Reference != Connection().Document.Reference), out var missing).IsValid);
        Assert.False(IntegrationConnectionDocuments.Inspect(original, missing!, out _).IsValid);
    }
    [Theory]
    [InlineData("FTP", "ftp://files.example.com")]
    [InlineData("Kafka", "kafka://broker.example.com")]
    [InlineData("RPC", "https://rpc.example.com")]
    public void MediumDeclarationsRemainOpenWithoutClaimingAnInstalledInterpreter(string medium, string endpoint)
    {
        var profile = ProviderOperationProfile.Define("test/transport", "v1", "service", "read", medium);
        var connection = IntegrationConnection.Define("test/transport-access", "v1", "service", "account", medium, endpoint);
        Assert.True(IntegrationConnectionDocuments.Match(profile.Document, connection.Document, null, out var size).IsValid);
        Assert.Equal(1, size);
    }

    [Fact]
    public void ProfileHardLimitAlsoConstrainsGenerousConnections()
    {
        var compiled = Connected(Profile()).WithBatchSize(11)
            .Compile(new ConnectionBindings().Bind(MarketData, Connection(maximum: 100)));
        Assert.Contains(compiled.Validation.Diagnostics, d => d.Code == "integrations.connection.batchSize");
    }
}
