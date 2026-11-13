using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Model;

namespace Cohesive.Tests.Integrations;

public sealed partial class IngestionDefinitionTests
{
    // Ito owns these parameters. The dates and calendar revision are synthetic.
    sealed record RawEodScope(string Instrument, string Venue, string CalendarRevision, string Basis,
        DateOnly FirstSession, DateOnly LastSession, string TimeBasis, string AbsenceRule);

    [Fact]
    public void HistoricalCoverageBindingReopensWithTheSameRequirementAndIndependentConsumerProgress()
    {
        var fixture = Fixture(separateLedger: true);
        var ingestion = Declaration(fixture.Requests);
        var origin = Provenance();
        var scope = ContractPropertyDocuments.DeclareContext(new("ito/spy/raw-eod"), new("v1"),
            new RawEodScope("SPY", "XNYS", "synthetic-two-sessions/v1", "raw",
                new(2026, 9, 1), new(2026, 9, 2), "exchange session date", "authoritative session disposition required"), origin);
        var provider = IngestionProviderBindingDocuments.DeclareProvider(new("tiingo/eod"), new("v1"),
            new(fixture.Requests[0], Operation: "GET /tiingo/daily/{symbol}/prices", Medium: "HTTPS"), origin);
        var predicate = ContractPropertyDocuments.DeclarePredicate(new("coverage/predicate"), new("v1"),
            new { Rule = "all expected sessions accounted for with durable acceptance" }, origin);
        var assumptions = ContractPropertyDocuments.DeclareContext(new("coverage/assumptions"), new("v1"),
            new { CalendarIsAuthoritative = true, UnknownPublicationRequiresReconciliation = true }, origin);
        var property = ContractPropertyDocuments.Declare(new("coverage"), new("v1"),
            new(ContractPropertyKinds.DataIntegrity, [ContractPredicateClassification.Safety], predicate.Reference,
                new(["SPY"], "instrument/session", "publication-commit", scope.Reference, "exchange session date"),
                scope.Reference, assumptions.Reference), origin);
        var requirement = new IngestionContract(ingestion.Reference, [new(ContractRole.Requires, property.Reference)]);
        var address = new IngestionLedgerAddress("tenant-a/eod", "tiingo", "tenant-a/bars", "SPY");
        var source = requirement.Bind(provider.Reference, resource: "shared-tiingo-account", consumer: address);
        ExecutionDefinitionDocument Lower(IngestionProviderBinding value) =>
            IngestionProviderBindingDocuments.DeclareBinding(new("ito/eod/binding"), new("v1"), value, origin);

        var document = Lower(source);
        Assert.Equal(document.Reference, Lower(new(requirement, provider.Reference, source.Resource, address)).Reference);
        var json = ExecutionDefinitionJsonSerializer.Serialize(document);
        Valid(ExecutionDefinitionJsonSerializer.TryDeserialize(json, out var reopened));
        Valid(ExecutionDefinitionDocumentCatalog.TryCreate([scope, predicate, assumptions, property, provider, ingestion], out var catalog));
        var validation = IngestionProviderBindingDocuments.Inspect(reopened!, catalog!, fixture.Catalog, out var inspected);
        Valid(validation);
        Assert.Equal(source.Contract.Ingestion, inspected!.Binding.Contract.Ingestion);
        Assert.Equal(source.Contract.Properties.ToArray(), inspected.Binding.Contract.Properties.ToArray());
        Assert.Equal(source.Provider, inspected.Binding.Provider);
        Assert.Equal(source.Resource, inspected.Binding.Resource);
        Assert.Equal(source.Consumer, inspected.Binding.Consumer);
        Assert.Equal(CapabilityRealizationKind.Composed, inspected.Strategy);
        Assert.Equal(fixture.Requests[0], inspected.Offer.Acquire);
        Assert.Equal(fixture.Requests[1], inspected.Publish);
        Assert.Equal(scope.Reference, inspected.Properties[0].Definition.Parameters);
        Assert.Equal(4, reopened!.Metadata.SourceMap.Entries.Length);
        Assert.Contains(validation.Diagnostics, d => d.Code == "integrations.ingestion.realization.unqualified");
        var other = source with { Consumer = address with { Flow = "tenant-b/eod", Destination = "tenant-b/bars" } };
        Assert.NotEqual(document.Metadata.Fingerprint, Lower(other).Metadata.Fingerprint);

        // Another exact Request exists in the catalog, but does not implement this binding's acquisition.
        var wrong = IngestionProviderBindingDocuments.DeclareProvider(new("other/eod"), new("v1"),
            new(fixture.Requests[1], "publish", "HTTPS"), origin);
        Valid(ExecutionDefinitionDocumentCatalog.TryCreate([scope, predicate, assumptions, property, wrong, ingestion], out var otherCatalog));
        var invalid = IngestionProviderBindingDocuments.Inspect(Lower(source with { Provider = wrong.Reference }),
            otherCatalog!, fixture.Catalog, out var rejected);
        Assert.Null(rejected);
        Assert.Contains(invalid.Diagnostics, d => d.Code == "integrations.binding.acquire");

        invalid = IngestionProviderBindingDocuments.Inspect(document, otherCatalog!, fixture.Catalog, out rejected);
        Assert.False(invalid.IsValid);
        Assert.Null(rejected);
        invalid = IngestionProviderBindingDocuments.Inspect(Lower(source with { Contract = requirement with { Properties = [new(ContractRole.Requires, provider.Reference)] } }),
            catalog!, fixture.Catalog, out rejected);
        Assert.Contains(invalid.Diagnostics, d => d.Code == "contract.property.kind");
        Assert.Null(rejected);
    }
}
