using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Azure.Infra;
using Cohesive.Execution;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Azure.Infra.Tests;

public sealed class AzureInfrastructureObservationsTests
{
    static readonly Guid Subscription = Guid.Parse("11111111-1111-1111-1111-111111111111");
    static readonly Guid Tenant = Guid.Parse("22222222-2222-2222-2222-222222222222");
    static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    static readonly InfrastructurePhysicalResourceId Api = new("azure/app-service/sites/api");
    static readonly InfrastructurePhysicalResourceId State = new("azure/cosmos/db/state");
    static readonly InfrastructureNodeId ApiNode = new("workloads/api");
    static readonly SourceReference Source = SourceReference.Create("test", "pinned-fixture/v1");
    static readonly InfrastructureRealization Realization = CreateRealization();
    static readonly AzureInfrastructureObservationScope Scope = new("dev", Tenant, Subscription, Realization.ToReference(), SourceReference.Create("handoff", "sha256/exact-v1"));
    static readonly AzureInfrastructureObservationBinding ApiBinding = Binding(Api, "Microsoft.Web/sites/api");
    static readonly AzureInfrastructureObservationBinding StateBinding = Binding(State, "Microsoft.DocumentDB/databaseAccounts/account/sqlDatabases/state");

    [Fact]
    public void Fresh_runtime_evidence_uses_existing_evaluator_and_retains_identity_time_and_provenance()
    {
        var observations = Normalize([Evidence(StateBinding), Evidence(ApiBinding)]);
        Assert.True(InfrastructureReadinessEvaluator.Assess(Realization, observations).IsReady);
        Assert.Equal(Now, observations[0].ObservedAtUtc);
        Assert.Contains(Scope.Handoff, observations[0].SourceReferences);
        Assert.Contains(Source, observations[0].SourceReferences);
        Assert.Equal(observations.OrderBy(x => x.PhysicalResource.Value, StringComparer.Ordinal), observations);
        var reversed = Normalize([Evidence(ApiBinding), Evidence(StateBinding)]);
        Assert.True(observations.SequenceEqual(reversed));
    }

    [Theory]
    [InlineData(AzureInfrastructureEvidenceKind.Provisioning, "provisioningOnly")]
    [InlineData(AzureInfrastructureEvidenceKind.CollectionFailed, "collectionFailed")]
    public void Non_runtime_evidence_cannot_claim_ready_or_unhealthy(AzureInfrastructureEvidenceKind kind, string reason)
    {
        var result = Normalize([Evidence(ApiBinding), Evidence(StateBinding) with { Kind = kind }]);
        var state = result.Single(x => x.PhysicalResource == State);
        Assert.Equal(ExecutionReadinessStatus.Unknown, state.Readiness);
        Assert.Equal(ExecutionHealthStatus.Unknown, state.Health);
        Assert.Equal("infra.azure.observation." + reason, Assert.Single(state.Diagnostics).Code);
        Assert.False(InfrastructureReadinessEvaluator.Assess(Realization, result).FindDecision(ApiNode)!.IsReady);
    }

    [Theory]
    [InlineData(-301, "stale")]
    [InlineData(3, "future")]
    public void Stale_or_future_evidence_fails_closed_without_retimestamping(int offset, string reason)
    {
        var time = Now.AddSeconds(offset);
        var result = Normalize([Evidence(StateBinding, time)]);
        var state = Assert.Single(result);
        Assert.Equal(time, state.ObservedAtUtc);
        Assert.Equal(ExecutionReadinessStatus.Unknown, state.Readiness);
        Assert.Equal("infra.azure.observation." + reason, Assert.Single(state.Diagnostics).Code);
    }

    [Theory]
    [InlineData(-300)]
    [InlineData(2)]
    public void Exact_age_and_clock_tolerance_boundaries_are_admitted(int offset) =>
        Assert.Equal(ExecutionReadinessStatus.Ready, Assert.Single(Normalize([Evidence(StateBinding, Now.AddSeconds(offset))])).Readiness);

    [Fact]
    public void Missing_dependency_remains_missing_instead_of_synthesizing_readiness()
    {
        var assessment = InfrastructureReadinessEvaluator.Assess(Realization, Normalize([Evidence(ApiBinding)]));
        Assert.False(assessment.FindDecision(ApiNode)!.IsReady);
        Assert.Contains(State, assessment.FindDecision(ApiNode)!.UnknownDependencies);
    }

    [Theory]
    [InlineData(ExecutionHealthStatus.Unhealthy, ExecutionReadinessStatus.NotReady)]
    [InlineData(ExecutionHealthStatus.Unknown, ExecutionReadinessStatus.Unknown)]
    public void Runtime_dependency_failure_or_uncertainty_blocks_an_exposed_api(ExecutionHealthStatus health, ExecutionReadinessStatus readiness)
    {
        var state = Evidence(StateBinding) with { Observation = new(State, health, readiness, Now, [Source]) };
        var assessment = InfrastructureReadinessEvaluator.Assess(Realization, Normalize([Evidence(ApiBinding), state]));
        Assert.False(assessment.FindDecision(ApiNode)!.IsReady);
    }

    [Fact]
    public void All_scope_dimensions_are_fenced()
    {
        foreach (var scope in new[] {
            Scope with { Environment = "prod" }, Scope with { Tenant = Guid.NewGuid() },
            Scope with { Subscription = Guid.NewGuid() }, Scope with { Handoff = Source },
            Scope with { Realization = CreateRealization("other").ToReference() } })
            Assert.Throws<ArgumentException>(() => Normalize([], scope));
        Assert.Throws<ArgumentException>(() => AzureInfrastructureObservations.Normalize(CreateRealization("other"), Scope, Scope,
            [ApiBinding], [], Now, TimeSpan.FromMinutes(5), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("azure/app-service/sites/api")]
    [InlineData("/subscriptions/33333333-3333-3333-3333-333333333333/resourceGroups/rg/providers/Microsoft.Web/sites/api")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/..")]
    [InlineData("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Web/sites/api?secret=x")]
    public void Invalid_or_wrong_subscription_native_identity_is_rejected(string id) =>
        Assert.Throws<ArgumentException>(() => Normalize([], bindings: [ApiBinding with { ResourceId = id }]));

    [Fact]
    public void Binding_or_observation_identity_mismatch_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Normalize([Evidence(ApiBinding with { ResourceId = ApiBinding.ResourceId + "/slots/other" })]));
        Assert.Throws<ArgumentException>(() => Normalize([Evidence(ApiBinding) with { Observation = Evidence(StateBinding).Observation }]));
        Assert.Throws<ArgumentException>(() => Normalize([], bindings: [ApiBinding with { PhysicalResource = new("unrealized") }]));
        Assert.Throws<ArgumentException>(() => Normalize([Evidence(ApiBinding with { Source = Scope.Handoff })]));
    }

    [Fact]
    public void Duplicate_bindings_and_observations_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Normalize([], bindings: [ApiBinding, ApiBinding]));
        Assert.Throws<ArgumentException>(() => Normalize([Evidence(ApiBinding), Evidence(ApiBinding)]));
    }

    [Fact]
    public void Provider_diagnostics_are_not_forwarded_and_unknown_classification_is_rejected()
    {
        var secret = new DocumentValidationDiagnostic(Code: "provider", Severity: DiagnosticSeverity.Error, Message: "secret-token");
        var item = Evidence(ApiBinding) with { Observation = new(Api, ExecutionHealthStatus.Unknown, ExecutionReadinessStatus.Unknown, Now, [Source], [secret]) };
        var error = Assert.Throws<ArgumentException>(() => Normalize([item]));
        Assert.DoesNotContain("secret-token", error.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => Normalize([Evidence(ApiBinding) with { Kind = (AzureInfrastructureEvidenceKind)100 }]));
    }

    [Fact]
    public void Invalid_time_policy_and_uninitialized_collections_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AzureInfrastructureObservations.Normalize(Realization, Scope, Scope, [], [], Now, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => AzureInfrastructureObservations.Normalize(Realization, Scope, Scope, default, [], Now, TimeSpan.FromMinutes(5), TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() => AzureInfrastructureObservations.Normalize(Realization, Scope, Scope, [], [], Now.ToOffset(TimeSpan.FromHours(1)), TimeSpan.FromMinutes(5), TimeSpan.Zero));
    }

    [Fact]
    public void Payload_free_evidence_roundtrips_and_is_revalidated()
    {
        var value = Evidence(StateBinding);
        var json = JsonSerializer.Serialize(value);
        var restored = JsonSerializer.Deserialize<AzureInfrastructureEvidence>(json)!;
        Assert.True(Normalize([value]).SequenceEqual(Normalize([restored])));
        Assert.Throws<ArgumentException>(() => Normalize([restored with { Binding = restored.Binding with { ResourceId = "wrong" } }]));
    }

    static ImmutableArray<InfrastructureResourceObservation> Normalize(ImmutableArray<AzureInfrastructureEvidence> evidence,
        AzureInfrastructureObservationScope? observedScope = null, ImmutableArray<AzureInfrastructureObservationBinding> bindings = default) =>
        AzureInfrastructureObservations.Normalize(Realization, Scope, observedScope ?? Scope,
            bindings.IsDefault ? [ApiBinding, StateBinding] : bindings, evidence, Now, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));

    static AzureInfrastructureObservationBinding Binding(InfrastructurePhysicalResourceId physical, string resource) =>
        new(physical, $"/subscriptions/{Subscription:D}/resourceGroups/rg/providers/{resource}", Source);

    static AzureInfrastructureEvidence Evidence(AzureInfrastructureObservationBinding binding, DateTimeOffset? observed = null) =>
        new(binding, AzureInfrastructureEvidenceKind.Runtime,
            new(binding.PhysicalResource, ExecutionHealthStatus.Healthy, ExecutionReadinessStatus.Ready, observed ?? Now, [Source]));

    static InfrastructureRealization CreateRealization(string version = "v1")
    {
        InfrastructureNodeId stateNode = new("resources/state");
        var definition = Infrastructure.Define(new("readiness"), new(version), b => {
            var state = b.Resource(stateNode).Persistent(); b.Workload(ApiNode).RequiresReady(state);
        });
        InfrastructureCapabilityVariantId variant = new("azure");
        var profile = new InfrastructureCapabilityProfile(InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new("profiles/azure/v1"), new("azure"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], [new(variant)]);
        var closure = InfrastructureCapabilityCompiler.Compile(definition, profile, variant);
        var lifecycle = new InfrastructureLifecyclePlan(definition,
            [new(stateNode, State, new("azure"), new("pulumi/test/dev"), InfrastructureLifecycleDisposition.Managed)]);
        return InfrastructureRealizationCompiler.Compile(closure, lifecycle, [new(ApiNode, Api, new("azure"), [Source])]);
    }
}
