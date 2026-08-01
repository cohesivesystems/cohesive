using System.Text.Json.Serialization;
using Cohesive.Control;
using Cohesive.Execution;

namespace Cohesive.Storage.Materialization;

/// <summary>Exact durable ownership key of one materialization Control-loop epoch.</summary>
public sealed record MaterializationIndexSyncControlStateKey
{
    /// <summary>Creates an exact plan, definition, backend generation, workload, and loop key.</summary>
    /// <param name="materializationId">Logical materialization identity.</param>
    /// <param name="definitionFingerprint">Exact materialization-definition fingerprint.</param>
    /// <param name="controlDefinitionFingerprint">Exact effective Control-definition fingerprint.</param>
    /// <param name="planFingerprint">Exact persisted rebuild-plan fingerprint.</param>
    /// <param name="targetId">Stable physical backend identity owning the target-local generation.</param>
    /// <param name="generationId">Generation whose pause and continue operations retain this epoch.</param>
    /// <param name="workload">Explicit governed workload.</param>
    /// <param name="loopId">Effective Control loop identity.</param>
    /// <exception cref="ArgumentNullException">A fingerprint is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workload"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationIndexSyncControlStateKey(
        MaterializationId materializationId,
        ExecutionDefinitionFingerprint definitionFingerprint,
        ExecutionDefinitionFingerprint controlDefinitionFingerprint,
        MaterializationRebuildPlanFingerprint planFingerprint,
        MaterializationTargetId targetId,
        MaterializationGenerationId generationId,
        MaterializationIndexSyncWorkloadKind workload,
        ControlLoopId loopId)
    {
        MaterializationContract.RequireDefinedIdentity(materializationId.Value, nameof(materializationId));
        MaterializationContract.RequireDefinedIdentity(targetId.Value, nameof(targetId));
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        if (string.IsNullOrWhiteSpace(loopId.Value))
            throw new ArgumentException("A durable Control key requires a loop identity.", nameof(loopId));
        if (!Enum.IsDefined(workload))
            throw new ArgumentOutOfRangeException(nameof(workload), workload, "Unsupported index-sync workload.");

        MaterializationId = materializationId;
        DefinitionFingerprint = definitionFingerprint ?? throw new ArgumentNullException(nameof(definitionFingerprint));
        ControlDefinitionFingerprint = controlDefinitionFingerprint
            ?? throw new ArgumentNullException(nameof(controlDefinitionFingerprint));
        PlanFingerprint = planFingerprint ?? throw new ArgumentNullException(nameof(planFingerprint));
        TargetId = targetId;
        GenerationId = generationId;
        Workload = workload;
        LoopId = loopId;
        Epoch = DeriveEpoch(this);
    }

    /// <summary>Logical materialization identity.</summary>
    public MaterializationId MaterializationId { get; }

    /// <summary>Exact materialization-definition fingerprint.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Exact effective Control-definition fingerprint.</summary>
    public ExecutionDefinitionFingerprint ControlDefinitionFingerprint { get; }

    /// <summary>Exact persisted rebuild-plan fingerprint.</summary>
    public MaterializationRebuildPlanFingerprint PlanFingerprint { get; }

    /// <summary>Stable physical backend identity owning the target-local generation.</summary>
    public MaterializationTargetId TargetId { get; }

    /// <summary>Generation retaining this Control epoch across pause and continue.</summary>
    public MaterializationGenerationId GenerationId { get; }

    /// <summary>Explicit governed workload.</summary>
    public MaterializationIndexSyncWorkloadKind Workload { get; }

    /// <summary>Effective Control loop identity.</summary>
    public ControlLoopId LoopId { get; }

    /// <summary>Deterministic exact epoch derived from every key component.</summary>
    [JsonIgnore]
    public ControlEpochId Epoch { get; }

    static ControlEpochId DeriveEpoch(MaterializationIndexSyncControlStateKey key)
    {
        using MaterializationStableIdentity.DigestBuilder builder = new();
        builder.Append("materialization-control-epoch/v2");
        builder.Append(key.MaterializationId.Value);
        builder.Append(key.DefinitionFingerprint.Algorithm);
        builder.Append(key.DefinitionFingerprint.Canonicalization);
        builder.Append(key.DefinitionFingerprint.Value);
        builder.Append(key.ControlDefinitionFingerprint.Algorithm);
        builder.Append(key.ControlDefinitionFingerprint.Canonicalization);
        builder.Append(key.ControlDefinitionFingerprint.Value);
        builder.Append(key.PlanFingerprint.Algorithm);
        builder.Append(key.PlanFingerprint.Canonicalization);
        builder.Append(key.PlanFingerprint.Value);
        builder.Append(key.TargetId.Value);
        builder.Append(key.GenerationId.Value);
        builder.Append(key.Workload.ToString());
        builder.Append(key.LoopId.Value);
        return new($"materialization-control-epoch/v2/{builder.Complete()}");
    }
}

/// <summary>Immutable status-safe pairing of a compiled realization and its exact durable state.</summary>
public sealed record MaterializationIndexSyncControlSnapshot
{
    /// <summary>Creates and validates one immutable realization/state snapshot.</summary>
    /// <param name="key">Exact durable ownership key.</param>
    /// <param name="realization">Compiled persisted realization.</param>
    /// <param name="state">Current durable loop state.</param>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Loop, fingerprint, target, workload, or epoch evidence differs.</exception>
    internal MaterializationIndexSyncControlSnapshot(
        MaterializationIndexSyncControlStateKey key,
        MaterializationIndexSyncControlRealization realization,
        ControlLoopState state)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Realization = realization ?? throw new ArgumentNullException(nameof(realization));
        State = state ?? throw new ArgumentNullException(nameof(state));

        var definition = realization.EffectiveDefinition;
        if (key.LoopId != definition.Id
            || key.Workload != realization.Workload
            || key.ControlDefinitionFingerprint != definition.Fingerprint
            || !string.Equals(definition.Target, key.MaterializationId.Value, StringComparison.Ordinal)
            || state.LoopId != definition.Id
            || state.DefinitionFingerprint != definition.Fingerprint
            || state.Epoch != key.Epoch
            || !string.Equals(state.Target, definition.Target, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A Control snapshot must pair one exact realization with its loop, fingerprint, workload, target, and epoch state.",
                nameof(state));
        }
        var validation = AimdControlReferenceRegulator.ValidateState(definition, state);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                "A Control snapshot contains state invalid under its exact effective definition: "
                + string.Join(" ", validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")),
                nameof(state));
        }
    }

    /// <summary>Creates a snapshot whose complete key is derived from one exact persisted plan.</summary>
    /// <param name="plan">Exact persisted plan owning the realization and target backend.</param>
    /// <param name="generationId">Exact target-local generation retaining the Control epoch.</param>
    /// <param name="realization">Exact persisted realization owned by <paramref name="plan"/>.</param>
    /// <param name="state">Current durable loop state.</param>
    /// <returns>A plan-bound validated immutable snapshot.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The generation is default, the realization is not owned by the plan, or state evidence differs.
    /// </exception>
    public static MaterializationIndexSyncControlSnapshot Create(
        MaterializationRebuildPlan plan,
        MaterializationGenerationId generationId,
        MaterializationIndexSyncControlRealization realization,
        ControlLoopState state)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(realization);
        ArgumentNullException.ThrowIfNull(state);
        MaterializationContract.RequireDefinedIdentity(generationId.Value, nameof(generationId));
        if (!plan.ControlRealizations.Contains(realization))
        {
            throw new ArgumentException(
                "A Control snapshot realization must belong to the exact persisted plan.",
                nameof(realization));
        }

        MaterializationIndexSyncControlStateKey key = new(
            plan.Materialization.Definition.Id,
            plan.Materialization.DefinitionFingerprint,
            realization.EffectiveDefinition.Fingerprint,
            plan.Fingerprint,
            plan.Target.Id,
            generationId,
            realization.Workload,
            realization.EffectiveDefinition.Id);
        return new(key, realization, state);
    }

    /// <summary>Exact durable ownership key.</summary>
    public MaterializationIndexSyncControlStateKey Key { get; }

    /// <summary>Compiled persisted realization.</summary>
    public MaterializationIndexSyncControlRealization Realization { get; }

    /// <summary>Current durable state.</summary>
    public ControlLoopState State { get; }
}

/// <summary>Outcome of a durable materialization Control-state compare-and-swap.</summary>
public enum MaterializationIndexSyncControlWriteDisposition
{
    /// <summary>The requested state transition committed.</summary>
    Applied = 0,

    /// <summary>The exact mutation and resulting state were replayed.</summary>
    Replayed = 1,

    /// <summary>The addressed state did not exist.</summary>
    NotFound = 2,

    /// <summary>The expected durable revision was stale.</summary>
    RevisionConflict = 3,

    /// <summary>The stable mutation identity was reused for different content.</summary>
    IdentityConflict = 4
}

/// <summary>Result of a durable materialization Control-state compare-and-swap.</summary>
/// <param name="Disposition">Semantic write outcome.</param>
/// <param name="State">Current or replayed state, when present.</param>
public readonly record struct MaterializationIndexSyncControlWriteResult(
    MaterializationIndexSyncControlWriteDisposition Disposition,
    ControlLoopState? State);

/// <summary>Durable CAS authority for exact materialization Control-loop state.</summary>
public interface IMaterializationIndexSyncControlStateStore
{
    /// <summary>Reads the current state for an exact durable key.</summary>
    /// <param name="context">Explicit cancellation and tracing context.</param>
    /// <param name="key">Exact state ownership key.</param>
    /// <returns>Current state, or <see langword="null"/> when absent.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<ControlLoopState?> ReadAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key);

    /// <summary>Checks whether one exact durable mutation intent has already committed.</summary>
    /// <param name="context">Explicit cancellation and tracing context.</param>
    /// <param name="key">Exact state ownership key.</param>
    /// <param name="mutationId">Stable idempotency identity.</param>
    /// <param name="mutationFingerprint">Canonical semantic-intent fingerprint.</param>
    /// <returns>
    /// <see cref="MaterializationIndexSyncControlWriteDisposition.Replayed"/> with current state for an exact
    /// committed intent, <see cref="MaterializationIndexSyncControlWriteDisposition.IdentityConflict"/> for
    /// identity reuse, or <see cref="MaterializationIndexSyncControlWriteDisposition.NotFound"/> when absent.
    /// </returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A mutation identity or fingerprint is empty.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<MaterializationIndexSyncControlWriteResult> ReadMutationAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint);

    /// <summary>Creates initial state if the key is absent.</summary>
    /// <param name="context">Explicit cancellation and tracing context.</param>
    /// <param name="key">Exact state ownership key.</param>
    /// <param name="mutationId">Stable idempotency identity.</param>
    /// <param name="mutationFingerprint">Canonical semantic-intent fingerprint.</param>
    /// <param name="state">Initial state at revision one.</param>
    /// <returns>Semantic CAS result.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="mutationId"/> is empty or state ownership differs.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<MaterializationIndexSyncControlWriteResult> CreateAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint,
        ControlLoopState state);

    /// <summary>Replaces state only at the exact expected revision.</summary>
    /// <param name="context">Explicit cancellation and tracing context.</param>
    /// <param name="key">Exact state ownership key.</param>
    /// <param name="mutationId">Stable idempotency identity.</param>
    /// <param name="mutationFingerprint">Canonical semantic-intent fingerprint.</param>
    /// <param name="expectedRevision">Exact current revision.</param>
    /// <param name="state">Complete next state.</param>
    /// <returns>Semantic CAS result.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Mutation identity or state ownership is invalid.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<MaterializationIndexSyncControlWriteResult> CompareExchangeAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint,
        ControlRevision expectedRevision,
        ControlLoopState state);
}

/// <summary>Thread-safe reference implementation of durable materialization Control-state CAS semantics.</summary>
public sealed class InMemoryMaterializationIndexSyncControlStateStore : IMaterializationIndexSyncControlStateStore
{
    readonly object gate = new();
    readonly Dictionary<MaterializationIndexSyncControlStateKey, ControlLoopState> entries = [];
    readonly Dictionary<(MaterializationIndexSyncControlStateKey Key, string MutationId), string>
        mutations = [];

    /// <inheritdoc />
    public ValueTask<ControlLoopState?> ReadAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);
        context.ThrowIfCancellationRequested();
        lock (gate)
            return ValueTask.FromResult(entries.GetValueOrDefault(key));
    }

    /// <inheritdoc />
    public ValueTask<MaterializationIndexSyncControlWriteResult> ReadMutationAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(mutationId))
            throw new ArgumentException("A durable Control mutation requires an identity.", nameof(mutationId));
        if (string.IsNullOrWhiteSpace(mutationFingerprint))
            throw new ArgumentException("A durable Control mutation requires an intent fingerprint.", nameof(mutationFingerprint));
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!mutations.TryGetValue((key, mutationId), out var retainedFingerprint))
            {
                return ValueTask.FromResult(new MaterializationIndexSyncControlWriteResult(
                    MaterializationIndexSyncControlWriteDisposition.NotFound,
                    entries.GetValueOrDefault(key)));
            }
            return ValueTask.FromResult(new MaterializationIndexSyncControlWriteResult(
                string.Equals(retainedFingerprint, mutationFingerprint, StringComparison.Ordinal)
                    ? MaterializationIndexSyncControlWriteDisposition.Replayed
                    : MaterializationIndexSyncControlWriteDisposition.IdentityConflict,
                entries.GetValueOrDefault(key)));
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationIndexSyncControlWriteResult> CreateAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint,
        ControlLoopState state)
    {
        Validate(context, key, mutationId, mutationFingerprint, state);
        if (state.Revision != ControlRevision.Initial)
            throw new ArgumentException("Initial materialization Control state must use revision one.", nameof(state));

        lock (gate)
        {
            var mutationKey = (key, mutationId);
            if (mutations.TryGetValue(mutationKey, out var replayFingerprint))
            {
                return ValueTask.FromResult(string.Equals(replayFingerprint, mutationFingerprint, StringComparison.Ordinal)
                    ? new MaterializationIndexSyncControlWriteResult(
                        MaterializationIndexSyncControlWriteDisposition.Replayed,
                        entries.GetValueOrDefault(key))
                    : new MaterializationIndexSyncControlWriteResult(
                        MaterializationIndexSyncControlWriteDisposition.IdentityConflict,
                        entries.GetValueOrDefault(key)));
            }

            if (entries.TryGetValue(key, out var current))
            {
                return ValueTask.FromResult(new MaterializationIndexSyncControlWriteResult(
                    MaterializationIndexSyncControlWriteDisposition.RevisionConflict,
                    current));
            }

            entries.Add(key, state);
            mutations.Add(mutationKey, mutationFingerprint);
            return ValueTask.FromResult(new MaterializationIndexSyncControlWriteResult(
                MaterializationIndexSyncControlWriteDisposition.Applied,
                state));
        }
    }

    /// <inheritdoc />
    public ValueTask<MaterializationIndexSyncControlWriteResult> CompareExchangeAsync(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint,
        ControlRevision expectedRevision,
        ControlLoopState state)
    {
        Validate(context, key, mutationId, mutationFingerprint, state);
        var requiredRevision = expectedRevision.Next();
        if (state.Revision != requiredRevision)
            throw new ArgumentException("A replacement Control state must advance exactly one durable revision.", nameof(state));

        lock (gate)
        {
            var mutationKey = (key, mutationId);
            if (mutations.TryGetValue(mutationKey, out var replayFingerprint))
            {
                return ValueTask.FromResult(string.Equals(replayFingerprint, mutationFingerprint, StringComparison.Ordinal)
                    ? new MaterializationIndexSyncControlWriteResult(
                        MaterializationIndexSyncControlWriteDisposition.Replayed,
                        entries.GetValueOrDefault(key))
                    : new MaterializationIndexSyncControlWriteResult(
                        MaterializationIndexSyncControlWriteDisposition.IdentityConflict,
                        entries.GetValueOrDefault(key)));
            }
            if (!entries.TryGetValue(key, out var current))
            {
                return ValueTask.FromResult(new MaterializationIndexSyncControlWriteResult(
                    MaterializationIndexSyncControlWriteDisposition.NotFound,
                    null));
            }
            if (current.Revision != expectedRevision)
            {
                return ValueTask.FromResult(new MaterializationIndexSyncControlWriteResult(
                    MaterializationIndexSyncControlWriteDisposition.RevisionConflict,
                    current));
            }

            entries[key] = state;
            mutations.Add(mutationKey, mutationFingerprint);
            return ValueTask.FromResult(new MaterializationIndexSyncControlWriteResult(
                MaterializationIndexSyncControlWriteDisposition.Applied,
                state));
        }
    }

    static void Validate(
        OperationContext context,
        MaterializationIndexSyncControlStateKey key,
        string mutationId,
        string mutationFingerprint,
        ControlLoopState state)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(mutationId))
            throw new ArgumentException("A durable Control mutation requires an identity.", nameof(mutationId));
        if (string.IsNullOrWhiteSpace(mutationFingerprint))
            throw new ArgumentException("A durable Control mutation requires an intent fingerprint.", nameof(mutationFingerprint));
        context.ThrowIfCancellationRequested();
        if (state.LoopId != key.LoopId
            || state.Epoch != key.Epoch
            || state.DefinitionFingerprint != key.ControlDefinitionFingerprint
            || !string.Equals(state.Target, key.MaterializationId.Value, StringComparison.Ordinal))
            throw new ArgumentException("Control state does not belong to the exact durable key.", nameof(state));
    }
}
