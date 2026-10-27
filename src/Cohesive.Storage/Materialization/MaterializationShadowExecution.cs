using Cohesive.Prelude;

namespace Cohesive.Storage.Materialization;

/// <summary>Bounded candidate-generation work; neither operation activates a generation or changes live reads.</summary>
public enum MaterializationShadowOperation
{
    /// <summary>Resume baseline shards, then converge changes.</summary>
    Rebuild,
    /// <summary>Converge changes for an existing attempt.</summary>
    CatchUp
}

/// <summary>Runs existing rebuild and synchronization interpreters without a promotion step.</summary>
public static class MaterializationShadowExecution
{
    /// <summary>Executes one bounded shadow activation with stable retry identities.</summary>
    /// <param name="runtime">Admitted native ports, borrowed for this invocation.</param>
    /// <param name="operation">Baseline plus catch-up, or catch-up alone.</param>
    /// <param name="context">Invocation context, including cancellation.</param>
    /// <param name="attempt">Stable attempt identity and original start time.</param>
    /// <param name="invocation">Stable catch-up activation identity, retained on retry.</param>
    /// <param name="worker">Explicit worker identity protected by the caller's admitted concurrency policy.</param>
    /// <returns>Bounded convergence result; absence of a receipt is not success.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The operation is unknown.</exception>
    /// <exception cref="InvalidOperationException">Baseline initialization or a shard cannot complete.</exception>
    /// <remarks>Native resources remain caller-owned. Execution is resumable, not an unbounded polling loop.</remarks>
    public static async Task<MaterializationSynchronizationRunResult> RunAsync(
        BoundMaterialization runtime,
        MaterializationShadowOperation operation, OperationContext context, MaterializationRebuildAttempt attempt,
        MaterializationSynchronizationInvocationId invocation, MaterializationSynchronizationWorkerId worker)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var resolved = runtime.Resolved;
        var workStore = runtime.WorkStore;
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (operation == MaterializationShadowOperation.Rebuild)
        {
            var executor = new MaterializationRebuildExecutor(resolved);
            var begun = await executor.BeginAttemptAsync(context, attempt).ConfigureAwait(false);
            if (begun.Disposition != MaterializationRebuildInitializationDisposition.Ready)
                throw new InvalidOperationException($"Rebuild initialization failed: {System.Text.Json.JsonSerializer.Serialize(begun)}");
            foreach (var shard in resolved.Plan.Shards)
            {
                var result = await executor.RunShardAsync(context, attempt, shard.Id).ConfigureAwait(false);
                if (result.Disposition != MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired)
                    throw new InvalidOperationException($"Rebuild shard '{shard.Id}' failed: {System.Text.Json.JsonSerializer.Serialize(result)}");
            }
        }
        return await new MaterializationSynchronizationExecutor(resolved, workStore, MaterializationIndexSyncWorkloadKind.Rebuild)
            .ConvergeAsync(context, attempt, invocation, worker).ConfigureAwait(false);
    }
}
