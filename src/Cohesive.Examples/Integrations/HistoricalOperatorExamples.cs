using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Model.Serialization;
using Xunit.Abstractions;
using Cohesive.Processes.Execution;
using static Cohesive.Examples.Integrations.HistoricalMarketDataExamples;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    readonly ITestOutputHelper output;

    /// <summary>Connects executable examples to the test runner's output.</summary>
    /// <param name="output">Runner-owned output receiving the operator report.</param>
    public HistoricalQualificationExamples(ITestOutputHelper output) => this.output = output;

    /// <summary>The local operator view keeps successful forward progress separate from incomplete backfill.</summary>
    /// <returns>Completes after read-only inspection and export, without dispatching or changing either ledger.</returns>
    [Fact]
    public async Task OperatorViewSeparatesForwardProgressFromHealthyButIncompleteBackfill()
    {
        var destination = new HistoricalDestination();
        var forward = new ProcessFixture(new FixtureSetup(
            QualifyLocalHistorical(SingleSession(new(2026, 9, 2)), ForwardConsumer).Compilation!, 1, destination));
        var backfill = new ProcessFixture(new FixtureSetup(
            QualifyLocalHistorical(SingleSession(new(2026, 9, 1)), BackfillConsumer).Compilation!, 0, destination));
        var ownership = new InMemoryIntegrationLifecycleScope();
        var group = ownership.CreateGroup("research/tiingo/SPY", (_, _, actor) => actor == Operator);
        var clock = new HistoricalInspectionClock { UtcNow = At.AddMinutes(1) };
        var readers = new List<(ProcessFixture Fixture, IntegrationInspectionHost Host)>();
        group.Change(new("active", 0, IntegrationDesiredState.Active, Operator, At));
        foreach (var fixture in new[] { forward, backfill })
        {
            var control = group.Register(new(fixture.Address, group.Inspect().Revision, Operator, At)).Receipt.Controller;
            control.Change(new("active", 0, IntegrationDesiredState.Active, Operator, At));
            var binding = control.Bind(fixture.Compilation);
            var session = new ProcessContinuationIdentity(new(fixture.Address.Flow), new("attempt/1"));
            var admitted = control.Admit(new(binding, session, 1, Operator, At)).Receipt;
            var done = await RunHistorical(fixture, session);
            if (done.Disposition == ProcessActivationDisposition.Completed)
                Assert.True(control.Complete(new(admitted, done, Operator)).IsAccepted);
            else
                Assert.Equal(ProcessActivationDisposition.Failed, done.Disposition);
            // Fixture evidence means the source responded, not that it met the requested coverage.
            var evidence = new IntegrationHealthEvidence(binding.Binding,
                new(ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready, At,
                    Profile.Document.Metadata.Provenance, ["fixture/response/" + fixture.Address.Flow]), At.AddMinutes(5));
            readers.Add((fixture, new(binding, _ => ValueTask.FromResult<IntegrationHealthEvidence?>(evidence), clock)));
        }
        var pause = new IntegrationLifecycleChange("pause", group.Inspect().Revision, IntegrationDesiredState.Paused, Operator, At);
        Assert.True(group.Change(pause).IsAccepted);
        var before = await destination.Ledger.ReadAsync(ForwardConsumer, default);
        var report = await ReadHistoricalOperatorView(group, readers);
        Assert.False(report.GroupChangedDuringRead);
        Assert.Equal(IntegrationDesiredState.Paused, report.GroupAtEnd.Desired);
        var forwardView = report.Consumers[0];
        var backfillView = report.Consumers[1];
        Assert.Equal(ForwardConsumer, forwardView.Integration.Lifecycle.Consumer);
        Assert.Equal(BackfillConsumer, backfillView.Integration.Lifecycle.Consumer);
        Assert.Equal(new DateOnly(2026, 9, 2), forwardView.RequestedScope.StartDate);
        Assert.Equal(new DateOnly(2026, 9, 1), backfillView.RequestedScope.StartDate);
        Assert.Equal(ExecutionHealthStatus.Healthy, backfillView.Integration.ProviderHealth);
        Assert.Single(backfillView.SourceCoverage!.MissingSessions);
        Assert.Empty(backfillView.AcceptedSessions);
        Assert.Null(backfillView.Publication);
        Assert.Null(backfillView.SourceProgress);
        Assert.True(backfillView.Integration.Lifecycle.Draining);
        Assert.Equal(1, backfillView.Integration.BindingPendingWork.SessionCount);
        Assert.Equal(TimeSpan.FromMinutes(1), backfillView.Integration.BindingPendingWork.OldestAdmissionAge);
        Assert.False(forwardView.Integration.Lifecycle.Draining);
        Assert.Single(forwardView.AcceptedSessions);
        Assert.Equal(before, forwardView.SourceProgress);
        Assert.NotNull(forwardView.Publication);

        clock.UtcNow = At.AddMinutes(5);
        var expired = await ReadHistoricalOperatorView(group, readers);
        Assert.All(expired.Consumers, row => Assert.Equal(ExecutionHealthStatus.Unknown, row.Integration.ProviderHealth));
        Assert.Equal(before, expired.Consumers[0].SourceProgress);
        Assert.Single(expired.Consumers[0].AcceptedSessions);
        Assert.True(expired.Consumers[1].Integration.Lifecycle.Draining);
        // A host can export this view without adding another executable control or coverage model.
        var exported = JsonSerializer.Serialize(expired,
            InteractionEnvelopeJsonSerializer.CreateOptions(PortableDocumentJsonFormatting.Indented));
        output.WriteLine(exported);
        using var json = JsonDocument.Parse(exported);
        var rows = json.RootElement.GetProperty("consumers");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("sourceProgress").ValueKind);
        Assert.Equal(0, rows[1].GetProperty("acceptedSessions").GetArrayLength());
        Assert.Equal(pause, group.Inspect().LastChange);
        Assert.Equal(before, await destination.Ledger.ReadAsync(ForwardConsumer, default));
        Assert.Null(await destination.Ledger.ReadAsync(BackfillConsumer, default));
        Assert.Equal(1, forward.ProviderCalls);
        Assert.Equal(1, forward.Publications);
        Assert.Equal(1, backfill.ProviderCalls);
        Assert.Equal(0, backfill.Publications);
    }

    /// <summary>A group transition during asynchronous evidence reads remains visible rather than appearing atomic.</summary>
    /// <returns>Completes with both group revisions and the member observation retained.</returns>
    [Fact]
    public async Task OperatorViewRetainsGroupChangesDuringRead()
    {
        var fixture = new ProcessFixture(sessions: 2);
        var ownership = new InMemoryIntegrationLifecycleScope();
        var group = ownership.CreateGroup("research/tiingo/SPY", (_, _, _) => true);
        var control = group.Register(new(fixture.Address, 0, Operator, At)).Receipt.Controller;
        control.Change(new("active", 0, IntegrationDesiredState.Active, Operator, At));
        group.Change(new("active", 1, IntegrationDesiredState.Active, Operator, At));
        var host = new IntegrationInspectionHost(control.Bind(fixture.Compilation), _ =>
        {
            group.Change(new("pause-during-read", 2, IntegrationDesiredState.Paused, Operator, At));
            return ValueTask.FromResult<IntegrationHealthEvidence?>(null);
        }, new HistoricalInspectionClock { UtcNow = At });
        var report = await ReadHistoricalOperatorView(group, [(fixture, host)]);
        Assert.True(report.GroupChangedDuringRead);
        Assert.Equal(IntegrationDesiredState.Active, report.GroupAtStart.Desired);
        Assert.Equal(IntegrationDesiredState.Paused, report.GroupAtEnd.Desired);
        Assert.False(Assert.Single(report.Consumers).Integration.Lifecycle.Accepting);
        Assert.Equal(0, fixture.ProviderCalls);
    }

    // Example-only projection over explicitly supplied consumers. These sequential observations are not
    // an atomic group/ledger snapshot, nor an enumeration of every registered member. No control is executed.
    static async Task<HistoricalOperatorView> ReadHistoricalOperatorView(InMemoryIntegrationAdmissionGroup group,
        IReadOnlyList<(ProcessFixture Fixture, IntegrationInspectionHost Host)> consumers)
    {
        var before = group.Inspect();
        var rows = ImmutableArray.CreateBuilder<HistoricalInspection>(consumers.Count);
        foreach (var (fixture, host) in consumers)
            rows.Add(await InspectHistorical(fixture, host));
        return new(before, group.Inspect(), rows.MoveToImmutable());
    }

    sealed record HistoricalOperatorView(IntegrationAdmissionGroupSnapshot GroupAtStart,
        IntegrationAdmissionGroupSnapshot GroupAtEnd, ImmutableArray<HistoricalInspection> Consumers)
    {
        // Unchanged group revision says nothing about concurrent member, evidence or ledger changes.
        public bool GroupChangedDuringRead => GroupAtStart.Revision != GroupAtEnd.Revision;
    }
}
