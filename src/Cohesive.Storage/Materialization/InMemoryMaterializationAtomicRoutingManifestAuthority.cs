namespace Cohesive.Storage.Materialization;

/// <summary>Linearizable in-memory reference interpretation of one complete routing-manifest authority.</summary>
/// <remarks>
/// One semaphore is the manifest-wide linearization point. The implementation deliberately has no per-entry
/// mutation path: a successful compare-and-swap replaces every read and incremental-write route together.
/// </remarks>
public sealed class InMemoryMaterializationAtomicRoutingManifestAuthority :
    IMaterializationAtomicRoutingManifestAuthority,
    IDisposable
{
    readonly SemaphoreSlim gate = new(initialCount: 1, maxCount: 1);
    readonly MaterializationAtomicRoutingManifestRealization realization;
    readonly TimeProvider timeProvider;
    readonly Dictionary<MaterializationBackendRoutingCommandId, StoredCommit> commits = [];
    readonly Dictionary<MaterializationBackendRoutingCommandId, string> intents = [];
    MaterializationRoutingManifestSnapshot snapshot;
    bool disposed;

    /// <summary>Creates one authority for an exact compiled atomic realization.</summary>
    /// <param name="realization">Exact capability match implemented by this authority.</param>
    /// <param name="initialSnapshot">Optional complete initial state; defaults to an uninitialized manifest.</param>
    /// <param name="timeProvider">Clock used to timestamp successful commits.</param>
    /// <exception cref="ArgumentNullException"><paramref name="realization"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="initialSnapshot"/> is outside the exact authority scope.</exception>
    public InMemoryMaterializationAtomicRoutingManifestAuthority(
        MaterializationAtomicRoutingManifestRealization realization,
        MaterializationRoutingManifestSnapshot? initialSnapshot = null,
        TimeProvider? timeProvider = null)
    {
        this.realization = realization ?? throw new ArgumentNullException(nameof(realization));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        snapshot = initialSnapshot ?? InitialSnapshot(realization.Requirement);
        if (!Owns(snapshot))
        {
            throw new ArgumentException(
                "The initial manifest must cover the authority's exact plan-set and placement scope.",
                nameof(initialSnapshot));
        }
    }

    /// <inheritdoc />
    public MaterializationAtomicRoutingManifestCapability Capability => realization.Capability;

    /// <inheritdoc />
    public async ValueTask<MaterializationRoutingManifestSnapshot> InspectAsync(
        OperationContext context,
        MaterializationRebuildPlanSetReference planSet)
    {
        RequireContext(context);
        ArgumentNullException.ThrowIfNull(planSet);
        if (!SamePlanSet(planSet, realization.Requirement.PlanSet))
        {
            throw new ArgumentException("This authority does not own the requested plan set.", nameof(planSet));
        }

        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MaterializationAtomicRoutingManifestResult> CompareExchangeAsync(
        OperationContext context,
        MaterializationAtomicRoutingManifestRequest request)
    {
        RequireContext(context);
        ArgumentNullException.ThrowIfNull(request);
        await EnterAsync(context).ConfigureAwait(false);
        try
        {
            var requestJson = MaterializationAtomicRoutingManifestJsonSerializer.SerializeRequest(request);
            if (commits.TryGetValue(request.CommandId, out var committed))
            {
                return string.Equals(committed.RequestJson, requestJson, StringComparison.Ordinal)
                    ? new(
                        schemaVersion: MaterializationAtomicRoutingManifestResult.CurrentSchemaVersion,
                        disposition: MaterializationBackendRoutingDisposition.Replayed,
                        request: request,
                        snapshot: snapshot,
                        receipt: committed.Receipt)
                    : Reject(
                        request,
                        MaterializationBackendRoutingDisposition.IdentityConflict,
                        "The command identity was reused for different canonical content.");
            }
            if (intents.TryGetValue(request.CommandId, out var priorIntent))
            {
                if (!string.Equals(priorIntent, requestJson, StringComparison.Ordinal))
                {
                    return Reject(
                        request,
                        MaterializationBackendRoutingDisposition.IdentityConflict,
                        "The command identity was reused for different canonical content.");
                }
            }
            else
            {
                intents.Add(request.CommandId, requestJson);
            }
            if (!Owns(request.Prior)
                || !MaterializationContract.CanonicalEquals(request.Realization, realization))
            {
                return Reject(
                    request,
                    MaterializationBackendRoutingDisposition.EvidenceConflict,
                    "The request belongs to another manifest authority, scope, or capability realization.");
            }

            var observed = snapshot;
            var staleFence = observed.LatestFence is { } latest && request.Fence.Ordinal < latest.Ordinal;
            if (staleFence)
            {
                return Reject(
                    request,
                    MaterializationBackendRoutingDisposition.StaleFence,
                    "A newer manifest authority superseded the command.");
            }
            if (!MaterializationContract.CanonicalEquals(request.Prior, observed))
            {
                return Reject(
                    request,
                    MaterializationBackendRoutingDisposition.RevisionConflict,
                    "The complete expected prior manifest no longer matches current state.");
            }
            if (observed.Revision.Ordinal == long.MaxValue)
            {
                return Reject(
                    request,
                    MaterializationBackendRoutingDisposition.StateConflict,
                    "The manifest revision space is exhausted.");
            }

            var revision = observed.Revision.Next();
            var committedAtUtc = timeProvider.GetUtcNow();
            if (committedAtUtc < request.IssuedAtUtc)
            {
                committedAtUtc = request.IssuedAtUtc;
            }

            var receipt = new MaterializationAtomicRoutingManifestReceipt(
                schemaVersion: MaterializationAtomicRoutingManifestReceipt.CurrentSchemaVersion,
                commandId: request.CommandId,
                authority: observed.Authority,
                planSet: observed.PlanSet,
                priorRevision: observed.Revision,
                revision: revision,
                fence: request.Fence,
                committedAtUtc: committedAtUtc);
            snapshot = new(
                schemaVersion: MaterializationRoutingManifestSnapshot.CurrentSchemaVersion,
                authority: observed.Authority,
                planSet: observed.PlanSet,
                revision: revision,
                latestFence: request.Fence,
                entries: request.DesiredEntries);
            commits.Add(request.CommandId, new(requestJson, receipt));
            return new(
                schemaVersion: MaterializationAtomicRoutingManifestResult.CurrentSchemaVersion,
                disposition: MaterializationBackendRoutingDisposition.Applied,
                request: request,
                snapshot: snapshot,
                receipt: receipt);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Releases the authority's synchronization resource.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
    }

    bool Owns(MaterializationRoutingManifestSnapshot candidate) =>
        string.Equals(candidate.Authority, realization.Requirement.Authority, StringComparison.Ordinal)
        && SamePlanSet(candidate.PlanSet, realization.Requirement.PlanSet)
        && candidate.Entries.Select(static entry => entry.PlacementSlice.Fingerprint)
            .SequenceEqual(realization.Requirement.Scope.Select(static slice => slice.Fingerprint));

    MaterializationAtomicRoutingManifestResult Reject(
        MaterializationAtomicRoutingManifestRequest request,
        MaterializationBackendRoutingDisposition disposition,
        string detail) =>
        new(
            schemaVersion: MaterializationAtomicRoutingManifestResult.CurrentSchemaVersion,
            disposition: disposition,
            request: request,
            snapshot: snapshot,
            detail: detail);

    async ValueTask EnterAsync(OperationContext context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
    }

    static void RequireContext(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
    }

    static bool SamePlanSet(
        MaterializationRebuildPlanSetReference left,
        MaterializationRebuildPlanSetReference right) =>
        left.PlanSet == right.PlanSet;

    static MaterializationRoutingManifestSnapshot InitialSnapshot(
        MaterializationAtomicRoutingManifestRequirement requirement) =>
        new(
            schemaVersion: MaterializationRoutingManifestSnapshot.CurrentSchemaVersion,
            authority: requirement.Authority,
            planSet: requirement.PlanSet,
            revision: MaterializationBackendRoutingRevision.Initial,
            latestFence: null,
            entries:
            [
                .. requirement.Scope.Select(static slice => new MaterializationRoutingManifestEntry(
                    schemaVersion: MaterializationRoutingManifestEntry.CurrentSchemaVersion,
                    placementSlice: slice,
                    read: null,
                    write: null,
                    readiness: null,
                    configuration: null))
            ]);

    sealed record StoredCommit(
        string RequestJson,
        MaterializationAtomicRoutingManifestReceipt Receipt);
}
