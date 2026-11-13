using System.Collections.Immutable;
using System.Text;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Integrations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Model.Serialization;

namespace Cohesive.Examples.Integrations;

/// <summary>Executable authoring example for a two-session historical market-data integration.</summary>
public sealed class HistoricalMarketDataExamples
{
    /// <summary>Authors the operation, provider contribution and independent consumer, then inspects the persisted closure.</summary>
    [Fact]
    public void HistoricalCoverageBindingRetainsScopeAndEvidenceObligations()
    {
        var selection = CreateSelection();
        var calendar = selection.Calendar;
        var normalizer = selection.Normalizer;
        var model = new HistoricalMarketData();
        var profile = ProviderOperationProfile.Define("tiingo/eod", "v1", "tiingo",
                operation: "GET /tiingo/daily/{symbol}/prices", medium: "HTTPS")
            .AuthenticateWith("api-key").RequiresEntitlements("daily-prices");
        var connection = IntegrationConnection.Define("research/tiingo", "v1", "tiingo",
                resource: "tiingo/research-account", medium: "HTTPS", endpoint: "https://api.tiingo.com")
            .AuthenticateWith("api-key", "env:TIINGO_API_KEY").EntitledTo("daily-prices");
        var logical = ConnectionReference.Named("market-data");
        var connections = new ConnectionBindings().Bind(logical, connection);
        var provider = model.Collect.Provider(profile).Provides(model.RetainedResponse);
        var consumer = new IngestionLedgerAddress("research/eod", "tiingo", "research/bars", "SPY");
        var instance = model.Collect.For(selection).Use(provider)
            .Through(logical).ForConsumer(consumer);
        var compiled = instance.Compile(connections);
        Valid(compiled.Validation);
        Assert.True(compiled.IsSuccessful);
        Assert.Equal(compiled.Binding!.Reference, instance.Compile(connections).Binding!.Reference);

        // Persist and reopen the compiled closure, as a later process or inspection tool would do.
        var documents = compiled.Documents;
        var reopened = documents.Select(document =>
        {
            Valid(ExecutionDefinitionJsonSerializer.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(document), out var value));
            return value!;
        }).ToArray();
        Valid(ExecutionDefinitionDocumentCatalog.TryCreate(reopened, out var definitions));
        Valid(InteractionContractCatalog.TryCreate(reopened.Where(document => document.Kind == InteractionContractDocuments.Kind), out var requests));
        var validation = IngestionProviderBindingDocuments.Inspect(reopened.Single(d => d.Reference == compiled.Binding.Reference), definitions!, requests!, out var inspected);
        Valid(validation);
        Assert.NotNull(inspected);
        Assert.Equal(ContractRole.Requires, inspected!.Properties[0].Use.Role);
        Assert.Equal(inspected!.Properties[1].Use.Property, inspected.ProviderProperties[0].Use.Property);
        Assert.Equal(ContractRole.Provides, inspected.ProviderProperties[0].Use.Role);
        Assert.True(definitions!.TryResolve(inspected!.Properties[0].Definition.Parameters, out var parameters));
        var retainedScope = parameters!.GetDefinition<HistoricalCoverageScope>();
        Assert.Equal(selection.ExpectedSessions.ToArray(), retainedScope.ExpectedSessions.ToArray());
        Assert.Equal(calendar, retainedScope.Calendar);
        Assert.Equal(normalizer, retainedScope.Normalizer);
        Assert.Contains("/publish", inspected.Publish.Definition.DefinitionId.Value);
        Assert.Contains("/acquire", inspected.Offer.Acquire.Definition.DefinitionId.Value);
        Assert.Equal(CapabilityRealizationKind.Composed, inspected.Strategy);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "integrations.ingestion.realization.unqualified");

        // Another consumer can share the account while maintaining independent progress.
        var otherConsumer = consumer with { Flow = "risk/eod", Destination = "risk/bars" };
        var otherBinding = instance.ForConsumer(otherConsumer).Compile(connections);
        Valid(otherBinding.Validation);
        Assert.NotEqual(compiled.Binding.Metadata.Fingerprint, otherBinding.Binding!.Metadata.Fingerprint);
    }

    /// <summary>Creates the shared synthetic two-session selection and its pinned interpretation fixtures.</summary>
    /// <returns>A fresh immutable selection used by the authoring and evidence examples.</returns>
    internal static HistoricalCoverageScope CreateSelection()
    {
        // The application owns the expected sessions, observation time, currency and normalization meaning.
        // These are synthetic fixtures. Their digests pin the exact illustrative policy bytes.
        var calendar = IngestionContentReference.Describe("example/calendar/v1",
            Encoding.UTF8.GetBytes("XNYS synthetic sessions: 2026-09-01,2026-09-02"), "text/plain");
        var normalizer = IngestionContentReference.Describe("example/raw-normalizer/v1",
            Encoding.UTF8.GetBytes("raw OHLCV; USD; one valid bar per expected session; missing bars remain unresolved"), "text/plain");
        return new HistoricalCoverageScope(
            Instrument: "example/instrument/SPY", Symbol: "SPY", Venue: "XNYS", Currency: "USD",
            StartDate: new(2026, 9, 1), EndDateInclusive: new(2026, 9, 2),
            RequestedAsOfUtc: new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
            ExpectedSessions: [new(2026, 9, 1), new(2026, 9, 2)], Calendar: calendar, Normalizer: normalizer);
    }

    /// <summary>Domain predicate interpreted by the application's coverage validator.</summary>
    /// <param name="RequireDurableAcceptance">Whether accounted-for observations must have a durable acceptance receipt.</param>
    public sealed record AccountForEveryExpectedSession(bool RequireDurableAcceptance);

    /// <summary>Domain predicate interpreted by the acquisition evidence verifier.</summary>
    /// <param name="RequireDigestVerification">Whether retained response bytes must match their declared digest.</param>
    public sealed record RetainAcquiredResponse(bool RequireDigestVerification);

    /// <summary>Premises required by the example's coverage and recovery policies.</summary>
    /// <param name="CalendarIsAuthoritative">Whether the pinned calendar defines expected observations.</param>
    /// <param name="UnknownPublicationRequiresReconciliation">Whether a lost acknowledgment requires receipt reconciliation.</param>
    public sealed record CoverageAssumptions(bool CalendarIsAuthoritative, bool UnknownPublicationRequiresReconciliation);

    /// <summary>Application-owned declarations for historical observations and their acceptance evidence.</summary>
    public sealed class HistoricalMarketData : IntegrationModel<HistoricalCoverageScope>
    {
        /// <summary>Durable coverage required at publication.</summary>
        public PropertyHandle<HistoricalCoverageScope> AcceptedCoverage { get; }
        /// <summary>Response retention required at acquisition.</summary>
        public PropertyHandle<HistoricalCoverageScope> RetainedResponse { get; }
        /// <summary>Acquisition through settlement interaction.</summary>
        public IngestionHandle<HistoricalCoverageScope> Collect { get; }

        /// <summary>Authors the model with explicit recovery and domain assumptions.</summary>
        public HistoricalMarketData() : base("example/historical-market-data", "v1")
        {
            Payload<HistoricalCoverageScope>("selection");
            Payload<PreparedPublication>("prepared-publication");
            Payload<PublicationReceipt>("publication-receipt");
            Payload<ProgressReceipt>("progress-receipt");
            Payload<SettlementReceipt>("settlement-receipt");
            Payload<IntegrationFailure>("failure");
            var recovery = Recovery("recoverable")
                .StableRequestIdentity().ReconcileAmbiguousOutcomes().ReusePriorDisposition()
                .RetainEvidenceFor(TimeSpan.FromDays(30));
            var acquire = Request<HistoricalCoverageScope>("acquire")
                .Result<PreparedPublication>("prepared").Failure<IntegrationFailure>("rejected").Recovery(recovery);
            var publish = Request<PreparedPublication>("publish")
                .Result<PublicationReceipt>("published").Failure<IntegrationFailure>("rejected").Recovery(recovery);
            var advance = Request<PublicationReceipt>("advance-ledger")
                .Result<ProgressReceipt>("advanced").Failure<IntegrationFailure>("rejected").Recovery(recovery);
            var settle = Request<ProgressReceipt>("settle")
                .Result<SettlementReceipt>("settled").Failure<IntegrationFailure>("rejected").Recovery(recovery);
            var assumptions = new CoverageAssumptions(CalendarIsAuthoritative: true, UnknownPublicationRequiresReconciliation: true);

            AcceptedCoverage = Property("accepted-coverage")
                .Kind(ContractPropertyKinds.DataIntegrity).Invariant()
                .Predicate(new AccountForEveryExpectedSession(RequireDurableAcceptance: true))
                .Assume(assumptions)
                .Scope(scope => scope.Subject(s => s.Instrument)
                    .KeyDomain("instrument/session-date").Boundary(publish)
                    .Interval(s => s.StartDate, s => s.EndDateInclusive, end: TemporalBoundaryInclusion.Inclusive)
                    .TimeBasis("exchange-session valid time"));
            RetainedResponse = Property("retained-response")
                .Kind(ContractPropertyKinds.DataIntegrity).ClassifiedAs(ContractPredicateClassification.Safety)
                .Predicate(new RetainAcquiredResponse(RequireDigestVerification: true))
                .Assume(assumptions)
                .Scope(scope => scope.Subject(s => s.Instrument)
                    .KeyDomain("acquisition-attempt").Boundary(acquire)
                    .Interval(s => s.StartDate, s => s.EndDateInclusive, end: TemporalBoundaryInclusion.Inclusive)
                    .TimeBasis("exchange-session valid time"));
            Collect = Ingestion("collect").Acquire(acquire).Publish(publish).AdvanceProgress(advance).Settle(settle)
                .Requires(AcceptedCoverage).Requires(RetainedResponse);
        }
    }

    /// <summary>Reference to the retained acquisition and normalized publication candidate.</summary>
    /// <param name="Reference">Durable evidence identity.</param>
    public sealed record PreparedPublication(string Reference);
    /// <summary>Reference to durable publication evidence.</summary>
    /// <param name="Reference">Durable receipt identity.</param>
    public sealed record PublicationReceipt(string Reference);
    /// <summary>Reference to accepted ledger progress.</summary>
    /// <param name="Reference">Durable receipt identity.</param>
    public sealed record ProgressReceipt(string Reference);
    /// <summary>Reference to completed source settlement.</summary>
    /// <param name="Reference">Durable receipt identity.</param>
    public sealed record SettlementReceipt(string Reference);
    /// <summary>Explicit integration failure response.</summary>
    /// <param name="Reason">Failure reason for reconciliation or rejection.</param>
    public sealed record IntegrationFailure(string Reason);

    static void Valid(DocumentValidationResult result) =>
        Assert.True(result.IsValid, string.Join("\n", result.Diagnostics.Select(diagnostic => diagnostic.Message)));

    /// <summary>Application-owned historical selection with pinned interpretation evidence.</summary>
    /// <param name="Instrument">Stable instrument identity.</param>
    /// <param name="Symbol">Explicit provider ticker mapping.</param>
    /// <param name="Venue">Venue used to interpret session dates.</param>
    /// <param name="Currency">Declared quote currency.</param>
    /// <param name="StartDate">Inclusive first requested date.</param>
    /// <param name="EndDateInclusive">Inclusive last requested date.</param>
    /// <param name="RequestedAsOfUtc">Requested observation boundary in UTC.</param>
    /// <param name="ExpectedSessions">Exact expected exchange-session dates.</param>
    /// <param name="Calendar">Pinned source defining the expected sessions.</param>
    /// <param name="Normalizer">Pinned raw-data and absence-admissibility policy.</param>
    /// <param name="PriceBasis">Required raw or adjusted interpretation; the example adapter supports Raw.</param>
    public sealed record HistoricalCoverageScope(string Instrument, string Symbol, string Venue, string Currency,
        DateOnly StartDate, DateOnly EndDateInclusive, DateTimeOffset RequestedAsOfUtc,
        ImmutableArray<DateOnly> ExpectedSessions, IngestionContentReference Calendar, IngestionContentReference Normalizer, string PriceBasis = "Raw");
}
