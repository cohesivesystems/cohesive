using System.Collections.Concurrent;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Runtime-owned index of hierarchical admission gates shared by Cosmos materialization source instances.
/// </summary>
/// <remarks>
/// One index should be shared by all materialization sources in the same runtime ownership boundary. It applies a
/// container gate to every operation and, for fixed logical-partition sources, an additional partition gate. The
/// index retains gate state until disposed so source construction order cannot silently replace effective limits.
/// Dispose the index only after all bound source operations have completed.
/// </remarks>
public sealed class CosmosMaterializationAdmissionIndex : IDisposable
{
    readonly ConcurrentDictionary<string, AdmissionGate> gates = new(StringComparer.Ordinal);
    int disposed;

    /// <summary>Creates an empty runtime-owned hierarchical admission index.</summary>
    public CosmosMaterializationAdmissionIndex()
    {
    }

    /// <summary>Releases all admission gates owned by this runtime index.</summary>
    /// <remarks>The caller must ensure that no bound source operation is active or can subsequently start.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        foreach (var gate in gates.Values)
            gate.Semaphore.Dispose();
        gates.Clear();
    }

    internal CosmosMaterializationAdmission Bind(
        string containerIdentity,
        string? partitionIdentity,
        int maximumContainerParallelism,
        int maximumPartitionParallelism)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        containerIdentity = Guard.RequireNotNullOrWhiteSpace(containerIdentity);
        if (partitionIdentity is not null)
            partitionIdentity = Guard.RequireNotNullOrWhiteSpace(partitionIdentity);
        if (maximumContainerParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumContainerParallelism),
                maximumContainerParallelism,
                "Container admission parallelism must be positive.");
        }
        if (maximumPartitionParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPartitionParallelism),
                maximumPartitionParallelism,
                "Partition admission parallelism must be positive.");
        }

        var container = GetGate(string.Concat("container\0", containerIdentity), maximumContainerParallelism);
        var partition = partitionIdentity is null
            ? null
            : GetGate(
                string.Concat("partition\0", containerIdentity, "\0", partitionIdentity),
                maximumPartitionParallelism);
        return new(container, partition);
    }

    AdmissionGate GetGate(string key, int maximumParallelism)
    {
        var gate = gates.GetOrAdd(
            key,
            static (_, limit) => new(limit),
            maximumParallelism);
        if (gate.MaximumParallelism != maximumParallelism)
        {
            throw new ArgumentException(
                "Cosmos materialization sources sharing one admission identity must declare the same effective parallelism.",
                nameof(maximumParallelism));
        }
        return gate;
    }

    internal sealed class AdmissionGate
    {
        internal AdmissionGate(int maximumParallelism)
        {
            MaximumParallelism = maximumParallelism;
            Semaphore = new(maximumParallelism, maximumParallelism);
        }

        internal int MaximumParallelism { get; }

        internal SemaphoreSlim Semaphore { get; }
    }
}

internal sealed class CosmosMaterializationAdmission(
    CosmosMaterializationAdmissionIndex.AdmissionGate container,
    CosmosMaterializationAdmissionIndex.AdmissionGate? partition)
{
    internal async ValueTask<CosmosMaterializationAdmissionLease> EnterAsync(CancellationToken cancellationToken)
    {
        await container.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (partition is null)
            return new(container, partition: null);

        try
        {
            await partition.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(container, partition);
        }
        catch
        {
            container.Semaphore.Release();
            throw;
        }
    }
}

internal sealed class CosmosMaterializationAdmissionLease(
    CosmosMaterializationAdmissionIndex.AdmissionGate container,
    CosmosMaterializationAdmissionIndex.AdmissionGate? partition) : IDisposable
{
    int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        partition?.Semaphore.Release();
        container.Semaphore.Release();
    }
}
