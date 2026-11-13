using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Processes.Execution;

namespace Cohesive.Examples.Integrations;

public sealed partial class HistoricalQualificationExamples
{
    static readonly ProcessControlAuthorizationContext Operator = new("research-operator", new("research", "local"), "fixture/authorized");
    static readonly ProcessContinuationIdentity Session = new(new("example/historical"), new("attempt/1"));

    /// <summary>Stops new admission while the actual historical Process drains across a worker restart.</summary>
    /// <param name="desired">Operator-selected stopped state.</param>
    /// <param name="boundary">Request boundary at which the lifecycle changes.</param>
    /// <returns>Completes after the admitted Process finishes and the integration stops draining.</returns>
    [Theory]
    [InlineData(IntegrationDesiredState.Paused, 0)]
    [InlineData(IntegrationDesiredState.Paused, 1)]
    [InlineData(IntegrationDesiredState.Deactivated, 0)]
    [InlineData(IntegrationDesiredState.Deactivated, 2)]
    public async Task StoppedHistoricalIntegrationDrainsExistingProcess(IntegrationDesiredState desired, int boundary)
    {
        var original = new ProcessFixture(2);
        var control = new InMemoryIntegrationLifecycle(original.Address, (_, actor) => actor == Operator);
        Assert.False(control.Inspect().Accepting);
        Assert.True(control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At), out var active).IsValid);
        Assert.True(control.Admit(original.Compilation, Session, active.Revision, Operator, At, out var admitted).IsValid);
        var worker = original;
        var decision = worker.Start();
        for (var index = 0; decision.Disposition == ProcessActivationDisposition.DurableCut; index++)
        {
            var request = Assert.IsType<RequestEnvelope>(Assert.Single(decision.Emissions));
            var outcome = await worker.Handle(request);
            if (index == boundary)
            {
                Assert.True(control.Change(new("stop", active.Revision, desired, Operator, At), out var stopped).IsValid);
                Assert.True(stopped.Draining);
                Assert.False(stopped.Accepting);
                Assert.False(control.Admit(original.Compilation, new(new("new-session"), new("attempt/1")),
                    stopped.Revision, Operator, At, out _).IsValid);
                // Retrieving an existing admission is not a new admission or permission to repeat a source call.
                Assert.True(control.Admit(original.Compilation, Session, active.Revision, Operator, At, out var replay).IsValid);
                Assert.Equal(admitted, replay);
                Assert.False(control.Complete(admitted!, decision, Operator).IsValid);
                worker = new ProcessFixture(original, "policy");
            }
            decision = worker.Reply(decision, outcome);
        }
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        Assert.False(control.Complete(admitted! with { Binding = admitted.Process }, decision, Operator).IsValid);
        Assert.False(control.Complete(admitted!, decision, new("intruder", new("research", "local"), "forged")).IsValid);
        Assert.True(control.Inspect().Draining);
        Assert.True(control.Complete(admitted!, decision, Operator).IsValid);
        Assert.True(control.Complete(admitted!, decision, Operator).IsValid);
        Assert.Equal(Operator, control.InspectCompletion(Session)!.Authorization);
        Assert.Equal(admitted, control.InspectCompletion(Session)!.Admission);
        var final = control.Inspect();
        Assert.Equal(desired, final.Desired);
        Assert.False(final.Draining);
        Assert.Empty(final.Pending);
        Assert.Equal(1, original.ProviderCalls);
        Assert.Equal(1, original.Publications);
        Assert.Equal(1, original.Settlements);
        Assert.Equal(1, (await original.Ledger.ReadAsync(original.Address, default))!.Revision);
    }

    /// <summary>A failed Process cannot silently erase unresolved effects from the operator's draining view.</summary>
    /// <returns>Completes after showing the conservative unresolved-session boundary.</returns>
    [Fact]
    public async Task FailedHistoricalSessionRequiresExplicitResolution()
    {
        var fixture = new ProcessFixture(1);
        var control = new InMemoryIntegrationLifecycle(fixture.Address, (_, actor) => actor == Operator);
        Assert.True(control.Change(new("activate", 0, IntegrationDesiredState.Active, Operator, At), out var active).IsValid);
        Assert.True(control.Admit(fixture.Compilation, Session, active.Revision, Operator, At, out var admitted).IsValid);
        var waiting = fixture.Start();
        var failed = fixture.Reply(waiting, await fixture.Handle(Assert.IsType<RequestEnvelope>(Assert.Single(waiting.Emissions))));
        Assert.Equal(ProcessActivationDisposition.Failed, failed.Disposition);
        Assert.False(control.Complete(admitted!, failed, Operator).IsValid);
        Assert.True(control.Change(new("pause", active.Revision, IntegrationDesiredState.Paused, Operator, At), out var stopped).IsValid);
        Assert.True(stopped.Draining);
        Assert.Single(stopped.Pending);
        Assert.Equal(0, fixture.Publications);
    }
}
