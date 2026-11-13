using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Processes.Execution;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;
using static Cohesive.Examples.Integrations.TiingoCoverageEvidenceExamples;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    /// <summary>A successful source response is operational evidence, not accepted coverage or source progress.</summary>
    /// <param name="sessions">Observed rows for the two-session request.</param>
    /// <returns>Completes after inspecting acquisition separately from publication and ledger completion.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task HealthyHistoricalSourceDoesNotImplyAcceptedCoverage(int sessions)
    {
        var fixture = new ProcessFixture(sessions);
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(fixture.Address, (_, actor) => actor == Operator).Receipt;
        var bound = control.Bind(fixture.Compilation);
        control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var admission = control.Admit(new(bound, Session, 1, Operator, At)).Receipt;
        var provider = new IntegrationHealthEvidence(bound.Binding,
            new(ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready, At, Profile.Document.Metadata.Provenance,
                ["fixture/http-response/1"]), At.AddMinutes(5));
        var clock = new HistoricalInspectionClock { UtcNow = At };
        var inspectionHost = new IntegrationInspectionHost(bound, _ => ValueTask.FromResult<IntegrationHealthEvidence?>(provider), clock);
        var decision = fixture.Start();
        var outcome = await fixture.Handle(Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions)));

        clock.UtcNow = At.AddSeconds(3);
        var afterFetch = await InspectHistorical(fixture, inspectionHost);
        Assert.Equal(ExecutionHealthStatus.Healthy, afterFetch.Integration.ProviderHealth);
        Assert.Equal(sessions, afterFetch.SourceCoverage!.Bars.Length);
        Assert.Equal(2 - sessions, afterFetch.SourceCoverage.MissingSessions.Length);
        Assert.Empty(afterFetch.AcceptedSessions);
        Assert.Null(afterFetch.Publication);
        Assert.Null(afterFetch.SourceProgress);
        Assert.Equal(1, afterFetch.Integration.ConsumerPendingWork.SessionCount);

        decision = fixture.Reply(decision, outcome);
        while (decision.Disposition == ProcessActivationDisposition.DurableCut)
            decision = fixture.Reply(decision, await fixture.Handle(Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions))));
        if (decision.Disposition == ProcessActivationDisposition.Completed)
            Assert.True(control.Complete(new(admission, decision, Operator)).IsAccepted);
        control.Change(new("pause", 1, IntegrationDesiredState.Paused, Operator, At));

        clock.UtcNow = At.AddMinutes(1);
        var final = await InspectHistorical(fixture, inspectionHost);
        Assert.Equal(ExecutionHealthStatus.Healthy, final.Integration.ProviderHealth);
        Assert.False(final.Integration.Lifecycle.Accepting);
        Assert.Equal(sessions != 2, final.Integration.Lifecycle.Draining);
        Assert.Equal<DateOnly>(sessions == 2 ? CreateSelection().ExpectedSessions : [], final.AcceptedSessions);
        Assert.Equal(sessions == 2 ? 1L : null, final.SourceProgress?.Revision);
        Assert.Equal(sessions == 2, final.Publication is not null);

        clock.UtcNow = At.AddMinutes(5);
        var expired = await InspectHistorical(fixture, inspectionHost);
        Assert.Equal(ExecutionHealthStatus.Unknown, expired.Integration.ProviderHealth);
        Assert.Equal<DateOnly>(final.AcceptedSessions, expired.AcceptedSessions);
        Assert.Equal(final.SourceProgress, expired.SourceProgress);
        Assert.Equal(final.Publication, expired.Publication);
        Assert.Equal(provider, expired.Integration.ProviderEvidence);
        // This is a read-only JSON projection suitable for a host inspection endpoint. No provider is called.
        using var output = new MemoryStream();
        await inspectionHost.WriteJsonAsync(output);
        Assert.Contains("fixture/http-response/1", System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(1, fixture.ProviderCalls);
    }

    static async Task<HistoricalInspection> InspectHistorical(ProcessFixture fixture, IntegrationInspectionHost host)
    {
        var view = await host.ReadAsync();
        var publication = fixture.Publication;
        ImmutableArray<DateOnly> accepted = [];
        if (publication is not null)
        {
            var bytes = Assert.IsType<byte[]>(fixture.PublishedBytes);
            var actual = IngestionContentReference.Describe("publication.json", bytes, "application/json");
            Assert.Equal(actual.Sha256, publication.ContentFingerprint);
            accepted = [.. JsonSerializer.Deserialize<DailyBar[]>(bytes)!.Select(b => DateOnly.FromDateTime(b.Date.UtcDateTime))];
        }
        return new(view, fixture.Coverage, accepted, publication, await fixture.Ledger.ReadAsync(fixture.Address, default));
    }

    sealed class HistoricalInspectionClock : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; }
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    // Coverage stays domain-owned. Operational health cannot assert the publication contract on its behalf.
    sealed record HistoricalInspection(IntegrationInspection Integration, CoverageEvidence? SourceCoverage,
        ImmutableArray<DateOnly> AcceptedSessions, IngestionPublicationReceipt? Publication, IngestionLedgerEntry? SourceProgress);
}
