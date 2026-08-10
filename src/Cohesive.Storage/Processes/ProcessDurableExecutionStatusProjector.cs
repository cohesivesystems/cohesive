using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Projects one durable Process checkpoint into the common execution-status contract.</summary>
public static class ProcessDurableExecutionStatusProjector
{
    /// <summary>Projects canonical durable Process state without exposing inputs, bindings, or operation payloads.</summary>
    /// <param name="checkpoint">Complete durable Process checkpoint to project.</param>
    /// <returns>
    /// Common execution status whose token, wait, progress, demand, and health facets come from the checkpoint's
    /// existing canonical authorities.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The checkpoint cannot be represented by the common status contract.</exception>
    public static ExecutionStatus Project(ProcessDurableCheckpoint checkpoint)
        => Project(checkpoint, extensions: []);

    /// <summary>Projects canonical durable Process state with typed runtime-owned status extensions.</summary>
    /// <param name="checkpoint">Complete durable Process checkpoint to project.</param>
    /// <param name="extensions">Typed block-specific runtime status extensions to attach.</param>
    /// <returns>
    /// Common execution status whose Process facets come from the checkpoint and whose extension facets retain
    /// their original block authorities.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The checkpoint or supplied extensions cannot be represented by the common status contract.
    /// </exception>
    public static ExecutionStatus Project(
        ProcessDurableCheckpoint checkpoint,
        ImmutableArray<ExecutionRuntimeStatusExtension> extensions)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return ProcessExecutionStatusProjector.Project(
            checkpoint.Continuation,
            checkpoint.Control,
            checkpoint.DurableOperations,
            extensions,
            ExecutionStatusDisclosure.Disclosed);
    }
}
