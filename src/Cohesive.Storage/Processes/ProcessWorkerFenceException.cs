namespace Cohesive.Storage.Processes;

/// <summary>Stops a physical activation whose effect authority is stale or expired.</summary>
/// <remarks>This is not a semantic operation failure. The durable runtime returns the existing store disposition
/// without committing an activation checkpoint. Adapters must check the fence atomically with the protected effect.</remarks>
public sealed class ProcessWorkerFenceException : Exception
{
    /// <summary>Reports a rejected physical worker fence.</summary>
    /// <param name="disposition">Either stale fence or expired lease; other store outcomes are not fence rejections.</param>
    /// <exception cref="ArgumentOutOfRangeException">The disposition is not a worker-fence rejection.</exception>
    public ProcessWorkerFenceException(ProcessStoreMutationDisposition disposition)
        : base($"Process effect rejected: {disposition}.")
    {
        if (disposition is not (ProcessStoreMutationDisposition.StaleFence or ProcessStoreMutationDisposition.LeaseExpired))
            throw new ArgumentOutOfRangeException(nameof(disposition));
        Disposition = disposition;
    }

    /// <summary>Physical authority rejection, distinct from the logical operation outcome.</summary>
    public ProcessStoreMutationDisposition Disposition { get; }
}
