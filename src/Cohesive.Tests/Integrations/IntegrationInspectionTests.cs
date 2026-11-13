using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Integrations;

public sealed partial class IntegrationModelTests
{
    static ExecutionHealthObservation Health(DateTimeOffset at, ExecutionHealthStatus status = ExecutionHealthStatus.Healthy) =>
        new(status, status == ExecutionHealthStatus.Unhealthy ? ExecutionReadinessStatus.NotReady : ExecutionReadinessStatus.Ready,
            at, Profile().Document.Metadata.Provenance, ["fixture/probe/1"],
            status == ExecutionHealthStatus.Unhealthy ? [new("fixture.authentication-denied", DiagnosticSeverity.Error, "Authentication was rejected.")] : []);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(59, true)]
    [InlineData(60, false)]
    public void HealthExpiresWithoutDiscardingOriginalEvidence(int minutes, bool current)
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        var evidence = new IntegrationHealthEvidence(binding.Binding, Health(AdmissionTime), AdmissionTime.AddHours(1));
        var view = IntegrationInspection.Create(binding, control.Inspect(), AdmissionTime.AddMinutes(minutes), evidence);
        Assert.Equal(current, view.HasCurrentProviderEvidence);
        Assert.Equal(current ? ExecutionHealthStatus.Healthy : ExecutionHealthStatus.Unknown, view.ProviderHealth);
        Assert.Equal(current ? ExecutionReadinessStatus.Ready : ExecutionReadinessStatus.Unknown, view.ProviderReadiness);
        Assert.Same(evidence, view.ProviderEvidence);
        Assert.False(view.Lifecycle.Accepting);
    }

    [Fact]
    public void ActiveAndPendingDoNotInventHealthOrAmbiguousEffects()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        control.Change(Change("start", 0, IntegrationDesiredState.Active));
        control.Admit(Admission(binding));
        control.Admit(Admission(binding) with { Continuation = new(new("later"), new("attempt/1")), AtUtc = AdmissionTime.AddMinutes(1) });
        var view = IntegrationInspection.Create(binding, control.Inspect(), AdmissionTime.AddMinutes(2));
        Assert.True(view.Lifecycle.Accepting);
        Assert.Equal(ExecutionHealthStatus.Unknown, view.ProviderHealth);
        Assert.Equal(ExecutionReadinessStatus.Unknown, view.ProviderReadiness);
        Assert.Equal(2, view.PendingSessionCount);
        Assert.Equal(AdmissionTime, view.OldestPendingAtUtc);
        Assert.Null(view.ProviderEvidence);
    }

    [Fact]
    public void AuthenticationFailureRetainsItsDiagnosticButDoesNotChangeLifecycle()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        control.Change(Change("start", 0, IntegrationDesiredState.Active));
        var observation = Health(AdmissionTime, ExecutionHealthStatus.Unhealthy);
        var view = IntegrationInspection.Create(binding, control.Inspect(), AdmissionTime,
            new(binding.Binding, observation, AdmissionTime.AddMinutes(5)));
        Assert.Equal(ExecutionHealthStatus.Unhealthy, view.ProviderHealth);
        Assert.Equal(ExecutionReadinessStatus.NotReady, view.ProviderReadiness);
        Assert.Same(observation, view.ProviderEvidence!.Observation);
        Assert.Equal("fixture.authentication-denied", Assert.Single(view.ProviderEvidence.Observation.Diagnostics).Code);
        Assert.True(control.Inspect().Accepting);
        Assert.Empty(control.Inspect().Pending);
    }

    [Fact]
    public void HealthCannotBeBorrowedFromAnotherBindingOrConsumer()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        var other = control.Bind(Candidate("v2"));
        var evidence = new IntegrationHealthEvidence(other.Binding, Health(AdmissionTime), AdmissionTime.AddHours(1));
        Assert.Throws<ArgumentException>(() => IntegrationInspection.Create(binding, control.Inspect(), AdmissionTime, evidence));
        Assert.Throws<ArgumentException>(() => IntegrationInspection.Create(binding,
            control.Inspect() with { Consumer = new("other", "source", "sink", "partition") }, AdmissionTime));
        Assert.Throws<ArgumentException>(() => IntegrationInspection.Create(binding, control.Inspect(), AdmissionTime.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void HealthRequiresValidExpiryAndSourceAttribution()
    {
        var binding = Lifecycle().Bind(Candidate());
        Assert.Throws<ArgumentException>(() => new IntegrationHealthEvidence(binding.Binding, Health(AdmissionTime), AdmissionTime));
        Assert.Throws<ArgumentException>(() => new IntegrationHealthEvidence(binding.Binding, Health(AdmissionTime), AdmissionTime.AddMinutes(-1)));
        Assert.Throws<ArgumentException>(() => new IntegrationHealthEvidence(binding.Binding, Health(AdmissionTime), AdmissionTime.AddHours(1).ToOffset(TimeSpan.FromHours(1))));
        var noSource = new ExecutionHealthObservation(ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready,
            AdmissionTime, Profile().Document.Metadata.Provenance);
        Assert.Throws<ArgumentException>(() => new IntegrationHealthEvidence(binding.Binding, noSource, AdmissionTime.AddHours(1)));
    }
    [Fact]
    public void RetainedHealthReopensWithoutRenewingItsValidityWindow()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        var evidence = new IntegrationHealthEvidence(binding.Binding, Health(AdmissionTime), AdmissionTime.AddMinutes(1));
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var restored = JsonSerializer.Deserialize<IntegrationHealthEvidence>(JsonSerializer.Serialize(evidence, options), options)!;
        Assert.Equal(evidence.Binding, restored.Binding);
        Assert.Equal(evidence.ValidUntilUtc, restored.ValidUntilUtc);
        Assert.Equal(evidence.Observation.ObservedAtUtc, restored.Observation.ObservedAtUtc);
        Assert.Equal(evidence.Observation.Provenance, restored.Observation.Provenance);
        var view = IntegrationInspection.Create(binding, control.Inspect(), AdmissionTime.AddMinutes(1), restored);
        Assert.False(view.HasCurrentProviderEvidence);
        Assert.Equal(ExecutionHealthStatus.Unknown, view.ProviderHealth);
        Assert.Null(view.OldestPendingAtUtc);
        Assert.Equal(0, view.PendingSessionCount);
    }

    [Fact]
    public void PendingSnapshotCannotInventAnAgeFromFutureOrForeignAdmissions()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        control.Change(Change("start", 0, IntegrationDesiredState.Active));
        var admission = control.Admit(Admission(binding)).Receipt;
        var snapshot = control.Inspect();
        Assert.Throws<ArgumentException>(() => IntegrationInspection.Create(binding, snapshot, AdmissionTime.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => IntegrationInspection.Create(binding,
            snapshot with { Pending = [admission with { Consumer = new("other", "source", "sink", "partition") }] }, AdmissionTime));
    }

}
