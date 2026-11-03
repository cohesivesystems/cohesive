using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Creates one partition/provider physical impact runtime for an exact persisted impact plan.</summary>
/// <param name="impactPlan">Exact plan whose inverse reads and canonical hydration are bound.</param>
/// <returns>A runtime implementing the plan fingerprint.</returns>
/// <exception cref="ArgumentNullException"><paramref name="impactPlan"/> is <see langword="null"/>.</exception>
/// <exception cref="ArgumentException">The plan differs from the runtime's canonical relation semantics.</exception>
public delegate IMaterializationImpactRuntime MaterializationPartitionImpactRuntimeFactory(
    MaterializationImpactPlan impactPlan);

/// <summary>One exact provider source binding for a canonical relation acquisition input and partition scope.</summary>
public sealed class MaterializationPartitionSourceBinding
{
    /// <summary>Creates one exact source binding.</summary>
    /// <param name="input">Canonical Relations acquisition input implemented by the source.</param>
    /// <param name="scope">Exact provider physical-plan, placement, logical partition, and ordering scope.</param>
    /// <param name="source">Paged baseline and positioned change source implementing the binding.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> or <paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentException">The input or physical source identity differs from the exact scope.</exception>
    public MaterializationPartitionSourceBinding(
        RelationQueryInputId input,
        MaterializationSourceScope scope,
        IMaterializationPullChangeSource source)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        if (scope.Input != input)
            throw new ArgumentException("A partition source binding must retain its exact acquisition input.", nameof(scope));
        if (scope.Source != source.Descriptor.Source
            || scope.LogicalPartition != source.Descriptor.RelationReader.Descriptor.LogicalPartition)
        {
            throw new ArgumentException(
                "A partition source binding must retain its exact physical source and logical partition.",
                nameof(source));
        }
        Input = input;
    }

    /// <summary>Canonical Relations acquisition input implemented by the source.</summary>
    public RelationQueryInputId Input { get; }

    /// <summary>Exact provider physical-plan, placement, logical partition, and ordering scope.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Paged baseline and positioned change source implementing the binding.</summary>
    public IMaterializationPullChangeSource Source { get; }
}

/// <summary>Complete exact provider runtime evidence for one logical partition rebuild shard.</summary>
public sealed class MaterializationPartitionBinding
{
    readonly ImmutableDictionary<RelationQueryInputId, MaterializationPartitionSourceBinding> sources;
    readonly MaterializationPartitionImpactRuntimeFactory impactRuntimeFactory;
    readonly ImmutableArray<MaterializationPartitionSourceBinding> orderedSources;

    /// <summary>Creates one partition binding covering every canonical acquisition input exactly once.</summary>
    /// <param name="partition">Stable partition identity.</param>
    /// <param name="logicalPartition">Explicit partition authority shared by all source bindings.</param>
    /// <param name="rootRead">Exact bounded root enumeration used by this partition shard.</param>
    /// <param name="hydrator">Exact canonical relation hydration interpretation.</param>
    /// <param name="sourceBindings">One source binding for every canonical acquisition input.</param>
    /// <param name="impactRuntimeFactory">Exact provider inverse-read and canonical hydration runtime factory.</param>
    /// <exception cref="ArgumentNullException">A required runtime binding is null.</exception>
    /// <exception cref="ArgumentException">An identity is absent, repeated, or inconsistent.</exception>
    public MaterializationPartitionBinding(
        string partition,
        RelationQueryLogicalPartitionIdentity logicalPartition,
        RelationQuerySourceReadRequest rootRead,
        IMaterializationRebuildHydrator hydrator,
        IEnumerable<MaterializationPartitionSourceBinding> sourceBindings,
        MaterializationPartitionImpactRuntimeFactory impactRuntimeFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        Partition = partition;
        LogicalPartition = logicalPartition;
        RootRead = rootRead ?? throw new ArgumentNullException(nameof(rootRead));
        Hydrator = hydrator ?? throw new ArgumentNullException(nameof(hydrator));
        ArgumentNullException.ThrowIfNull(sourceBindings);
        var normalized = sourceBindings.ToArray();
        if (normalized.Any(static binding => binding is null))
            throw new ArgumentException("Partition source bindings cannot contain null entries.", nameof(sourceBindings));
        if (normalized.GroupBy(static binding => binding.Input).Any(static group => group.Skip(1).Any()))
            throw new ArgumentException("A partition source catalog cannot repeat an acquisition input.", nameof(sourceBindings));
        orderedSources = [.. normalized.OrderBy(static binding => binding.Input.Value, StringComparer.Ordinal)];
        sources = normalized.ToImmutableDictionary(static binding => binding.Input);
        this.impactRuntimeFactory = Guard.RequireNotNull(impactRuntimeFactory);
    }

    /// <summary>Stable partition identity.</summary>
    public string Partition { get; }

    /// <summary>Declared logical partition validated against every source before any I/O.</summary>
    public RelationQueryLogicalPartitionIdentity LogicalPartition { get; }

    /// <summary>Exact bounded root enumeration used by this partition shard.</summary>
    public RelationQuerySourceReadRequest RootRead { get; }

    /// <summary>Exact canonical relation hydration interpretation.</summary>
    public IMaterializationRebuildHydrator Hydrator { get; }

    /// <summary>Source bindings in canonical acquisition-input order.</summary>
    public ImmutableArray<MaterializationPartitionSourceBinding> Sources => orderedSources;

    /// <summary>Gets the exact source binding for one canonical acquisition input.</summary>
    /// <param name="input">Canonical acquisition input.</param>
    /// <returns>The exact partition/provider source binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="input"/> is absent.</exception>
    public MaterializationPartitionSourceBinding GetSource(RelationQueryInputId input) => sources[input];

    internal IMaterializationImpactRuntime CreateImpactRuntime(MaterializationImpactPlan impactPlan) =>
        impactRuntimeFactory(impactPlan);
}

/// <summary>Canonical single-provider plan-set artifacts and their exact partition runtime bindings.</summary>
public sealed class MaterializationPartitionedRebuild
{
    readonly ImmutableDictionary<MaterializationRebuildShardId, MaterializationPartitionBinding> partitionsByShard;
    readonly ImmutableDictionary<MaterializationChangeFeedId, IMaterializationPullChangeSource> sourcesByFeed;
    readonly ImmutableDictionary<MaterializationChangeFeedId, IMaterializationImpactRuntime> impactRuntimesByFeed;

    internal MaterializationPartitionedRebuild(
        string provider,
        MaterializationRebuildRequestDocument request,
        MaterializationRebuildMembershipEvidence membership,
        MaterializationTargetPlacementPlan placement,
        MaterializationRebuildPlan plan,
        MaterializationRebuildPlanSet planSet,
        ImmutableDictionary<MaterializationRebuildShardId, MaterializationPartitionBinding> partitionsByShard,
        ImmutableDictionary<MaterializationChangeFeedId, IMaterializationPullChangeSource> sourcesByFeed,
        ImmutableDictionary<MaterializationChangeFeedId, IMaterializationImpactRuntime> impactRuntimesByFeed)
    {
        Provider = provider;
        Request = request;
        Membership = membership;
        Placement = placement;
        Plan = plan;
        PlanSet = planSet;
        this.partitionsByShard = partitionsByShard;
        this.sourcesByFeed = sourcesByFeed;
        this.impactRuntimesByFeed = impactRuntimesByFeed;
    }

    /// <summary>Stable provider interpretation identity.</summary>
    public string Provider { get; }

    /// <summary>Canonical rebuild request.</summary>
    public MaterializationRebuildRequestDocument Request { get; }

    /// <summary>Complete frozen partition membership.</summary>
    public MaterializationRebuildMembershipEvidence Membership { get; }

    /// <summary>Canonical partition-to-target placement.</summary>
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
            var partition = partitionsByShard[shard.Id];
            return new MaterializationRebuildShardBinding(
                shard: shard,
                source: partition.GetSource(shard.Scope.Input).Source,
                hydrator: partition.Hydrator);
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

/// <summary>Compiles the canonical relation materialization into one partition-sharded provider leaf and plan set.</summary>
public static class MaterializationPartitionedRebuildCompiler
{
    /// <summary>Compiles canonical planning artifacts and verifies every exact partition/provider runtime binding.</summary>
    /// <param name="document">Canonical materialization requirements and retained relation.</param>
    /// <param name="relation">Exact compiled relation matching the materialization.</param>
    /// <param name="limits">Declared page, work and scheduling bounds.</param>
    /// <param name="provider">Stable provider interpretation identity.</param>
    /// <param name="target">Exact generational index target descriptor for this provider interpretation.</param>
    /// <param name="partitionBindings">Complete partition runtime bindings; input order is immaterial.</param>
    /// <param name="impactPlan">Exact persisted impact plan already bound by every partition runtime.</param>
    /// <param name="capacityDomain">Caller-attested native concurrency and capacity evidence.</param>
    /// <param name="provenance">Caller-owned authoring source and producer.</param>
    /// <returns>Linked canonical plan-set artifacts with exact runtime source bindings.</returns>
    /// <exception cref="ArgumentNullException">A required artifact or collection is null.</exception>
    /// <exception cref="ArgumentException">Partition, source, profile, scope, or relation-plan evidence is incomplete or inconsistent.</exception>
    /// <exception cref="InvalidOperationException">A canonical compiler rejects the supplied evidence.</exception>
    public static MaterializationPartitionedRebuild Compile(
        MaterializationDocument document,
        CompiledRelationQueryPlan relation,
        MaterializationRebuildLimits limits,
        string provider,
        MaterializationTargetDescriptor target,
        IEnumerable<MaterializationPartitionBinding> partitionBindings,
        MaterializationImpactPlan impactPlan,
        MaterializationPhysicalCapacityDomain capacityDomain,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(partitionBindings);
        ArgumentNullException.ThrowIfNull(impactPlan);
        ArgumentNullException.ThrowIfNull(capacityDomain);
        ArgumentNullException.ThrowIfNull(provenance);
        _ = MaterializationImpactPlanLinker.Link(impactPlan, document.Definition);
        var partitions = partitionBindings.OrderBy(static partition => partition.Partition, StringComparer.Ordinal).ToArray();
        if (partitions.Length == 0)
            throw new ArgumentException("A partitioned rebuild requires at least one partition shard.", nameof(partitionBindings));
        if (partitions.GroupBy(static partition => partition.Partition, StringComparer.Ordinal).Any(static group => group.Skip(1).Any()))
            throw new ArgumentException("A partitioned rebuild cannot repeat a partition shard.", nameof(partitionBindings));
        if (target.MaterializationId != document.Definition.Id)
            throw new ArgumentException("The provider target belongs to another materialization.", nameof(target));

        var compiledPlanReference = RelationQueryCompiledPlanReference.From(relation);
        var compiledPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(compiledPlanReference);
        var expectedInputs = document.Definition.Sources
            .Select(static source => source.Input)
            .OrderBy(static input => input.Value, StringComparer.Ordinal)
            .ToArray();
        foreach (var partition in partitions)
        {
            var observedInputs = partition.Sources.Select(static source => source.Input).ToArray();
            if (!expectedInputs.SequenceEqual(observedInputs))
                throw new ArgumentException($"Partition '{partition.Partition}' does not bind every canonical source input once.", nameof(partitionBindings));
            if (RelationQueryCompiledPlanReferenceFingerprinter.Compute(partition.Hydrator.Plan) != compiledPlanFingerprint)
                throw new ArgumentException($"Partition '{partition.Partition}' hydrator belongs to another canonical relation plan.", nameof(partitionBindings));
            var logicalPartition = partition.LogicalPartition;
            if (partition.Sources.Any(source => source.Scope.LogicalPartition != logicalPartition))
                throw new ArgumentException($"Partition '{partition.Partition}' contains a source from another logical partition.", nameof(partitionBindings));
        }

        var sourcePlans = expectedInputs.Select(input =>
        {
            var representative = partitions[0].GetSource(input).Source.Descriptor;
            foreach (var partition in partitions.Skip(1))
            {
                var candidate = partition.GetSource(input).Source.Descriptor;
                if (candidate.Source != representative.Source
                    || !CanonicalProfileBytes(candidate.CapabilityProfile)
                        .SequenceEqual(CanonicalProfileBytes(representative.CapabilityProfile)))
                {
                    throw new ArgumentException(
                        $"Provider '{provider}' source input '{input.Value}' changes capability evidence across partition scopes.",
                        nameof(partitionBindings));
                }
            }
            var requirements = document.Definition.Sources.Single(source => source.Input == input).Capabilities;
            var rebuildMatch = MaterializationCapabilityMatcher.MatchForMode(
                requirements: requirements,
                profile: representative.CapabilityProfile,
                mode: MaterializationSynchronizationMode.Rebuild);
            if (!rebuildMatch.IsSatisfied)
            {
                throw new ArgumentException(
                    $"Provider '{provider}' cannot realize rebuild input '{input.Value}': "
                    + string.Join(" ", rebuildMatch.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                    nameof(partitionBindings));
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
                    nameof(partitionBindings));
            }
            return new MaterializationRebuildSourcePlan(
                input: input,
                source: representative.Source,
                profile: representative.CapabilityProfile,
                capabilityMatch: rebuildMatch);
        }).ToImmutableArray();

        var subjects = partitions
            .Select(static partition => new MaterializationPlacementSubjectId($"tenant/{partition.Partition}"))
            .ToImmutableArray();
        var pool = MaterializationBackendPoolDocument.FromDefinition(new(
            id: new($"materialization/{provider}/partition-pool/v1"),
            materializationId: document.Definition.Id,
            definitionFingerprint: document.DefinitionFingerprint,
            members: [target],
            defaultTarget: target.Id,
            provenance: provenance));
        var request = new MaterializationRebuildRequestDocument(
            schemaVersion: MaterializationRebuildRequestDocument.CurrentSchemaVersion,
            materialization: document,
            selection: new MaterializationExplicitPlacementSubjectSelection(subjects),
            placement: new(MaterializationBackendPoolReference.FromDocument(pool)),
            scheduling: new(maximumStartsPerActivation: limits.MaximumStartsPerActivation, maximumParallelism: limits.MaximumParallelism),
            promotion: new(MaterializationRebuildPromotionMode.Independent),
            provenance: provenance);
        var membership = Require(
            artifact: MaterializationRebuildPlanSetCompiler.FreezeMembership(
                request: request,
                observedMembers: subjects,
                authority: new(
                    authority: $"materialization/{provider}/declared-membership",
                    revision: "explicit-partitions/v1",
                    cut: "provider-positioned-change-feed/v1",
                    completeness: MaterializationRebuildMembershipCompleteness.Complete,
                    evidenceReferences: [$"materialization/{provider}/explicit-partitions/v1"]),
                provenance: provenance),
            stage: "membership");
        var placement = Require(
            artifact: MaterializationRebuildPlanSetCompiler.CompilePlacement(
                request: request,
                membership: membership,
                backendPool: pool,
                assignments: [.. subjects.Select(subject => new MaterializationTargetPlacementAssignment(subject, target.Id))],
                capacityDomains: [capacityDomain],
                capacityAssignments: [new(target.Id, capacityDomain.Id)],
                provenance: provenance),
            stage: "placement");
        var placementSlice = placement.Slices.Single();

        var rootInput = relation.InputContract.Sources.Single(source => source.Role == RelationQuerySourceInputRole.RelationRoot).Input.Id;
        var shards = partitions.Select(partition =>
        {
            var root = partition.GetSource(rootInput);
            if (root.Scope.PhysicalPlan != partition.RootRead.PhysicalPlan
                || root.Scope.Placement.Id != partition.RootRead.PlacementBinding
                || root.Scope.Source != partition.RootRead.Source)
            {
                throw new ArgumentException(
                    $"Partition '{partition.Partition}' root read differs from its exact source scope.",
                    nameof(partitionBindings));
            }
            return new MaterializationRebuildShardPlan(
                id: Shard(partition.Partition),
                scope: root.Scope,
                read: partition.RootRead,
                hydrationPhysicalPlan: partition.Hydrator.PhysicalPlan);
        }).ToImmutableArray();

        var feeds = ImmutableArray.CreateBuilder<MaterializationChangeFeedPlan>(partitions.Length * expectedInputs.Length);
        var sourcesByFeed = ImmutableDictionary.CreateBuilder<MaterializationChangeFeedId, IMaterializationPullChangeSource>();
        var impactRuntimesByFeed = ImmutableDictionary.CreateBuilder<MaterializationChangeFeedId, IMaterializationImpactRuntime>();
        foreach (var partition in partitions)
        {
            var impactRuntime = partition.CreateImpactRuntime(impactPlan);
            if (impactRuntime.ImpactPlan != impactPlan.Fingerprint)
            {
                throw new ArgumentException(
                    $"Partition '{partition.Partition}' impact runtime implements another persisted plan.",
                    nameof(partitionBindings));
            }
            foreach (var input in expectedInputs)
            {
                var binding = partition.GetSource(input);
                MaterializationChangeFeedId feedId = new($"feed/{partition.Partition}/{Uri.EscapeDataString(input.Value)}");
                MaterializationCurrentStateEnrichmentPlan? currentState = null;
                if (input == rootInput)
                {
                    currentState = (binding.Source as IMaterializationCurrentStateEnrichmentSource)
                        ?.CurrentStateEnrichment;
                    if (currentState is null)
                    {
                        var changeRequirement = document.Definition.Sources
                            .Single(source => source.Input == input)
                            .Capabilities
                            .Single(capability =>
                                capability.Capability == MaterializationCapabilityKind.SourceChangeDelivery);
                        var compilation = MaterializationCurrentStateEnrichmentCompiler.Compile(
                            input: input,
                            shape: binding.Scope.Shape,
                            source: binding.Scope.Source,
                            changeRequirement: changeRequirement,
                            profile: binding.Source.Descriptor.CapabilityProfile,
                            policy: new(
                                maximumIdentitiesPerRead: limits.MaximumPageItems,
                                maximumReadBytes: limits.MaximumPageBytes,
                                evidenceReference: $"materialization/{provider}/current-state/native/v1"));
                        if (!compilation.IsSuccessful
                            || !CanonicalProfileBytes(compilation.Profile!)
                                .SequenceEqual(CanonicalProfileBytes(binding.Source.Descriptor.CapabilityProfile)))
                        {
                            throw new ArgumentException(
                                $"Provider '{provider}' root feed '{input.Value}' has neither a complete native change image "
                                + "nor an exact runtime current-state enrichment binding: "
                                + string.Join(" ", compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)),
                                nameof(partitionBindings));
                        }
                        currentState = compilation.Plan;
                    }
                }
                var feed = new MaterializationChangeFeedPlan(
                    id: feedId,
                    scope: binding.Scope,
                    channel: Channel(binding.Scope),
                    currentStateEnrichment: currentState);
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
                evidenceReference: $"materialization/{provider}/change-feed-catalog/{Uri.EscapeDataString(group.Key.Value)}/v1"))
            .ToImmutableArray();
        var targetMatch = MaterializationCapabilityMatcher.MatchForMode(
            requirements: document.Definition.TargetCapabilities,
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
            materialization: document,
            placementSlice: placementSlice,
            impactPlan: impactPlan,
            sources: sourcePlans,
            target: target,
            targetCapabilityMatch: targetMatch,
            shards: shards,
            changeFeedCatalogs: catalogs,
            changeFeeds: exactFeeds,
            limits: limits,
            provenance: provenance);
        var planSet = Require(
            artifact: MaterializationRebuildPlanSetLinker.Link(
                request: request,
                membership: membership,
                placement: placement,
                leafPlans: [plan],
                provenance: provenance),
            stage: "link");
        var partitionsByShard = partitions.ToImmutableDictionary(
            partition => Shard(partition.Partition),
            static partition => partition);
        return new(
            provider: provider,
            request: request,
            membership: membership,
            placement: placement,
            plan: plan,
            planSet: planSet,
            partitionsByShard: partitionsByShard,
            sourcesByFeed: sourcesByFeed.ToImmutable(),
            impactRuntimesByFeed: impactRuntimesByFeed.ToImmutable());
    }

    // Persisted identity namespace predates the public partition terminology; retain it for resume.
    static MaterializationRebuildShardId Shard(string partition) => new($"tenant/{partition}");

    static ChannelRealizationPlanFingerprint Channel(MaterializationSourceScope scope)
    {
        var canonical = Encoding.UTF8.GetBytes(
            MaterializationChannelSemantics.ToChannelScopeId(scope).Value);
        return new(
            algorithm: "sha256",
            canonicalization: "cohesive/materialization/provider-positioned-source-channel/v1",
            value: Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    static byte[] CanonicalProfileBytes(MaterializationCapabilityProfile profile) =>
        JsonSerializer.SerializeToUtf8Bytes(profile, MaterializationJsonSerializer.CreateOptions());

    static T Require<T>(T? artifact, IEnumerable<string> diagnostics)
        where T : class => artifact ?? throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics));

    static T Require<T>(MaterializationRebuildPlanningResult<T> artifact, string stage)
        where T : class => artifact.Artifact ?? throw new InvalidOperationException(
            $"Partitioned rebuild {stage} failed: "
            + string.Join(" ", artifact.Diagnostics.Select(static diagnostic => diagnostic.Message)));
}
