using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable Storage-owned wire coordinates for rebuild plan-set execution status.</summary>
public static class MaterializationRebuildPlanSetStatusWireNames
{
    /// <summary>Stable authority and extension identity for rebuild plan-set execution status.</summary>
    public const string SemanticAuthority = "cohesive.storage.materialization.rebuild-plan-set.status";

    /// <summary>Exact portable payload schema version.</summary>
    public const string CurrentSchemaVersion = "materialization-rebuild-plan-set-status/v1";

    /// <summary>Typed execution-extension identity derived from <see cref="SemanticAuthority"/>.</summary>
    public static ExecutionExtensionId ExtensionId { get; } = new(SemanticAuthority);

    /// <summary>Typed extension schema version derived from <see cref="CurrentSchemaVersion"/>.</summary>
    public static ExecutionExtensionSchemaVersion SchemaVersion { get; } = new(CurrentSchemaVersion);

    /// <summary>Projects the canonical execution-status path for one exact plan-set authority.</summary>
    /// <param name="planSet">Exact content-addressed plan-set authority.</param>
    /// <returns>A stable path derived only from canonical materialization, request, and plan-set identities.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="planSet"/> is <see langword="null"/>.</exception>
    public static ExecutionSemanticPath StatusPath(MaterializationRebuildPlanSetReference planSet)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        return new(
        [
            "materializations",
            planSet.Request.Materialization.Materialization.Value,
            "rebuildRequests",
            planSet.Request.Request.Value,
            "planSets",
            MaterializationRebuildIdentities.PlanSetIdentity(planSet),
            "executionStatus"
        ]);
    }
}

/// <summary>
/// Projects one exact durable rebuild plan-set parent Process into common execution status and attributable explain
/// evidence.
/// </summary>
public static class MaterializationRebuildPlanSetStatusProjector
{
    static readonly JsonSerializerOptions CanonicalJsonOptions = StrictDocumentJson.CreateOptions();
    static readonly ScalarTypeRef StringType = new(ScalarTypeKind.String);
    static readonly ScalarTypeRef IntegerType = new(ScalarTypeKind.Int64);
    static readonly ScalarTypeRef InstantType = new(ScalarTypeKind.Instant);
    static readonly EnumTypeRef TerminalOutcomeType = EnumType<ExecutionTerminalOutcomeKind>();
    static readonly EnumTypeRef ChildDispositionType = EnumType<ProcessChildDisposition>();
    static readonly EnumTypeRef LeafOutcomeType = EnumType<MaterializationRebuildPlanSetLeafOutcome>();
    static readonly EnumTypeRef AggregateOutcomeType = EnumType<MaterializationRebuildPlanSetOutcome>();
    static readonly EnumTypeRef PromotionModeType = EnumType<MaterializationRebuildPromotionMode>();
    static readonly EnumTypeRef ProgressiveFailurePolicyType =
        EnumType<MaterializationProgressivePromotionFailurePolicy>();
    static readonly ObjectTypeRef FingerprintType = new(
    [
        new("algorithm", StringType),
        new("canonicalization", StringType),
        new("value", StringType)
    ]);
    static readonly ObjectTypeRef DefinitionType = new(
    [
        new("definitionId", StringType),
        new("revisionId", StringType),
        new("fingerprint", FingerprintType)
    ]);
    static readonly ObjectTypeRef ContinuationType = new(
    [
        new("processInstanceId", StringType),
        new("processAttemptId", StringType)
    ]);
    static readonly ObjectTypeRef CapacityDomainType = new(
    [
        new("id", StringType),
        new("maximumParallelism", IntegerType)
    ]);
    static readonly ObjectTypeRef ChildType = new(
    [
        new("sliceId", StringType),
        new("target", StringType),
        new("placementSliceFingerprint", FingerprintType),
        new("subjects", StringType, cardinality: FieldCardinality.Many),
        new("capacityDomain", StringType),
        new("buildChild", ContinuationType, nullability: FieldNullability.Nullable),
        new("buildDisposition", ChildDispositionType, nullability: FieldNullability.Nullable),
        new("buildTerminalOutcome", StringType, nullability: FieldNullability.Nullable),
        new("buildTerminalResult", StringType, nullability: FieldNullability.Nullable),
        new("readyReference", StringType, nullability: FieldNullability.Nullable),
        new("promotionChild", ContinuationType, nullability: FieldNullability.Nullable),
        new("promotionDisposition", ChildDispositionType, nullability: FieldNullability.Nullable),
        new("promotionTerminalOutcome", StringType, nullability: FieldNullability.Nullable),
        new("promotionChildResult", StringType, nullability: FieldNullability.Nullable),
        new("promotionResult", StringType, nullability: FieldNullability.Nullable),
        new("leafOutcome", LeafOutcomeType, nullability: FieldNullability.Nullable),
        new("failureEvidence", StringType, nullability: FieldNullability.Nullable)
    ]);
    static readonly ObjectTypeRef ProgressType = new(
    [
        new("buildStarted", IntegerType),
        new("buildSettled", IntegerType),
        new("ready", IntegerType),
        new("promotionStarted", IntegerType),
        new("promotionSettled", IntegerType),
        new("promoted", IntegerType)
    ]);
    static readonly ValueContract StatusContract = new(new ObjectTypeRef(
    [
        new("planSetReference", StringType),
        new("requestFingerprint", FingerprintType),
        new("membershipFingerprint", FingerprintType),
        new("placementFingerprint", FingerprintType),
        new("promotionMode", PromotionModeType),
        new("progressiveFailurePolicy", ProgressiveFailurePolicyType, nullability: FieldNullability.Nullable),
        new("parentDefinition", DefinitionType),
        new("parentContinuation", ContinuationType),
        new("storageRevision", StringType),
        new("updatedAtUtc", InstantType),
        new("completedActivationCount", IntegerType),
        new("terminalOutcome", TerminalOutcomeType),
        new("terminalDetail", StringType, nullability: FieldNullability.Nullable),
        new("maximumStartsPerActivation", IntegerType),
        new("maximumParallelism", IntegerType),
        new("capacityDomains", CapacityDomainType, cardinality: FieldCardinality.Many),
        new("progress", ProgressType),
        new("children", ChildType, cardinality: FieldCardinality.Many),
        new("readyBarrier", StringType, nullability: FieldNullability.Nullable),
        new("aggregateOutcome", AggregateOutcomeType, nullability: FieldNullability.Nullable),
        new("aggregateReceipt", StringType, nullability: FieldNullability.Nullable)
    ]));

    /// <summary>Creates common runtime details and exact plan-set explain evidence from one durable snapshot.</summary>
    /// <param name="planSet">Complete constructor-verified linked plan set selected for inspection.</param>
    /// <param name="artifacts">Exact compiled parent and descendant Process artifacts for <paramref name="planSet"/>.</param>
    /// <param name="snapshot">Latest coherent durable parent Process snapshot.</param>
    /// <param name="provenance">Attributable producer and source evidence for this observation.</param>
    /// <returns>
    /// Common token, wait, progress, demand, capacity, and health facets with one versioned plan-set extension.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The artifacts, checkpoint, partition work, child references, barrier, promotion evidence, or aggregate receipt
    /// does not belong to the exact supplied plan set and current parent attempt.
    /// </exception>
    /// <exception cref="InvalidOperationException">The projected payload violates its portable contract.</exception>
    public static ExecutionRuntimeStatusDetails CreateRuntimeDetails(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessDurableStoreSnapshot snapshot,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var evidence = Inspect(planSet, artifacts, snapshot);
        var state = snapshot.Checkpoint.Continuation;
        var activeChildren = state.Children.Count(static child => child.Disposition is
            ProcessChildDisposition.Active or ProcessChildDisposition.CancellationRequested);
        var pendingChildren = state.Children.Count(static child => child.Disposition == ProcessChildDisposition.Pending);
        var settledChildren = state.Children.Count(static child => IsSettled(child.Disposition));
        var totalMilestones = checked((long)planSet.LeafPlans.Length * 2);
        var health = evidence.Receipt?.Outcome == MaterializationRebuildPlanSetOutcome.PartiallyPromoted
            ? ExecutionHealthStatus.Degraded
            : state.Terminal.Kind is ExecutionTerminalOutcomeKind.Failed
                or ExecutionTerminalOutcomeKind.Cancelled
                or ExecutionTerminalOutcomeKind.Terminated
                ? ExecutionHealthStatus.Unhealthy
                : state.Children.Any(static child => child.Disposition is
                    ProcessChildDisposition.Failed
                    or ProcessChildDisposition.CancelledBeforeStart)
                    ? ExecutionHealthStatus.Degraded
                    : ExecutionHealthStatus.Healthy;

        return new(
            tokensDisclosure: ExecutionStatusDisclosure.Disclosed,
            tokens:
            [
                .. state.Tokens.Select(static token => new ExecutionTokenStatus(
                    tokenId: token.Id,
                    node: token.Node,
                    disposition: token.Disposition))
            ],
            waitsDisclosure: ExecutionStatusDisclosure.Disclosed,
            waits:
            [
                .. state.Waits.Where(static wait => wait.Active).Select(static wait => new ExecutionWaitStatus(
                    tokenId: wait.Token,
                    node: wait.Node,
                    waitingSinceUtc: wait.RegisteredAtUtc,
                    deadlineUtc: wait.Timers.IsEmpty
                        ? null
                        : wait.Timers.Min(static timer => timer.DueAtUtc)))
            ],
            progressDisclosure: ExecutionStatusDisclosure.Disclosed,
            progress: new(
                completed: Math.Min(settledChildren, totalMilestones),
                total: totalMilestones,
                unit: "leaf-phase"),
            demandDisclosure: ExecutionStatusDisclosure.Disclosed,
            demand: new(ready: pendingChildren, delayed: 0),
            capacityDisclosure: ExecutionStatusDisclosure.Disclosed,
            capacity: new(
                active: activeChildren,
                limit: planSet.Scheduling.MaximumParallelism),
            health,
            extensions: [ProjectExtension(planSet, artifacts, snapshot, provenance, evidence)]);
    }

    /// <summary>Creates one exact, disclosed plan-set status extension from a durable parent snapshot.</summary>
    /// <param name="planSet">Complete constructor-verified linked plan set selected for inspection.</param>
    /// <param name="artifacts">Exact compiled parent and descendant Process artifacts for <paramref name="planSet"/>.</param>
    /// <param name="snapshot">Latest coherent durable parent Process snapshot.</param>
    /// <param name="provenance">Attributable producer and source evidence for this observation.</param>
    /// <returns>A versioned extension retaining exact authority and canonical barrier, promotion, and receipt evidence.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The artifacts, checkpoint, partition work, child references, barrier, promotion evidence, or aggregate receipt
    /// does not belong to the exact supplied plan set and current parent attempt.
    /// </exception>
    /// <exception cref="InvalidOperationException">The projected payload violates its portable contract.</exception>
    public static ExecutionRuntimeStatusExtension CreateExtension(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessDurableStoreSnapshot snapshot,
        ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var evidence = Inspect(planSet, artifacts, snapshot);
        return ProjectExtension(planSet, artifacts, snapshot, provenance, evidence);
    }

    static ExecutionRuntimeStatusExtension ProjectExtension(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessDurableStoreSnapshot snapshot,
        ExecutionProvenance provenance,
        StatusEvidence evidence)
    {
        var checkpoint = snapshot.Checkpoint;
        var state = checkpoint.Continuation;
        var root = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        root.Add("planSetReference", ObservationValue.FromString(
            MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(evidence.PlanSet)));
        root.Add("requestFingerprint", ProjectFingerprint(planSet.Request.Request));
        root.Add("membershipFingerprint", ProjectFingerprint(planSet.Membership.Fingerprint));
        root.Add("placementFingerprint", ProjectFingerprint(planSet.Placement.Fingerprint));
        root.Add("promotionMode", ObservationValue.FromString(planSet.Promotion.Mode.ToString()));
        root.Add(
            "progressiveFailurePolicy",
            StringOrNull(planSet.Promotion.ProgressiveFailurePolicy?.ToString()));
        root.Add("parentDefinition", ProjectDefinition(artifacts.ParentPlan.DefinitionReference));
        root.Add("parentContinuation", ProjectContinuation(state.Continuation));
        root.Add("storageRevision", ObservationValue.FromString(snapshot.Revision.Value));
        root.Add("updatedAtUtc", ObservationValue.FromDateTimeOffset(checkpoint.UpdatedAtUtc));
        root.Add("completedActivationCount", ObservationValue.FromInt64(state.CompletedActivationCount));
        root.Add("terminalOutcome", ObservationValue.FromString(state.Terminal.Kind.ToString()));
        root.Add("terminalDetail", StringOrNull(CanonicalJson(state.Terminal.Detail?.Value)));
        root.Add(
            "maximumStartsPerActivation",
            ObservationValue.FromInt64(planSet.Scheduling.MaximumStartsPerActivation));
        root.Add("maximumParallelism", ObservationValue.FromInt64(planSet.Scheduling.MaximumParallelism));
        root.Add("capacityDomains", ProjectCapacityDomains(planSet));
        root.Add("progress", ProjectProgress(evidence));
        root.Add("children", ProjectChildren(evidence));
        root.Add("readyBarrier", StringOrNull(evidence.BarrierJson));
        root.Add("aggregateOutcome", StringOrNull(evidence.Receipt?.Outcome.ToString()));
        root.Add("aggregateReceipt", StringOrNull(evidence.ReceiptJson));

        var value = PortableValue.Concrete(StatusContract, ObservationValue.FromObject(root.ToImmutable()));
        var validation = PortableExecutionValidator.Validate(value);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "The projected rebuild plan-set status violates its portable contract: "
                + string.Join(" ", validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        }

        return new(
            MaterializationRebuildPlanSetStatusWireNames.ExtensionId,
            MaterializationRebuildPlanSetStatusWireNames.SchemaVersion,
            ExecutionStatusValue.Disclose(value),
            provenance);
    }

    static StatusEvidence Inspect(
        MaterializationRebuildPlanSet planSet,
        MaterializationRebuildPlanSetProcessArtifacts artifacts,
        ProcessDurableStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(planSet);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(snapshot);
        var planSetReference = MaterializationRebuildPlanSetReference.FromPlanSet(planSet);
        if (artifacts.PlanSet != planSetReference)
            throw new ArgumentException("Compiled Process artifacts belong to another rebuild plan set.", nameof(artifacts));
        PlanSetProjection.ValidateParentContext(planSet, artifacts.ParentPlan, snapshot.Checkpoint);

        var state = snapshot.Checkpoint.Continuation;
        var bindingBySlice = planSet.LeafPlans.ToDictionary(static binding => binding.Slice.Id.Value, StringComparer.Ordinal);
        var capacityBySlice = planSet.Placement.CapacityBindings.ToDictionary(
            static binding => binding.Slice.Value,
            static binding => binding.CapacityDomain.Value,
            StringComparer.Ordinal);
        var promotionWork = ValidatePartitions(
            state,
            planSetReference,
            bindingBySlice,
            capacityBySlice,
            snapshot);

        var buildBySlice = Children(
            state,
            MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId,
            artifacts.Leaf.CoordinatorPlan.DefinitionReference,
            bindingBySlice,
            snapshot);
        var promotionBySlice = Children(
            state,
            MaterializationRebuildPlanSetProcessFactory.PromoteLeavesNodeId,
            artifacts.PromotionWorkerPlan.DefinitionReference,
            bindingBySlice,
            snapshot);
        var childEvidence = ImmutableArray.CreateBuilder<LeafEvidence>(planSet.LeafPlans.Length);
        foreach (var binding in planSet.LeafPlans)
        {
            var slice = binding.Slice.Id.Value;
            buildBySlice.TryGetValue(slice, out var build);
            promotionBySlice.TryGetValue(slice, out var promotionChild);
            var authority = new MaterializationRebuildLeafExecutionAuthority(
                MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
                planSetReference,
                binding);
            var ready = build is { Disposition: ProcessChildDisposition.Completed }
                ? ParseReady(build, authority, snapshot)
                : null;
            var promotion = promotionChild is { Disposition: ProcessChildDisposition.Completed }
                ? ParsePromotion(promotionChild, authority, ready, snapshot)
                : null;
            childEvidence.Add(new(
                binding,
                capacityBySlice[slice],
                build,
                ready,
                promotionChild,
                promotion));
        }

        var children = childEvidence.MoveToImmutable();
        foreach (var (slice, ready) in promotionWork)
        {
            var observed = children.Single(candidate => candidate.Binding.Slice.Id.Value == slice);
            if (observed.Ready != ready)
            {
                throw new ArgumentException(
                    "Promotion work does not consume the exact readiness result retained by its build child.",
                    nameof(snapshot));
            }
        }
        var (barrier, barrierJson) = ParseBarrier(
            planSet,
            state,
            children,
            ReadyBarrierBinding(artifacts),
            artifacts.ParentPlan,
            snapshot);
        var (receipt, receiptJson) = ParseReceipt(
            planSet,
            state,
            barrier,
            artifacts.ParentPlan,
            snapshot);

        return new(planSetReference, children, barrier, barrierJson, receipt, receiptJson);
    }

    static Dictionary<string, MaterializationReadyGenerationReference> ValidatePartitions(
        ProcessContinuationState state,
        MaterializationRebuildPlanSetReference planSet,
        IReadOnlyDictionary<string, MaterializationRebuildLeafPlanBinding> bindingBySlice,
        IReadOnlyDictionary<string, string> capacityBySlice,
        ProcessDurableStoreSnapshot snapshot)
    {
        var relevant = state.Partitions.Where(partition => partition.Node is var node
            && (node == MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId
                || node == MaterializationRebuildPlanSetProcessFactory.PromoteLeavesNodeId)).ToArray();
        if (relevant.GroupBy(static partition => partition.Node).Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "The current parent attempt cannot retain multiple occurrences of one bounded plan-set phase.",
                nameof(snapshot));
        }

        Dictionary<string, MaterializationReadyGenerationReference> promotionWork = new(StringComparer.Ordinal);
        foreach (var partition in relevant)
        {
            HashSet<string> observed = new(StringComparer.Ordinal);
            foreach (var work in partition.Work)
            {
                if (!bindingBySlice.TryGetValue(work.ProgressIdentity, out var binding)
                    || !observed.Add(work.ProgressIdentity)
                    || !capacityBySlice.TryGetValue(work.ProgressIdentity, out var capacity)
                    || !string.Equals(work.CapacityIdentity, capacity, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Bounded parent work contains an unknown, duplicate, or capacity-mismatched placement slice.",
                        nameof(snapshot));
                }

                try
                {
                    var payload = RequireWorkPayload(work, capacity);
                    var authority = new MaterializationRebuildLeafExecutionAuthority(
                        MaterializationRebuildLeafExecutionAuthority.CurrentSchemaVersion,
                        planSet,
                        binding);
                    if (partition.Node == MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId)
                    {
                        if (MaterializationRebuildWorkReferenceJsonSerializer.DeserializeAuthority(payload) != authority)
                            throw new JsonException("Build work contains a substituted linked-leaf authority.");
                    }
                    else
                    {
                        var ready = MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(payload);
                        if (ready.Authority != authority || !promotionWork.TryAdd(work.ProgressIdentity, ready))
                            throw new JsonException("Promotion work contains substituted or duplicate readiness evidence.");
                    }
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException)
                {
                    throw new ArgumentException(
                        "Bounded parent work retained an invalid or inexact leaf payload.",
                        nameof(snapshot),
                        exception);
                }
            }

            if (observed.Count != bindingBySlice.Count
                || bindingBySlice.Keys.Any(slice => !observed.Contains(slice)))
            {
                throw new ArgumentException(
                    "A retained bounded parent phase must cover every and only linked placement slice.",
                    nameof(snapshot));
            }
        }
        return promotionWork;
    }

    static string RequireWorkPayload(ProcessPartitionWorkState work, string capacityDomain)
    {
        var partition = work.Partition;
        if (partition.State != PortableValueState.Concrete
            || partition.Value is not { Kind: ObservationValueKind.Object } root
            || root.Fields is not { Count: 3 }
            || !root.TryGetProperty("sliceId", out var sliceValue)
            || sliceValue.Kind != ObservationValueKind.String
            || !string.Equals(sliceValue.String, work.ProgressIdentity, StringComparison.Ordinal)
            || !root.TryGetProperty("capacityDomain", out var capacityValue)
            || capacityValue.Kind != ObservationValueKind.String
            || !string.Equals(capacityValue.String, capacityDomain, StringComparison.Ordinal)
            || !root.TryGetProperty("payload", out var payloadValue)
            || payloadValue.Kind != ObservationValueKind.String
            || payloadValue.String is not { } payload)
        {
            throw new ArgumentException(
                "Partition work must retain exact slice, capacity-domain, and payload fields.",
                nameof(work));
        }
        return payload;
    }

    static Dictionary<string, ProcessChildState> Children(
        ProcessContinuationState state,
        ExecutionNodeId node,
        ExecutionDefinitionReference expectedProcess,
        IReadOnlyDictionary<string, MaterializationRebuildLeafPlanBinding> bindingBySlice,
        ProcessDurableStoreSnapshot snapshot)
    {
        Dictionary<string, ProcessChildState> result = new(StringComparer.Ordinal);
        foreach (var child in state.Children.Where(child => child.Node == node))
        {
            if (child.ProgressIdentity is not { } slice
                || !bindingBySlice.ContainsKey(slice)
                || child.Process != expectedProcess
                || !result.TryAdd(slice, child))
            {
                throw new ArgumentException(
                    "Parent child state contains an unknown, duplicated, or definition-mismatched placement leaf.",
                    nameof(snapshot));
            }
        }
        return result;
    }

    static MaterializationReadyGenerationReference ParseReady(
        ProcessChildState child,
        MaterializationRebuildLeafExecutionAuthority authority,
        ProcessDurableStoreSnapshot snapshot)
    {
        try
        {
            var ready = MaterializationReadyGenerationReferenceJsonSerializer.Deserialize(
                RequireString(child.Result, "completed build child"));
            if (ready.Authority != authority || ready.Attempt.Continuation != child.Continuation)
                throw new JsonException("Ready evidence does not belong to its exact linked build child.");
            return ready;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "A completed build child retained invalid or inexact ready-generation evidence.",
                nameof(snapshot),
                exception);
        }
    }

    static MaterializationIndependentPromotionResult ParsePromotion(
        ProcessChildState child,
        MaterializationRebuildLeafExecutionAuthority authority,
        MaterializationReadyGenerationReference? ready,
        ProcessDurableStoreSnapshot snapshot)
    {
        try
        {
            var promotion = MaterializationIndependentPromotionResultJsonSerializer.Deserialize(
                RequireString(child.Result, "completed promotion child"));
            if (promotion.Request.Authority != authority
                || ready is null
                || !ready.MatchesActiveGeneration(promotion.Request.ActiveGeneration))
            {
                throw new JsonException("Promotion evidence does not consume the exact linked readiness result.");
            }
            return promotion;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "A completed promotion child retained invalid or inexact independent-promotion evidence.",
                nameof(snapshot),
                exception);
        }
    }

    static (MaterializationRebuildReadyBarrier? Barrier, string? Json) ParseBarrier(
        MaterializationRebuildPlanSet planSet,
        ProcessContinuationState state,
        ImmutableArray<LeafEvidence> children,
        ValueBindingId barrierBinding,
        Cohesive.Processes.Compilation.CompiledProcessPlan parentPlan,
        ProcessDurableStoreSnapshot snapshot)
    {
        var values = state.Tokens
            .SelectMany(static token => token.Bindings)
            .Where(binding => binding.Binding == barrierBinding)
            .Select(static binding => binding.Value)
            .Distinct()
            .Take(2)
            .ToArray();
        if (values.Length == 0)
            return (null, null);
        if (values.Length != 1)
            throw new ArgumentException("The parent continuation retains conflicting readiness-barrier binding evidence.", nameof(snapshot));

        try
        {
            var value = values[0];
            if (value.State != PortableValueState.Concrete
                || value.Value is not { } root
                || !root.TryGetProperty("barrier", out var barrierValue)
                || barrierValue.Kind != ObservationValueKind.String
                || barrierValue.String is not { } json)
            {
                throw new JsonException("The readiness-barrier binding is not a concrete barrier result.");
            }
            var barrier = MaterializationRebuildReadyBarrierJsonSerializer.DeserializeStructural(json);
            barrier.ValidateAgainst(planSet, parentPlan, snapshot.Checkpoint);
            if (!barrier.ReadyGenerations.SequenceEqual(children.Select(static child => child.Ready!)))
            {
                throw new JsonException("The readiness barrier differs from exact build-child evidence.");
            }
            return (barrier, MaterializationRebuildReadyBarrierJsonSerializer.Serialize(barrier));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "The parent continuation retained invalid or inexact readiness-barrier evidence.",
                nameof(snapshot),
                exception);
        }
    }

    static (MaterializationRebuildPlanSetReceipt? Receipt, string? Json) ParseReceipt(
        MaterializationRebuildPlanSet planSet,
        ProcessContinuationState state,
        MaterializationRebuildReadyBarrier? barrier,
        Cohesive.Processes.Compilation.CompiledProcessPlan parentPlan,
        ProcessDurableStoreSnapshot snapshot)
    {
        var terminalValue = state.Terminal.Detail?.Value;
        if (terminalValue?.State != PortableValueState.Concrete
            || terminalValue.Value is not { Kind: ObservationValueKind.String, String: { } json }
            || !DeclaresSchema(json, MaterializationRebuildPlanSetReceipt.CurrentSchemaVersion))
        {
            if (state.Terminal.Kind == ExecutionTerminalOutcomeKind.Completed)
            {
                throw new ArgumentException(
                    "A completed plan-set parent must retain its exact canonical aggregate receipt.",
                    nameof(snapshot));
            }
            return (null, null);
        }

        try
        {
            var receipt = MaterializationRebuildPlanSetReceiptJsonSerializer.DeserializeStructural(json);
            var expectedTerminal = receipt.Outcome == MaterializationRebuildPlanSetOutcome.Failed
                ? ExecutionTerminalOutcomeKind.Failed
                : ExecutionTerminalOutcomeKind.Completed;
            if (state.Terminal.Kind != expectedTerminal)
            {
                throw new JsonException(
                    "Aggregate outcome and parent terminal kind contradict each other.");
            }
            receipt.ValidateAgainst(planSet, parentPlan, snapshot.Checkpoint);
            if (receipt.ReadyBarrier != barrier)
                throw new JsonException("The aggregate receipt differs from exact parent-attempt evidence.");
            return (receipt, MaterializationRebuildPlanSetReceiptJsonSerializer.Serialize(receipt));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new ArgumentException(
                "The parent continuation retained invalid or inexact aggregate receipt evidence.",
                nameof(snapshot),
                exception);
        }
    }

    static bool DeclaresSchema(string json, string schemaVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schemaVersion", out var value)
                && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), schemaVersion, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static ObservationValue ProjectChildren(StatusEvidence evidence)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(evidence.Children.Length);
        foreach (var child in evidence.Children)
        {
            var receipt = evidence.Receipt?.Leaves.Single(candidate =>
                candidate.Authority.PlacementSlice.Id == child.Binding.Slice.Id);
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("sliceId", ObservationValue.FromString(child.Binding.Slice.Id.Value));
            fields.Add("target", ObservationValue.FromString(child.Binding.Slice.Target.Value));
            fields.Add("placementSliceFingerprint", ProjectFingerprint(child.Binding.Slice.Fingerprint));
            fields.Add(
                "subjects",
                ObservationValue.FromImmutableArray(
                [
                    .. child.Binding.Slice.Subjects.Select(static subject =>
                        ObservationValue.FromString(subject.Value))
                ]));
            fields.Add("capacityDomain", ObservationValue.FromString(child.CapacityDomain));
            fields.Add("buildChild", ProjectContinuationOrNull(child.Build?.Continuation));
            fields.Add("buildDisposition", StringOrNull(child.Build?.Disposition.ToString()));
            fields.Add("buildTerminalOutcome", StringOrNull(child.Build?.TerminalOutcome?.Value));
            fields.Add("buildTerminalResult", StringOrNull(CanonicalJson(child.Build?.Result)));
            fields.Add("readyReference", StringOrNull(child.Ready is null
                ? null
                : MaterializationReadyGenerationReferenceJsonSerializer.Serialize(child.Ready)));
            fields.Add("promotionChild", ProjectContinuationOrNull(child.PromotionChild?.Continuation));
            fields.Add("promotionDisposition", StringOrNull(child.PromotionChild?.Disposition.ToString()));
            fields.Add("promotionTerminalOutcome", StringOrNull(child.PromotionChild?.TerminalOutcome?.Value));
            fields.Add("promotionChildResult", StringOrNull(CanonicalJson(child.PromotionChild?.Result)));
            fields.Add("promotionResult", StringOrNull(child.Promotion is null
                ? null
                : MaterializationIndependentPromotionResultJsonSerializer.Serialize(child.Promotion)));
            fields.Add("leafOutcome", StringOrNull(receipt?.Outcome.ToString()));
            fields.Add("failureEvidence", StringOrNull(receipt?.Failure is null
                ? null
                : CanonicalJson(receipt.Failure)));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectProgress(StatusEvidence evidence)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("buildStarted", ObservationValue.FromInt64(evidence.Children.Count(static child => child.Build is not null)));
        fields.Add("buildSettled", ObservationValue.FromInt64(evidence.Children.Count(static child =>
            child.Build is { } build && IsSettled(build.Disposition))));
        fields.Add("ready", ObservationValue.FromInt64(evidence.Children.Count(static child => child.Ready is not null)));
        fields.Add("promotionStarted", ObservationValue.FromInt64(evidence.Children.Count(static child => child.PromotionChild is not null)));
        fields.Add("promotionSettled", ObservationValue.FromInt64(evidence.Children.Count(static child =>
            child.PromotionChild is { } promotion && IsSettled(promotion.Disposition))));
        fields.Add("promoted", ObservationValue.FromInt64(evidence.Children.Count(static child =>
            child.Promotion is { IsCurrentlySelected: true })));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectCapacityDomains(MaterializationRebuildPlanSet planSet)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(planSet.Placement.CapacityDomains.Length);
        foreach (var domain in planSet.Placement.CapacityDomains)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            fields.Add("id", ObservationValue.FromString(domain.Id.Value));
            fields.Add("maximumParallelism", ObservationValue.FromInt64(domain.MaximumParallelism));
            values.Add(ObservationValue.FromObject(fields.ToImmutable()));
        }
        return ObservationValue.FromImmutableArray(values.MoveToImmutable());
    }

    static ObservationValue ProjectDefinition(ExecutionDefinitionReference definition)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("definitionId", ObservationValue.FromString(definition.DefinitionId.Value));
        fields.Add("revisionId", ObservationValue.FromString(definition.RevisionId.Value));
        fields.Add("fingerprint", ProjectFingerprint(definition.Fingerprint));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectFingerprint(ExecutionDefinitionFingerprint fingerprint)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("algorithm", ObservationValue.FromString(fingerprint.Algorithm));
        fields.Add("canonicalization", ObservationValue.FromString(fingerprint.Canonicalization));
        fields.Add("value", ObservationValue.FromString(fingerprint.Value));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectFingerprint(MaterializationRebuildPlanningFingerprint fingerprint)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("algorithm", ObservationValue.FromString(fingerprint.Algorithm));
        fields.Add("canonicalization", ObservationValue.FromString(fingerprint.Canonicalization));
        fields.Add("value", ObservationValue.FromString(fingerprint.Value));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectContinuation(ProcessContinuationIdentity continuation)
    {
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        fields.Add("processInstanceId", ObservationValue.FromString(continuation.ProcessInstanceId.Value));
        fields.Add("processAttemptId", ObservationValue.FromString(continuation.ProcessAttemptId.Value));
        return ObservationValue.FromObject(fields.ToImmutable());
    }

    static ObservationValue ProjectContinuationOrNull(ProcessContinuationIdentity? continuation) =>
        continuation is null ? ObservationValue.Null : ProjectContinuation(continuation);

    static ObservationValue StringOrNull(string? value) =>
        value is null ? ObservationValue.Null : ObservationValue.FromString(value);

    static string? CanonicalJson<T>(T? value)
        where T : class => value is null
        ? null
        : Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(
            value,
            CanonicalJsonOptions));

    static string RequireString(PortableValue? value, string role)
    {
        if (value?.State == PortableValueState.Concrete
            && value.Value is { Kind: ObservationValueKind.String, String: { } text })
        {
            return text;
        }
        throw new ArgumentException($"The {role} must retain one concrete string result.", nameof(value));
    }

    static bool IsSettled(ProcessChildDisposition disposition) => disposition is
        ProcessChildDisposition.Completed
        or ProcessChildDisposition.Failed
        or ProcessChildDisposition.Detached
        or ProcessChildDisposition.CancelledBeforeStart;

    static ValueBindingId ReadyBarrierBinding(MaterializationRebuildPlanSetProcessArtifacts artifacts)
    {
        var node = artifacts.ParentPlan.GetNode(MaterializationRebuildPlanSetProcessFactory.ReadinessBarrierNodeId);
        var request = node as RequestProcessNode
            ?? throw new InvalidOperationException("The exact parent readiness-barrier node is not a Request.");
        var outcome = request.Outcomes.Single(candidate =>
            candidate.Outcome == MaterializationRebuildPlanSetProcessFactory.ReadyOutcome);
        return outcome.Continuation.Output?.Binding
            ?? throw new InvalidOperationException("The exact parent readiness outcome has no durable result binding.");
    }

    static EnumTypeRef EnumType<TEnum>()
        where TEnum : struct, Enum =>
        new(typeof(TEnum).Name, [.. Enum.GetNames<TEnum>()]);

    sealed record LeafEvidence(
        MaterializationRebuildLeafPlanBinding Binding,
        string CapacityDomain,
        ProcessChildState? Build,
        MaterializationReadyGenerationReference? Ready,
        ProcessChildState? PromotionChild,
        MaterializationIndependentPromotionResult? Promotion);

    sealed record StatusEvidence(
        MaterializationRebuildPlanSetReference PlanSet,
        ImmutableArray<LeafEvidence> Children,
        MaterializationRebuildReadyBarrier? Barrier,
        string? BarrierJson,
        MaterializationRebuildPlanSetReceipt? Receipt,
        string? ReceiptJson);
}
