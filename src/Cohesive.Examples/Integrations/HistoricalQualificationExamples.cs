using System.Collections.Immutable;
using Cohesive.Contracts;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Integrations.Authoring;
using Cohesive.Model;
using Cohesive.Relations.IR;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;

namespace Cohesive.Examples.Integrations;

/// <summary>Bounded raw Tiingo-shaped adapter qualification using explicit offline resource evidence.</summary>
/// <remarks>These are deterministic fixture offers, not live Tiingo entitlements or availability attestations.
/// Production facts must come from the actual adapter/resource authority. Domain-specific semantics stay out of Integrations.</remarks>
public sealed partial class HistoricalQualificationExamples
{
    const string Schema = "example/historical-acquisition/v1";
    static readonly DateTimeOffset At = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    static readonly ProviderOperationProfile Profile = ProviderOperationProfile.Define("example/tiingo/raw", "v1", "tiingo",
        "daily-prices:v1", "HTTPS").AuthenticateWith("api-key").RequiresEntitlements("daily-prices");
    static IntegrationConnection Connection(string revision) => IntegrationConnection.Define("example/tiingo/research", revision,
        "tiingo", "tiingo/research-account", "HTTPS", "https://api.tiingo.com")
        .AuthenticateWith("api-key", "env:TIINGO_API_KEY").EntitledTo("daily-prices");
    static readonly IntegrationCapabilityEvaluator<HistoricalAcquisitionCapabilities> Evaluator =
        new("example/historical-qualification", "v1", Schema, Evaluate);

    /// <summary>Assesses the default fixture against the actual authored Contract properties.</summary>
    /// <param name="selection">Requested scope.</param>
    /// <param name="capabilities">Optional deliberately varied fixture offer.</param>
    /// <param name="connectionRevision">Concrete connection revision supporting the offer.</param>
    /// <returns>Pending-evidence admission or explicit rejection.</returns>
    internal static IntegrationQualificationResult Assess(HistoricalCoverageScope selection,
        HistoricalAcquisitionCapabilities? capabilities = null, string connectionRevision = "v1")
    {
        var connection = Connection(connectionRevision);
        var model = new HistoricalMarketData();
        var logical = ConnectionReference.Named("market-data");
        var compiled = model.Collect.For(selection).Use(model.Collect.Provider(Profile).Provides(model.RetainedResponse))
            .Through(logical).ForConsumer(new("research/eod", "tiingo", "research/bars", "SPY"))
            .Compile(new ConnectionBindings().Bind(logical, connection));
        var evidence = ContractPropertyDocuments.DeclareContext(new("example/conformance/" + connectionRevision), new("v1"),
            new HistoricalOfferEvidence("offline/raw-adapter-fixture/v1", "tiingo/research-account"), Profile.Document.Metadata.Provenance);
        var offer = IntegrationCapabilityOffer.Declare("example/tiingo/offer/" + connectionRevision, "v1", Profile, connection,
            Schema, capabilities ?? Capabilities(selection), evidence, At.AddDays(-1), At.AddDays(1));
        return IntegrationQualification.Qualify(compiled, offer, Evaluator, At);
    }

    static HistoricalAcquisitionCapabilities Capabilities(HistoricalCoverageScope scope) => new(scope.Instrument, scope.Symbol,
        scope.Venue, scope.Currency, ["Raw"], new(2000, 1, 1), new(2026, 9, 2), MaximumRangeDays: 400,
        scope.Calendar, scope.Normalizer, CompleteResponse: true, RetainsBytes: true, ReconcilesUnknown: true, Retention: TimeSpan.FromDays(30));

    /// <summary>Two resources face the same predicates; incompatible adjusted-only access cannot substitute for raw data.</summary>
    [Fact]
    public void SameHistoricalContractRejectsIncompatibleOfferAndPinsPendingEvidence()
    {
        var scope = CreateSelection();
        var good = Assess(scope);
        var incompatible = Assess(scope, Capabilities(scope) with { PriceBases = ["Adjusted"] }, "adjusted-only");
        Assert.True(good.IsAdmissible);
        Assert.False(incompatible.IsAdmissible);
        Assert.All(good.Decisions, d => Assert.Equal(IntegrationQualificationStatus.RequiresRuntimeEvidence, d.Status));
        Assert.Contains(incompatible.Validation.Diagnostics, d => d.Code == "integrations.qualification.price-basis");
        var selected = IntegrationCandidateSelection.Resolve([incompatible, good], IntegrationCandidateSelector.ExactlyOne);
        Assert.Single(selected.Selected);
        Assert.False(IntegrationCandidateSelection.Resolve([good, incompatible], IntegrationCandidateSelector.Exactly,
            incompatible.Candidate.Binding!.Reference).Validation.IsValid);
        Assert.True(IntegrationQualification.Inspect(good.Compilation!, At).IsValid);
        Assert.False(IntegrationQualification.Inspect(good.Compilation!, At.AddDays(1)).IsValid);
    }

    /// <summary>Requests outside declared interpretation, recovery or scope bounds cannot reach dispatch.</summary>
    /// <param name="variant">Incompatible fixture capability.</param>
    [Theory]
    [InlineData("instrument")]
    [InlineData("interval")]
    [InlineData("calendar")]
    [InlineData("normalizer")]
    [InlineData("pagination")]
    [InlineData("retention")]
    [InlineData("recovery")]
    [InlineData("range")]
    public void BoundedOfferDiagnosesUnsupportedSemantics(string variant)
    {
        var scope = CreateSelection();
        var baseline = Capabilities(scope);
        var changed = variant switch
        {
            "instrument" => baseline with { Instrument = "different-instrument" },
            "interval" => baseline with { AvailableFrom = scope.EndDateInclusive },
            "calendar" => baseline with { Calendar = scope.Normalizer },
            "normalizer" => baseline with { Normalizer = scope.Calendar },
            "pagination" => baseline with { CompleteResponse = false },
            "retention" => baseline with { Retention = TimeSpan.FromDays(1) },
            "recovery" => baseline with { ReconcilesUnknown = false },
            _ => baseline with { MaximumRangeDays = 1 }
        };
        var rejected = Assess(scope, changed);
        Assert.False(rejected.IsAdmissible);
        Assert.NotEmpty(rejected.Decisions);
    }

    static ImmutableArray<IntegrationRequirementDecision> Evaluate(IngestionProviderBindingInspection binding,
        HistoricalAcquisitionCapabilities capability, IntegrationCapabilityOfferDefinition offer, DateTimeOffset atUtc,
        ExecutionDefinitionDocumentCatalog catalog)
    {
        catalog.TryResolve(offer.Evidence, out var evidenceDocument);
        var evidence = evidenceDocument!.GetDefinition<HistoricalOfferEvidence>();
        catalog.TryResolve(binding.Offer.Acquire.Definition, out var acquisition);
        catalog.TryResolve(binding.Publish.Definition, out var publication);
        InteractionContractCatalog.TryCreate([acquisition!, publication!], out var requests);
        requests!.ValidateReference(binding.Offer.Acquire.Definition, "/acquire", out var contract);
        var retention = ((RequestContractDefinition)contract!).Response.RetentionHorizon;
        return [.. binding.Properties.Where(p => p.Use.Role == ContractRole.Requires).Select(property =>
        {
            catalog.TryResolve(property.Definition.Parameters, out var parameters);
            var scope = parameters!.GetDefinition<HistoricalCoverageScope>();
            catalog.TryResolve(property.Definition.Predicate, out var predicate);
            catalog.TryResolve(property.Definition.Assumptions, out var assumptions);
            catalog.TryResolve(property.Definition.Scope.Interval, out var intervalDocument);
            var interval = intervalDocument!.GetDefinition<IntegrationDateInterval>();
            var boundaryRequest = property.Definition.Scope.KeyDomain == "instrument/session-date" ? binding.Publish : binding.Offer.Acquire;
            requests.ValidateReference(boundaryRequest.Definition, "/boundary", out var boundaryContract);
            var outcome = ((RequestContractDefinition)boundaryContract!).Response.TerminalOutcomes.OfType<RequestResultDefinition>().Single().Id.Value;
            var expectedBoundary = boundaryRequest.Definition.DefinitionId.Value + "/" + boundaryRequest.Definition.RevisionId.Value
                + "/" + boundaryRequest.Definition.Fingerprint.Value + "/results/" + outcome;
            string? code = null;
            if (ContractPropertyDocuments.DeclareContext(parameters.Metadata.DefinitionId, parameters.Metadata.RevisionId,
                scope, parameters.Metadata.Provenance).Reference != parameters.Reference
                || ContractPropertyDocuments.DeclareContext(intervalDocument.Metadata.DefinitionId, intervalDocument.Metadata.RevisionId,
                    interval, intervalDocument.Metadata.Provenance).Reference != intervalDocument.Reference
                || property.Definition.QuantitativeBound is not null) code = "unsupported-context";
            else if (!property.Definition.Classification.SequenceEqual(property.Definition.Scope.KeyDomain == "instrument/session-date"
                ? [ContractPredicateClassification.Safety, ContractPredicateClassification.Invariant] : [ContractPredicateClassification.Safety]))
                code = "classification";
            else if (!property.Definition.Scope.Subjects.SequenceEqual([scope.Instrument]) || interval.StartInclusive != scope.StartDate
                || interval.End != scope.EndDateInclusive || interval.EndInclusion != TemporalBoundaryInclusion.Inclusive
                || property.Definition.Scope.TimeBasis != "exchange-session valid time" || property.Definition.Scope.Boundary != expectedBoundary)
                code = "property-scope";
            else if (evidence.Suite != "offline/raw-adapter-fixture/v1" || evidence.Resource != binding.Binding.Resource) code = "evidence";
            else if (scope.Instrument != capability.Instrument || scope.Symbol != capability.Symbol || scope.Venue != capability.Venue || scope.Currency != capability.Currency) code = "scope";
            else if (scope.StartDate > scope.EndDateInclusive || scope.StartDate < capability.AvailableFrom || scope.EndDateInclusive > capability.AvailableThrough
                || scope.RequestedAsOfUtc > atUtc) code = "interval";
            else if (scope.ExpectedSessions.IsDefaultOrEmpty || !scope.ExpectedSessions.SequenceEqual(scope.ExpectedSessions.Distinct().Order())
                || scope.ExpectedSessions.Any(d => d < scope.StartDate || d > scope.EndDateInclusive)) code = "calendar";
            else if (!capability.PriceBases.Contains(scope.PriceBasis, StringComparer.Ordinal)) code = "price-basis";
            else if (capability.MaximumRangeDays <= 0 || scope.EndDateInclusive.DayNumber - scope.StartDate.DayNumber + 1 > capability.MaximumRangeDays) code = "range";
            else if (capability.Calendar != scope.Calendar || capability.Normalizer != scope.Normalizer) code = "interpretation";
            else if (!capability.CompleteResponse) code = "pagination";
            else if (!capability.RetainsBytes || capability.Retention < retention) code = "retention";
            else if (!capability.ReconcilesUnknown) code = "recovery";
            else if (ContractPropertyDocuments.DeclareContext(assumptions!.Metadata.DefinitionId, assumptions.Metadata.RevisionId,
                new CoverageAssumptions(true, true), assumptions.Metadata.Provenance).Reference != assumptions.Reference) code = "assumptions";
            else
            {
                var expected = property.Definition.Scope.KeyDomain switch
                {
                    "instrument/session-date" => ContractPropertyDocuments.DeclarePredicate(predicate!.Metadata.DefinitionId, predicate.Metadata.RevisionId,
                        new AccountForEveryExpectedSession(true), predicate.Metadata.Provenance),
                    "acquisition-attempt" => ContractPropertyDocuments.DeclarePredicate(predicate!.Metadata.DefinitionId, predicate.Metadata.RevisionId,
                        new RetainAcquiredResponse(true), predicate.Metadata.Provenance),
                    _ => null
                };
                if (expected?.Reference != predicate!.Reference || property.Definition.Kind != ContractPropertyKinds.DataIntegrity) code = "predicate";
            }
            return new IntegrationRequirementDecision(property.Use.Property,
                code is null ? IntegrationQualificationStatus.RequiresRuntimeEvidence : IntegrationQualificationStatus.Unsupported,
                code is null ? CapabilityRealizationKind.Composed : CapabilityRealizationKind.Unavailable,
                code ?? "runtime-evidence",
                code is null ? "Within the declared fixture envelope. Retained bytes, normalized expected-session coverage and confirmed publication remain runtime obligations."
                    : "The exact historical scope or recovery requirement is unsupported: " + code + ". No weaker interpretation or fallback is permitted.");
        })];
    }

    /// <summary>Bounded fixture capability data; availability is a resource assertion, not a provider-wide fact.</summary>
    /// <param name="Instrument">Supported instrument.</param>
    /// <param name="Symbol">Explicit ticker mapping.</param>
    /// <param name="Venue">Session venue.</param>
    /// <param name="Currency">Quote currency.</param>
    /// <param name="PriceBases">Supported price interpretations.</param>
    /// <param name="AvailableFrom">Inclusive declared historical boundary.</param>
    /// <param name="AvailableThrough">Inclusive declared availability boundary.</param>
    /// <param name="MaximumRangeDays">Inclusive calendar days per bounded call.</param>
    /// <param name="Calendar">Exact supported calendar artifact.</param>
    /// <param name="Normalizer">Exact normalizer artifact.</param>
    /// <param name="CompleteResponse">Whether the adapter implements a complete response protocol without unresolved pagination.</param>
    /// <param name="RetainsBytes">Whether the composed handler can retain acquisition bytes.</param>
    /// <param name="ReconcilesUnknown">Whether unknown outcomes follow the declared reconciliation protocol.</param>
    /// <param name="Retention">Supported evidence retention horizon.</param>
    internal sealed record HistoricalAcquisitionCapabilities(string Instrument, string Symbol, string Venue, string Currency,
        ImmutableArray<string> PriceBases, DateOnly AvailableFrom, DateOnly AvailableThrough, int MaximumRangeDays,
        IngestionContentReference Calendar, IngestionContentReference Normalizer, bool CompleteResponse, bool RetainsBytes,
        bool ReconcilesUnknown, TimeSpan Retention);

    /// <summary>Explicit offline fixture evidence; it is not a remote attestation.</summary>
    /// <param name="Suite">Known fixture conformance suite revision.</param>
    /// <param name="Resource">External account identity covered by that fixture.</param>
    internal sealed record HistoricalOfferEvidence(string Suite, string Resource);
}
