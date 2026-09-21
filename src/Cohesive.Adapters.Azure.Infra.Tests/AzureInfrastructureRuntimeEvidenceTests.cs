using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Azure.Infra;
using Cohesive.Execution;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using static Cohesive.Adapters.Azure.Infra.Tests.AzureInfrastructureObservationsTests;

namespace Cohesive.Adapters.Azure.Infra.Tests;

public sealed class AzureInfrastructureRuntimeEvidenceTests
{
    static readonly AzureInfrastructureRuntimeContract ApiContract = Contract(ApiBinding);
    static readonly AzureInfrastructureRuntimeContract StateContract = Contract(StateBinding);

    [Fact]
    public void Exact_runtime_contracts_reuse_evaluator_and_preserve_attribution()
    {
        var observations = Normalize([Evidence(StateContract), Evidence(ApiContract)]);
        Assert.True(InfrastructureReadinessEvaluator.Assess(Realization, observations).IsReady);
        foreach (var observation in observations)
        {
            Assert.Contains(ApiContract.Producer, observation.SourceReferences);
            Assert.Contains(ApiContract.CheckContract, observation.SourceReferences);
            Assert.Contains(ApiContract.Deployment, observation.SourceReferences);
            Assert.Contains(Scope.Handoff, observation.SourceReferences);
            Assert.Equal(Now, observation.ObservedAtUtc);
        }
        Assert.True(observations.SequenceEqual(Normalize([Evidence(ApiContract), Evidence(StateContract)])));
    }

    [Fact]
    public void Every_attribution_dimension_is_fenced()
    {
        foreach (var contract in new[] {
            StateContract with { Producer = Source }, StateContract with { CheckContract = Source },
            StateContract with { Deployment = Source }, StateContract with { Binding = ApiBinding } })
            Assert.Throws<ArgumentException>(() => Normalize([Evidence(StateContract) with { Contract = contract }]));
        Assert.Throws<ArgumentException>(() => Normalize([Evidence(StateContract)], Scope with { Handoff = Source }));
    }

    [Fact]
    public void Missing_child_evidence_is_not_replaced_by_healthy_parent()
    {
        var result = InfrastructureReadinessEvaluator.Assess(Realization, Normalize([Evidence(ApiContract)]));
        Assert.False(result.IsReady);
        Assert.Contains(State, result.FindDecision(ApiNode)!.UnknownDependencies);
    }

    [Theory]
    [InlineData(-301, "stale")]
    [InlineData(3, "future")]
    public void Freshness_remains_owned_by_shared_boundary(int seconds, string reason)
    {
        var evidence = Evidence(StateContract, Now.AddSeconds(seconds));
        var result = Assert.Single(Normalize([evidence]));
        Assert.Equal(evidence.Observation.ObservedAtUtc, result.ObservedAtUtc);
        Assert.Equal(ExecutionReadinessStatus.Unknown, result.Readiness);
        Assert.Equal("infra.azure.observation." + reason, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Duplicate_or_conflicting_results_and_contracts_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Normalize([Evidence(StateContract), Evidence(StateContract)]));
        Assert.Throws<ArgumentException>(() => AzureInfrastructureRuntimeObservations.Normalize(Realization, Scope, Scope,
            [StateContract, StateContract], [], Now, TimeSpan.FromMinutes(5), TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => Normalize([Evidence(StateContract) with { Observation = Evidence(ApiContract).Observation }]));
    }

    [Fact]
    public void Deserialization_does_not_bypass_admission()
    {
        var item = JsonSerializer.Deserialize<AzureInfrastructureRuntimeEvidence>(JsonSerializer.Serialize(Evidence(StateContract)))!;
        Assert.Equal(ExecutionReadinessStatus.Ready, Assert.Single(Normalize([item])).Readiness);
        Assert.Throws<ArgumentException>(() => Normalize([item with { Contract = item.Contract with { Deployment = Source } }]));
    }

    [Fact]
    public void Incomplete_expectations_are_rejected_even_without_results()
    {
        foreach (var contract in new[] { StateContract with { Producer = default }, StateContract with { CheckContract = default },
            StateContract with { Deployment = default }, StateContract with { Binding = StateBinding with { ResourceId = "wrong" } } })
            Assert.Throws<ArgumentException>(() => AzureInfrastructureRuntimeObservations.Normalize(Realization, Scope, Scope,
                [contract], [], Now, TimeSpan.FromMinutes(5), TimeSpan.Zero));
    }

    [Theory]
    [InlineData(ExecutionHealthStatus.Unhealthy, ExecutionReadinessStatus.NotReady)]
    [InlineData(ExecutionHealthStatus.Unknown, ExecutionReadinessStatus.Unknown)]
    public void Runtime_failure_is_not_overwritten_by_healthy_subject(ExecutionHealthStatus health, ExecutionReadinessStatus readiness)
    {
        var child = Evidence(StateContract) with { Observation = new(State, health, readiness, Now, [Source]) };
        Assert.False(InfrastructureReadinessEvaluator.Assess(Realization, Normalize([Evidence(ApiContract), child])).IsReady);
    }

    [Fact]
    public void Raw_diagnostics_are_rejected_without_disclosure()
    {
        var diagnostic = new DocumentValidationDiagnostic(Code: "provider", Severity: DiagnosticSeverity.Error, Message: "private-payload");
        var item = Evidence(StateContract) with { Observation = new(State, ExecutionHealthStatus.Healthy,
            ExecutionReadinessStatus.Ready, Now, [Source], [diagnostic]) };
        var error = Assert.Throws<ArgumentException>(() => Normalize([item]));
        Assert.DoesNotContain("private-payload", error.Message);
    }

    static AzureInfrastructureRuntimeContract Contract(AzureInfrastructureObservationBinding binding) => new(binding,
        SourceReference.Create("runtime-producer", "synthetic/v1"), SourceReference.Create("runtime-check", "complete-resource-admission/v1"),
        SourceReference.Create("application-deployment", "exact-build-1"));

    static AzureInfrastructureRuntimeEvidence Evidence(AzureInfrastructureRuntimeContract contract, DateTimeOffset? time = null) =>
        new(contract, new(contract.Binding.PhysicalResource, ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready, time ?? Now, [Source]));

    static ImmutableArray<InfrastructureResourceObservation> Normalize(ImmutableArray<AzureInfrastructureRuntimeEvidence> evidence,
        AzureInfrastructureObservationScope? scope = null) => AzureInfrastructureRuntimeObservations.Normalize(
            Realization, Scope, scope ?? Scope, [ApiContract, StateContract], evidence, Now, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
}
