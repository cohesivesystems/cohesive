using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Materialize;

/// <summary>Creates one tenant/provider physical impact runtime for an exact persisted impact plan.</summary>
/// <param name="impactPlan">Exact plan whose inverse reads and canonical hydration are bound.</param>
/// <returns>A runtime implementing the plan fingerprint.</returns>
/// <exception cref="ArgumentNullException"><paramref name="impactPlan"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException">The plan differs from the runtime's canonical freight semantics.</exception>
public delegate IMaterializationImpactRuntime FreightOrderMaterializationImpactRuntimeFactory(
    MaterializationImpactPlan impactPlan);

/// <summary>One exact provider source binding for a canonical freight acquisition input and tenant scope.</summary>
public sealed class FreightOrderRebuildSourceBinding
{
    /// <summary>Creates one exact source binding.</summary>
    /// <param name="input">Canonical Relations acquisition input implemented by the source.</param>
    /// <param name="scope">Exact provider physical-plan, placement, tenant partition, and ordering scope.</param>
    /// <param name="source">Paged baseline and positioned change source implementing the binding.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> or <paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentException">The input or physical source identity differs from the exact scope.</exception>
    public FreightOrderRebuildSourceBinding(
        RelationQueryInputId input,
        MaterializationSourceScope scope,
        IMaterializationPullChangeSource source)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (scope.Input != input)
            throw new ArgumentException("A freight source binding must retain its exact acquisition input.", nameof(scope));
        if (scope.Source != source.Descriptor.Source
            || scope.LogicalPartition != source.Descriptor.RelationReader.Descriptor.LogicalPartition)
        {
            throw new ArgumentException(
                "A freight source binding must retain its exact physical source and logical partition.",
                nameof(source));
        }
        Input = input;
    }

    /// <summary>Canonical Relations acquisition input implemented by the source.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Exact provider physical-plan, placement, tenant partition, and ordering scope.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Paged baseline and positioned change source implementing the binding.</summary>
    public IMaterializationPullChangeSource Source { get; }
}

/// <summary>Complete exact provider runtime evidence for one freight tenant rebuild shard.</summary>
public sealed class FreightOrderRebuildTenantBinding
{
    readonly ImmutableDictionary<RelationQueryInputId, FreightOrderRebuildSourceBinding> sources;
    readonly FreightOrderMaterializationImpactRuntimeFactory impactRuntimeFactory;

    /// <summary>Creates one tenant binding covering every canonical acquisition input exactly once.</summary>
    /// <param name="tenant">Stable tenant identity.</param>
    /// <param name="rootRead">Exact bounded root enumeration used by this tenant shard.</param>
    /// <param name="hydrator">Exact canonical relation hydration interpretation.</param>
    /// <param name="sourceBindings">One source binding for every canonical acquisition input.</param>
    /// <param name="impactRuntimeFactory">Exact provider inverse-read and canonical hydration runtime factory.</param>
    /// <exception cref="ArgumentNullException">A required runtime binding is null.</exception>
    /// <exception cref="ArgumentException">An identity is absent, repeated, or inconsistent.</exception>
    public FreightOrderRebuildTenantBinding(
        string tenant,
        RelationQuerySourceReadRequest rootRead,
        IMaterializationRebuildHydrator hydrator,
        IEnumerable<FreightOrderRebuildSourceBinding> sourceBindings,
        FreightOrderMaterializationImpactRuntimeFactory impactRuntimeFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        Tenant = tenant;
        RootRead = rootRead ?? throw new ArgumentNullException(nameof(rootRead));
        Hydrator = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
        ArgumentNullException.ThrowIfNull(sourceBindings);
        var normalized = sourceBindings.ToArray();
        if (normalized.Any(static binding => binding is null))
            throw new ArgumentException("Tenant source bindings cannot contain null entries.", nameof(sourceBindings));
        if (normalized.GroupBy(static binding => binding.Input).Any(static group => group.Skip(1).Any()))
            throw new ArgumentException("A tenant source catalog cannot repeat an acquisition input.", nameof(sourceBindings));
        sources = normalized.ToImmutableDictionary(static binding => binding.Input);
        this.impactRuntimeFactory = Guard.RequireNotNull(impactRuntimeFactory);
    }

    /// <summary>Stable tenant identity.</summary>
    public string Tenant { get; }

    /// <summary>Exact bounded root enumeration used by this tenant shard.</summary>
    public RelationQuerySourceReadRequest RootRead { get; }

    /// <summary>Exact canonical relation hydration interpretation.</summary>
    public IMaterializationRebuildHydrator Hydrator { get; }

    /// <summary>Source bindings in canonical acquisition-input order.</summary>
    public ImmutableArray<FreightOrderRebuildSourceBinding> Sources =>
        [.. sources.Values.OrderBy(static binding => binding.Input.Value, StringComparer.Ordinal)];

    /// <summary>Gets the exact source binding for one canonical acquisition input.</summary>
    /// <param name="input">Canonical acquisition input.</param>
    /// <returns>The exact tenant/provider source binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is absent.</exception>
    public FreightOrderRebuildSourceBinding GetSource(RelationQueryInputId input) => sources[input];

    internal IMaterializationImpactRuntime CreateImpactRuntime(MaterializationImpactPlan impactPlan) =>
        impactRuntimeFactory(impactPlan);
}

/// <summary>Canonical single-provider plan-set artifacts and their exact tenant runtime bindings.</summary>
public sealed class FreightOrderRebuildPlanCompilation
{
    readonly ImmutableDictionary<MaterializationRebuildShardId, FreightOrderRebuildTenantBinding> tenantsByShard;
    readonly ImmutableDictionary<MaterializationChangeFeedId, IMaterializationPullChangeSource> sourcesByFeed;
    readonly ImmutableDictionary<MaterializationChangeFeedId, IMaterializationImpactRuntime> impactRuntimesByFeed;

    internal FreightOrderRebuildPlanCompilation(
        string provider,
        MaterializationRebuildRequestDocument request,
        MaterializationRebuildMembershipEvidence membership,
        MaterializationTargetPlacementPlan placement,
        MaterializationRebuildPlan plan,
        MaterializationRebuildPlanSet planSet,
        ImmutableDictionary<MaterializationRebuildShardId, FreightOrderRebuildTenantBinding> tenantsByShard,
        ImmutableDictionary<MaterializationChangeFeedId, IMaterializationPullChangeSource> sourcesByFeed,
        ImmutableDictionary<MaterializationChangeFeedId, IMaterializationImpactRuntime> impactRuntimesByFeed)
    {
        Provider = provider;
        Request = request;
        Membership = membership;
        Placement = placement;
        Plan = plan;
        PlanSet = planSet;
        this.tenantsByShard = tenantsByShard;
        this.sourcesByFeed = sourcesByFeed;
        this.impactRuntimesByFeed = impactRuntimesByFeed;
    }

    /// <summary>Stable provider interpretation identity.</summary>
    public string Provider { get; }

    /// <summary>Canonical rebuild request.</summary>
    public MaterializationRebuildRequestDocument Request { get; }

    /// <summary>Complete frozen tenant membership.</summary>
    public MaterializationRebuildMembershipEvidence Membership { get; }

    /// <summary>Canonical tenant-to-target placement.</summary>
    public MaterializationTargetPlacementPlan Placement { get; }

    /// <summary>Single provider leaf plan.</summary>
    public MaterializationRebuildPlan Plan { get; }

    /// <summary>Linked single-leaf plan-set authority.</summary>
    public MaterializationRebuildPlanSet PlanSet { get; }

    /// <summary>Resolves the portable plan against exact runtime target, progress, and impact interpretations.</summary>
    /// <param name="target">Runtime target matching the persisted target descriptor.</param>
    /// <param name="progressStore">Durable application-progress authority.</param>
    /// <param name="controlRuntimeProvider">
    /// Durable Control runtime provider implementing this exact plan's declared safe-point policy.
    /// </param>
    /// <returns>A fully validated runtime rebuild plan.</returns>
    /// <exception cref="ArgumentNullException">A required runtime dependency is null.</exception>
    /// <exception cref="ArgumentException">Any runtime dependency differs from persisted plan evidence.</exception>
    public ResolvedMaterializationRebuildPlan Resolve(
        IMaterializationTarget target,
        IMaterializationProgressStore progressStore,
        MaterializationIndexSyncControlRuntimeProvider? controlRuntimeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(progressStore);
        var shards = Plan.Shards.Select(shard =>
        {
            var tenant = tenantsByShard[shard.Id];
            return new MaterializationRebuildShardBinding(
                shard: shard,
                source: tenant.GetSource(shard.Scope.Input).Source,
                hydrator: tenant.Hydrator);
        });
        var feeds = Plan.ChangeFeeds.Select(feed => new MaterializationChangeFeedBinding(
            feed: feed,
            channel: feed.Channel,
            source: sourcesByFeed[feed.Id],
            interpreter: new(
                plan: Plan.ImpactPlan,
                definition: Plan.Materialization.Definition,
                runtime: impactRuntimesByFeed[feed.Id])));
        return new(
            planSet: PlanSet,
            plan: Plan,
            target: target,
            progressStore: progressStore,
            shardBindings: shards,
            changeFeedBindings: feeds,
            controlRuntimeProvider: controlRuntimeProvider);
    }
}

/// <summary>Compiles the canonical freight materialization into one tenant-sharded provider leaf and plan set.</summary>
public static class FreightOrderRebuildPlanCompiler
{
    const int MaximumPageItems = 2;
    const long MaximumPageBytes = 1 * 1024 * 1024;
    const int MaximumBulkItems = 16;
    const long MaximumBulkBytes = 1 * 1024 * 1024;

    /// <summary>Compiles canonical planning artifacts and verifies every exact tenant/provider runtime binding.</summary>
    /// <param name="semantics">Canonical provider-neutral freight semantics.</param>
    /// <param name="provider">Stable provider interpretation identity.</param>
    /// <param name="target">Exact generational index target descriptor for this provider interpretation.</param>
    /// <param name="tenantBindings">Complete tenant runtime bindings; input order is immaterial.</param>
    /// <param name="impactPlan">Exact persisted impact plan already bound by every tenant runtime.</param>
    /// <returns>Linked canonical plan-set artifacts with exact runtime source bindings.</returns>
    /// <exception cref="ArgumentNullException">A required artifact or collection is null.</exception>
    /// <exception cref="ArgumentException">Tenant, source, profile, scope, or relation-plan evidence is incomplete or inconsistent.</exception>
    /// <exception cref="InvalidOperationException">A canonical compiler rejects the supplied evidence.</exception>
    public static FreightOrderRebuildPlanCompilation Compile(
        FreightOrderMaterializationSemantics semantics,
        string provider,
        MaterializationTargetDescriptor target,
        IEnumerable<FreightOrderRebuildTenantBinding> tenantBindings,
        MaterializationImpactPlan impactPlan)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tenantBindings);
        ArgumentNullException.ThrowIfNull(impactPlan);
        _ = MaterializationImpactPlanLinker.Link(impactPlan, semantics.Definition);
        var tenants = tenantBindings.OrderBy(static tenant => tenant.Tenant, StringComparer.Ordinal).ToArray();
        if (tenants.Length == 0)
            throw new ArgumentException("A freight rebuild requires at least one tenant shard.", nameof(tenantBindings));
        if (tenants.GroupBy(static tenant => tenant.Tenant, StringComparer.Ordinal).Any(static group => group.Skip(1).Any()))
            throw new ArgumentException("A freight rebuild cannot repeat a tenant shard.", nameof(tenantBindings));
        if (target.MaterializationId != semantics.Definition.Id)
            throw new ArgumentException("The provider target belongs to another materialization.", nameof(target));

        var compiledPlanReference = RelationQueryCompiledPlanReference.From(semantics.Plan);
        var compiledPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(compiledPlanReference);
        var expectedInputs = semantics.Definition.Sources
            .Select(static source => source.Input)
            .OrderBy(static input => input.Value, StringComparer.Ordinal)
            .ToArray();
        foreach (var tenant in tenants)
        {
            var observedInputs = tenant.Sources.Select(static source => source.Input).ToArray();
            if (!expectedInputs.SequenceEqual(observedInputs))
                throw new ArgumentException($"Tenant '{tenant.Tenant}' does not bind every canonical source input once.", nameof(tenantBindings));
            if (RelationQueryCompiledPlanReferenceFingerprinter.Compute(tenant.Hydrator.Plan) != compiledPlanFingerprint)
                throw new ArgumentException($"Tenant '{tenant.Tenant}' hydrator belongs to another canonical relation plan.", nameof(tenantBindings));
            var logicalPartition = LogicalPartition(tenant.Tenant);
            if (tenant.Sources.Any(source => source.Scope.LogicalPartition != logicalPartition))
                throw new ArgumentException($"Tenant '{tenant.Tenant}' contains a source from another logical partition.", nameof(tenantBindings));
        }

        var sourcePlans = expectedInputs.Select(input =>
        {
            var representative = tenants[0].GetSource(input).Source.Descriptor;
            foreach (var tenant in tenants.Skip(1))
            {
                var candidate = tenant.GetSource(input).Source.Descriptor;
                if (candidate.Source != representative.Source
                    || !CanonicalProfileBytes(candidate.CapabilityProfile)
                        .SequenceEqual(CanonicalProfileBytes(representative.CapabilityProfile)))
                {
                    throw new ArgumentException(
                        $"Provider '{provider}' source input '{input.Value}' changes capability evidence across tenant scopes.",
                        nameof(tenantBindings));
                }
            }
            var requirements = semantics.Definition.Sources.Single(source => source.Input == input).Capabilities;
            var rebuildMatch = MaterializationCapabilityMatcher.MatchForMode(
                requirements: requirements,
                profile: representative.CapabilityProfile,
                mode: MaterializationSynchronizationMode.Rebuild);
            if (!rebuildMatch.IsSatisfied)
            {
                throw new ArgumentException(
                    $"Provider '{provider}' cannot realize rebuild input '{input.Value}': "
                    + string.Join(" ", rebuildMatch.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                    nameof(tenantBindings));
            }
            var incrementalMatch = MaterializationCapabilityMatcher.MatchForMode(
                requirements: requirements,
                profile: representative.CapabilityProfile,
                mode: MaterializationSynchronizationMode.Incremental);
            if (!incrementalMatch.IsSatisfied)
            {
                throw new ArgumentException(
                    $"Provider '{provider}' cannot continue incremental input '{input.Value}': "
                    + string.Join(" ", incrementalMatch.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                    nameof(tenantBindings));
            }
            return new MaterializationRebuildSourcePlan(
                input: input,
                source: representative.Source,
                profile: representative.CapabilityProfile,
                capabilityMatch: rebuildMatch);
        }).ToImmutableArray();

        var subjects = tenants
            .Select(static tenant => new MaterializationPlacementSubjectId($"tenant/{tenant.Tenant}"))
            .ToImmutableArray();
        var provenance = Provenance(provider, "plan");
        var pool = MaterializationBackendPoolDocument.FromDefinition(new(
            id: new($"materialization-harness/{provider}/freight-pool/v1"),
            materializationId: semantics.Definition.Id,
            definitionFingerprint: semantics.DefinitionFingerprint,
            members: [target],
            defaultTarget: target.Id,
            provenance: Provenance(provider, "pool")));
        var request = new MaterializationRebuildRequestDocument(
            schemaVersion: MaterializationRebuildRequestDocument.CurrentSchemaVersion,
            materialization: semantics.Document,
            selection: new MaterializationExplicitPlacementSubjectSelection(subjects),
            placement: new(MaterializationBackendPoolReference.FromDocument(pool)),
            scheduling: new(maximumStartsPerActivation: 1, maximumParallelism: 1),
            promotion: new(MaterializationRebuildPromotionMode.Independent),
            provenance: Provenance(provider, "request"));
        var membership = Require(
            artifact: MaterializationRebuildPlanSetCompiler.FreezeMembership(
                request: request,
                observedMembers: subjects,
                authority: new(
                    authority: $"materialization-harness/{provider}/seed-membership",
                    revision: "freight-baseline/v1",
                    cut: "provider-positioned-change-feed/v1",
                    completeness: MaterializationRebuildMembershipCompleteness.Complete,
                    evidenceReferences: [$"materialization-harness/{provider}/seed/freight-baseline.json"]),
                provenance: Provenance(provider, "membership")),
            stage: "membership");
        var capacityDomain = new MaterializationPhysicalCapacityDomain(
            id: new($"materialization-harness/{provider}/capacity"),
            maximumParallelism: 1,
            evidenceReferences: [$"materialization-harness/{provider}/local-compose/v1"]);
        var placement = Require(
            artifact: MaterializationRebuildPlanSetCompiler.CompilePlacement(
                request: request,
                membership: membership,
                backendPool: pool,
                assignments: [.. subjects.Select(subject => new MaterializationTargetPlacementAssignment(subject, target.Id))],
                capacityDomains: [capacityDomain],
                capacityAssignments: [new(target.Id, capacityDomain.Id)],
                provenance: Provenance(provider, "placement")),
            stage: "placement");
        var placementSlice = placement.Slices.Single();

        var rootInput = semantics.Root.Input.Id;
        var shards = tenants.Select(tenant =>
        {
            var root = tenant.GetSource(rootInput);
            if (root.Scope.PhysicalPlan != tenant.RootRead.PhysicalPlan
                || root.Scope.Placement.Id != tenant.RootRead.PlacementBinding
                || root.Scope.Source != tenant.RootRead.Source)
            {
                throw new ArgumentException(
                    $"Tenant '{tenant.Tenant}' root read differs from its exact source scope.",
                    nameof(tenantBindings));
            }
            return new MaterializationRebuildShardPlan(
                id: Shard(tenant.Tenant),
                scope: root.Scope,
                read: tenant.RootRead,
                hydrationPhysicalPlan: tenant.Hydrator.PhysicalPlan);
        }).ToImmutableArray();

        var feeds = ImmutableArray.CreateBuilder<MaterializationChangeFeedPlan>(tenants.Length * expectedInputs.Length);
        var sourcesByFeed = ImmutableDictionary.CreateBuilder<MaterializationChangeFeedId, IMaterializationPullChangeSource>();
        var impactRuntimesByFeed = ImmutableDictionary.CreateBuilder<MaterializationChangeFeedId, IMaterializationImpactRuntime>();
        foreach (var tenant in tenants)
        {
            var impactRuntime = tenant.CreateImpactRuntime(impactPlan);
            if (impactRuntime.ImpactPlan != impactPlan.Fingerprint)
            {
                throw new ArgumentException(
                    $"Tenant '{tenant.Tenant}' impact runtime implements another persisted plan.",
                    nameof(tenantBindings));
            }
            foreach (var input in expectedInputs)
            {
                var binding = tenant.GetSource(input);
                MaterializationChangeFeedId feedId = new($"feed/{tenant.Tenant}/{Uri.EscapeDataString(input.Value)}");
                var feed = new MaterializationChangeFeedPlan(
                    id: feedId,
                    scope: binding.Scope,
                    channel: Channel(binding.Scope));
                feeds.Add(feed);
                sourcesByFeed.Add(feedId, binding.Source);
                impactRuntimesByFeed.Add(feedId, impactRuntime);
            }
        }
        var exactFeeds = feeds.MoveToImmutable();
        var catalogs = exactFeeds
            .GroupBy(static feed => feed.Scope.Input)
            .Select(group => new MaterializationChangeFeedCatalogEvidence(
                input: group.Key,
                source: sourcePlans.Single(source => source.Input == group.Key).Source,
                scopes: [.. group.Select(static feed => feed.Scope)],
                evidenceReference: $"materialization-harness/{provider}/change-feed-catalog/{Uri.EscapeDataString(group.Key.Value)}/v1"))
            .ToImmutableArray();
        var targetMatch = MaterializationCapabilityMatcher.MatchForMode(
            requirements: semantics.Definition.TargetCapabilities,
            profile: target.Capabilities,
            mode: MaterializationSynchronizationMode.Rebuild);
        if (!targetMatch.IsSatisfied)
        {
            throw new ArgumentException(
                $"Provider '{provider}' target cannot realize rebuild semantics: "
                + string.Join(" ", targetMatch.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                nameof(target));
        }
        var plan = new MaterializationRebuildPlan(
            materialization: semantics.Document,
            placementSlice: placementSlice,
            impactPlan: impactPlan,
            sources: sourcePlans,
            target: target,
            targetCapabilityMatch: targetMatch,
            shards: shards,
            changeFeedCatalogs: catalogs,
            changeFeeds: exactFeeds,
            limits: new(
                maximumPageItems: MaximumPageItems,
                maximumPageBytes: MaximumPageBytes,
                maximumBulkItems: MaximumBulkItems,
                maximumBulkBytes: MaximumBulkBytes,
                maximumPagesPerShard: 128,
                maximumStartsPerActivation: MaterializationRebuildProcessFactory.MaximumStartsPerActivation,
                maximumParallelism: MaterializationRebuildProcessFactory.MaximumParallelism,
                maximumChangeFeedsPerConvergenceActivation: 64),
            provenance: provenance);
        var planSet = Require(
            artifact: MaterializationRebuildPlanSetLinker.Link(
                request: request,
                membership: membership,
                placement: placement,
                leafPlans: [plan],
                provenance: Provenance(provider, "link")),
            stage: "link");
        var tenantsByShard = tenants.ToImmutableDictionary(
            tenant => Shard(tenant.Tenant),
            static tenant => tenant);
        return new(
            provider: provider,
            request: request,
            membership: membership,
            placement: placement,
            plan: plan,
            planSet: planSet,
            tenantsByShard: tenantsByShard,
            sourcesByFeed: sourcesByFeed.ToImmutable(),
            impactRuntimesByFeed: impactRuntimesByFeed.ToImmutable());
    }

    /// <summary>Returns the canonical logical partition identity for one freight tenant.</summary>
    /// <param name="tenant">Stable tenant identity.</param>
    /// <returns>Provider-neutral tenant partition identity.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenant"/> is empty.</exception>
    public static RelationQueryLogicalPartitionIdentity LogicalPartition(string tenant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        return new($"materialization-harness/freight/tenant/{tenant}");
    }

    internal static MaterializationImpactPlan CompileImpactPlan(
        FreightOrderMaterializationSemantics semantics,
        string provider)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var compilation = MaterializationImpactPlanCompiler.Compile(
            document: semantics.Document,
            policy: new(
                id: new($"materialization-harness/{provider}/freight-impact/v1"),
                strategyPreference: [MaterializationImpactStrategyKind.InverseTraversal],
                maximumAffectedRoots: 64,
                maximumReadBytes: MaximumPageBytes));
        return Require(
            artifact: compilation.Plan,
            diagnostics: compilation.Diagnostics.Select(static diagnostic => diagnostic.Message));
    }

    static MaterializationRebuildShardId Shard(string tenant) => new($"tenant/{tenant}");

    static ChannelRealizationPlanFingerprint Channel(MaterializationSourceScope scope)
    {
        var canonical = Encoding.UTF8.GetBytes(
            MaterializationChannelSemantics.ToChannelScopeId(scope).Value);
        return new(
            algorithm: "sha256",
            canonicalization: "materialization-harness/provider-positioned-source-channel/v1",
            value: Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    static byte[] CanonicalProfileBytes(MaterializationCapabilityProfile profile) =>
        JsonSerializer.SerializeToUtf8Bytes(profile, MaterializationJsonSerializer.CreateOptions());

    static ExecutionProvenance Provenance(string provider, string stage) => new(
        producer: new("cohesive-materialization-harness", "1"),
        source: new($"eng/materialization-harness/{provider}/{stage}"),
        origin: DocumentOrigin.Generated);

    static T Require<T>(T? artifact, IEnumerable<string> diagnostics)
        where T : class => artifact ?? throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics));

    static T Require<T>(MaterializationRebuildPlanningResult<T> artifact, string stage)
        where T : class => artifact.Artifact ?? throw new InvalidOperationException(
            $"Freight rebuild {stage} failed: "
            + string.Join(" ", artifact.Diagnostics.Select(static diagnostic => diagnostic.Message)));
}
