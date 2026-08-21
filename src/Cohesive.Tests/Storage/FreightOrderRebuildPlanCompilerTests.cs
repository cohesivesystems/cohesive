using System.Collections.Immutable;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Materialize;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class FreightOrderRebuildPlanCompilerTests
{
    [Fact]
    public void ProviderPlansAreDeterministicAcrossTenantInputOrder()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        var postgresPhysical = Program.CreateProviderPlan(Program.ProviderKind.Postgres, semantics);
        var target = Target(semantics, "postgres");
        var forwardBindings = Bindings(semantics, "postgres", postgresPhysical, ["acme", "northwind"]);
        var reverseBindings = Bindings(semantics, "postgres", postgresPhysical, ["northwind", "acme"]);

        var forward = FreightOrderRebuildPlanCompiler.Compile(
            semantics: semantics,
            provider: "postgres",
            target: target,
            tenantBindings: forwardBindings,
            impactPlan: postgresPhysical.ImpactPlan);
        var reverse = FreightOrderRebuildPlanCompiler.Compile(
            semantics: semantics,
            provider: "postgres",
            target: target,
            tenantBindings: reverseBindings,
            impactPlan: postgresPhysical.ImpactPlan);

        Assert.Equal(forward.Request.Fingerprint, reverse.Request.Fingerprint);
        Assert.Equal(forward.Membership.Fingerprint, reverse.Membership.Fingerprint);
        Assert.Equal(forward.Placement.Fingerprint, reverse.Placement.Fingerprint);
        Assert.Equal(forward.Plan.Fingerprint, reverse.Plan.Fingerprint);
        Assert.Equal(forward.PlanSet.Fingerprint, reverse.PlanSet.Fingerprint);
        Assert.Equal(
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlanSetBytes(forward.PlanSet),
            MaterializationRebuildPlanningJsonSerializer.GetCanonicalPlanSetBytes(reverse.PlanSet));
        Assert.Equal(["tenant/acme", "tenant/northwind"], forward.Plan.Shards.Select(static shard => shard.Id.Value));
        Assert.Equal(12, forward.Plan.ChangeFeeds.Length);
        Assert.Equal(6, forward.Plan.ImpactPlan.Routes.Length);
        Assert.All(
            forward.Plan.ImpactPlan.Routes,
            static route => Assert.True(route.Strategy is
                MaterializationDirectRootImpactStrategy or MaterializationInverseTraversalImpactStrategy));
        Assert.Single(forward.PlanSet.LeafPlans);
        Assert.Equal(target.Id, forward.Plan.Target.Id);
    }

    [Fact]
    public void ProviderInterpretationsShareCanonicalSemanticsButRetainDistinctPhysicalEvidence()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        var postgresPhysical = Program.CreateProviderPlan(Program.ProviderKind.Postgres, semantics);
        var cosmosPhysical = Program.CreateProviderPlan(Program.ProviderKind.Cosmos, semantics);

        var postgres = FreightOrderRebuildPlanCompiler.Compile(
            semantics: semantics,
            provider: "postgres",
            target: Target(semantics, "postgres"),
            tenantBindings: Bindings(semantics, "postgres", postgresPhysical, ["acme", "northwind"]),
            impactPlan: postgresPhysical.ImpactPlan);
        var cosmos = FreightOrderRebuildPlanCompiler.Compile(
            semantics: semantics,
            provider: "cosmos",
            target: Target(semantics, "cosmos"),
            tenantBindings: Bindings(semantics, "cosmos", cosmosPhysical, ["acme", "northwind"]),
            impactPlan: cosmosPhysical.ImpactPlan);

        Assert.Equal(semantics.DefinitionFingerprint, postgres.Plan.Materialization.DefinitionFingerprint);
        Assert.Equal(semantics.DefinitionFingerprint, cosmos.Plan.Materialization.DefinitionFingerprint);
        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                postgres.Plan.Materialization.Definition.Relation.CompiledPlan),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                cosmos.Plan.Materialization.Definition.Relation.CompiledPlan));
        Assert.NotEqual(
            postgres.Plan.Shards[0].HydrationPhysicalPlan,
            cosmos.Plan.Shards[0].HydrationPhysicalPlan);
        Assert.NotEqual(postgres.Plan.Target.Id, cosmos.Plan.Target.Id);
        Assert.NotEqual(postgres.Plan.Sources[0].Source, cosmos.Plan.Sources[0].Source);
        Assert.NotEqual(postgres.Plan.Fingerprint, cosmos.Plan.Fingerprint);
    }

    [Fact]
    public void MixedTenantBindingIsRejectedBeforeSourceIo()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        var physical = Program.CreateProviderPlan(Program.ProviderKind.Postgres, semantics);
        var valid = Bindings(semantics, "postgres", physical, ["acme", "northwind"]);
        var mixed = new FreightOrderRebuildTenantBinding(
            tenant: "acme",
            rootRead: valid[1].RootRead,
            hydrator: valid[1].Hydrator,
            sourceBindings: valid[1].Sources,
            impactRuntimeFactory: impactPlan => new TestImpactRuntime(impactPlan.Fingerprint));

        var exception = Assert.Throws<ArgumentException>(() => FreightOrderRebuildPlanCompiler.Compile(
            semantics: semantics,
            provider: "postgres",
            target: Target(semantics, "postgres"),
            tenantBindings: [mixed, valid[1]],
            impactPlan: physical.ImpactPlan));

        Assert.Contains("another logical partition", exception.Message, StringComparison.Ordinal);
        Assert.All(
            mixed.Sources.Select(static source => Assert.IsType<CountingReader>(source.Source.Descriptor.RelationReader)),
            static reader => Assert.Equal(0, reader.ReadCount));
    }

    [Fact]
    public void MixedProviderBindingIsRejectedBeforeSourceIo()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        var postgresPhysical = Program.CreateProviderPlan(Program.ProviderKind.Postgres, semantics);
        var cosmosPhysical = Program.CreateProviderPlan(Program.ProviderKind.Cosmos, semantics);
        var postgres = Bindings(semantics, "postgres", postgresPhysical, ["acme", "northwind"]);
        var cosmos = Bindings(semantics, "cosmos", cosmosPhysical, ["northwind"]);

        var exception = Assert.Throws<ArgumentException>(() => FreightOrderRebuildPlanCompiler.Compile(
            semantics: semantics,
            provider: "postgres",
            target: Target(semantics, "postgres"),
            tenantBindings: [postgres[0], cosmos[0]],
            impactPlan: postgresPhysical.ImpactPlan));

        Assert.Contains("changes capability evidence", exception.Message, StringComparison.Ordinal);
        Assert.All(
            cosmos[0].Sources.Select(static source => Assert.IsType<CountingReader>(source.Source.Descriptor.RelationReader)),
            static reader => Assert.Equal(0, reader.ReadCount));
    }

    static ImmutableArray<FreightOrderRebuildTenantBinding> Bindings(
        FreightOrderMaterializationSemantics semantics,
        string provider,
        Program.ProviderPlan physical,
        ImmutableArray<string> tenants)
    {
        var profileBySource = Profiles(semantics, provider, physical);
        return
        [
            .. tenants.Select(tenant =>
            {
                var logicalPartition = FreightOrderRebuildPlanCompiler.LogicalPartition(tenant);
                var bindings = semantics.Definition.Sources.Select(requirement =>
                {
                    var isRoot = requirement.Input == semantics.Root.Input.Id;
                    var placement = isRoot
                        ? physical.ScanRoot
                        : physical.HydrationPlacement.Bindings.Single(candidate =>
                            candidate.Input == requirement.Input);
                    var physicalPlan = isRoot
                        ? physical.ScanPhysicalPlan.Fingerprint
                        : physical.HydrationPhysicalPlan.Fingerprint;
                    var sourceInstance = (isRoot ? physical.ScanPlacement : physical.HydrationPlacement)
                        .SourceInstances.Single(candidate => candidate.Id == placement.Source);
                    var reader = new CountingReader(new(
                        source: placement.Source,
                        executionDomain: sourceInstance.ExecutionDomain,
                        targetProfile: sourceInstance.TargetProfile,
                        logicalPartition: logicalPartition,
                        partitionBinding: new("tenantId")));
                    var source = new InMemoryMaterializationSource(new(
                        relationReader: reader,
                        capabilityProfile: profileBySource[placement.Source]));
                    return new FreightOrderRebuildSourceBinding(
                        input: requirement.Input,
                        scope: new(
                            physicalPlan: physicalPlan,
                            placement: placement,
                            logicalPartition: logicalPartition,
                            partition: new($"{provider}/{tenant}/{Uri.EscapeDataString(requirement.Input.Value)}"),
                            orderingScope: new($"{provider}/{tenant}/{Uri.EscapeDataString(requirement.Input.Value)}/ordering")),
                        source: source);
                }).ToImmutableArray();
                return new FreightOrderRebuildTenantBinding(
                    tenant: tenant,
                    rootRead: Program.CreateRootRead(physical, semantics.Root),
                    hydrator: new TestHydrator(
                        plan: RelationQueryCompiledPlanReference.From(semantics.Plan),
                        physicalPlan: physical.HydrationPhysicalPlan.Fingerprint),
                    sourceBindings: bindings,
                    impactRuntimeFactory: impactPlan => new TestImpactRuntime(impactPlan.Fingerprint));
            })
        ];
    }

    static ImmutableDictionary<RelationQuerySourceInstanceId, MaterializationCapabilityProfile> Profiles(
        FreightOrderMaterializationSemantics semantics,
        string provider,
        Program.ProviderPlan physical)
    {
        var requirementsBySource = semantics.Definition.Sources
            .GroupBy(requirement => physical.HydrationPlacement.Bindings
                .Single(binding => binding.Input == requirement.Input).Source);
        return requirementsBySource.ToImmutableDictionary(
            static group => group.Key,
            group => new MaterializationCapabilityProfile(
                id: new($"materialization-harness/tests/{provider}/{Uri.EscapeDataString(group.Key.Value)}/v1"),
                role: MaterializationEndpointRole.Source,
                subject: group.Key.Value,
                evidence:
                [
                    .. group.SelectMany(static source => source.Capabilities)
                        .GroupBy(static requirement => requirement.Capability)
                        .Select(capabilities => Evidence(
                            $"source/{Uri.EscapeDataString(group.Key.Value)}/{(int)capabilities.Key}",
                            capabilities.Key,
                            [.. capabilities.SelectMany(static requirement => requirement.Guarantees).Distinct()],
                            MergeLimits(capabilities.SelectMany(static requirement => requirement.OperatingLimits))))
                ],
                description: "Deterministic synthetic source capability profile for canonical freight rebuild planning."));
    }

    static MaterializationTargetDescriptor Target(
        FreightOrderMaterializationSemantics semantics,
        string provider)
    {
        MaterializationTargetId targetId = new($"freight-order-search/{provider}");
        var profile = new MaterializationCapabilityProfile(
            id: new($"materialization-harness/tests/elastic/{provider}/v1"),
            role: MaterializationEndpointRole.Target,
            subject: targetId.Value,
            evidence:
            [
                .. semantics.Definition.TargetCapabilities
                    .GroupBy(static requirement => requirement.Capability)
                    .Select(capabilities => Evidence(
                        $"target/{(int)capabilities.Key}",
                        capabilities.Key,
                        [.. capabilities.SelectMany(static requirement => requirement.Guarantees).Distinct()],
                        MergeLimits(capabilities.SelectMany(static requirement => requirement.OperatingLimits))))
            ]);
        return new(targetId, semantics.Definition.Id, profile);
    }

    static MaterializationCapabilityEvidence Evidence(
        string id,
        MaterializationCapabilityKind capability,
        ImmutableArray<MaterializationGuaranteeKind> guarantees,
        ImmutableArray<MaterializationOperatingLimit> limits) => new(
        id: new(id),
        capability: capability,
        realization: CapabilityRealizationKind.Native,
        guarantees: guarantees,
        operatingLimits: limits,
        sourceReferences: ["tests/freight-order-rebuild-plan-compiler/v1"]);

    static ImmutableArray<MaterializationOperatingLimit> MergeLimits(
        IEnumerable<MaterializationOperatingLimit> limits) =>
    [
        .. limits
            .GroupBy(static limit => limit.Kind)
            .Select(static group => new MaterializationOperatingLimit(
                kind: group.Key,
                maximum: group.Max(static limit => limit.Maximum)))
    ];

    sealed class CountingReader(RelationQuerySourceReaderDescriptor descriptor) : IRelationQuerySourceReader
    {
        public int ReadCount { get; private set; }

        public RelationQuerySourceReaderDescriptor Descriptor { get; } = descriptor;

        public ValueTask<RelationQuerySourceReadResult> ReadAsync(
            RelationQuerySourceReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(new RelationQuerySourceReadResult(
                state: RelationQuerySourceReadState.Complete,
                observations: [],
                evidenceReference: "tests/freight-order-rebuild-plan-compiler/empty"));
        }
    }

    sealed class TestHydrator(
        RelationQueryCompiledPlanReference plan,
        RelationQueryPhysicalPlanFingerprint physicalPlan) : IMaterializationRebuildHydrator
    {
        public RelationQueryCompiledPlanReference Plan { get; } = plan;

        public RelationQueryPhysicalPlanFingerprint PhysicalPlan { get; } = physicalPlan;

        public ValueTask<MaterializationRebuildHydrationResult> HydrateAsync(
            OperationContext context,
            MaterializationRebuildHydrationRequest request)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            context.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new MaterializationRebuildHydrationResult(
                rows: [],
                evidenceReference: "tests/freight-order-rebuild-plan-compiler/empty"));
        }
    }

    sealed class TestImpactRuntime(MaterializationImpactPlanFingerprint impactPlan) : IMaterializationImpactRuntime
    {
        public MaterializationImpactPlanFingerprint ImpactPlan { get; } = impactPlan;

        public ValueTask<ImmutableArray<MaterializationAffectedRoot>> ResolveRootsAsync(
            OperationContext context,
            MaterializationImpactRootResolutionRequest request) =>
            throw new NotSupportedException("The planning test does not execute impact reads.");

        public ValueTask<ImmutableArray<MaterializationRootProjection>> HydrateAsync(
            OperationContext context,
            MaterializationImpactHydrationRequest request) =>
            throw new NotSupportedException("The planning test does not execute impact hydration.");
    }
}
