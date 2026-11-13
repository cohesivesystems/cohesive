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
        var view = binding.Inspect(AdmissionTime.AddMinutes(minutes), evidence);
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
        var view = binding.Inspect(AdmissionTime.AddMinutes(2));
        Assert.True(view.Lifecycle.Accepting);
        Assert.Equal(ExecutionHealthStatus.Unknown, view.ProviderHealth);
        Assert.Equal(ExecutionReadinessStatus.Unknown, view.ProviderReadiness);
        Assert.Equal(2, view.ConsumerPendingWork.SessionCount);
        Assert.Equal(AdmissionTime, view.ConsumerPendingWork.OldestAdmissionAtUtc);
        Assert.Null(view.ProviderEvidence);
    }

    [Fact]
    public void AuthenticationFailureRetainsItsDiagnosticButDoesNotChangeLifecycle()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        control.Change(Change("start", 0, IntegrationDesiredState.Active));
        var observation = Health(AdmissionTime, ExecutionHealthStatus.Unhealthy);
        var view = binding.Inspect(AdmissionTime,
            new(binding.Binding, observation, AdmissionTime.AddMinutes(5)));
        Assert.Equal(ExecutionHealthStatus.Unhealthy, view.ProviderHealth);
        Assert.Equal(ExecutionReadinessStatus.NotReady, view.ProviderReadiness);
        Assert.Same(observation, view.ProviderEvidence!.Observation);
        Assert.Equal("fixture.authentication-denied", Assert.Single(view.ProviderEvidence.Observation.Diagnostics).Code);
        Assert.True(control.Inspect().Accepting);
        Assert.Empty(control.Inspect().Pending);
    }

    [Fact]
    public void HealthCannotBeBorrowedFromAnotherBinding()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        var other = control.Bind(Candidate("v2"));
        var evidence = new IntegrationHealthEvidence(other.Binding, Health(AdmissionTime), AdmissionTime.AddHours(1));
        Assert.Throws<ArgumentException>(() => binding.Inspect(AdmissionTime, evidence));
        Assert.Throws<ArgumentException>(() => binding.Inspect(AdmissionTime.ToOffset(TimeSpan.FromHours(1))));
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
    [Theory]
    [InlineData(ExecutionHealthStatus.Healthy)]
    [InlineData(ExecutionHealthStatus.Unhealthy)]
    public void RetainedHealthReopensWithoutRenewingItsValidityWindow(ExecutionHealthStatus health)
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        var evidence = new IntegrationHealthEvidence(binding.Binding, Health(AdmissionTime, health), AdmissionTime.AddMinutes(1));
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var restored = JsonSerializer.Deserialize<IntegrationHealthEvidence>(JsonSerializer.Serialize(evidence, options), options)!;
        // The wrapper uses reference identity, not record equality over nested collection-bearing evidence.
        Assert.NotEqual(evidence, restored);
        Assert.Equal(JsonSerializer.Serialize(evidence, options), JsonSerializer.Serialize(restored, options));
        var view = binding.Inspect(AdmissionTime.AddMinutes(1), restored);
        Assert.False(view.HasCurrentProviderEvidence);
        Assert.Equal(ExecutionHealthStatus.Unknown, view.ProviderHealth);
        Assert.Null(view.ConsumerPendingWork.OldestAdmissionAtUtc);
        Assert.Equal(0, view.ConsumerPendingWork.SessionCount);
    }

    [Fact]
    public void ClockSkewRetainsTimestampsAndClampsPendingAge()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        control.Change(Change("start", 0, IntegrationDesiredState.Active));
        var admission = control.Admit(Admission(binding)).Receipt;
        var evidence = new IntegrationHealthEvidence(binding.Binding, Health(AdmissionTime), AdmissionTime.AddMinutes(5));
        var view = binding.Inspect(AdmissionTime.AddSeconds(-2), evidence);
        Assert.Equal(admission.AtUtc, view.ConsumerPendingWork.OldestAdmissionAtUtc);
        Assert.Equal(TimeSpan.Zero, view.ConsumerPendingWork.OldestAdmissionAge);
        Assert.Equal(ExecutionHealthStatus.Unknown, view.ProviderHealth);
        Assert.Contains(view.Diagnostics, d => d.Code == "integrations.inspection.admission-clock-skew");
        Assert.Contains(view.Diagnostics, d => d.Code == "integrations.inspection.provider-clock-skew");
        var later = binding.Inspect(AdmissionTime.AddSeconds(2), evidence);
        Assert.Equal(TimeSpan.FromSeconds(2), later.ConsumerPendingWork.OldestAdmissionAge);
        Assert.Empty(later.Diagnostics);
    }

    [Fact]
    public async Task HostExportsSkewedInspectionAndLeavesTheStreamOpen()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        control.Change(Change("start", 0, IntegrationDesiredState.Active));
        control.Admit(Admission(binding));
        var evidence = new IntegrationHealthEvidence(binding.Binding, Health(AdmissionTime), AdmissionTime.AddMinutes(5));
        var reads = 0;
        var clock = new InspectionTestClock(AdmissionTime.AddSeconds(-1));
        var host = new IntegrationInspectionHost(binding, _ => { reads++; return ValueTask.FromResult<IntegrationHealthEvidence?>(evidence); }, clock);
        using var stream = new MemoryStream();
        await host.WriteJsonAsync(stream);
        Assert.Equal(1, reads);
        Assert.True(stream.CanWrite);
        using var json = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(1, json.RootElement.GetProperty("consumerPendingWork").GetProperty("sessionCount").GetInt32());
        Assert.Contains("integrations.inspection.admission-clock-skew", json.RootElement.GetRawText());
        Assert.Single(control.Inspect().Pending);
    }

    [Fact]
    public async Task HostDoesNotDisguiseReaderFailuresAndHonorsCancellation()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        var reads = 0;
        var host = new IntegrationInspectionHost(binding, _ => { reads++; throw new IOException("store unavailable"); });
        await Assert.ThrowsAsync<IOException>(() => host.ReadAsync().AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.ReadAsync(cancellation.Token).AsTask());
        Assert.Equal(1, reads);
        var noEvidence = new IntegrationInspectionHost(binding, _ => ValueTask.FromResult<IntegrationHealthEvidence?>(null), new InspectionTestClock(AdmissionTime));
        Assert.Equal(ExecutionHealthStatus.Unknown, (await noEvidence.ReadAsync()).ProviderHealth);
    }

    [Fact]
    public void BindingBacklogCannotBeConfusedWithAnotherBindingsPendingWork()
    {
        var control = Lifecycle();
        var binding = control.Bind(Candidate());
        var other = control.Bind(Candidate("v2"));
        control.Change(Change("start", 0, IntegrationDesiredState.Active));
        control.Admit(Admission(other) with { Continuation = new(new("other/1"), new("attempt/1")) });
        control.Admit(Admission(other) with { Continuation = new(new("other/2"), new("attempt/1")), AtUtc = AdmissionTime.AddMinutes(1) });
        var evidence = new IntegrationHealthEvidence(binding.Binding, Health(AdmissionTime), AdmissionTime.AddHours(1));
        var empty = binding.Inspect(AdmissionTime.AddMinutes(2), evidence);
        Assert.Equal(ExecutionHealthStatus.Healthy, empty.ProviderHealth);
        Assert.Equal(2, empty.ConsumerPendingWork.SessionCount);
        Assert.Equal(0, empty.BindingPendingWork.SessionCount);
        Assert.Null(empty.BindingPendingWork.OldestAdmissionAtUtc);
        Assert.Null(empty.BindingPendingWork.OldestAdmissionAge);

        control.Admit(Admission(binding) with { AtUtc = AdmissionTime.AddMinutes(2) });
        var mixed = binding.Inspect(AdmissionTime.AddMinutes(5), evidence);
        Assert.Equal(3, mixed.ConsumerPendingWork.SessionCount);
        Assert.Equal(1, mixed.BindingPendingWork.SessionCount);
        Assert.Equal(AdmissionTime, mixed.ConsumerPendingWork.OldestAdmissionAtUtc);
        Assert.Equal(AdmissionTime.AddMinutes(2), mixed.BindingPendingWork.OldestAdmissionAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(5), mixed.ConsumerPendingWork.OldestAdmissionAge);
        Assert.Equal(TimeSpan.FromMinutes(3), mixed.BindingPendingWork.OldestAdmissionAge);
        Assert.Equal(2, other.Inspect(AdmissionTime.AddMinutes(5)).BindingPendingWork.SessionCount);
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(mixed, options));
        Assert.Equal(3, json.RootElement.GetProperty("consumerPendingWork").GetProperty("sessionCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("bindingPendingWork").GetProperty("sessionCount").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("pendingSessionCount", out _));
    }

    [Fact]
    public void ExperimentalHostIsNotExportedAsPublicApi()
    {
        Assert.False(typeof(IntegrationInspectionHost).IsVisible);
    }

    sealed class InspectionTestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

}
