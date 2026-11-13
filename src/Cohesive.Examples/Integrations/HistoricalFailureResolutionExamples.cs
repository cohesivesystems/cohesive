using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Processes.Execution;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    const string AccountedFailure = "local-evidence/coverage-failure/1";

    /// <summary>Closes a fully accounted coverage failure without pretending data was published.</summary>
    /// <returns>Completes after authorized resolution ends draining while preserving failed Process evidence.</returns>
    [Fact]
    public async Task AccountedCoverageFailureCanBeResolvedWhilePaused()
    {
        var fixture = new ProcessFixture(1);
        ProcessActivationDecision? failed = null;
        var verificationCalls = 0;
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(fixture.Address, (_, actor) => actor == Operator,
            request => { verificationCalls++; return request.EvidenceReference == AccountedFailure ? failed : null; });
        control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var admissionRequest = new IntegrationAdmissionRequest(control.Bind(fixture.Compilation), Session, 1, Operator, At);
        var admission = control.Admit(admissionRequest).Receipt;
        var waiting = fixture.Start();
        failed = fixture.Reply(waiting, await fixture.Handle(Request(waiting)));
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        // Trusted host evidence: the acquisition returned; its coverage was rejected before publication.
        // A terminal failure alone is insufficient. This fixture knows all dispatched effects were answered.
        Assert.Equal(1, fixture.ProviderCalls);
        Assert.Equal(0, fixture.Publications);
        Assert.Null(await fixture.Ledger.ReadAsync(fixture.Address, default));
        control.Change(new("pause", 1, IntegrationDesiredState.Paused, Operator, At));
        var command = new IntegrationFailureResolutionRequest("resolve/1", admission, 2, AccountedFailure, Operator, At);
        var intruder = new ProcessControlAuthorizationContext("intruder", Operator.AuthorityScope, "denied");
        Assert.Equal(IntegrationFailureResolutionOutcome.Unauthorized,
            control.ResolveFailure(command with { Authorization = intruder }).Outcome);
        Assert.Equal(IntegrationFailureResolutionOutcome.RevisionConflict,
            control.ResolveFailure(command with { ExpectedRevision = 1 }).Outcome);
        Assert.Equal(IntegrationFailureResolutionOutcome.AdmissionMismatch,
            control.ResolveFailure(command with { Admission = admission with { Binding = admission.Process } }).Outcome);
        Assert.Equal(0, verificationCalls);
        Assert.Equal(IntegrationFailureResolutionOutcome.EvidenceRejected,
            control.ResolveFailure(command with { EvidenceReference = "missing" }).Outcome);
        Assert.True(control.Inspect().Draining);
        Assert.Throws<ArgumentException>(() => control.ResolveFailure(command with { EvidenceReference = " " }));
        Assert.Throws<ArgumentException>(() => control.ResolveFailure(command with { AtUtc = At.AddSeconds(-1) }));
        var result = control.ResolveFailure(command);
        Assert.Equal(IntegrationFailureResolutionOutcome.Applied, result.Outcome);
        Assert.Equal(3, result.Receipt.Revision);
        Assert.Same(failed, result.Receipt.Decision);
        Assert.Same(result.Receipt, control.InspectFailureResolution(Session));
        Assert.Null(control.InspectCompletion(Session));
        Assert.False(control.Inspect().Draining);
        Assert.False(control.Inspect().Accepting);
        Assert.Equal(2, verificationCalls);
        Assert.Equal(IntegrationCompletionOutcome.ResolvedFailure, control.Complete(new(admission, failed, Operator)).Outcome);
        Assert.Same(admission, control.Admit(admissionRequest).Receipt);
        Assert.Empty(control.Inspect().Pending);
        Assert.Equal(IntegrationFailureResolutionOutcome.IdentityConflict,
            control.ResolveFailure(command with { EvidenceReference = "replacement" }).Outcome);
        Assert.Equal(IntegrationFailureResolutionOutcome.AlreadyClosed,
            control.ResolveFailure(command with { Id = "different-command", ExpectedRevision = 3 }).Outcome);
        Assert.Equal(IntegrationFailureResolutionOutcome.Unauthorized,
            control.ResolveFailure(command with { Authorization = intruder }).Outcome);
        control.Change(new("resume", 3, IntegrationDesiredState.Active, Operator, At));
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var reopened = JsonSerializer.Deserialize<IntegrationFailureResolutionRequest>(JsonSerializer.Serialize(command, options), options)!;
        var replay = control.ResolveFailure(reopened);
        Assert.Equal(IntegrationFailureResolutionOutcome.Replayed, replay.Outcome);
        Assert.Same(result.Receipt, replay.Receipt);
        Assert.Equal(4, control.Inspect().Revision);
        Assert.Equal(2, verificationCalls);
        Assert.Equal(0, fixture.Publications);
        Assert.Equal(0, fixture.Settlements);
        Assert.Null(await fixture.Ledger.ReadAsync(fixture.Address, default));
    }

    /// <summary>Rejects evidence that does not establish an exact terminal failed attempt.</summary>
    /// <param name="evidence">Invalid host evidence case.</param>
    /// <returns>Completes with the admission still pending.</returns>
    [Theory]
    [InlineData("unresolved")]
    [InlineData("foreign-attempt")]
    [InlineData("success")]
    [InlineData("missing-verifier")]
    [InlineData("verifier-failure")]
    public async Task InvalidFailureResolutionDoesNotCloseAdmission(string evidence)
    {
        var fixture = new ProcessFixture(evidence == "success" ? 2 : 1);
        var identity = evidence == "foreign-attempt" ? new ProcessContinuationIdentity(new("other"), new("attempt/1")) : Session;
        var decision = fixture.Start(identity);
        if (evidence != "unresolved")
            while (decision.Disposition == ProcessActivationDisposition.DurableCut)
                decision = fixture.Reply(decision, await fixture.Handle(Request(decision)));
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(fixture.Address, (_, _) => true,
            evidence == "missing-verifier" ? null : _ => evidence == "verifier-failure"
                ? throw new IOException("Evidence store unavailable.") : decision);
        control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var admission = control.Admit(new(control.Bind(fixture.Compilation), Session, 1, Operator, At)).Receipt;
        control.Change(new("pause", 1, IntegrationDesiredState.Paused, Operator, At));
        var command = new IntegrationFailureResolutionRequest("resolve", admission, 2, AccountedFailure, Operator, At);
        if (evidence == "verifier-failure") Assert.Throws<IOException>(() => control.ResolveFailure(command));
        else Assert.Equal(evidence == "missing-verifier" ? IntegrationFailureResolutionOutcome.EvidenceRejected
            : IntegrationFailureResolutionOutcome.DecisionMismatch, control.ResolveFailure(command).Outcome);
        Assert.True(control.Inspect().Draining);
        Assert.Equal(2, control.Inspect().Revision);
        Assert.Null(control.InspectFailureResolution(Session));
    }

    /// <summary>A lifecycle change during external evidence lookup fences the pending resolution.</summary>
    /// <returns>Completes after proving verification runs outside the lock and cannot commit against a stale revision.</returns>
    [Fact]
    public async Task ResolutionRechecksRevisionAfterEvidenceLookup()
    {
        var fixture = new ProcessFixture(1);
        var waiting = fixture.Start();
        var failed = fixture.Reply(waiting, await fixture.Handle(Request(waiting)));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(fixture.Address, (_, _) => true, _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return failed;
        });
        control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var admission = control.Admit(new(control.Bind(fixture.Compilation), Session, 1, Operator, At)).Receipt;
        var command = new IntegrationFailureResolutionRequest("resolve", admission, 1, AccountedFailure, Operator, At);
        var resolving = Task.Run(() => control.ResolveFailure(command));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            // If verification held the lock, this command would block until the verifier timed out.
            Assert.True(control.Change(new("pause", 1, IntegrationDesiredState.Paused, Operator, At)).IsAccepted);
        }
        finally { release.Set(); }
        Assert.Equal(IntegrationFailureResolutionOutcome.RevisionConflict, (await resolving).Outcome);
        Assert.True(control.Inspect().Draining);
        Assert.Null(control.InspectFailureResolution(Session));
        Assert.Equal(IntegrationFailureResolutionOutcome.Applied,
            control.ResolveFailure(command with { ExpectedRevision = 2 }).Outcome);
    }

    /// <summary>Concurrent exact retries retain one receipt and advance the control revision only once.</summary>
    /// <returns>Completes after both callers observe the same accepted resolution.</returns>
    [Fact]
    public async Task ConcurrentFailureResolutionReplaysOneReceipt()
    {
        var fixture = new ProcessFixture(1);
        var waiting = fixture.Start();
        var failed = fixture.Reply(waiting, await fixture.Handle(Request(waiting)));
        using var verifying = new Barrier(2);
        var control = new InMemoryIntegrationLifecycleScope().CreateConsumer(fixture.Address, (_, _) => true, _ =>
        {
            if (!verifying.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            return failed;
        });
        control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var admission = control.Admit(new(control.Bind(fixture.Compilation), Session, 1, Operator, At)).Receipt;
        var command = new IntegrationFailureResolutionRequest("resolve", admission, 1, AccountedFailure, Operator, At);
        var results = await Task.WhenAll(Task.Run(() => control.ResolveFailure(command)), Task.Run(() => control.ResolveFailure(command)));
        Assert.Single(results, r => r.Outcome == IntegrationFailureResolutionOutcome.Applied);
        Assert.Single(results, r => r.Outcome == IntegrationFailureResolutionOutcome.Replayed);
        Assert.Same(results[0].Receipt, results[1].Receipt);
        Assert.Equal(2, control.Inspect().Revision);
        Assert.True(control.Inspect().Accepting);
        Assert.Empty(control.Inspect().Pending);
    }
}
