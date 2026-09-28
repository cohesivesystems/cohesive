using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Runtime;

namespace Cohesive.Storage.Processes;

/// <summary>Reads protected canonical Process values from the existing durable checkpoint authority.</summary>
/// <remarks>No parallel result store or current-entity read is introduced. The caller must authorize the
/// logical authority before invocation. Store resolution is invocation-scoped; exact plan resolution must
/// return an already prepared immutable plan. Every loaded checkpoint retains compatibility validation.</remarks>
public sealed class ProcessDurableExecutionValueRepository(
    Func<OperationContext, InteractionAuthorityScope, IProcessDurableStore> stores,
    Func<ExecutionDefinitionReference, CompiledProcessPlan?> plans) : IProcessExecutionValueRepository
{
    readonly Func<OperationContext, InteractionAuthorityScope, IProcessDurableStore> stores =
        stores ?? throw new ArgumentNullException(nameof(stores));
    readonly Func<ExecutionDefinitionReference, CompiledProcessPlan?> plans =
        plans ?? throw new ArgumentNullException(nameof(plans));

    /// <inheritdoc />
    public async ValueTask<ProcessExecutionValueReadResult> GetValuesAsync(OperationContext context,
        InteractionAuthorityScope authorityScope, ProcessInstanceId processInstanceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorityScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(processInstanceId.Value);
        context.ThrowIfCancellationRequested();
        var store = stores(context, authorityScope) ?? throw new InvalidOperationException("The store binding returned null.");
        var snapshot = await store.LoadAsync(context, processInstanceId).ConfigureAwait(false);
        if (snapshot is null) return ProcessExecutionValueReadResult.NotFound();
        var checkpoint = snapshot.Checkpoint;
        if (checkpoint.ContinuationIdentity.ProcessInstanceId != processInstanceId
            || checkpoint.Start.Request.Context.Authorization.AuthorityScope != authorityScope)
            return ProcessExecutionValueReadResult.NotFound();
        var plan = plans(checkpoint.Definition);
        if (plan is null || plan.DefinitionReference != checkpoint.Definition)
            throw new InvalidOperationException("The exact retained Process plan is unavailable.");
        var validation = ProcessCheckpointCompatibilityValidator.Validate(plan, checkpoint);
        if (!validation.IsValid)
            throw new InvalidOperationException("The retained Process checkpoint is incompatible: "
                + string.Join("; ", validation.Diagnostics.Select(item => item.Code)));
        if (checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None)
            return ProcessExecutionValueReadResult.InProgress(new(checkpoint.Definition, processInstanceId,
                checkpoint.Start.Request.Input));
        var evidence = checkpoint.Activations.Select(item => item.Evidence).ToImmutableArray();
        return ProcessExecutionValueReadResult.Available(new(checkpoint.Definition, processInstanceId,
            checkpoint.Start.Request.Input, checkpoint.Continuation.Terminal, checkpoint.ContinuationIdentity,
            evidence, ProcessOperationFailure.Project(checkpoint.Continuation, evidence)));
    }
}
