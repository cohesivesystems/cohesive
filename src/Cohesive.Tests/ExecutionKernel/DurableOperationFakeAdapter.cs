using System.Collections.Immutable;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

internal sealed class DurableOperationFakeAdapter : IDurableOperationAdapter, IDurableOperationBatchAdapter
{
    readonly Dictionary<EmissionId, Queue<DurableOperationAttemptObservation>> executionScripts = [];
    readonly Queue<DurableOperationReconciliationObservation> reconciliationScript = [];
    readonly HashSet<DurableOperationDeduplicationKey> logicalConsequences = [];
    readonly List<DurableOperationInvocation> invocations = [];
    readonly List<DurableOperationReconciliationRequest> reconciliations = [];
    readonly List<ImmutableArray<DurableOperationInvocation>> batches = [];

    internal DurableOperationFakeAdapter(
        RequestContractReference supportedRequest,
        DurableOperationIdempotencyEvidence idempotencyEvidence =
            DurableOperationIdempotencyEvidence.TargetDeduplication,
        DurableOperationReconciliationCapability reconciliation =
            DurableOperationReconciliationCapability.Supported)
    {
        Capabilities = new(idempotencyEvidence, reconciliation, [supportedRequest]);
    }

    public DurableOperationAdapterCapabilities Capabilities { get; }

    internal IReadOnlyList<DurableOperationInvocation> Invocations => invocations;

    internal IReadOnlyList<DurableOperationReconciliationRequest> Reconciliations => reconciliations;

    internal IReadOnlyList<ImmutableArray<DurableOperationInvocation>> Batches => batches;

    internal int LogicalConsequenceCount => logicalConsequences.Count;

    internal DurableOperationFakeAdapter Script(
        EmissionId requestId,
        params DurableOperationAttemptObservation[] observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        executionScripts[requestId] = new(observations);
        return this;
    }

    internal DurableOperationFakeAdapter ScriptReconciliation(
        params DurableOperationReconciliationObservation[] observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        foreach (var observation in observations)
            reconciliationScript.Enqueue(observation);
        return this;
    }

    public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation)
    {
        context.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(invocation);
        return ValueTask.FromResult(Observe(invocation));
    }

    public ValueTask<ImmutableArray<DurableOperationBatchItemObservation>> ExecuteBatchAsync(
        OperationContext context,
        ImmutableArray<DurableOperationInvocation> batch)
    {
        context.ThrowIfCancellationRequested();
        if (batch.IsDefaultOrEmpty)
            throw new ArgumentException("A fake physical batch cannot be default or empty.", nameof(batch));

        batches.Add(batch);
        var observations = ImmutableArray.CreateBuilder<DurableOperationBatchItemObservation>(batch.Length);
        foreach (var invocation in batch)
        {
            var observation = Observe(invocation);
            observations.Add(
                new(
                    invocation.Request.Context.EmissionId,
                    invocation.AttemptId,
                    invocation.Fence,
                    observation));
        }

        return ValueTask.FromResult(observations.MoveToImmutable());
    }

    DurableOperationAttemptObservation Observe(DurableOperationInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        invocations.Add(invocation);
        if (!executionScripts.TryGetValue(invocation.Request.Context.EmissionId, out var script)
            || script.Count == 0)
        {
            throw new InvalidOperationException(
                $"No fake adapter observation remains for '{invocation.Request.Context.EmissionId.Value}'.");
        }

        var observation = script.Dequeue();
        if (observation is DurableOperationOutcomeObservation)
            logicalConsequences.Add(invocation.DeduplicationKey);
        return observation;
    }

    public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request)
    {
        context.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        reconciliations.Add(request);
        if (reconciliationScript.Count == 0)
            throw new InvalidOperationException("No fake reconciliation observation remains.");

        return ValueTask.FromResult(reconciliationScript.Dequeue());
    }
}
