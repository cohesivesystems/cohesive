using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Prelude;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>
/// One first-time host-operation observation awaiting materialization as a receipt at the aggregate commit time.
/// </summary>
/// <param name="Key">Exact attempt-, activation-, token-, node-, and occurrence-scoped replay key.</param>
/// <param name="OperationDefinition">Exact Transition or Relation/Query definition invoked.</param>
/// <param name="Result">Closed typed success or structured failure returned by the host.</param>
internal sealed record ProcessOperationReplayObservation(
    ProcessOperationOccurrence Key,
    ExecutionDefinitionReference OperationDefinition,
    ProcessOperationResult Result);

/// <summary>
/// Activation-scoped host wrapper that reuses committed operation evidence and captures only first-time results.
/// </summary>
/// <remarks>
/// The wrapper does not assign persistence timestamps or mutate a checkpoint. A Storage-owned checkpoint reducer
/// materializes <see cref="Observations"/> as <see cref="ProcessOperationReceipt"/> values at the atomic commit
/// boundary. Repeated calls for a first-time key within the same activation reuse the captured result.
/// </remarks>
internal sealed class ProcessOperationReplayHost : IProcessReferenceHost, IAsyncProcessReferenceHost
{
    readonly IProcessReferenceHost? inner;
    readonly IAsyncProcessReferenceHost? asyncInner;
    readonly Dictionary<ProcessOperationOccurrence, ProcessOperationReceipt> receipts;
    readonly Dictionary<ProcessOperationOccurrence, ProcessOperationReplayObservation> observedByKey = [];
    readonly List<ProcessOperationReplayObservation> observations = [];

    /// <summary>Creates an activation-scoped replay wrapper.</summary>
    /// <param name="inner">Host used only for operation occurrences without committed or captured evidence.</param>
    /// <param name="receipts">Committed attempt-scoped operation receipts available for exact replay.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="receipts"/> contains a null value or repeats an operation occurrence key.
    /// </exception>
    internal ProcessOperationReplayHost(
        IProcessReferenceHost inner,
        ImmutableArray<ProcessOperationReceipt> receipts = default)
        : this(receipts)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    internal ProcessOperationReplayHost(
        IAsyncProcessReferenceHost inner,
        ImmutableArray<ProcessOperationReceipt> receipts = default)
        : this(receipts)
    {
        asyncInner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    ProcessOperationReplayHost(ImmutableArray<ProcessOperationReceipt> receipts)
    {
        var normalized = receipts.IsDefault ? [] : receipts;
        this.receipts = new(normalized.Length);
        foreach (var receipt in normalized)
        {
            if (receipt is null)
            {
                throw new ArgumentException("Committed operation receipts cannot contain null entries.", nameof(receipts));
            }

            if (!this.receipts.TryAdd(receipt.Key, receipt))
            {
                throw new ArgumentException(
                    $"Committed operation occurrence '{Describe(receipt.Key)}' is duplicated.",
                    nameof(receipts));
            }
        }
    }

    /// <summary>First-time host observations in deterministic invocation order.</summary>
    /// <returns>An immutable snapshot that excludes replayed committed operations.</returns>
    internal ImmutableArray<ProcessOperationReplayObservation> Observations => [.. observations];

    /// <inheritdoc />
    public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var key = Key(
            invocation.Continuation,
            invocation.Activation,
            invocation.Token,
            invocation.Node,
            invocation.Occurrence);
        return Resolve(
            key,
            invocation.Definition,
            () => SyncHost.InvokeTransition(invocation));
    }

    /// <inheritdoc />
    public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        var key = Key(
            evaluation.Continuation,
            evaluation.Activation,
            evaluation.Token,
            evaluation.Node,
            evaluation.Occurrence);
        return Resolve(
            key,
            evaluation.Definition,
            () => SyncHost.EvaluateRelation(evaluation));
    }

    /// <inheritdoc />
    public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
        SyncHost.ResolveSignalTarget(resolution ?? throw new ArgumentNullException(nameof(resolution)));

    IProcessReferenceHost SyncHost => inner
        ?? throw new InvalidOperationException("This replay host requires asynchronous invocation.");

    IAsyncProcessReferenceHost AsyncHost => asyncInner
        ?? throw new InvalidOperationException("This replay host requires synchronous invocation.");

    /// <inheritdoc />
    public ValueTask<ProcessOperationResult> InvokeTransitionAsync(
        OperationContext context,
        ProcessTransitionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        return ResolveAsync(context,
            Key(invocation.Continuation, invocation.Activation, invocation.Token, invocation.Node, invocation.Occurrence),
            invocation.Definition,
            () => AsyncHost.InvokeTransitionAsync(context, invocation));
    }

    /// <inheritdoc />
    public ValueTask<ProcessOperationResult> EvaluateRelationAsync(
        OperationContext context,
        ProcessRelationEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return ResolveAsync(context,
            Key(evaluation.Continuation, evaluation.Activation, evaluation.Token, evaluation.Node, evaluation.Occurrence),
            evaluation.Definition,
            () => AsyncHost.EvaluateRelationAsync(context, evaluation));
    }

    /// <inheritdoc />
    public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(
        OperationContext context,
        ProcessSignalTargetResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolution);
        context.ThrowIfCancellationRequested();
        return AsyncHost.ResolveSignalTargetAsync(context, resolution);
    }

    ProcessOperationResult Resolve(
        ProcessOperationOccurrence key,
        ExecutionDefinitionReference definition,
        Func<ProcessOperationResult> invoke) =>
        Replay(key, definition) ?? Capture(key, definition, invoke());

    async ValueTask<ProcessOperationResult> ResolveAsync(
        OperationContext context,
        ProcessOperationOccurrence key,
        ExecutionDefinitionReference definition,
        Func<ValueTask<ProcessOperationResult>> invoke)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        var retained = Replay(key, definition);
        if (retained is not null)
        {
            return retained;
        }
        var result = await invoke().ConfigureAwait(false);
        context.ThrowIfCancellationRequested();
        return Capture(key, definition, result);
    }

    ProcessOperationResult? Replay(
        ProcessOperationOccurrence key,
        ExecutionDefinitionReference definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (receipts.TryGetValue(key, out var receipt))
        {
            RequireDefinition(key, definition, receipt.OperationDefinition, "committed receipt");
            return receipt.Result;
        }
        if (observedByKey.TryGetValue(key, out var observed))
        {
            RequireDefinition(key, definition, observed.OperationDefinition, "captured observation");
            return observed.Result;
        }
        return null;
    }

    ProcessOperationResult Capture(
        ProcessOperationOccurrence key,
        ExecutionDefinitionReference definition,
        ProcessOperationResult? value)
    {
        var result = value
            ?? throw new InvalidOperationException(
                $"The Process host returned null for operation occurrence '{Describe(key)}'.");
        if (!result.IsValidOutcome())
        {
            throw new InvalidOperationException(
                $"The Process host returned an invalid result for operation occurrence '{Describe(key)}'.");
        }

        var observation = new ProcessOperationReplayObservation(key, definition, result);
        observedByKey.Add(key, observation);
        observations.Add(observation);
        return result;
    }

    static ProcessOperationOccurrence Key(
        ProcessContinuationIdentity continuation,
        ActivationId activation,
        TokenId token,
        ExecutionNodeId node,
        long occurrence) =>
        new(continuation, activation, token, node, occurrence);

    static void RequireDefinition(
        ProcessOperationOccurrence key,
        ExecutionDefinitionReference requested,
        ExecutionDefinitionReference retained,
        string evidenceKind)
    {
        if (requested != retained)
        {
            throw new InvalidOperationException(
                $"Operation occurrence '{Describe(key)}' is bound to another definition in its {evidenceKind}.");
        }
    }

    static string Describe(ProcessOperationOccurrence key) =>
        $"{key.Continuation.ProcessInstanceId.Value}/{key.Continuation.ProcessAttemptId.Value}/"
        + $"{key.Activation.Value}/{key.Token.Value}/{key.Node.Value}/{key.Occurrence}";
}
