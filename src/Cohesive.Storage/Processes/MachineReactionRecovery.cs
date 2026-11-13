using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Progress from one bounded pass over admitted Process identities.</summary>
/// <param name="Progress">Results keyed by instance for registered policies; unrelated instances are omitted.</param>
/// <param name="NextCursor">Last scanned identity, including unrelated instances, or null after an empty page.</param>
public sealed record MachineReactionRecoveryPage(
    ImmutableDictionary<ProcessInstanceId, MachineReactionProgress> Progress, ProcessInstanceId? NextCursor);

/// <summary>Discovers admitted reactions and routes recovery to their exact registered Process revision and authority.</summary>
/// <remarks>The Process store remains the admission and recovery authority. This component adds no queue or scheduler.
/// A supervisor advances the cursor until an empty page, then starts another pass after its polling interval.
/// Losing the cursor rescans safely. No ordering between policies is implied. Registration is immutable;
/// concurrent calls rely on the hosts' existing Process leases and fences.</remarks>
public sealed class MachineReactionRecovery
{
    readonly IProcessDurableStore store;
    readonly IProcessInstanceSource source;
    readonly Dictionary<(ExecutionDefinitionReference Definition, InteractionAuthorityScope Authority), MachineReactionHost> hosts = [];

    /// <summary>Registers reaction hosts over one discoverable Process store.</summary>
    /// <param name="source">Store used by every host; it must also implement IProcessInstanceSource.</param>
    /// <param name="hosts">Hosts indexed by exact Process reference and authority; registration is copied.</param>
    /// <exception cref="ArgumentNullException">An argument or host is null.</exception>
    /// <exception cref="ArgumentException">Discovery is unsupported, a host uses a different store instance, or a registration is duplicated.</exception>
    public MachineReactionRecovery(IProcessDurableStore source, IReadOnlyList<MachineReactionHost> hosts)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(hosts);
        store = source;
        this.source = source as IProcessInstanceSource
            ?? throw new ArgumentException("Reaction recovery requires discovery on its Process store.", nameof(source));
        foreach (var host in hosts)
        {
            ArgumentNullException.ThrowIfNull(host);
            if (!ReferenceEquals(source, host.Store))
                throw new ArgumentException("Recovery hosts must share the supplied Process store instance.", nameof(hosts));
            if (!this.hosts.TryAdd((host.Policy.Payload.Process.Reference, host.Authority), host))
                throw new ArgumentException("A reaction Process revision and authority may have only one recovery host.", nameof(hosts));
        }
    }

    /// <summary>Scans one bounded page and recovers each matching instance with fresh host authorization.</summary>
    /// <param name="context">Trusted store access, worker identity, clock and cancellation.</param>
    /// <param name="after">Provider-scoped exclusive cursor, or null to start a pass.</param>
    /// <param name="maximumInstances">Maximum scanned instances, from 1 through 1000; default 32.</param>
    /// <returns>Matching progress and the last scanned identity; an empty discovery page returns a null cursor.</returns>
    /// <exception cref="ArgumentNullException">The context is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page bound is outside 1–1000.</exception>
    /// <exception cref="UnauthorizedAccessException">A matching host denies recovery or target evidence access.</exception>
    /// <exception cref="InvalidOperationException">Retained evidence is inconsistent or storage cannot confirm progress.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels execution.</exception>
    /// <remarks>Each scanned instance is loaded once for routing. Execution may read again for fencing or reconciliation.
    /// Unrelated or deleted instances are skipped. Failures propagate without returning a new cursor; retrying the page
    /// safely replays earlier progress. Reporting and polling cadence belong to the supervisor.</remarks>
    public async Task<MachineReactionRecoveryPage> RunPageAsync(OperationContext context,
        ProcessInstanceId? after = null, int maximumInstances = 32)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumInstances, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumInstances, 1000);
        var instances = await source.ListInstancesAsync(context, after, maximumInstances).ConfigureAwait(false);
        var progress = ImmutableDictionary.CreateBuilder<ProcessInstanceId, MachineReactionProgress>();
        foreach (var instance in instances)
        {
            context.ThrowIfCancellationRequested();
            var snapshot = await store.LoadAsync(context, instance).ConfigureAwait(false);
            if (snapshot is null) continue;
            var start = snapshot.Checkpoint.Start.Request;
            if (hosts.TryGetValue((start.Definition, start.Context.Authorization.AuthorityScope), out var host))
                progress.Add(instance, await host.RecoverSnapshotAsync(context, snapshot).ConfigureAwait(false));
        }
        return new(progress.ToImmutable(), instances.IsEmpty ? null : instances[^1]);
    }
}
