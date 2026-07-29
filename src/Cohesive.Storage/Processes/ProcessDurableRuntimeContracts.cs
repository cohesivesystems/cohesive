using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Stable diagnostics emitted by the Storage-owned durable Process runtime.</summary>
public static class ProcessDurableRuntimeDiagnosticCodes
{
    /// <summary>The physical store cannot preserve the execution guarantees required by the runtime.</summary>
    public const string StoreCapabilityInsufficient = "storage.processes.runtime.storeCapability.insufficient";

    /// <summary>An activation identity was reused for different canonical activation content.</summary>
    public const string ActivationIdentityConflict = "storage.processes.runtime.activation.identityConflict";

    /// <summary>An activation input was not present as exact durable inbox evidence.</summary>
    public const string ActivationInputNotAdmitted = "storage.processes.runtime.activation.inputNotAdmitted";

    /// <summary>A terminal activation would leave input that was already pending before its durable cut.</summary>
    public const string TerminalInputUndispositioned =
        "storage.processes.runtime.activation.terminalInputUndispositioned";

    /// <summary>A durable Request emitted by the Process has no exact physical execution binding.</summary>
    public const string RequestBindingUnavailable = "storage.processes.runtime.request.bindingUnavailable";

    /// <summary>The Process lifecycle does not currently permit ordinary activation.</summary>
    public const string ActivationLifecycleBlocked = "storage.processes.runtime.activation.lifecycleBlocked";

    /// <summary>The requested durable Request operation is not retained by the Process aggregate.</summary>
    public const string OperationNotFound = "storage.processes.runtime.operation.notFound";

    /// <summary>No impure adapter is registered for the exact durable Request contract.</summary>
    public const string OperationAdapterUnavailable = "storage.processes.runtime.operation.adapterUnavailable";

    /// <summary>The selected adapter cannot preserve the exact durable Request binding.</summary>
    public const string OperationAdapterIncompatible = "storage.processes.runtime.operation.adapterIncompatible";

    /// <summary>The durable result target is outside the runtime's currently supported admission surface.</summary>
    public const string OperationTargetUnsupported = "storage.processes.runtime.operation.targetUnsupported";

    /// <summary>A deterministic durable Reply identity is already retained with different canonical content.</summary>
    public const string OperationReplyIdentityConflict =
        "storage.processes.runtime.operation.reply.identityConflict";

    /// <summary>The durable operation requires authored recovery or terminal evidence before it can advance.</summary>
    public const string OperationRecoveryRequired = "storage.processes.runtime.operation.recoveryRequired";
}

/// <summary>Observable outcome of one Storage-owned durable Process runtime operation.</summary>
public enum ProcessDurableRuntimeDisposition
{
    /// <summary>No runtime disposition was supplied; invalid in a completed result.</summary>
    Unspecified = 0,

    /// <summary>The requested logical change committed atomically.</summary>
    Applied = 1,

    /// <summary>Previously committed evidence satisfied the exact request without another logical change.</summary>
    Replayed = 2,

    /// <summary>No durable aggregate exists for the requested logical Process instance.</summary>
    NotFound = 3,

    /// <summary>The restored checkpoint is incompatible with the exact compiled Process plan.</summary>
    Incompatible = 4,

    /// <summary>The Process is paused and ordinary activation was not attempted.</summary>
    Paused = 5,

    /// <summary>The Process lifecycle or continuation is terminal and cannot perform the requested work.</summary>
    Terminal = 6,

    /// <summary>The pure Process or control interpreter rejected the request without a durable mutation.</summary>
    Rejected = 7,

    /// <summary>Another live worker owns the physical Process aggregate.</summary>
    LeaseHeld = 8,

    /// <summary>The preflight physical revision changed before ownership could be acquired.</summary>
    RevisionConflict = 9,

    /// <summary>The worker fence was superseded and this runtime must discard all staged work.</summary>
    StaleFence = 10,

    /// <summary>The worker lease elapsed before the requested atomic commit.</summary>
    LeaseExpired = 11,

    /// <summary>A stable logical identity was reused for different canonical content.</summary>
    IdentityConflict = 12,

    /// <summary>A provider-neutral local mutation conflicted with retained local state.</summary>
    LocalMutationConflict = 13,

    /// <summary>The physical commit outcome remains unknown after bounded exact retries.</summary>
    CommitOutcomeUnknown = 14,

    /// <summary>The requested operation is outside the runtime's current supported lifecycle surface.</summary>
    Unsupported = 15
}

/// <summary>Classification of an exception thrown across a Process-store mutation boundary.</summary>
public enum ProcessStoreMutationExceptionClassification
{
    /// <summary>The exception proves that the mutation did not cross an ambiguous physical boundary.</summary>
    NotAmbiguous = 0,

    /// <summary>The caller must retry the exact same commit identity and canonical content to discover its outcome.</summary>
    Ambiguous = 1
}

/// <summary>Classifies provider exceptions without changing the store-mutation intent being retried.</summary>
public interface IProcessStoreMutationExceptionClassifier
{
    /// <summary>Classifies one exception thrown by an atomic Process-store mutation.</summary>
    /// <param name="exception">Provider exception whose mutation-boundary meaning is known by the adapter.</param>
    /// <returns>Whether the exact mutation must be retried to reconcile an unknown outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    ProcessStoreMutationExceptionClassification Classify(Exception exception);
}

/// <summary>Conservative classifier that treats every provider exception presented to it as outcome-ambiguous.</summary>
/// <remarks>
/// The durable runtime propagates causal caller cancellation before classification. A provider-local
/// <see cref="OperationCanceledException"/> or <see cref="TaskCanceledException"/> observed while the caller token
/// remains live is still presented here because its physical mutation outcome may be unknown.
/// </remarks>
public sealed class ConservativeProcessStoreMutationExceptionClassifier : IProcessStoreMutationExceptionClassifier
{
    /// <summary>Shared stateless conservative classifier.</summary>
    public static ConservativeProcessStoreMutationExceptionClassifier Instance { get; } = new();

    ConservativeProcessStoreMutationExceptionClassifier()
    {
    }

    /// <inheritdoc />
    public ProcessStoreMutationExceptionClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return ProcessStoreMutationExceptionClassification.Ambiguous;
    }
}

/// <summary>Resolves one exact durable execution binding for a canonical Request contract.</summary>
public interface IProcessDurableRequestBindingResolver
{
    /// <summary>Attempts to resolve the binding used to initialize one durable Request operation.</summary>
    /// <param name="request">Exact canonical Request emitted by a Process activation.</param>
    /// <param name="binding">Receives the exact durable execution binding when available.</param>
    /// <returns><see langword="true"/> when an exact binding is available; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    bool TryResolve(RequestEnvelope request, out DurableRequestBinding? binding);
}

/// <summary>Binding resolver that deliberately supports no durable Request contract.</summary>
public sealed class EmptyProcessDurableRequestBindingResolver : IProcessDurableRequestBindingResolver
{
    /// <summary>Shared stateless empty resolver.</summary>
    public static EmptyProcessDurableRequestBindingResolver Instance { get; } = new();

    EmptyProcessDurableRequestBindingResolver()
    {
    }

    /// <inheritdoc />
    public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(request);
        binding = null;
        return false;
    }
}

/// <summary>Resolves the impure adapter for one already-bound durable Request operation.</summary>
public interface IProcessDurableOperationAdapterResolver
{
    /// <summary>Attempts to resolve the adapter for an exact canonical Request.</summary>
    /// <param name="request">Exact canonical logical Request retained by the durable operation.</param>
    /// <param name="adapter">Receives the impure adapter when available.</param>
    /// <returns><see langword="true"/> when an exact adapter is available; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? adapter);
}

/// <summary>Adapter resolver that deliberately supports no durable Request contract.</summary>
public sealed class EmptyProcessDurableOperationAdapterResolver : IProcessDurableOperationAdapterResolver
{
    /// <summary>Shared stateless empty resolver.</summary>
    public static EmptyProcessDurableOperationAdapterResolver Instance { get; } = new();

    EmptyProcessDurableOperationAdapterResolver()
    {
    }

    /// <inheritdoc />
    public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(request);
        adapter = null;
        return false;
    }
}

/// <summary>Classifies an adapter exception as explicit durable failure evidence.</summary>
public interface IProcessOperationExceptionClassifier
{
    /// <summary>Classifies an exception thrown after a durable dispatch marker committed.</summary>
    /// <param name="exception">Adapter exception carrying provider-specific execution evidence.</param>
    /// <returns>Explicit phase, effect, retry, code, and optional portable detail to persist.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    DurableOperationFailure Classify(Exception exception);
}

/// <summary>Conservative adapter classifier that treats a thrown in-call exception as effect-ambiguous.</summary>
public sealed class ConservativeProcessOperationExceptionClassifier : IProcessOperationExceptionClassifier
{
    /// <summary>Stable failure code used when an adapter throws without more precise evidence.</summary>
    public const string AmbiguousAdapterException = "operation.adapter.exception.ambiguous";

    /// <summary>Shared stateless conservative classifier.</summary>
    public static ConservativeProcessOperationExceptionClassifier Instance { get; } = new();

    ConservativeProcessOperationExceptionClassifier()
    {
    }

    /// <inheritdoc />
    public DurableOperationFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(
            DurableOperationFailurePhase.InCall,
            DurableOperationEffectEvidence.Ambiguous,
            DurableOperationFailureDisposition.Retryable,
            AmbiguousAdapterException);
    }
}

/// <summary>Plans provider-neutral local mutations that share an activation's aggregate commit.</summary>
/// <remarks>
/// Implementations are interpretation policy, not semantic Process authority. They must be deterministic for the
/// supplied checkpoint and activation decision and must perform no external I/O.
/// </remarks>
public interface IProcessLocalMutationPlanner
{
    /// <summary>Plans local writes to commit atomically with one accepted finite activation.</summary>
    /// <param name="checkpoint">Exact compatible checkpoint consumed by the activation.</param>
    /// <param name="activation">Exact finite activation request.</param>
    /// <param name="decision">Pure canonical Process decision.</param>
    /// <returns>Provider-neutral local mutations, or an empty collection when none are required.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    ImmutableArray<ProcessLocalMutation> Plan(
        ProcessDurableCheckpoint checkpoint,
        ProcessActivation activation,
        ProcessActivationDecision decision);
}

/// <summary>Local-mutation planner that contributes no writes.</summary>
public sealed class EmptyProcessLocalMutationPlanner : IProcessLocalMutationPlanner
{
    /// <summary>Shared stateless empty planner.</summary>
    public static EmptyProcessLocalMutationPlanner Instance { get; } = new();

    EmptyProcessLocalMutationPlanner()
    {
    }

    /// <inheritdoc />
    public ImmutableArray<ProcessLocalMutation> Plan(
        ProcessDurableCheckpoint checkpoint,
        ProcessActivation activation,
        ProcessActivationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(decision);
        return [];
    }
}

/// <summary>Physical policy for one durable Process runtime worker.</summary>
public sealed record ProcessDurableRuntimeOptions
{
    /// <summary>Creates validated worker and bounded ambiguity-retry policy.</summary>
    /// <param name="workerId">Globally unique physical worker identity used for leases and fences.</param>
    /// <param name="workerLease">Strictly positive Process aggregate ownership lifetime.</param>
    /// <param name="maxAmbiguousStoreMutationAttempts">
    /// Positive total number of attempts allowed for one unchanged ambiguous store-mutation intent, including the
    /// first.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="workerId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="workerId"/> is empty or white-space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="workerLease"/> is not positive or <paramref name="maxAmbiguousStoreMutationAttempts"/> is less
    /// than one.
    /// </exception>
    public ProcessDurableRuntimeOptions(
        string workerId,
        TimeSpan workerLease,
        int maxAmbiguousStoreMutationAttempts = 3)
    {
        WorkerId = Guard.RequireNotNullOrWhiteSpace(workerId);
        if (workerLease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(workerLease),
                workerLease,
                "A durable Process worker lease must be positive.");
        }

        if (maxAmbiguousStoreMutationAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAmbiguousStoreMutationAttempts),
                maxAmbiguousStoreMutationAttempts,
                "At least one physical store-mutation attempt is required.");
        }

        WorkerLease = workerLease;
        MaxAmbiguousStoreMutationAttempts = maxAmbiguousStoreMutationAttempts;
    }

    /// <summary>Globally unique physical worker identity.</summary>
    public string WorkerId { get; }

    /// <summary>Process aggregate ownership lifetime.</summary>
    public TimeSpan WorkerLease { get; }

    /// <summary>Total bounded attempts for one unchanged ambiguous store-mutation intent.</summary>
    public int MaxAmbiguousStoreMutationAttempts { get; }
}

/// <summary>Result of initializing one durable Process aggregate.</summary>
public sealed record ProcessDurableInitializationResult
{
    /// <summary>Creates a durable initialization result.</summary>
    /// <param name="disposition">Observable runtime outcome.</param>
    /// <param name="snapshot">Current aggregate snapshot when the instance exists.</param>
    /// <param name="diagnostics">Structured compatibility or capability diagnostics.</param>
    internal ProcessDurableInitializationResult(
        ProcessDurableRuntimeDisposition disposition,
        ProcessDurableStoreSnapshot? snapshot = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        Disposition = disposition;
        Snapshot = snapshot;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>Observable runtime outcome.</summary>
    public ProcessDurableRuntimeDisposition Disposition { get; }

    /// <summary>Current aggregate snapshot when the instance exists.</summary>
    public ProcessDurableStoreSnapshot? Snapshot { get; }

    /// <summary>Structured compatibility or capability diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>Result of one finite durable Process activation.</summary>
public sealed record ProcessDurableActivationResult
{
    /// <summary>Creates one durable activation result.</summary>
    /// <param name="disposition">Observable runtime outcome.</param>
    /// <param name="snapshot">Current aggregate snapshot when the instance exists.</param>
    /// <param name="decision">Pure Process decision when interpretation occurred.</param>
    /// <param name="commit">Exact commit intent when one was constructed, including unresolved ambiguous intent.</param>
    /// <param name="diagnostics">Structured compatibility, lifecycle, or interpretation diagnostics.</param>
    internal ProcessDurableActivationResult(
        ProcessDurableRuntimeDisposition disposition,
        ProcessDurableStoreSnapshot? snapshot = null,
        ProcessActivationDecision? decision = null,
        ProcessDurableCommit? commit = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        Disposition = disposition;
        Snapshot = snapshot;
        Decision = decision;
        Commit = commit;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>Observable runtime outcome.</summary>
    public ProcessDurableRuntimeDisposition Disposition { get; }

    /// <summary>Current aggregate snapshot when the instance exists.</summary>
    public ProcessDurableStoreSnapshot? Snapshot { get; }

    /// <summary>Pure Process decision when interpretation occurred.</summary>
    public ProcessActivationDecision? Decision { get; }

    /// <summary>Exact immutable commit intent when one was constructed.</summary>
    public ProcessDurableCommit? Commit { get; }

    /// <summary>Structured compatibility, lifecycle, or interpretation diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>Result of one durable lifecycle-control or attempt-affinity operation.</summary>
public sealed record ProcessDurableControlResult
{
    /// <summary>Creates one durable control result.</summary>
    /// <param name="disposition">Observable runtime outcome.</param>
    /// <param name="snapshot">Current aggregate snapshot when the instance exists.</param>
    /// <param name="decision">Pure lifecycle-control decision when evaluation occurred.</param>
    /// <param name="commit">Exact commit intent when one was constructed, including unresolved ambiguous intent.</param>
    /// <param name="diagnostics">Structured compatibility or control diagnostics.</param>
    internal ProcessDurableControlResult(
        ProcessDurableRuntimeDisposition disposition,
        ProcessDurableStoreSnapshot? snapshot = null,
        ProcessControlDecision? decision = null,
        ProcessDurableCommit? commit = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        Disposition = disposition;
        Snapshot = snapshot;
        Decision = decision;
        Commit = commit;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>Observable runtime outcome.</summary>
    public ProcessDurableRuntimeDisposition Disposition { get; }

    /// <summary>Current aggregate snapshot when the instance exists.</summary>
    public ProcessDurableStoreSnapshot? Snapshot { get; }

    /// <summary>Pure lifecycle-control decision when evaluation occurred.</summary>
    public ProcessControlDecision? Decision { get; }

    /// <summary>Exact immutable commit intent when one was constructed.</summary>
    public ProcessDurableCommit? Commit { get; }

    /// <summary>Structured compatibility or control diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>Result of advancing one durable Request operation by all currently safe physical stages.</summary>
public sealed record ProcessDurableOperationResult
{
    /// <summary>Creates one durable operation runtime result.</summary>
    /// <param name="disposition">Observable Process-runtime outcome.</param>
    /// <param name="snapshot">Current aggregate snapshot when the instance exists.</param>
    /// <param name="operation">Latest durable logical operation state when found.</param>
    /// <param name="commit">Last exact commit intent, including unresolved ambiguous intent.</param>
    /// <param name="recoveryIntent">Authored reconciliation or escalation intent when external action is required.</param>
    /// <param name="diagnostics">Structured compatibility, binding, adapter, or target diagnostics.</param>
    internal ProcessDurableOperationResult(
        ProcessDurableRuntimeDisposition disposition,
        ProcessDurableStoreSnapshot? snapshot = null,
        DurableOperationState? operation = null,
        ProcessDurableCommit? commit = null,
        DurableOperationRecoveryIntent? recoveryIntent = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        Disposition = disposition;
        Snapshot = snapshot;
        Operation = operation;
        Commit = commit;
        RecoveryIntent = recoveryIntent;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>Observable Process-runtime outcome.</summary>
    public ProcessDurableRuntimeDisposition Disposition { get; }

    /// <summary>Current aggregate snapshot when the instance exists.</summary>
    public ProcessDurableStoreSnapshot? Snapshot { get; }

    /// <summary>Latest durable logical operation state when found.</summary>
    public DurableOperationState? Operation { get; }

    /// <summary>Last exact immutable commit intent when one was constructed.</summary>
    public ProcessDurableCommit? Commit { get; }

    /// <summary>Authored reconciliation or escalation intent when external action is required.</summary>
    public DurableOperationRecoveryIntent? RecoveryIntent { get; }

    /// <summary>Structured compatibility, binding, adapter, or target diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}
