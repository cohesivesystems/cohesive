using System.Collections.Immutable;

using Cohesive.Configuration;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Integrations.Authoring;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;
using Layer = Cohesive.Configuration.SelectionLayer<Cohesive.Integrations.IntegrationChoice, Cohesive.Integrations.IntegrationSelectionPolicy>;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    /// <summary>Reopens historical admission and gates actual offline calls against current policy and evidence.</summary>
    /// <param name="change">The configuration or evidence change after admission.</param>
    /// <param name="allowed">Whether a new acquisition remains permitted.</param>
    [Theory]
    [InlineData("default", true)]
    [InlineData("policy", false)]
    [InlineData("grant", false)]
    [InlineData("expiry", false)]
    public void ReopenedHistoricalAdmissionGatesLocalProviderCalls(string change, bool allowed)
    {
        var scope = CreateSelection();
        var original = Connection("v1");
        var replacement = Connection("v2");
        var logical = ConnectionReference.Named("market-data");
        SelectionGrant[] grants = [new("research-admin", new())];
        var admission = Layer.Define("example/local-policy", "v1", "research-admin", new())
            .Prefer(IntegrationChoice.Select(Profile, original));
        var selected = IntegrationSelection.Resolve(logical, new(), [admission.Document], grants,
            [Profile], [original, replacement]);
        Assert.True(selected.Resolution.Validation.IsValid);
        var model = new HistoricalMarketData();
        var candidate = model.Collect.For(scope)
            .Use(model.Collect.Provider(selected.Profile!).Provides(model.RetainedResponse))
            .Through(logical).ForConsumer(new("research/eod", "tiingo", "research/bars", "SPY"))
            .Compile(selected.Bindings!);
        var evidence = ContractPropertyDocuments.DeclareContext(new("example/local-conformance"), new("v1"),
            new HistoricalOfferEvidence("offline/raw-adapter-fixture/v1", "tiingo/research-account"), Profile.Document.Metadata.Provenance);
        var offer = IntegrationCapabilityOffer.Declare("example/local-offer", "v1", Profile, original, Schema,
            Capabilities(scope), evidence, At.AddDays(-1), At.AddDays(1));
        var qualified = IntegrationQualification.Qualify(candidate, offer, Evaluator, At);
        Assert.True(qualified.IsAdmissible);
        var compiled = Reopen(qualified.Compilation!);
        Assert.Equal(qualified.Compilation!.Binding!.Reference, compiled.Binding!.Reference);
        Assert.All(qualified.Decisions, decision =>
            Assert.Equal(IntegrationQualificationStatus.RequiresRuntimeEvidence, decision.Status));

        var current = Layer.Define("example/local-policy", "v2", "research-admin", new())
            .Prefer(IntegrationChoice.Select(Profile, replacement));
        if (change == "policy") current = current.Enforce(new(AllowedProviders: []));
        var first = new LocalAcquisition();
        var second = new LocalAcquisition();
        var runtime = new IntegrationRuntime().Register(Profile, original, first).Register(Profile, replacement, second);
        var atDispatch = change == "expiry" ? At.AddDays(1) : At;
        SelectionGrant[] currentGrants = change == "grant" ? [] : grants;

        void Dispatch() => runtime.ResolveForDispatch<LocalAcquisition>(compiled,
            [current.Document], currentGrants, atDispatch).Acquire();
        if (allowed) Dispatch();
        else Assert.Throws<InvalidOperationException>(Dispatch);
        Assert.Equal(allowed ? 1 : 0, first.Calls);
        Assert.Equal(0, second.Calls);

        // Reconciliation can locate the original implementation without renewing admission.
        // Resolving it performs no I/O; the host must reconcile retained results, not call Acquire again.
        Assert.Same(first, runtime.Resolve<LocalAcquisition>(compiled));
        Assert.Equal(allowed ? 1 : 0, first.Calls);
        Assert.Equal(0, second.Calls);
    }

    static IntegrationCompilation Reopen(IntegrationCompilation compilation)
    {
        var documents = compilation.Documents.Select(document =>
        {
            Assert.True(ExecutionDefinitionJsonSerializer.TryDeserialize(
                ExecutionDefinitionJsonSerializer.Serialize(document), out var reopened).IsValid);
            return reopened!;
        }).ToImmutableArray();
        return compilation with
        {
            Documents = documents,
            Binding = documents.Single(d => d.Reference == compilation.Binding!.Reference),
            Process = documents.Single(d => d.Reference == compilation.Process!.Reference),
            Qualification = documents.Single(d => d.Reference == compilation.Qualification!.Reference)
        };
    }

    // An observable effect boundary, not a second ingestion coordinator or a coverage attestation.
    sealed class LocalAcquisition
    {
        internal int Calls { get; private set; }
        internal void Acquire() => Calls++;
    }
}
