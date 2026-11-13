using Cohesive.Execution;
using Cohesive.Integrations;

namespace Cohesive.Tests.Integrations;

public sealed partial class IntegrationModelTests
{
    static readonly ProcessControlAuthorizationContext LifecycleActor = new("operator", new("test", "tenant"), "authorization/1");
    static readonly ProcessContinuationIdentity LifecycleSession = new(new("session/1"), new("attempt/1"));
    static InMemoryIntegrationLifecycle Lifecycle() => new(Candidate().Binding!.GetDefinition<IngestionProviderBinding>().Consumer,
        (_, actor) => actor == LifecycleActor);
    static IntegrationLifecycleChange Change(string id, long revision, IntegrationDesiredState desired) => new(id, revision, desired, LifecycleActor, AdmissionTime);
    static IntegrationAdmissionRequest Admission(IntegrationAdmissionBinding binding, long revision = 1) =>
        new(binding, LifecycleSession, revision, LifecycleActor, AdmissionTime);

    [Fact]
    public void LifecycleCommandsAreAuthorizedFencedAndReplayOriginalDisposition()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        var stopped = control.Admit(Admission(binding, revision: 0));
        Assert.Equal(IntegrationLifecycleOutcome.AdmissionStopped, stopped.Outcome);
        Assert.False(stopped.IsAccepted);
        Assert.Throws<InvalidOperationException>(() => stopped.Receipt);
        var activate = Change("activate", 0, IntegrationDesiredState.Active);
        Assert.Equal(IntegrationLifecycleOutcome.Unauthorized, control.Change(activate with
            { Authorization = new("intruder", new("test", "tenant"), "forged") }).Outcome);
        Assert.Equal(0, control.Inspect().Revision);
        var active = control.Change(activate).Receipt;
        Assert.True(control.Change(Change("pause", 1, IntegrationDesiredState.Paused)).IsAccepted);
        var paused = control.Inspect();
        Assert.Equal(IntegrationLifecycleOutcome.RevisionConflict, control.Change(Change("stale-resume", 1, IntegrationDesiredState.Active)).Outcome);
        Assert.Equal(IntegrationLifecycleOutcome.IdentityConflict, control.Change(activate with { Desired = IntegrationDesiredState.Paused }).Outcome);
        var replay = control.Change(activate);
        Assert.Equal(IntegrationLifecycleOutcome.Replayed, replay.Outcome);
        Assert.Same(active, replay.Receipt);
        Assert.Equal(paused, control.Inspect());
        var resumed = control.Change(Change("resume", 2, IntegrationDesiredState.Active)).Receipt;
        Assert.True(control.Inspect().Accepting);
        Assert.Equal(3, resumed.Revision);
    }

    [Fact]
    public void OriginalAdmissionSurvivesPauseWithoutSwitchingItsBinding()
    {
        var control = Lifecycle();
        var request = Admission(control.Bind(Candidate()));
        Assert.True(control.Change(Change("activate", 0, IntegrationDesiredState.Active)).IsAccepted);
        var admitted = control.Admit(request).Receipt;
        Assert.True(control.Change(Change("pause", 1, IntegrationDesiredState.Paused)).IsAccepted);
        Assert.True(control.Inspect().Draining);
        var replay = control.Admit(request);
        Assert.Equal(IntegrationLifecycleOutcome.Replayed, replay.Outcome);
        Assert.Same(admitted, replay.Receipt);
        Assert.Equal(IntegrationLifecycleOutcome.IdentityConflict, control.Admit(request with { Binding = control.Bind(Candidate("v2")) }).Outcome);
        Assert.Equal(IntegrationLifecycleOutcome.AdmissionStopped, control.Admit(request with
            { Continuation = new(new("session/2"), new("attempt/1")), ExpectedRevision = 2 }).Outcome);
        Assert.Single(control.Inspect().Pending);
    }

    [Fact]
    public async Task PauseAndAdmissionLinearizeAtTheSameRevisionBoundary()
    {
        var control = Lifecycle();
        var request = Admission(control.Bind(Candidate()));
        Assert.True(control.Change(Change("activate", 0, IntegrationDesiredState.Active)).IsAccepted);
        using var ready = new Barrier(2);
        var admission = Task.Run(() => { ready.SignalAndWait(); return control.Admit(request); });
        var pause = Task.Run(() => { ready.SignalAndWait(); return control.Change(Change("pause", 1, IntegrationDesiredState.Paused)); });
        await Task.WhenAll(admission, pause);
        var admitted = await admission;
        Assert.True((await pause).IsAccepted);
        var state = control.Inspect();
        Assert.Equal(IntegrationDesiredState.Paused, state.Desired);
        Assert.Equal(admitted.IsAccepted ? 1 : 0, state.Pending.Length);
        Assert.Equal(admitted.IsAccepted, state.Draining);
        Assert.Equal(IntegrationLifecycleOutcome.AdmissionStopped, control.Admit(request with
            { Continuation = new(new("after-pause"), new("attempt/1")), ExpectedRevision = state.Revision }).Outcome);
    }

    [Fact]
    public void BindingRejectsUnrelatedProcessConsumerAndForeignController()
    {
        var control = Lifecycle();
        var candidate = Candidate();
        Assert.Throws<ArgumentException>(() => control.Bind(candidate with { Process = candidate.Binding }));
        var other = new InMemoryIntegrationLifecycle(new("other", "source", "sink", "partition"), (_, _) => true);
        Assert.Throws<ArgumentException>(() => other.Bind(candidate));
        var sameConsumer = Lifecycle();
        Assert.Equal(IntegrationLifecycleOutcome.BindingMismatch, sameConsumer.Admit(Admission(control.Bind(candidate))).Outcome);
        Assert.Empty(control.Inspect().Pending);
    }

    [Fact]
    public void ChangeReceiptsDoNotCapturePendingSessions()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        var start = control.Change(Change("start", 0, IntegrationDesiredState.Active)).Receipt;
        for (var i = 0; i < 200; i++)
            Assert.True(control.Admit(Admission(binding) with { Continuation = new(new("session/" + i), new("attempt/1")) }).IsAccepted);
        for (var i = 1; i <= 200; i++)
            Assert.Equal(i + 1, control.Change(Change("change/" + i, i, IntegrationDesiredState.Active)).Receipt.Revision);
        Assert.Same(start, control.Change(Change("start", 0, IntegrationDesiredState.Active)).Receipt);
        Assert.Equal(200, control.Inspect().Pending.Length);
    }
}
