using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Integrations;

public sealed partial class IntegrationModelTests
{
    static readonly ProcessControlAuthorizationContext LifecycleActor = new("operator", new("test", "tenant"), "authorization/1");
    static readonly ProcessContinuationIdentity LifecycleSession = new(new("session/1"), new("attempt/1"));
    static InMemoryIntegrationLifecycle Lifecycle() => new(Candidate().Binding!.GetDefinition<IngestionProviderBinding>().Consumer,
        (_, actor) => actor == LifecycleActor);
    static IntegrationLifecycleChange Change(string id, long revision, IntegrationDesiredState desired) => new(id, revision, desired, LifecycleActor, AdmissionTime);

    [Fact]
    public void LifecycleCommandsAreAuthorizedFencedAndReplayOriginalDisposition()
    {
        var control = Lifecycle();
        Assert.False(control.Admit(Candidate(), LifecycleSession, 0, LifecycleActor, AdmissionTime, out _).IsValid);
        var activate = Change("activate", 0, IntegrationDesiredState.Active);
        Assert.False(control.Change(activate with { Authorization = new("intruder", new("test", "tenant"), "forged") }, out _).IsValid);
        Assert.Equal(0, control.Inspect().Revision);
        Assert.True(control.Change(activate, out var active).IsValid);
        Assert.True(control.Change(Change("pause", 1, IntegrationDesiredState.Paused), out var paused).IsValid);
        Assert.False(control.Change(Change("stale-resume", 1, IntegrationDesiredState.Active), out _).IsValid);
        Assert.False(control.Change(activate with { Desired = IntegrationDesiredState.Deactivated }, out _).IsValid);
        Assert.True(control.Change(activate, out var replay).IsValid);
        Assert.Equal(active, replay);
        Assert.Equal(paused, control.Inspect());
        Assert.True(control.Change(Change("resume", 2, IntegrationDesiredState.Active), out var resumed).IsValid);
        Assert.True(resumed.Accepting);
        Assert.Equal(3, resumed.Revision);
    }

    [Fact]
    public void OriginalAdmissionSurvivesDeactivationWithoutSwitchingItsBinding()
    {
        var control = Lifecycle();
        Assert.True(control.Change(Change("activate", 0, IntegrationDesiredState.Active), out _).IsValid);
        Assert.True(control.Admit(Candidate(), LifecycleSession, 1, LifecycleActor, AdmissionTime, out var admitted).IsValid);
        Assert.True(control.Change(Change("deactivate", 1, IntegrationDesiredState.Deactivated), out var stopped).IsValid);
        Assert.True(stopped.Draining);
        Assert.True(control.Admit(Candidate(), LifecycleSession, 1, LifecycleActor, AdmissionTime, out var replay).IsValid);
        Assert.Equal(admitted, replay);
        Assert.False(control.Admit(Candidate("v2"), LifecycleSession, 1, LifecycleActor, AdmissionTime, out _).IsValid);
        Assert.False(control.Admit(Candidate(), new(new("session/2"), new("attempt/1")), 2, LifecycleActor, AdmissionTime, out _).IsValid);
        Assert.Single(control.Inspect().Pending);
    }

    [Fact]
    public async Task PauseAndAdmissionLinearizeAtTheSameRevisionBoundary()
    {
        var control = Lifecycle();
        Assert.True(control.Change(Change("activate", 0, IntegrationDesiredState.Active), out _).IsValid);
        using var ready = new Barrier(2);
        var candidate = Candidate();
        var admission = Task.Run(() => { ready.SignalAndWait(); return control.Admit(candidate, LifecycleSession, 1, LifecycleActor, AdmissionTime, out _); });
        var pause = Task.Run(() => { ready.SignalAndWait(); return control.Change(Change("pause", 1, IntegrationDesiredState.Paused), out _); });
        await Task.WhenAll(admission, pause);
        var admitted = await admission;
        Assert.True((await pause).IsValid);
        var state = control.Inspect();
        Assert.Equal(IntegrationDesiredState.Paused, state.Desired);
        Assert.Equal(admitted.IsValid ? 1 : 0, state.Pending.Length);
        Assert.Equal(admitted.IsValid, state.Draining);
        Assert.False(control.Admit(candidate, new(new("after-pause"), new("attempt/1")), state.Revision, LifecycleActor, AdmissionTime, out _).IsValid);
    }

    [Fact]
    public void AdmissionRejectsUnrelatedProcessAndConsumer()
    {
        var control = Lifecycle();
        Assert.True(control.Change(Change("activate", 0, IntegrationDesiredState.Active), out _).IsValid);
        var candidate = Candidate();
        Assert.False(control.Admit(candidate with { Process = candidate.Binding }, LifecycleSession, 1, LifecycleActor, AdmissionTime, out _).IsValid);
        var other = new InMemoryIntegrationLifecycle(new("other", "source", "sink", "partition"), (_, _) => true);
        Assert.True(other.Change(Change("activate", 0, IntegrationDesiredState.Active), out _).IsValid);
        Assert.False(other.Admit(candidate, LifecycleSession, 1, LifecycleActor, AdmissionTime, out _).IsValid);
        Assert.Empty(control.Inspect().Pending);
    }
}
