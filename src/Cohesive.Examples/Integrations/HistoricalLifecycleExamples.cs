using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Processes.Execution;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    static readonly ProcessControlAuthorizationContext Operator = new("research-operator", new("research", "local"), "fixture/authorized");
    static readonly ProcessContinuationIdentity Session = new(new("example/historical"), new("attempt/1"));

    /// <summary>Stops new admission while the actual historical Process drains across a worker restart.</summary>
    /// <param name="boundary">Request boundary at which the lifecycle changes.</param>
    /// <returns>Completes after the admitted Process finishes and the integration stops draining.</returns>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StoppedHistoricalIntegrationDrainsExistingProcess(int boundary)
    {
        var original = new ProcessFixture(2);
        var control = new InMemoryIntegrationLifecycle(original.Address, (_, actor) => actor == Operator);
        Assert.False(control.Inspect().Accepting);
        var active = control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At)).Receipt;
        var admissionRequest = new IntegrationAdmissionRequest(control.Bind(original.Compilation), Session, active.Revision, Operator, At);
        var admitted = control.Admit(admissionRequest).Receipt;
        var worker = original;
        var decision = worker.Start();
        for (var index = 0; decision.Disposition == ProcessActivationDisposition.DurableCut; index++)
        {
            var request = Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions));
            var outcome = await worker.Handle(request);
            if (index == boundary)
            {
                var stop = control.Change(new("stop", active.Revision, IntegrationDesiredState.Paused, Operator, At)).Receipt;
                var stopped = control.Inspect();
                Assert.True(stopped.Draining);
                Assert.False(stopped.Accepting);
                Assert.Equal(IntegrationLifecycleOutcome.AdmissionStopped, control.Admit(admissionRequest with
                    { Continuation = new(new("new-session"), new("attempt/1")), ExpectedRevision = stop.Revision }).Outcome);
                // Retrieving an existing admission is not a new admission or permission to repeat a source call.
                var replay = control.Admit(admissionRequest);
                Assert.Equal(IntegrationLifecycleOutcome.Replayed, replay.Outcome);
                Assert.Same(admitted, replay.Receipt);
                Assert.False(control.Complete(new(admitted, decision, Operator)).IsAccepted);
                worker = new ProcessFixture(original, "policy");
            }
            decision = worker.Reply(decision, outcome);
        }
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.False(control.Complete(new(admitted with { Binding = admitted.Process }, decision, Operator)).IsAccepted);
        Assert.False(control.Complete(new(admitted, decision, new("intruder", new("research", "local"), "forged"))).IsAccepted);
        Assert.True(control.Inspect().Draining);
        Assert.True(control.Complete(new(admitted, decision, Operator)).IsAccepted);
        Assert.True(control.Complete(new(admitted, decision, Operator)).IsAccepted);
        Assert.Equal(Operator, control.InspectCompletion(Session)!.Authorization);
        Assert.Equal(admitted, control.InspectCompletion(Session)!.Admission);
        var final = control.Inspect();
        Assert.Equal(IntegrationDesiredState.Paused, final.Desired);
        Assert.False(final.Draining);
        Assert.Empty(final.Pending);
        Assert.Equal(1, original.ProviderCalls);
        Assert.Equal(1, original.Publications);
        Assert.Equal(1, original.Settlements);
        Assert.Equal(1, (await original.Ledger.ReadAsync(original.Address, default))!.Revision);
    }

    /// <summary>Completed history preserves replay and recorder attribution without remaining in the pending index.</summary>
    /// <returns>Completes after draining multiple historical sessions and replaying their original evidence.</returns>
    [Fact]
    public async Task CompletedHistoryPreservesReceiptsAndOriginalRecorder()
    {
        var first = new ProcessFixture(2);
        var control = new InMemoryIntegrationLifecycle(first.Address, (_, _) => true);
        var binding = control.Bind(first.Compilation);
        control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At));
        var survivor = control.Admit(new(binding, new(new("pending"), new("attempt/1")), 1, Operator, At)).Receipt;
        for (var index = 0; index < 16; index++)
        {
            var fixture = new ProcessFixture(2);
            var identity = new ProcessContinuationIdentity(new("completed/" + index), new("attempt/1"));
            var request = new IntegrationAdmissionRequest(binding, identity, 1, Operator, At);
            var admitted = control.Admit(request).Receipt;
            var decision = fixture.Start(identity);
            while (decision.Disposition == ProcessActivationDisposition.DurableCut)
                decision = fixture.Reply(decision, await fixture.Handle(Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions))));
            var completed = control.Complete(new(admitted, decision, Operator));
            Assert.Equal(IntegrationLifecycleOutcome.Applied, completed.Outcome);
            var otherRecorder = new ProcessControlAuthorizationContext("second-operator", Operator.AuthorityScope, "authorization/2");
            var replay = control.Complete(new(admitted, decision, otherRecorder));
            Assert.Equal(IntegrationLifecycleOutcome.ReplayedWithOriginalRecorder, replay.Outcome);
            Assert.Same(completed.Receipt, replay.Receipt);
            Assert.Equal(Operator, replay.Receipt.Authorization);
            Assert.Equal(IntegrationLifecycleOutcome.Replayed, control.Complete(new(admitted, decision, Operator)).Outcome);
            Assert.Same(admitted, control.Admit(request).Receipt);
            Assert.Equal(survivor, Assert.Single(control.Inspect().Pending));
        }
    }

    /// <summary>A failed Process cannot silently erase unresolved effects from the operator's draining view.</summary>
    /// <returns>Completes after showing the conservative unresolved-session boundary.</returns>
    [Fact]
    public async Task FailedHistoricalSessionRequiresExplicitResolution()
    {
        var fixture = new ProcessFixture(1);
        var control = new InMemoryIntegrationLifecycle(fixture.Address, (_, actor) => actor == Operator);
        var active = control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At)).Receipt;
        var admitted = control.Admit(new(control.Bind(fixture.Compilation), Session, active.Revision, Operator, At)).Receipt;
        var waiting = fixture.Start();
        var failed = fixture.Reply(waiting, await fixture.Handle(Assert.IsType<RequestEnvelope>(Assert.Single(waiting.Emissions))));
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.False(control.Complete(new(admitted, failed, Operator)).IsAccepted);
        Assert.True(control.Change(new("pause", active.Revision, IntegrationDesiredState.Paused, Operator, At)).IsAccepted);
        var stopped = control.Inspect();
        Assert.True(stopped.Draining);
        Assert.Single(stopped.Pending);
        Assert.Equal(0, fixture.Publications);
    }
}
