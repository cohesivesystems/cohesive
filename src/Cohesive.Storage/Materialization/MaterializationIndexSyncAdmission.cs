using System.Collections.Immutable;
using Cohesive.Control;

namespace Cohesive.Storage.Materialization;

/// <summary>Typed physical admission coordinate separating source, transform, and target resource namespaces.</summary>
public readonly record struct MaterializationIndexSyncAdmissionResource
{
    /// <summary>Creates a stage-qualified physical resource coordinate.</summary>
    /// <param name="stage">Pipeline stage whose work shares the resource.</param>
    /// <param name="physicalIdentity">Stable adapter or transform resource identity.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stage"/> is unsupported.</exception>
    /// <exception cref="ArgumentException"><paramref name="physicalIdentity"/> is empty or white space.</exception>
    public MaterializationIndexSyncAdmissionResource(ControlStageKind stage, string physicalIdentity)
    {
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported Control stage.");
        if (string.IsNullOrWhiteSpace(physicalIdentity))
            throw new ArgumentException("An admission resource requires a physical identity.", nameof(physicalIdentity));

        Stage = stage;
        PhysicalIdentity = physicalIdentity;
    }

    /// <summary>Pipeline stage owning this admission namespace.</summary>
    public ControlStageKind Stage { get; }

    /// <summary>Stable physical identity within the stage namespace.</summary>
    public string PhysicalIdentity { get; }

    /// <summary>Formats the coordinate without collapsing its typed equality semantics.</summary>
    /// <returns>A stage-qualified diagnostic representation.</returns>
    public override string ToString() => $"{Stage}/{PhysicalIdentity}";
}

/// <summary>Current non-preemptive capacity limits for one shared physical index-sync resource.</summary>
public readonly record struct MaterializationIndexSyncAdmissionLimits
{
    /// <summary>Creates explicit shared-pool limits.</summary>
    /// <param name="totalMaximum">Maximum total in-flight operations.</param>
    /// <param name="realtimeMaximum">Maximum realtime operations within total capacity.</param>
    /// <param name="rebuildMaximum">Maximum rebuild operations within unreserved surplus capacity.</param>
    /// <param name="realtimeReservation">Explicit slots unavailable to rebuild and reserved for realtime work.</param>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive or exceeds total capacity.</exception>
    public MaterializationIndexSyncAdmissionLimits(
        int totalMaximum,
        int realtimeMaximum,
        int rebuildMaximum,
        int realtimeReservation)
    {
        if (totalMaximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalMaximum), totalMaximum, "Total capacity must be positive.");
        if (realtimeMaximum <= 0 || realtimeMaximum > totalMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realtimeMaximum),
                realtimeMaximum,
                "Realtime capacity must be positive and no larger than total capacity.");
        }
        if (rebuildMaximum <= 0 || rebuildMaximum > totalMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rebuildMaximum),
                rebuildMaximum,
                "Rebuild surplus capacity must be positive and no larger than total capacity.");
        }
        if (realtimeReservation < 0 || realtimeReservation >= totalMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realtimeReservation),
                realtimeReservation,
                "Realtime reservation must be non-negative and leave positive surplus capacity.");
        }
        if (rebuildMaximum > totalMaximum - realtimeReservation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rebuildMaximum),
                rebuildMaximum,
                "Rebuild admission cannot consume slots explicitly reserved for realtime work.");
        }

        TotalMaximum = totalMaximum;
        RealtimeMaximum = realtimeMaximum;
        RebuildMaximum = rebuildMaximum;
        RealtimeReservation = realtimeReservation;
    }

    /// <summary>Maximum total in-flight operations.</summary>
    public int TotalMaximum { get; }

    /// <summary>Maximum realtime in-flight operations.</summary>
    public int RealtimeMaximum { get; }

    /// <summary>Maximum rebuild operations admitted from unreserved surplus.</summary>
    public int RebuildMaximum { get; }

    /// <summary>Explicit total-capacity slots reserved for realtime and unavailable to rebuild.</summary>
    public int RealtimeReservation { get; }
}

/// <summary>Exact workload-owned, revision-fenced contribution to one shared admission resource.</summary>
/// <remarks>
/// Contributions update only their declared workload. The gate combines independently owned realtime and rebuild
/// maxima, so a candidate-generation rebuild cannot overwrite an active-generation realtime decision.
/// </remarks>
internal sealed record MaterializationIndexSyncAdmissionContribution
{
    internal MaterializationIndexSyncAdmissionContribution(
        MaterializationRebuildPlanFingerprint planFingerprint,
        MaterializationTargetId targetId,
        MaterializationGenerationId generationId,
        MaterializationIndexSyncWorkloadKind workload,
        ControlStageKind stage,
        int totalMaximum,
        int maximumConcurrency,
        int realtimeReservation,
        ImmutableArray<MaterializationIndexSyncControlSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(planFingerprint);
        MaterializationContract.RequireDefinedIdentity(targetId.Value, nameof(targetId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        if (!Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unsupported index-sync workload.");
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported Control stage.");
        if (totalMaximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(totalMaximum), totalMaximum, "Total capacity must be positive.");
        if (maximumConcurrency <= 0 || maximumConcurrency > totalMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency),
                maximumConcurrency,
                "A workload contribution must be positive and no larger than total capacity.");
        }
        if (realtimeReservation < 0 || realtimeReservation >= totalMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realtimeReservation),
                realtimeReservation,
                "Realtime reservation must be non-negative and leave positive rebuild surplus.");
        }

        var normalized = snapshots.IsDefault ? [] : snapshots;
        var observedKeys = new HashSet<MaterializationIndexSyncControlStateKey>();
        foreach (var snapshot in normalized)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Key.PlanFingerprint != planFingerprint
                || snapshot.Key.TargetId != targetId
                || snapshot.Key.GenerationId != generationId
                || snapshot.Key.Workload != workload
                || snapshot.Realization.EffectiveDefinition.Stage != stage)
            {
                throw new ArgumentException(
                    "Admission evidence must belong to the contribution's exact plan, target, generation, workload, and stage.",
                    nameof(snapshots));
            }
            if (!observedKeys.Add(snapshot.Key))
                throw new ArgumentException("Admission evidence cannot repeat a Control state key.", nameof(snapshots));
        }

        PlanFingerprint = planFingerprint;
        TargetId = targetId;
        GenerationId = generationId;
        Workload = workload;
        Stage = stage;
        TotalMaximum = totalMaximum;
        MaximumConcurrency = maximumConcurrency;
        RealtimeReservation = realtimeReservation;
        Snapshots = normalized;
    }

    internal MaterializationRebuildPlanFingerprint PlanFingerprint { get; }

    internal MaterializationTargetId TargetId { get; }

    internal MaterializationGenerationId GenerationId { get; }

    internal MaterializationIndexSyncWorkloadKind Workload { get; }

    internal ControlStageKind Stage { get; }

    internal int TotalMaximum { get; }

    internal int MaximumConcurrency { get; }

    internal int RealtimeReservation { get; }

    internal ImmutableArray<MaterializationIndexSyncControlSnapshot> Snapshots { get; }

    internal ContributionKey Key => new(PlanFingerprint, TargetId, GenerationId, Workload, Stage);

    internal readonly record struct ContributionKey(
        MaterializationRebuildPlanFingerprint PlanFingerprint,
        MaterializationTargetId TargetId,
        MaterializationGenerationId GenerationId,
        MaterializationIndexSyncWorkloadKind Workload,
        ControlStageKind Stage);
}

/// <summary>Truthful point-in-time queue and in-flight evidence for one shared admission resource.</summary>
public readonly record struct MaterializationIndexSyncAdmissionSnapshot
{
    /// <summary>Creates a queue snapshot.</summary>
    /// <param name="resource">Stage-qualified physical resource identity.</param>
    /// <param name="limits">Currently applied non-preemptive limits.</param>
    /// <param name="queuedRealtime">Realtime waiters.</param>
    /// <param name="queuedRebuild">Rebuild waiters.</param>
    /// <param name="inFlightRealtime">Admitted realtime operations.</param>
    /// <param name="inFlightRebuild">Admitted rebuild operations.</param>
    /// <exception cref="ArgumentException"><paramref name="resource"/> is empty or a count is negative.</exception>
    public MaterializationIndexSyncAdmissionSnapshot(
        MaterializationIndexSyncAdmissionResource resource,
        MaterializationIndexSyncAdmissionLimits limits,
        int queuedRealtime,
        int queuedRebuild,
        int inFlightRealtime,
        int inFlightRebuild)
    {
        if (string.IsNullOrWhiteSpace(resource.PhysicalIdentity))
            throw new ArgumentException("An admission snapshot requires a physical resource identity.", nameof(resource));
        if (queuedRealtime < 0 || queuedRebuild < 0 || inFlightRealtime < 0 || inFlightRebuild < 0)
            throw new ArgumentException("Admission queue and in-flight counts cannot be negative.", nameof(queuedRealtime));

        Resource = resource;
        Limits = limits;
        QueuedRealtime = queuedRealtime;
        QueuedRebuild = queuedRebuild;
        InFlightRealtime = inFlightRealtime;
        InFlightRebuild = inFlightRebuild;
    }

    /// <summary>Stable physical resource identity.</summary>
    public MaterializationIndexSyncAdmissionResource Resource { get; }

    /// <summary>Currently applied non-preemptive limits.</summary>
    public MaterializationIndexSyncAdmissionLimits Limits { get; }

    /// <summary>Realtime work awaiting admission.</summary>
    public int QueuedRealtime { get; }

    /// <summary>Rebuild work awaiting surplus admission.</summary>
    public int QueuedRebuild { get; }

    /// <summary>Currently admitted realtime work.</summary>
    public int InFlightRealtime { get; }

    /// <summary>Currently admitted rebuild work.</summary>
    public int InFlightRebuild { get; }

    /// <summary>Total queued work.</summary>
    public int QueuedTotal => checked(QueuedRealtime + QueuedRebuild);

    /// <summary>Total in-flight work.</summary>
    public int InFlightTotal => checked(InFlightRealtime + InFlightRebuild);

    /// <summary>Creates an available typed queue-depth measurement.</summary>
    /// <returns>Exact last-value queue-depth evidence with one sample.</returns>
    public ControlMeasurement ToQueueDepthMeasurement() => new(
        metric: ControlMetricKind.QueueDepth,
        statistic: ControlStatisticKind.Last,
        availability: ControlMeasurementAvailability.Available,
        value: new(QueuedTotal, ControlUnit.Count),
        sampleCount: 1);
}

/// <summary>Pure deterministic realtime-first admission policy.</summary>
public static class MaterializationIndexSyncAdmissionPolicy
{
    /// <summary>Determines whether the next waiter of one workload may enter without preemption.</summary>
    /// <param name="snapshot">Current truthful resource snapshot.</param>
    /// <param name="workload">Candidate workload.</param>
    /// <returns><see langword="true"/> when the candidate may be admitted now.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workload"/> is unsupported.</exception>
    public static bool CanAdmit(
        MaterializationIndexSyncAdmissionSnapshot snapshot,
        MaterializationIndexSyncWorkloadKind workload)
    {
        if (!Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unsupported index-sync workload.");
        if (snapshot.InFlightTotal >= snapshot.Limits.TotalMaximum)
            return false;

        return workload switch
        {
            MaterializationIndexSyncWorkloadKind.Realtime =>
                snapshot.InFlightRealtime < snapshot.Limits.RealtimeMaximum,
            MaterializationIndexSyncWorkloadKind.Rebuild =>
                snapshot.QueuedRealtime == 0
                && snapshot.InFlightRebuild < snapshot.Limits.RebuildMaximum,
            _ => false
        };
    }
}

/// <summary>Non-preemptive admission lease for one shared physical resource.</summary>
public sealed class MaterializationIndexSyncAdmissionLease : IAsyncDisposable, IDisposable
{
    Action? release;

    internal MaterializationIndexSyncAdmissionLease(Action release) => this.release = release;

    /// <summary>Releases the admitted slot exactly once.</summary>
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();

    /// <summary>Releases the admitted slot exactly once.</summary>
    /// <returns>A synchronously completed operation.</returns>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Shared realtime-first, non-preemptive admission gate for physical index-sync resources.</summary>
public sealed class MaterializationIndexSyncAdmissionGate
{
    readonly object gate = new();
    readonly Dictionary<MaterializationIndexSyncAdmissionResource, ResourceState> resources = [];
    readonly HashSet<MaterializationIndexSyncAdmissionContribution.ContributionKey> retiredContributions = [];

    /// <summary>Applies new limits at an exact caller-attested work-admission boundary.</summary>
    /// <remarks>Lower limits drain already-admitted work; they never preempt it.</remarks>
    /// <param name="resource">Stable physical resource identity.</param>
    /// <param name="limits">New applied limits.</param>
    /// <exception cref="ArgumentException"><paramref name="resource"/> is empty.</exception>
    public void ApplyLimits(
        MaterializationIndexSyncAdmissionResource resource,
        MaterializationIndexSyncAdmissionLimits limits)
    {
        if (string.IsNullOrWhiteSpace(resource.PhysicalIdentity))
            throw new ArgumentException("An admission gate requires a physical resource identity.", nameof(resource));
        lock (gate)
        {
            var state = GetOrCreate(resource, limits);
            if (state.Authority == ResourceLimitAuthority.Contribution)
            {
                throw new InvalidOperationException(
                    "Unversioned limits cannot replace revision-fenced workload contributions on the same resource.");
            }
            state.Limits = limits;
            Dispatch(state);
        }
    }

    /// <summary>Applies one exact workload-owned contribution without replacing the other workload's limits.</summary>
    /// <param name="resource">Stable physical resource identity.</param>
    /// <param name="contribution">Exact plan, generation, workload, and durable-revision evidence.</param>
    /// <returns>The combined effective limits after accepting or ignoring the contribution.</returns>
    /// <remarks>
    /// A delayed contribution whose revision vector is older than the retained vector is ignored. Contributions from
    /// distinct generations or plans remain independently fenced and combine conservatively until lifecycle code
    /// explicitly retires them in a future durable admission realization.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="contribution"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="resource"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// Equal revision evidence changes limits, a contributor changes its loop set, or physical capacities conflict.
    /// </exception>
    internal MaterializationIndexSyncAdmissionLimits ApplyContribution(
        MaterializationIndexSyncAdmissionResource resource,
        MaterializationIndexSyncAdmissionContribution contribution)
    {
        if (string.IsNullOrWhiteSpace(resource.PhysicalIdentity))
            throw new ArgumentException("An admission gate requires a physical resource identity.", nameof(resource));
        ArgumentNullException.ThrowIfNull(contribution);
        if (resource.Stage != contribution.Stage)
        {
            throw new ArgumentException(
                "An admission contribution must belong to the resource coordinate's exact stage.",
                nameof(contribution));
        }

        lock (gate)
        {
            if (retiredContributions.Contains(contribution.Key))
            {
                throw new InvalidOperationException(
                    "A retired admission contributor cannot re-enter after its fenced generation lifecycle cut.");
            }
            resources.TryGetValue(resource, out var retained);
            if (retained is not null && retained.Authority != ResourceLimitAuthority.Contribution)
            {
                throw new InvalidOperationException(
                    "Revision-fenced contributions cannot replace an unversioned admission resource.");
            }
            if (retained is not null
                && retained.Contributions.TryGetValue(contribution.Key, out var previous)
                && !CanReplace(previous, contribution))
            {
                return retained.Limits;
            }

            var effective = CalculateLimits(retained, contribution);
            var state = retained ?? new ResourceState(resource, effective, ResourceLimitAuthority.Contribution);
            if (retained is null)
                resources.Add(resource, state);
            state.Contributions[contribution.Key] = contribution;
            state.Limits = effective;
            Dispatch(state);
            return effective;
        }
    }

    /// <summary>Retires one exact generation/workload contribution set at a durable lifecycle cut.</summary>
    /// <param name="planFingerprint">Exact persisted plan owning the generation.</param>
    /// <param name="targetId">Exact physical target owning the target-local generation.</param>
    /// <param name="generationId">Generation proven abandoned, displaced, or finished for the workload.</param>
    /// <param name="workload">Workload whose contribution ownership ended.</param>
    /// <remarks>
    /// Retirement tombstones every stage coordinate before removing current contributions, so delayed operations from
    /// the retired generation fail closed and cannot re-register stale limits.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="planFingerprint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A target or generation identity is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workload"/> is unsupported.</exception>
    internal void RetireContributions(
        MaterializationRebuildPlanFingerprint planFingerprint,
        MaterializationTargetId targetId,
        MaterializationGenerationId generationId,
        MaterializationIndexSyncWorkloadKind workload)
    {
        ArgumentNullException.ThrowIfNull(planFingerprint);
        MaterializationContract.RequireDefinedIdentity(targetId.Value, nameof(targetId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        if (!Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unsupported index-sync workload.");

        lock (gate)
        {
            foreach (var stage in Enum.GetValues<ControlStageKind>())
            {
                retiredContributions.Add(new(
                    planFingerprint,
                    targetId,
                    generationId,
                    workload,
                    stage));
            }

            foreach (var state in resources.Values)
            {
                RetireQueuedWaiters(
                    state,
                    planFingerprint,
                    targetId,
                    generationId,
                    workload);
                var removed = false;
                foreach (var key in state.Contributions.Keys.ToArray())
                {
                    if (key.PlanFingerprint != planFingerprint
                        || key.TargetId != targetId
                        || key.GenerationId != generationId
                        || key.Workload != workload)
                    {
                        continue;
                    }
                    state.Contributions.Remove(key);
                    removed = true;
                }
                if (!removed || state.Contributions.Count == 0)
                    continue;
                state.Limits = CalculateLimits(state);
                Dispatch(state);
            }
        }
    }

    /// <summary>Waits for workload-prioritized admission to one physical resource.</summary>
    /// <param name="resource">Stable physical resource identity.</param>
    /// <param name="workload">Explicit realtime or rebuild workload.</param>
    /// <param name="limits">Initial limits when the resource has not been registered.</param>
    /// <param name="cancellationToken">Cancellation while queued.</param>
    /// <returns>A lease that releases the admitted slot exactly once.</returns>
    /// <exception cref="ArgumentException"><paramref name="resource"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workload"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">
    /// The resource is owned by revision-fenced workload contributions and cannot admit unversioned work.
    /// </exception>
    /// <exception cref="OperationCanceledException">Cancellation occurs before admission.</exception>
    public ValueTask<MaterializationIndexSyncAdmissionLease> AcquireAsync(
        MaterializationIndexSyncAdmissionResource resource,
        MaterializationIndexSyncWorkloadKind workload,
        MaterializationIndexSyncAdmissionLimits limits,
        CancellationToken cancellationToken = default) => AcquireAsync(
            resource,
            workload,
            limits,
            contributionKey: null,
            cancellationToken);

    internal ValueTask<MaterializationIndexSyncAdmissionLease> AcquireAsync(
        MaterializationIndexSyncAdmissionResource resource,
        MaterializationIndexSyncWorkloadKind workload,
        MaterializationIndexSyncAdmissionLimits limits,
        MaterializationIndexSyncAdmissionContribution.ContributionKey contributionKey,
        CancellationToken cancellationToken) => AcquireAsync(
            resource,
            workload,
            limits,
            (MaterializationIndexSyncAdmissionContribution.ContributionKey?)contributionKey,
            cancellationToken);

    ValueTask<MaterializationIndexSyncAdmissionLease> AcquireAsync(
        MaterializationIndexSyncAdmissionResource resource,
        MaterializationIndexSyncWorkloadKind workload,
        MaterializationIndexSyncAdmissionLimits limits,
        MaterializationIndexSyncAdmissionContribution.ContributionKey? contributionKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(resource.PhysicalIdentity))
            throw new ArgumentException("An admission gate requires a physical resource identity.", nameof(resource));
        if (!Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unsupported index-sync workload.");
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (gate)
        {
            var state = GetOrCreate(resource, limits);
            if (contributionKey is { } key)
            {
                if (key.Stage != resource.Stage || key.Workload != workload)
                {
                    throw new ArgumentException(
                        "An admission waiter must belong to its contribution's exact stage and workload.",
                        nameof(contributionKey));
                }
                if (retiredContributions.Contains(key) || !state.Contributions.ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        "A retired or unregistered admission contributor cannot queue new work.");
                }
            }
            else if (state.Authority == ResourceLimitAuthority.Contribution)
            {
                throw new InvalidOperationException(
                    "Unversioned admission cannot consume a revision-fenced contribution resource.");
            }

            waiter = new(state, workload, contributionKey);
            waiter.Node = workload == MaterializationIndexSyncWorkloadKind.Realtime
                ? state.Realtime.AddLast(waiter)
                : state.Rebuild.AddLast(waiter);
            waiter.RegisterCancellation(this, cancellationToken);
            if (waiter.Node is not null)
                Dispatch(state);
        }

        return new(waiter.Completion.Task);
    }

    /// <summary>Reads a truthful queue and in-flight snapshot.</summary>
    /// <param name="resource">Stable physical resource identity.</param>
    /// <returns>Current snapshot, or <see langword="null"/> when the resource has never been registered.</returns>
    /// <exception cref="ArgumentException"><paramref name="resource"/> is empty.</exception>
    public MaterializationIndexSyncAdmissionSnapshot? GetSnapshot(MaterializationIndexSyncAdmissionResource resource)
    {
        if (string.IsNullOrWhiteSpace(resource.PhysicalIdentity))
            throw new ArgumentException("An admission gate requires a physical resource identity.", nameof(resource));
        lock (gate)
        {
            return resources.TryGetValue(resource, out var state)
                ? Snapshot(state)
                : null;
        }
    }

    /// <summary>Reads all registered resource snapshots in canonical identity order.</summary>
    /// <returns>Immutable truthful snapshots.</returns>
    public ImmutableArray<MaterializationIndexSyncAdmissionSnapshot> GetSnapshots()
    {
        lock (gate)
        {
            return
            [
                .. resources.Values
                    .OrderBy(static state => state.Resource.Stage)
                    .ThenBy(static state => state.Resource.PhysicalIdentity, StringComparer.Ordinal)
                    .Select(Snapshot)
            ];
        }
    }

    ResourceState GetOrCreate(
        MaterializationIndexSyncAdmissionResource resource,
        MaterializationIndexSyncAdmissionLimits limits)
    {
        if (resources.TryGetValue(resource, out var state))
            return state;
        state = new(resource, limits, ResourceLimitAuthority.Unversioned);
        resources.Add(resource, state);
        return state;
    }

    void Dispatch(ResourceState state)
    {
        while (state.InFlightRealtime + state.InFlightRebuild < state.Limits.TotalMaximum)
        {
            Waiter? waiter = null;
            if (state.Realtime.First is { } realtime
                && state.InFlightRealtime < state.Limits.RealtimeMaximum)
            {
                waiter = realtime.Value;
                state.Realtime.RemoveFirst();
                state.InFlightRealtime++;
            }
            else if (state.Realtime.Count == 0
                && state.Rebuild.First is { } rebuild
                && state.InFlightRebuild < state.Limits.RebuildMaximum)
            {
                waiter = rebuild.Value;
                state.Rebuild.RemoveFirst();
                state.InFlightRebuild++;
            }
            else
            {
                break;
            }

            waiter.Node = null;
            _ = waiter.CancellationRegistration.Unregister();
            waiter.Completion.TrySetResult(new(() => Release(state, waiter.Workload)));
        }
    }

    void Release(ResourceState state, MaterializationIndexSyncWorkloadKind workload)
    {
        lock (gate)
        {
            if (workload == MaterializationIndexSyncWorkloadKind.Realtime)
                state.InFlightRealtime--;
            else
                state.InFlightRebuild--;
            Dispatch(state);
        }
    }

    void Cancel(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (waiter.Node is null)
                return;
            if (waiter.Workload == MaterializationIndexSyncWorkloadKind.Realtime)
                waiter.State.Realtime.Remove(waiter.Node);
            else
                waiter.State.Rebuild.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Completion.TrySetCanceled(cancellationToken);
            Dispatch(waiter.State);
        }
    }

    static void RetireQueuedWaiters(
        ResourceState state,
        MaterializationRebuildPlanFingerprint planFingerprint,
        MaterializationTargetId targetId,
        MaterializationGenerationId generationId,
        MaterializationIndexSyncWorkloadKind workload)
    {
        RetireQueuedWaiters(state.Realtime, planFingerprint, targetId, generationId, workload);
        RetireQueuedWaiters(state.Rebuild, planFingerprint, targetId, generationId, workload);
    }

    static void RetireQueuedWaiters(
        LinkedList<Waiter> queue,
        MaterializationRebuildPlanFingerprint planFingerprint,
        MaterializationTargetId targetId,
        MaterializationGenerationId generationId,
        MaterializationIndexSyncWorkloadKind workload)
    {
        var node = queue.First;
        while (node is not null)
        {
            var next = node.Next;
            var waiter = node.Value;
            if (waiter.ContributionKey is { } key
                && key.PlanFingerprint == planFingerprint
                && key.TargetId == targetId
                && key.GenerationId == generationId
                && key.Workload == workload)
            {
                queue.Remove(node);
                waiter.Node = null;
                _ = waiter.CancellationRegistration.Unregister();
                waiter.Completion.TrySetException(new InvalidOperationException(
                    "Queued admission work was retired at its generation lifecycle cut."));
            }
            node = next;
        }
    }

    static MaterializationIndexSyncAdmissionSnapshot Snapshot(ResourceState state) => new(
        state.Resource,
        state.Limits,
        state.Realtime.Count,
        state.Rebuild.Count,
        state.InFlightRealtime,
        state.InFlightRebuild);

    static bool CanReplace(
        MaterializationIndexSyncAdmissionContribution retained,
        MaterializationIndexSyncAdmissionContribution candidate)
    {
        if (retained.Snapshots.Length != candidate.Snapshots.Length)
        {
            throw new InvalidOperationException(
                "One admission contributor cannot change its revision-fenced Control loop set.");
        }

        var advanced = false;
        for (var index = 0; index < retained.Snapshots.Length; index++)
        {
            var prior = retained.Snapshots[index];
            MaterializationIndexSyncControlSnapshot? current = null;
            for (var candidateIndex = 0; candidateIndex < candidate.Snapshots.Length; candidateIndex++)
            {
                if (candidate.Snapshots[candidateIndex].Key == prior.Key)
                {
                    current = candidate.Snapshots[candidateIndex];
                    break;
                }
            }
            if (current is null)
            {
                throw new InvalidOperationException(
                    "One admission contributor cannot change its revision-fenced Control loop set.");
            }
            if (current.State.Revision.Ordinal < prior.State.Revision.Ordinal)
                return false;
            if (current.State.Revision.Ordinal > prior.State.Revision.Ordinal)
                advanced = true;
        }

        if (!advanced
            && (retained.TotalMaximum != candidate.TotalMaximum
                || retained.MaximumConcurrency != candidate.MaximumConcurrency
                || retained.RealtimeReservation != candidate.RealtimeReservation))
        {
            throw new InvalidOperationException(
                "Equal admission revision evidence cannot produce different physical or workload limits.");
        }
        return true;
    }

    static MaterializationIndexSyncAdmissionLimits CalculateLimits(
        ResourceState? state,
        MaterializationIndexSyncAdmissionContribution candidate)
    {
        var totalMaximum = candidate.TotalMaximum;
        var realtimeReservation = candidate.RealtimeReservation;
        var realtimeMaximum = candidate.Workload == MaterializationIndexSyncWorkloadKind.Realtime
            ? candidate.MaximumConcurrency
            : int.MaxValue;
        var rebuildMaximum = candidate.Workload == MaterializationIndexSyncWorkloadKind.Rebuild
            ? candidate.MaximumConcurrency
            : int.MaxValue;

        if (state is not null)
        {
            foreach (var pair in state.Contributions)
            {
                if (pair.Key == candidate.Key)
                    continue;
                var contribution = pair.Value;
                totalMaximum = Math.Min(totalMaximum, contribution.TotalMaximum);
                realtimeReservation = Math.Max(realtimeReservation, contribution.RealtimeReservation);
                if (contribution.Workload == MaterializationIndexSyncWorkloadKind.Realtime)
                    realtimeMaximum = Math.Min(realtimeMaximum, contribution.MaximumConcurrency);
                else
                    rebuildMaximum = Math.Min(rebuildMaximum, contribution.MaximumConcurrency);
            }
        }

        if (realtimeReservation >= totalMaximum)
        {
            throw new InvalidOperationException(
                $"Admission contributions reserve {realtimeReservation} realtime slots from shared capacity {totalMaximum}.");
        }
        if (realtimeMaximum == int.MaxValue)
            realtimeMaximum = totalMaximum;
        if (rebuildMaximum == int.MaxValue)
            rebuildMaximum = totalMaximum - realtimeReservation;

        return new(
            totalMaximum,
            realtimeMaximum: Math.Min(realtimeMaximum, totalMaximum),
            rebuildMaximum: Math.Min(rebuildMaximum, totalMaximum - realtimeReservation),
            realtimeReservation);
    }

    static MaterializationIndexSyncAdmissionLimits CalculateLimits(ResourceState state)
    {
        using var enumerator = state.Contributions.Values.GetEnumerator();
        if (!enumerator.MoveNext())
            return state.Limits;
        var first = enumerator.Current;
        var totalMaximum = first.TotalMaximum;
        var realtimeReservation = first.RealtimeReservation;
        var realtimeMaximum = first.Workload == MaterializationIndexSyncWorkloadKind.Realtime
            ? first.MaximumConcurrency
            : int.MaxValue;
        var rebuildMaximum = first.Workload == MaterializationIndexSyncWorkloadKind.Rebuild
            ? first.MaximumConcurrency
            : int.MaxValue;
        while (enumerator.MoveNext())
        {
            var contribution = enumerator.Current;
            totalMaximum = Math.Min(totalMaximum, contribution.TotalMaximum);
            realtimeReservation = Math.Max(realtimeReservation, contribution.RealtimeReservation);
            if (contribution.Workload == MaterializationIndexSyncWorkloadKind.Realtime)
                realtimeMaximum = Math.Min(realtimeMaximum, contribution.MaximumConcurrency);
            else
                rebuildMaximum = Math.Min(rebuildMaximum, contribution.MaximumConcurrency);
        }
        if (realtimeReservation >= totalMaximum)
        {
            throw new InvalidOperationException(
                $"Admission contributions reserve {realtimeReservation} realtime slots from shared capacity {totalMaximum}.");
        }
        if (realtimeMaximum == int.MaxValue)
            realtimeMaximum = totalMaximum;
        if (rebuildMaximum == int.MaxValue)
            rebuildMaximum = totalMaximum - realtimeReservation;
        return new(
            totalMaximum,
            realtimeMaximum: Math.Min(realtimeMaximum, totalMaximum),
            rebuildMaximum: Math.Min(rebuildMaximum, totalMaximum - realtimeReservation),
            realtimeReservation);
    }

    sealed class ResourceState(
        MaterializationIndexSyncAdmissionResource resource,
        MaterializationIndexSyncAdmissionLimits limits,
        ResourceLimitAuthority authority)
    {
        internal MaterializationIndexSyncAdmissionResource Resource { get; } = resource;

        internal MaterializationIndexSyncAdmissionLimits Limits { get; set; } = limits;

        internal ResourceLimitAuthority Authority { get; } = authority;

        internal Dictionary<MaterializationIndexSyncAdmissionContribution.ContributionKey,
            MaterializationIndexSyncAdmissionContribution> Contributions { get; } = [];

        internal LinkedList<Waiter> Realtime { get; } = [];

        internal LinkedList<Waiter> Rebuild { get; } = [];

        internal int InFlightRealtime { get; set; }

        internal int InFlightRebuild { get; set; }
    }

    enum ResourceLimitAuthority
    {
        Unversioned,
        Contribution
    }

    sealed class Waiter
    {
        internal Waiter(
            ResourceState state,
            MaterializationIndexSyncWorkloadKind workload,
            MaterializationIndexSyncAdmissionContribution.ContributionKey? contributionKey)
        {
            State = state;
            Workload = workload;
            ContributionKey = contributionKey;
        }

        internal ResourceState State { get; }

        internal MaterializationIndexSyncWorkloadKind Workload { get; }

        internal MaterializationIndexSyncAdmissionContribution.ContributionKey? ContributionKey { get; }

        internal TaskCompletionSource<MaterializationIndexSyncAdmissionLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal CancellationTokenRegistration CancellationRegistration { get; private set; }

        internal LinkedListNode<Waiter>? Node { get; set; }

        internal void RegisterCancellation(
            MaterializationIndexSyncAdmissionGate owner,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.CanBeCanceled)
                CancellationRegistration = cancellationToken.Register(() => owner.Cancel(this, cancellationToken));
        }
    }
}
