using Cohesive.Configuration;
using Cohesive.Execution;
using Cohesive.Integrations;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;
using Layer = Cohesive.Configuration.SelectionLayer<Cohesive.Integrations.IntegrationChoice, Cohesive.Integrations.IntegrationSelectionPolicy>;

namespace Cohesive.Examples.Integrations;

/// <summary>System, tenant and user selection with inherited restrictions and pinned historical-data scope.</summary>
public sealed class ScopedHistoricalDataExamples
{
    /// <summary>Resolves an analyst preference within system and tenant policies, then checks a later revocation.</summary>
    [Fact]
    public void HistoricalDataUsesScopedDefaultsWithoutWeakeningPolicies()
    {
        var logical = ConnectionReference.Named("historical-data");
        var tenant = new SelectionScope("research");
        var user = new SelectionScope("research", "analyst");
        var profile = ProviderOperationProfile.Define("example/daily", "v1", "market-data", "daily-prices", "HTTPS")
            .RequiresEntitlements("historical").Batching(defaultSize: 1, maximumSize: 10);
        var standard = IntegrationConnection.Define("example/standard", "v1", "market-data", "shared-account", "HTTPS",
            "https://market-data.example").EntitledTo("historical").LimitBatchSize(2);
        var premium = IntegrationConnection.Define("example/premium", "v1", "market-data", "research-account", "HTTPS",
            "https://market-data.example").EntitledTo("historical", "premium").LimitBatchSize(10);

        var system = Layer.Define("example/system", "v1", "platform-admin", new())
            .Prefer(IntegrationChoice.Select(profile, standard))
            .Enforce(new(AllowedProviders: ["market-data"], RequiredEntitlements: ["historical"], MaximumBatchSize: 8));
        var research = Layer.Define("example/research", "v1", "research-admin", tenant)
            .Prefer(IntegrationChoice.Select(profile, premium, batchSize: 4))
            .Enforce(new(AllowedResources: ["research-account"], MaximumBatchSize: 6));
        var analyst = Layer.Define("example/analyst", "v1", "analyst", user)
            .Prefer(IntegrationChoice.Select(profile, premium, batchSize: 3));
        SelectionGrant[] grants = [new("platform-admin", new()), new("research-admin", tenant), new("analyst", user, true)];
        var selected = IntegrationSelection.Resolve(logical, user,
            [system.Document, research.Document, analyst.Document], grants, [profile], [standard, premium]);
        Assert.True(selected.Resolution.Validation.IsValid);
        var snapshot = selected.Resolution.Snapshot!.GetDefinition<ScopedSelectionSnapshot<IntegrationChoice>>();
        Assert.Equal(analyst.Document.Reference, snapshot.Winner);
        Assert.Equal(3, snapshot.Choice.BatchSize);

        var model = new HistoricalMarketData();
        var compiled = model.Collect.For(CreateSelection())
            .Use(model.Collect.Provider(selected.Profile!).Provides(model.RetainedResponse))
            .Through(logical).ForConsumer(new("research/eod", "market-data", "bars", "SPY"))
            .Compile(selected.Bindings!);
        Assert.True(compiled.IsSuccessful);
        Assert.Contains(compiled.Documents, d => d.Reference == selected.Resolution.Snapshot.Reference);
        Assert.True(ExecutionDefinitionDocumentCatalog.TryCreate(compiled.Documents, out var originalDefinitions).IsValid);

        // Current policies gate NEW effects. Existing receipts and reconciliation keep the original identities.
        var revoked = Layer.Define("example/research", "v2", "research-admin", tenant)
            .Enforce(new(AllowedResources: []));
        var dispatch = IntegrationSelection.CheckDispatch(selected.Resolution.Snapshot,
            [system.Document, revoked.Document, analyst.Document], grants, originalDefinitions!);
        Assert.False(dispatch.IsValid);
        Assert.Equal(premium.Document.Reference, snapshot.Choice.Connection);

        // Specificity and even authorized local overrides cannot bypass a system hard bound.
        var invalidLocal = Layer.Define("example/local", "v1", "analyst", user, SelectionPrecedence.Local)
            .Prefer(IntegrationChoice.Select(profile, premium, batchSize: 9));
        var denied = IntegrationSelection.Resolve(logical, user,
            [system.Document, research.Document, invalidLocal.Document], grants, [profile], [standard, premium]);
        Assert.Null(denied.Resolution.Snapshot);
    }
}
