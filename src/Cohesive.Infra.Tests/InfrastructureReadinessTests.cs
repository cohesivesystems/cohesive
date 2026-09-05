using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureReadinessTests
{
    static readonly InfrastructureNodeId Api = new("workload/api");
    static readonly InfrastructureNodeId State = new("resource/state");
    static readonly InfrastructurePhysicalResourceId ApiPhysical = new("aspire/project/ari-api");
    static readonly InfrastructurePhysicalResourceId StatePhysical = new("aspire/container/cosmos");
    static readonly SourceReference AspireSource = SourceReference.Create("aspire", "app-host/ari");
    static readonly DateTimeOffset ObservedAt = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Realization_lowers_canonical_readiness_to_exact_physical_resources()
    {
        var realization = Realization();

        var obligation = Assert.Single(realization.ReadinessObligations);
        Assert.Equal(InfrastructureReadinessDependency.DeriveId(Api, State), obligation.Dependency);
        Assert.Equal(Api, obligation.Subject);
        Assert.Equal(ApiPhysical, obligation.SubjectPhysicalResource);
        Assert.Equal(State, obligation.RequiredNode);
        Assert.Equal(StatePhysical, obligation.RequiredPhysicalResource);
        Assert.True(realization.IsReadinessObligationComplete);
    }

    [Fact]
    public void Ready_workload_is_blocked_by_an_unhealthy_exact_dependency()
    {
        var assessment = InfrastructureReadinessEvaluator.Assess(
            Realization(),
            [
                Observation(ApiPhysical, ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready),
                Observation(StatePhysical, ExecutionHealthStatus.Unhealthy, ExecutionReadinessStatus.NotReady)
            ]);

        Assert.False(assessment.IsReady);
        var api = Assert.IsType<InfrastructureReadinessDecision>(assessment.FindDecision(Api));
        Assert.Equal(ExecutionReadinessStatus.Ready, api.ObservedReadiness);
        Assert.Equal(ExecutionReadinessStatus.NotReady, api.EffectiveReadiness);
        Assert.True(api.BlockingDependencies.SequenceEqual([StatePhysical]));
        var mismatch = Assert.Single(
            assessment.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureReadinessEvaluator.DiagnosticCodes.DependencyNotReady);
        Assert.Equal(InfrastructureReadinessDependency.DeriveId(Api, State).Value, mismatch.Evidence?.Subject);
        Assert.Contains(StatePhysical.Value, mismatch.Evidence!.Expected, StringComparison.Ordinal);
        Assert.Contains(AspireSource.Value, mismatch.Evidence.SourceReferences);
    }

    [Fact]
    public void Missing_dependency_observation_is_unknown_and_fails_closed()
    {
        var assessment = InfrastructureReadinessEvaluator.Assess(
            Realization(),
            [Observation(ApiPhysical, ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready)]);

        Assert.False(assessment.IsReady);
        var api = Assert.IsType<InfrastructureReadinessDecision>(assessment.FindDecision(Api));
        Assert.Equal(ExecutionReadinessStatus.Unknown, api.EffectiveReadiness);
        Assert.True(api.UnknownDependencies.SequenceEqual([StatePhysical]));
        Assert.Contains(
            assessment.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureReadinessEvaluator.DiagnosticCodes.ObservationMissing);
        Assert.Contains(
            assessment.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureReadinessEvaluator.DiagnosticCodes.DependencyUnknown);
    }

    [Fact]
    public void Assessment_is_deterministic_round_trippable_and_ready_only_when_all_obligations_are_met()
    {
        var realization = Realization();
        var state = Observation(StatePhysical, ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready);
        var api = Observation(ApiPhysical, ExecutionHealthStatus.Degraded, ExecutionReadinessStatus.Ready);

        var first = InfrastructureReadinessEvaluator.Assess(realization, [state, api]);
        var reordered = InfrastructureReadinessEvaluator.Assess(realization, [api, state]);
        var json = JsonSerializer.Serialize(first, JsonOptions);
        var restored = JsonSerializer.Deserialize<InfrastructureReadinessAssessment>(json, JsonOptions);

        Assert.True(first.IsReady);
        Assert.Empty(first.Diagnostics);
        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotNull(restored);
        Assert.Equal(first.Fingerprint, restored.Fingerprint);
        Assert.True(first.Observations.SequenceEqual(restored.Observations));
        Assert.True(first.Decisions.SequenceEqual(restored.Decisions));
    }

    [Fact]
    public void Runtime_health_cannot_override_an_incomplete_capability_realization()
    {
        InfrastructureCapabilityId unsupported = new("capability/unsupported");
        var definition = Infrastructure.Define(new("incomplete"), new("v1"), infrastructure =>
            infrastructure.Workload(Api).Requires(unsupported));
        InfrastructureCapabilityVariantId variant = new("local");
        var profile = new InfrastructureCapabilityProfile(
            InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new("profiles/incomplete/v1"),
            new("local"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [new(variant)]);
        var closure = InfrastructureCapabilityCompiler.Compile(definition, profile, variant);
        var realization = InfrastructureRealizationCompiler.Compile(
            closure,
            new InfrastructureLifecyclePlan(definition),
            [new(Api, ApiPhysical, new("local"), [AspireSource])]);

        var assessment = InfrastructureReadinessEvaluator.Assess(
            realization,
            [Observation(ApiPhysical, ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready)]);

        Assert.False(realization.IsCapabilityWitnessComplete);
        Assert.False(assessment.IsReady);
        Assert.Equal(ExecutionReadinessStatus.NotReady, assessment.FindDecision(Api)!.EffectiveReadiness);
        Assert.Contains(
            assessment.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureReadinessEvaluator.DiagnosticCodes.RealizationIncomplete);
        Assert.Contains(
            assessment.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.RequirementUnavailable);
    }

    static InfrastructureRealization Realization()
    {
        var definition = Infrastructure.Define(new("ari-readiness"), new("v1"), infrastructure =>
        {
            var state = infrastructure.Resource(State).Persistent();
            infrastructure.Workload(Api).RequiresReady(state);
        });
        InfrastructureCapabilityVariantId variant = new("local");
        var profile = new InfrastructureCapabilityProfile(
            InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new("profiles/local-readiness/v1"),
            new("local"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [new(variant)]);
        var closure = InfrastructureCapabilityCompiler.Compile(definition, profile, variant);
        var lifecycle = new InfrastructureLifecyclePlan(
            definition,
            [
                new(
                    State,
                    StatePhysical,
                    new("local"),
                    new("local/ari"),
                    InfrastructureLifecycleDisposition.Managed)
            ]);
        return InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            [new(Api, ApiPhysical, new("local"), [AspireSource])]);
    }

    static InfrastructureResourceObservation Observation(
        InfrastructurePhysicalResourceId physicalResource,
        ExecutionHealthStatus health,
        ExecutionReadinessStatus readiness) => new(
            physicalResource,
            health,
            readiness,
            ObservedAt,
            [AspireSource]);

    static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}
