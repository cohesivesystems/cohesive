using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable diagnostic codes returned by the reference rebuild execution interpretation.</summary>
public static class MaterializationRebuildDiagnosticCodes
{
    /// <summary>The pinned plan and supplied runtime binding differ.</summary>
    public const string RuntimeBindingMismatch = "materialization.rebuild.runtimeBinding.mismatch";

    /// <summary>A source page or canonical hydration result was not conclusive.</summary>
    public const string HydrationIncomplete = "materialization.rebuild.hydration.incomplete";

    /// <summary>A hydrated row lacked a concrete stable output identity.</summary>
    public const string OutputIdentityMissing = "materialization.rebuild.output.identityMissing";

    /// <summary>One bounded target mutation remained unsuccessful after the declared retry budget.</summary>
    public const string TargetMutationFailed = "materialization.rebuild.target.mutationFailed";

    /// <summary>
    /// Re-reading an uncheckpointed baseline page produced different canonical target intent after an earlier write.
    /// </summary>
    public const string SourceReplayDrift = "materialization.rebuild.source.replayDrift";

    /// <summary>A stale worker, revision, or attempt tried to advance authoritative progress.</summary>
    public const string ProgressFenced = "materialization.rebuild.progress.fenced";

    /// <summary>A shard exceeded a finite page or target-bulk operating boundary.</summary>
    public const string OperatingBoundaryExceeded = "materialization.rebuild.boundary.exceeded";
}

/// <summary>Exact Process attempt and stable start time owning one candidate generation.</summary>
public sealed record MaterializationRebuildAttempt
{
    /// <summary>Creates one rebuild-attempt identity.</summary>
    /// <param name="continuation">Logical coordinator Process instance and exact current attempt.</param>
    /// <param name="startedAtUtc">Stable UTC Process-attempt start time, retained across retry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="continuation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="startedAtUtc"/> is not UTC.</exception>
    [JsonConstructor]
    public MaterializationRebuildAttempt(
        ProcessContinuationIdentity continuation,
        DateTimeOffset startedAtUtc)
    {
        Continuation = Guard.RequireNotNull(continuation);
        MaterializationContract.RequireUtc(startedAtUtc, nameof(startedAtUtc));
        StartedAtUtc = startedAtUtc;
    }

    /// <summary>Logical coordinator Process instance and exact current attempt.</summary>
    public ProcessContinuationIdentity Continuation { get; }

    /// <summary>Stable UTC Process-attempt start time.</summary>
    public DateTimeOffset StartedAtUtc { get; }
}

/// <summary>Deterministic identities derived from one exact rebuild plan, attempt, shard, and page.</summary>
public static class MaterializationRebuildIdentities
{
    const string Prefix = "materialization-rebuild/v1";

    internal static MaterializationItemVersion BaselineItemVersion { get; } = new("1");

    /// <summary>Derives the one candidate generation owned by a Process attempt.</summary>
    /// <param name="plan">Exact pinned rebuild plan.</param>
    /// <param name="attempt">Exact Process attempt.</param>
    /// <returns>A generation identity that changes exactly when the Process attempt changes.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static MaterializationGenerationId Generation(
        MaterializationRebuildPlan plan,
        MaterializationRebuildAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(attempt);
        return new($"{Prefix}/generation/{Digest(
            plan.Materialization.Definition.Id.Value,
            plan.Materialization.DefinitionFingerprint.Value,
            plan.Fingerprint.Value,
            attempt.Continuation.ProcessInstanceId.Value,
            attempt.Continuation.ProcessAttemptId.Value)}");
    }

    /// <summary>Creates the generic Process attempt-affinity value for the candidate generation.</summary>
    /// <param name="slot">Stable Process node declaring the generation-affinity slot.</param>
    /// <param name="plan">Exact pinned rebuild plan.</param>
    /// <param name="attempt">Exact Process attempt.</param>
    /// <returns>A concrete String affinity containing the deterministic generation identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> or <paramref name="attempt"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="slot"/> is default.</exception>
    public static ProcessAttemptAffinity GenerationAffinity(
        ExecutionNodeId slot,
        MaterializationRebuildPlan plan,
        MaterializationRebuildAttempt attempt) =>
        GenerationAffinity(slot, Generation(plan, attempt));

    /// <summary>Creates the generic Process attempt-affinity value for an exact candidate generation.</summary>
    /// <param name="slot">Stable Process node declaring the generation-affinity slot.</param>
    /// <param name="generation">Exact candidate generation owned by the Process attempt.</param>
    /// <returns>A concrete String affinity containing <paramref name="generation"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="slot"/> or <paramref name="generation"/> is default.
    /// </exception>
    public static ProcessAttemptAffinity GenerationAffinity(
        ExecutionNodeId slot,
        MaterializationGenerationId generation)
    {
        if (string.IsNullOrWhiteSpace(slot.Value))
            throw new ArgumentException("A generation affinity requires a stable Process slot.", nameof(slot));
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        var value = PortableValue.Concrete(
            new ValueContract(new ScalarTypeRef(ScalarTypeKind.String)),
            ObservationValue.FromString(generation.Value));
        return new(slot, value);
    }

    internal static MaterializationProgressMutationId Fence(
        MaterializationRebuildPlan plan,
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildShardId shard,
        MaterializationProgressRevision? revision) =>
        new($"{Prefix}/progress-fence/{Digest(
            plan.Fingerprint.Value,
            attempt.Continuation.ProcessInstanceId.Value,
            attempt.Continuation.ProcessAttemptId.Value,
            shard.Value,
            revision?.Value ?? "absent")}");

    internal static MaterializationCheckpointId ChangeCut(
        MaterializationRebuildPlan plan,
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildShardId shard) =>
        new($"{Prefix}/change-cut/{Digest(
            plan.Fingerprint.Value,
            attempt.Continuation.ProcessInstanceId.Value,
            attempt.Continuation.ProcessAttemptId.Value,
            shard.Value)}");

    internal static MaterializationProgressMutationId ChangeCutMutation(
        MaterializationRebuildPlan plan,
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildShardId shard) =>
        new($"{Prefix}/change-cut-mutation/{Digest(
            plan.Fingerprint.Value,
            attempt.Continuation.ProcessInstanceId.Value,
            attempt.Continuation.ProcessAttemptId.Value,
            shard.Value)}");

    internal static string Page(
        MaterializationRebuildPlan plan,
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildShardPlan shard,
        MaterializationSourceContinuation? continuation) =>
        $"{Prefix}/page/{Digest(
            plan.Fingerprint.Value,
            attempt.Continuation.ProcessInstanceId.Value,
            attempt.Continuation.ProcessAttemptId.Value,
            shard.Id.Value,
            continuation?.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "start",
            continuation?.ReadFingerprint.Value ?? "start",
            continuation?.Value ?? "start")}";

    internal static MaterializationBatchId Batch(string page, int chunk, int retry) =>
        new($"{Prefix}/batch/{Digest(
            page,
            chunk.ToString(System.Globalization.CultureInfo.InvariantCulture),
            retry.ToString(System.Globalization.CultureInfo.InvariantCulture))}");

    internal static MaterializationItemMutationId Mutation(string page, string itemIdentity) =>
        new($"{Prefix}/mutation/{Digest(page, itemIdentity)}");

    internal static MaterializationCheckpointId BaselineCheckpoint(string page) =>
        new($"{Prefix}/baseline-checkpoint/{Digest(page)}");

    internal static MaterializationProgressMutationId BaselineCheckpointMutation(string page) =>
        new($"{Prefix}/baseline-checkpoint-mutation/{Digest(page)}");

    internal static MaterializationAbandonmentId Abandonment(
        MaterializationRebuildPlan plan,
        MaterializationRebuildAttempt attempt) =>
        new($"{Prefix}/abandonment/{Digest(
            plan.Fingerprint.Value,
            attempt.Continuation.ProcessInstanceId.Value,
            attempt.Continuation.ProcessAttemptId.Value)}");

    static string Digest(string first)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, first);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static string Digest(string first, string second)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, first);
        Append(hash, second);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static string Digest(string first, string second, string third)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, first);
        Append(hash, second);
        Append(hash, third);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static string Digest(string first, string second, string third, string fourth)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, first);
        Append(hash, second);
        Append(hash, third);
        Append(hash, fourth);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static string Digest(string first, string second, string third, string fourth, string fifth)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, first);
        Append(hash, second);
        Append(hash, third);
        Append(hash, fourth);
        Append(hash, fifth);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static string Digest(
        string first,
        string second,
        string third,
        string fourth,
        string fifth,
        string sixth,
        string seventh)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, first);
        Append(hash, second);
        Append(hash, third);
        Append(hash, fourth);
        Append(hash, fifth);
        Append(hash, sixth);
        Append(hash, seventh);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    static void Append(IncrementalHash hash, string value)
    {
        const int StackBufferSize = 256;
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, byteCount);
        hash.AppendData(length);

        byte[]? rented = null;
        Span<byte> bytes = byteCount <= StackBufferSize
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        try
        {
            Encoding.UTF8.GetBytes(value, bytes);
            hash.AppendData(bytes);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>One exact bounded page supplied to canonical Relations hydration.</summary>
public sealed record MaterializationRebuildHydrationRequest
{
    /// <summary>Creates a canonical hydration request.</summary>
    /// <param name="evaluation">Stable Relations evaluation identity for this logical page.</param>
    /// <param name="shard">Exact persisted shard definition.</param>
    /// <param name="page">Bounded source page.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shard"/> or <paramref name="page"/> is null.</exception>
    /// <exception cref="ArgumentException">The evaluation is default or the page belongs to another shard.</exception>
    public MaterializationRebuildHydrationRequest(
        RelationQueryEvaluationId evaluation,
        MaterializationRebuildShardPlan shard,
        MaterializationSourcePage page)
    {
        MaterializationContract.RequireDefinedIdentity(evaluation.Value, nameof(evaluation));
        Shard = Guard.RequireNotNull(shard);
        Page = Guard.RequireNotNull(page);
        if (page.Scope != shard.Scope
            || page.ReadFingerprint != MaterializationSourceReadFingerprinter.Compute(shard.Read))
        {
            throw new ArgumentException("A hydration page must belong to the exact persisted shard read.", nameof(page));
        }
        Evaluation = evaluation;
    }

    /// <summary>Stable Relations evaluation identity for this logical page.</summary>
    public RelationQueryEvaluationId Evaluation { get; }

    /// <summary>Exact persisted shard definition.</summary>
    public MaterializationRebuildShardPlan Shard { get; }

    /// <summary>Bounded source page.</summary>
    public MaterializationSourcePage Page { get; }
}

/// <summary>Conclusive canonical Relations outputs for one rebuild page.</summary>
public sealed record MaterializationRebuildHydrationResult
{
    /// <summary>Creates one hydration result.</summary>
    /// <param name="rows">Complete shaped output rows in deterministic interpreter order.</param>
    /// <param name="evidenceReference">Optional opaque execution evidence reference.</param>
    /// <exception cref="ArgumentException">A row is null or the evidence reference is empty.</exception>
    public MaterializationRebuildHydrationResult(
        ImmutableArray<RelationQueryOutputRow> rows,
        string? evidenceReference = null)
    {
        var normalized = rows.IsDefault ? [] : rows;
        if (normalized.Any(static row => row is null))
            throw new ArgumentException("Hydrated output rows cannot contain null entries.", nameof(rows));
        if (evidenceReference is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        Rows = normalized;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Complete shaped output rows in deterministic interpreter order.</summary>
    public ImmutableArray<RelationQueryOutputRow> Rows { get; }

    /// <summary>Optional opaque execution evidence reference.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Runtime interpretation port that executes one exact canonical Relations hydration plan.</summary>
public interface IMaterializationRebuildHydrator
{
    /// <summary>Exact compiled Relations plan interpreted by this hydrator.</summary>
    RelationQueryCompiledPlanReference Plan { get; }

    /// <summary>
    /// Exact physical-plan fingerprint pinning the hydration realization, placements, lowering, and execution policy.
    /// </summary>
    RelationQueryPhysicalPlanFingerprint PhysicalPlan { get; }

    /// <summary>Hydrates one bounded root page through canonical Relations execution.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="request">Exact page hydration request.</param>
    /// <returns>Conclusive selected output rows.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Canonical Relations execution is incomplete or failed.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask<MaterializationRebuildHydrationResult> HydrateAsync(
        OperationContext context,
        MaterializationRebuildHydrationRequest request);
}

/// <summary>Canonical Relations physical-execution interpretation for rebuild page hydration.</summary>
public sealed class RelationQueryMaterializationRebuildHydrator : IMaterializationRebuildHydrator
{
    readonly CompiledRelationQueryPlan plan;
    readonly CompiledRelationQueryPhysicalPlan physicalPlan;
    readonly RelationQueryRealizationReport realization;
    readonly RelationQueryInputId suppliedRoot;
    readonly RelationQueryOutputReference output;
    readonly RelationQueryPhysicalExecutor executor;

    /// <summary>Creates one exact Relations hydration interpretation.</summary>
    /// <param name="plan">Exact successful semantic plan.</param>
    /// <param name="physicalPlan">Exact physical plan whose root placement is supplied.</param>
    /// <param name="realization">Exact successful realization report cited by the physical plan.</param>
    /// <param name="suppliedRoot">Canonical relation-root input supplied by each rebuild page.</param>
    /// <param name="output">Complete demanded output selected by the materialization.</param>
    /// <param name="sourceReaders">Readers for non-root hydration inputs.</param>
    /// <exception cref="ArgumentNullException">A required reference or collection is null.</exception>
    /// <exception cref="ArgumentException">The selected input, output, or physical placement is incompatible.</exception>
    public RelationQueryMaterializationRebuildHydrator(
        CompiledRelationQueryPlan plan,
        CompiledRelationQueryPhysicalPlan physicalPlan,
        RelationQueryRealizationReport realization,
        RelationQueryInputId suppliedRoot,
        RelationQueryOutputReference output,
        IEnumerable<IRelationQuerySourceReader> sourceReaders)
    {
        this.plan = Guard.RequireNotNull(plan);
        this.physicalPlan = Guard.RequireNotNull(physicalPlan);
        this.realization = Guard.RequireNotNull(realization);
        this.output = Guard.RequireNotNull(output);
        ArgumentNullException.ThrowIfNull(sourceReaders);
        var exactPlan = RelationQueryCompiledPlanReference.From(plan);
        var exactPlanFingerprint = RelationQueryCompiledPlanReferenceFingerprinter.Compute(exactPlan);
        if (RelationQueryCompiledPlanReferenceFingerprinter.Compute(physicalPlan.Plan) != exactPlanFingerprint
            || RelationQueryCompiledPlanReferenceFingerprinter.Compute(realization.Plan) != exactPlanFingerprint
            || physicalPlan.Realization != realization.Fingerprint
            || !realization.IsRealizable)
        {
            throw new ArgumentException(
                "Rebuild hydration requires one exact realizable semantic, realization, and physical-plan chain.",
                nameof(physicalPlan));
        }
        MaterializationContract.RequireDefinedIdentity(suppliedRoot.Value, nameof(suppliedRoot));
        if (!plan.RequirementGraph.Outputs.Any(candidate => output.Covers(candidate) || candidate.Covers(output)))
            throw new ArgumentException("The selected output is absent from the exact compiled plan.", nameof(output));
        if (output.Field is not null)
            throw new ArgumentException("Rebuild hydration requires a complete shaped output.", nameof(output));
        var input = plan.InputContract.Sources.SingleOrDefault(source => source.Input.Id == suppliedRoot);
        if (input?.Role != RelationQuerySourceInputRole.RelationRoot)
            throw new ArgumentException("The supplied hydration input must be one canonical relation root.", nameof(suppliedRoot));
        var placement = physicalPlan.Placement.Bindings.SingleOrDefault(binding => binding.Input == suppliedRoot);
        if (placement?.Acquisition != RelationQuerySourceAcquisitionKind.Supplied)
            throw new ArgumentException("The hydration physical plan must mark the page root as supplied.", nameof(physicalPlan));

        this.suppliedRoot = suppliedRoot;
        executor = new(sourceReaders);
        Plan = exactPlan;
        PhysicalPlan = physicalPlan.Fingerprint;
    }

    /// <inheritdoc />
    public RelationQueryCompiledPlanReference Plan { get; }

    /// <inheritdoc />
    public RelationQueryPhysicalPlanFingerprint PhysicalPlan { get; }

    /// <inheritdoc />
    public async ValueTask<MaterializationRebuildHydrationResult> HydrateAsync(
        OperationContext context,
        MaterializationRebuildHydrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        var supplied = new RelationQuerySuppliedSourceInput(
            input: suppliedRoot,
            completeness: RelationQueryEvidenceCompleteness.Complete,
            observations: request.Page.Read.Observations,
            evidenceReference: request.Page.Read.EvidenceReference);
        var execution = await executor.ExecuteAsync(
                new RelationQueryPhysicalExecutionRequest(
                    plan: plan,
                    physicalPlan: physicalPlan,
                    realization: realization,
                    evaluation: request.Evaluation,
                    suppliedSources: [supplied]),
                context.CancellationToken)
            .ConfigureAwait(false);
        if (!execution.IsSuccessful || execution.Interpretation is null)
        {
            throw new InvalidOperationException(
                $"Canonical Relations hydration '{request.Evaluation.Value}' was not conclusive: "
                + string.Join(" ", execution.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }

        var rows = output.Kind switch
        {
            RelationQueryOutputReferenceKind.Relation
                when execution.Interpretation.Relation is { } relation
                     && relation.Relation == output.Relation
                     && relation.State == RelationQueryExecutionOutputState.Complete => relation.Rows,
            RelationQueryOutputReferenceKind.QueryResult
                when execution.Interpretation.QueryResults.SingleOrDefault(result => result.Result == output.QueryResult) is { } result
                     && result.State == RelationQueryExecutionOutputState.Complete => result.Rows,
            _ => throw new InvalidOperationException(
                $"Selected Relations output '{output.Id.Value}' was absent or incomplete after hydration.")
        };
        if (rows.Any(static row => !row.IsComplete))
            throw new InvalidOperationException("A rebuild cannot materialize rows with unresolved Relations gaps.");
        return new(rows, evidenceReference: request.Evaluation.Value);
    }
}

/// <summary>Runtime binding of one persisted shard to physical source and hydration interpretations.</summary>
public sealed class MaterializationRebuildShardBinding
{
    /// <summary>Creates one runtime shard binding.</summary>
    /// <param name="shard">Exact persisted shard definition.</param>
    /// <param name="source">Bounded scan and positioned change source.</param>
    /// <param name="hydrator">Exact canonical Relations hydration interpretation.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A source or plan identity differs from the persisted shard.</exception>
    public MaterializationRebuildShardBinding(
        MaterializationRebuildShardPlan shard,
        IMaterializationPullChangeSource source,
        IMaterializationRebuildHydrator hydrator)
    {
        Shard = Guard.RequireNotNull(shard);
        Source = Guard.RequireNotNull(source);
        Hydrator = Guard.RequireNotNull(hydrator);
        if (source.Descriptor.Source != shard.Scope.Source)
            throw new ArgumentException("A runtime source must implement the exact persisted shard source.", nameof(source));
        if (hydrator.PhysicalPlan != shard.HydrationPhysicalPlan)
        {
            throw new ArgumentException(
                "A runtime hydrator must implement the exact persisted hydration physical plan.",
                nameof(hydrator));
        }
    }

    /// <summary>Exact persisted shard definition.</summary>
    public MaterializationRebuildShardPlan Shard { get; }

    /// <summary>Bounded scan and positioned change source.</summary>
    public IMaterializationPullChangeSource Source { get; }

    /// <summary>Exact canonical Relations hydration interpretation.</summary>
    public IMaterializationRebuildHydrator Hydrator { get; }
}

/// <summary>Exact-context runtime bindings for one persisted rebuild realization plan.</summary>
public sealed class ResolvedMaterializationRebuildPlan
{
    readonly ImmutableDictionary<MaterializationRebuildShardId, MaterializationRebuildShardBinding> shards;

    /// <summary>Resolves a persisted plan against exact runtime ports.</summary>
    /// <param name="plan">Persisted rebuild realization plan.</param>
    /// <param name="target">Exact candidate-generation target.</param>
    /// <param name="progressStore">Durable application-progress authority.</param>
    /// <param name="shardBindings">One runtime binding for every persisted shard.</param>
    /// <exception cref="ArgumentNullException">A required argument or collection is null.</exception>
    /// <exception cref="ArgumentException">A binding is missing, duplicated, stale, or incompatible.</exception>
    public ResolvedMaterializationRebuildPlan(
        MaterializationRebuildPlan plan,
        IMaterializationTarget target,
        IMaterializationProgressStore progressStore,
        IEnumerable<MaterializationRebuildShardBinding> shardBindings)
    {
        Plan = Guard.RequireNotNull(plan);
        Target = Guard.RequireNotNull(target);
        ProgressStore = Guard.RequireNotNull(progressStore);
        ArgumentNullException.ThrowIfNull(shardBindings);
        var normalized = shardBindings.ToArray();
        if (normalized.Any(static binding => binding is null))
            throw new ArgumentException("Runtime shard bindings cannot contain null entries.", nameof(shardBindings));
        if (normalized.GroupBy(static binding => binding.Shard.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Runtime shard bindings cannot repeat a shard identity.", nameof(shardBindings));
        if (!plan.Shards.Select(static shard => shard.Id)
                .SequenceEqual(normalized.Select(static binding => binding.Shard.Id).OrderBy(static id => id.Value, StringComparer.Ordinal)))
        {
            throw new ArgumentException("Runtime bindings must cover every exact persisted shard once.", nameof(shardBindings));
        }
        if (!SameCanonical(target.Descriptor, plan.Target))
            throw new ArgumentException("The runtime target differs from the exact persisted target descriptor.", nameof(target));
        foreach (var binding in normalized)
        {
            var persisted = plan.Shards.Single(candidate => candidate.Id == binding.Shard.Id);
            if (!SameCanonical(persisted, binding.Shard)
                || RelationQueryCompiledPlanReferenceFingerprinter.Compute(binding.Hydrator.Plan)
                    != RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                        plan.Materialization.Definition.Relation.CompiledPlan)
                || binding.Hydrator.PhysicalPlan != persisted.HydrationPhysicalPlan)
            {
                throw new ArgumentException("A runtime shard binding differs from its exact persisted semantics.", nameof(shardBindings));
            }
            var source = plan.Sources.Single(candidate => candidate.Input == persisted.Scope.Input);
            if (binding.Source.Descriptor.Source != source.Source
                || !SameCanonical(binding.Source.Descriptor.CapabilityProfile, source.Profile))
            {
                throw new ArgumentException("A runtime source differs from its pinned capability evidence.", nameof(shardBindings));
            }
        }

        shards = normalized.ToImmutableDictionary(static binding => binding.Shard.Id);
    }

    /// <summary>Exact persisted rebuild realization plan.</summary>
    public MaterializationRebuildPlan Plan { get; }

    /// <summary>Exact candidate-generation target.</summary>
    public IMaterializationTarget Target { get; }

    /// <summary>Durable application-progress authority.</summary>
    public IMaterializationProgressStore ProgressStore { get; }

    /// <summary>Gets the exact runtime binding of one persisted shard.</summary>
    /// <param name="shard">Stable shard identity.</param>
    /// <returns>The exact resolved binding.</returns>
    /// <exception cref="KeyNotFoundException"><paramref name="shard"/> is absent.</exception>
    public MaterializationRebuildShardBinding GetShard(MaterializationRebuildShardId shard) => shards[shard];

    static bool SameCanonical<T>(T left, T right) where T : class =>
        StrictDocumentJson.GetCanonicalBytes(left, MaterializationJsonSerializer.CreateOptions())
            .AsSpan()
            .SequenceEqual(StrictDocumentJson.GetCanonicalBytes(right, MaterializationJsonSerializer.CreateOptions()));
}

/// <summary>Crash-injection boundary exposed by the deterministic reference rebuild executor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationRebuildCrashPoint
{
    /// <summary>A bounded source page was observed but not yet hydrated.</summary>
    AfterScan = 0,

    /// <summary>Canonical Relations hydration completed but no target bulk was acknowledged.</summary>
    AfterHydration = 1,

    /// <summary>One idempotent target bulk completed but baseline progress was not checkpointed.</summary>
    AfterBulk = 2,

    /// <summary>The baseline application checkpoint committed.</summary>
    AfterCheckpoint = 3
}

/// <summary>Attributable observation at one rebuild crash-injection boundary.</summary>
/// <param name="Attempt">Exact owning Process attempt.</param>
/// <param name="Generation">Candidate generation owned by the attempt.</param>
/// <param name="Shard">Stable shard being advanced.</param>
/// <param name="PageIdentity">Deterministic identity of the page operation.</param>
/// <param name="Point">Durability boundary that has just been crossed.</param>
/// <param name="Occurrence">Zero-based observation ordinal within the shard invocation.</param>
public sealed record MaterializationRebuildCrashObservation(
    MaterializationRebuildAttempt Attempt,
    MaterializationGenerationId Generation,
    MaterializationRebuildShardId Shard,
    string PageIdentity,
    MaterializationRebuildCrashPoint Point,
    int Occurrence);

/// <summary>Optional deterministic fault-injection and boundary-observation hook for rebuild conformance.</summary>
public interface IMaterializationRebuildCrashInjector
{
    /// <summary>Observes one exact post-operation boundary and may throw to simulate interruption.</summary>
    /// <param name="context">Explicit cancellation and tracing context.</param>
    /// <param name="observation">Exact attempt, generation, shard, page, point, and occurrence.</param>
    /// <returns>Completion when execution may continue.</returns>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    ValueTask ObserveAsync(OperationContext context, MaterializationRebuildCrashObservation observation);
}

/// <summary>No-op rebuild crash injector used by conventional execution.</summary>
public sealed class NoOpMaterializationRebuildCrashInjector : IMaterializationRebuildCrashInjector
{
    NoOpMaterializationRebuildCrashInjector() { }

    /// <summary>Shared stateless no-op instance.</summary>
    public static NoOpMaterializationRebuildCrashInjector Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask ObserveAsync(
        OperationContext context,
        MaterializationRebuildCrashObservation observation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observation);
        context.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Observable disposition of one rebuild attempt initialization.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationRebuildInitializationDisposition
{
    /// <summary>The candidate and every initial progress cut are durably established.</summary>
    Ready = 0,

    /// <summary>The target rejected candidate allocation.</summary>
    TargetRejected = 1,

    /// <summary>One source or progress aggregate could not establish its initial cut.</summary>
    ProgressRejected = 2
}

/// <summary>Result of idempotently initializing one rebuild Process attempt.</summary>
public sealed record MaterializationRebuildInitializationResult
{
    /// <summary>Creates an initialization result.</summary>
    /// <param name="disposition">Observable initialization disposition.</param>
    /// <param name="generation">Deterministic attempt-owned candidate generation.</param>
    /// <param name="generationSnapshot">Current candidate metadata when retained by the target.</param>
    /// <param name="progress">One progress snapshot per established shard.</param>
    /// <param name="diagnostics">Structured deterministic rejection diagnostics.</param>
    /// <exception cref="ArgumentException">Result evidence contradicts its disposition.</exception>
    public MaterializationRebuildInitializationResult(
        MaterializationRebuildInitializationDisposition disposition,
        MaterializationGenerationId generation,
        MaterializationGenerationSnapshot? generationSnapshot,
        ImmutableArray<MaterializationProgressSnapshot> progress = default,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported rebuild initialization disposition.");
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        var normalizedProgress = progress.IsDefault ? [] : progress;
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (normalizedProgress.Any(static snapshot => snapshot is null)
            || normalizedDiagnostics.Any(static diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Initialization evidence cannot contain null entries.", nameof(progress));
        }
        if (normalizedProgress.Any(snapshot => snapshot.Key.Generation != generation)
            || normalizedProgress.Select(static snapshot => snapshot.Key.Scope).Distinct().Count()
                != normalizedProgress.Length
            || !normalizedProgress.IsDefaultOrEmpty
                && normalizedProgress.Any(snapshot =>
                    snapshot.Key.Materialization != normalizedProgress[0].Key.Materialization
                    || snapshot.Key.DefinitionFingerprint != normalizedProgress[0].Key.DefinitionFingerprint))
        {
            throw new ArgumentException(
                "Initialization progress must identify unique scopes in one exact materialization generation.",
                nameof(progress));
        }
        if (disposition == MaterializationRebuildInitializationDisposition.Ready
            && (generationSnapshot is null
                || generationSnapshot.GenerationId != generation
                || generationSnapshot.State != MaterializationGenerationState.Loading
                || normalizedProgress.IsDefaultOrEmpty
                || normalizedProgress.Any(static snapshot =>
                    snapshot.LatestChangeCheckpoint?.Kind != MaterializationCheckpointKind.ChangeProgress)
                || generationSnapshot.MaterializationId != normalizedProgress[0].Key.Materialization
                || generationSnapshot.DefinitionFingerprint
                    != normalizedProgress[0].Key.DefinitionFingerprint
                || !normalizedDiagnostics.IsDefaultOrEmpty))
        {
            throw new ArgumentException(
                "Ready initialization requires exact Loading-candidate metadata, unique captured change cuts, and no diagnostics.",
                nameof(disposition));
        }
        if (disposition != MaterializationRebuildInitializationDisposition.Ready
            && normalizedDiagnostics.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Rejected initialization requires diagnostics.", nameof(diagnostics));
        }
        Disposition = disposition;
        Generation = generation;
        GenerationSnapshot = generationSnapshot;
        Progress = normalizedProgress;
        Diagnostics = normalizedDiagnostics;
    }

    /// <summary>Observable initialization disposition.</summary>
    public MaterializationRebuildInitializationDisposition Disposition { get; }

    /// <summary>Deterministic attempt-owned candidate generation.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Current candidate metadata when retained by the target.</summary>
    public MaterializationGenerationSnapshot? GenerationSnapshot { get; }

    /// <summary>Established progress snapshots in shard order.</summary>
    public ImmutableArray<MaterializationProgressSnapshot> Progress { get; }

    /// <summary>Structured deterministic rejection diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

/// <summary>Observable terminal disposition of one Storage-owned shard rebuild.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationRebuildShardDisposition
{
    /// <summary>The baseline scan completed and its initial change cut remains durable.</summary>
    BaselineCompleteCatchUpRequired = 0,

    /// <summary>Source acquisition or canonical Relations hydration was not conclusive.</summary>
    SourceOrHydrationFailed = 1,

    /// <summary>Target application failed under the declared bounded policy.</summary>
    TargetFailed = 2,

    /// <summary>A stale worker or revision was rejected.</summary>
    Fenced = 3,

    /// <summary>A finite operating boundary was exceeded.</summary>
    BoundaryExceeded = 4,

    /// <summary>
    /// Continuing the same attempt is unsafe; external control must issue RestartAttempt so lifecycle coordination
    /// durably abandons it and starts a new generation.
    /// </summary>
    RestartRequired = 5
}

/// <summary>Terminal evidence for one Storage-owned shard rebuild operation.</summary>
public sealed record MaterializationRebuildShardResult
{
    /// <summary>Creates a shard rebuild result.</summary>
    /// <param name="disposition">Observable terminal disposition.</param>
    /// <param name="shard">Stable shard identity.</param>
    /// <param name="generation">Attempt-owned candidate generation.</param>
    /// <param name="pages">Number of authoritatively checkpointed pages.</param>
    /// <param name="outputs">Number of successfully applied output rows.</param>
    /// <param name="progress">Latest coherent progress snapshot.</param>
    /// <param name="diagnostics">Structured failure diagnostics.</param>
    /// <exception cref="ArgumentException">Result evidence contradicts its disposition.</exception>
    public MaterializationRebuildShardResult(
        MaterializationRebuildShardDisposition disposition,
        MaterializationRebuildShardId shard,
        MaterializationGenerationId generation,
        int pages,
        long outputs,
        MaterializationProgressSnapshot progress,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported rebuild shard disposition.");
        MaterializationContract.RequireDefinedIdentity(shard.Value, nameof(shard));
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        if (pages < 0)
            throw new ArgumentOutOfRangeException(nameof(pages), pages, "A completed-page count cannot be negative.");
        if (outputs < 0)
            throw new ArgumentOutOfRangeException(nameof(outputs), outputs, "An output count cannot be negative.");
        Progress = Guard.RequireNotNull(progress);
        if (Progress.Key.Generation != generation)
        {
            throw new ArgumentException(
                "Shard progress must belong to the exact attempt generation.",
                nameof(progress));
        }
        var normalizedDiagnostics = diagnostics.IsDefault ? [] : diagnostics;
        if (normalizedDiagnostics.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Shard diagnostics cannot contain null entries.", nameof(diagnostics));
        var succeeded = disposition == MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired;
        if (succeeded == !normalizedDiagnostics.IsDefaultOrEmpty)
            throw new ArgumentException("Only a failed shard result carries diagnostics.", nameof(diagnostics));
        if (succeeded
            && (Progress.LatestBatchCheckpoint?.Kind != MaterializationCheckpointKind.BatchCompleted
                || Progress.LatestChangeCheckpoint?.Kind != MaterializationCheckpointKind.ChangeProgress))
        {
            throw new ArgumentException(
                "A successful shard result requires exact completed baseline progress and a retained change cut.",
                nameof(progress));
        }
        Disposition = disposition;
        Shard = shard;
        Generation = generation;
        Pages = pages;
        Outputs = outputs;
        Diagnostics = normalizedDiagnostics;
    }

    /// <summary>Observable terminal disposition.</summary>
    public MaterializationRebuildShardDisposition Disposition { get; }

    /// <summary>Stable shard identity.</summary>
    public MaterializationRebuildShardId Shard { get; }

    /// <summary>Attempt-owned candidate generation.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Number of authoritatively checkpointed pages in this invocation.</summary>
    public int Pages { get; }

    /// <summary>Number of successfully applied output rows in this invocation.</summary>
    [JsonConverter(typeof(Cohesive.Model.Serialization.StringEncodedInt64JsonConverter))]
    public long Outputs { get; }

    /// <summary>Latest coherent progress snapshot.</summary>
    public MaterializationProgressSnapshot Progress { get; }

    /// <summary>Structured failure diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }
}

static class MaterializationRebuildProgressSemantics
{
    internal static bool IsExactCompletedBaseline(
        MaterializationRebuildPlan plan,
        MaterializationRebuildShardPlan shard,
        MaterializationGenerationId generation,
        MaterializationProgressSnapshot snapshot)
    {
        var batch = snapshot.LatestBatchCheckpoint;
        return snapshot.Key.Materialization == plan.Materialization.Definition.Id
            && snapshot.Key.DefinitionFingerprint == plan.Materialization.DefinitionFingerprint
            && snapshot.Key.Generation == generation
            && snapshot.Key.Scope == shard.Scope
            && batch is
            {
                Kind: MaterializationCheckpointKind.BatchCompleted,
                Completion: { } completion,
                BatchPageOrdinal: > 0
            }
            && completion.Scope == shard.Scope
            && completion.ReadFingerprint == MaterializationSourceReadFingerprinter.Compute(shard.Read)
            && batch.BatchPageOrdinal <= plan.Limits.MaximumPagesPerShard
            && snapshot.LatestChangeCheckpoint?.Kind == MaterializationCheckpointKind.ChangeProgress;
    }
}

/// <summary>Durable readiness of a complete candidate baseline before incremental catch-up.</summary>
public sealed record MaterializationBaselineCompleteCatchUpRequired
{
    /// <summary>Creates baseline-completion evidence.</summary>
    /// <param name="attempt">Exact owning Process attempt.</param>
    /// <param name="plan">Exact pinned rebuild plan and shard authority.</param>
    /// <param name="generation">Loading candidate generation.</param>
    /// <param name="shards">One completed progress snapshot per persisted shard.</param>
    /// <exception cref="ArgumentNullException"><paramref name="attempt"/> or <paramref name="plan"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The generation is not owned by the attempt, or shard evidence is inexact, incomplete, or lacks its captured
    /// change cut.
    /// </exception>
    internal MaterializationBaselineCompleteCatchUpRequired(
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildPlan plan,
        MaterializationGenerationId generation,
        ImmutableArray<MaterializationProgressSnapshot> shards)
    {
        Attempt = Guard.RequireNotNull(attempt);
        ArgumentNullException.ThrowIfNull(plan);
        MaterializationContract.RequireDefinedIdentity(generation.Value, nameof(generation));
        if (generation != MaterializationRebuildIdentities.Generation(plan, attempt))
            throw new ArgumentException("Baseline readiness must cite the candidate owned by the exact attempt.", nameof(generation));
        var normalized = shards.IsDefault ? [] : shards;
        if (normalized.Length != plan.Shards.Length)
        {
            throw new ArgumentException(
                "Baseline readiness requires exactly one progress snapshot for every persisted shard.",
                nameof(shards));
        }
        for (var index = 0; index < normalized.Length; index++)
        {
            var snapshot = normalized[index];
            var expectedShard = plan.Shards[index];
            if (snapshot is null
                || !MaterializationRebuildProgressSemantics.IsExactCompletedBaseline(
                    plan,
                    expectedShard,
                    generation,
                    snapshot))
            {
                throw new ArgumentException(
                    "Baseline readiness requires exact ordered shard progress, completed batch progress, and a retained Channel cut.",
                    nameof(shards));
            }
        }
        Plan = plan.Fingerprint;
        Generation = generation;
        Shards = normalized;
    }

    /// <summary>Exact owning Process attempt.</summary>
    public MaterializationRebuildAttempt Attempt { get; }

    /// <summary>Exact pinned rebuild-plan fingerprint.</summary>
    public MaterializationRebuildPlanFingerprint Plan { get; }

    /// <summary>Loading candidate generation awaiting catch-up.</summary>
    public MaterializationGenerationId Generation { get; }

    /// <summary>Completed per-shard progress evidence.</summary>
    public ImmutableArray<MaterializationProgressSnapshot> Shards { get; }
}

/// <summary>
/// Storage-owned reference execution engine for candidate allocation, captured change cuts, bounded scan,
/// canonical Relations hydration, idempotent target bulks, and durable baseline checkpoints.
/// </summary>
public sealed class MaterializationRebuildExecutor
{
    static readonly JsonSerializerOptions IdentityJsonOptions = MaterializationJsonSerializer.CreateOptions();

    readonly ResolvedMaterializationRebuildPlan resolved;
    readonly IMaterializationRebuildCrashInjector crashInjector;

    /// <summary>Creates a reference rebuild executor over exact-context runtime bindings.</summary>
    /// <param name="resolved">Exact persisted plan resolved to runtime ports.</param>
    /// <param name="crashInjector">Optional deterministic boundary hook.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolved"/> is <see langword="null"/>.</exception>
    public MaterializationRebuildExecutor(
        ResolvedMaterializationRebuildPlan resolved,
        IMaterializationRebuildCrashInjector? crashInjector = null)
    {
        this.resolved = Guard.RequireNotNull(resolved);
        this.crashInjector = crashInjector ?? NoOpMaterializationRebuildCrashInjector.Instance;
    }

    /// <summary>Exact persisted rebuild realization interpreted by this executor.</summary>
    public MaterializationRebuildPlan Plan => resolved.Plan;

    /// <summary>Idempotently allocates the candidate and captures one initial Channel position per shard.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="attempt">Exact Process attempt owning the candidate.</param>
    /// <returns>Ready or structured rejected initialization evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationRebuildInitializationResult> BeginAttemptAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        context.ThrowIfCancellationRequested();
        var plan = resolved.Plan;
        var generation = MaterializationRebuildIdentities.Generation(plan, attempt);
        var begun = await resolved.Target.BeginGenerationAsync(
                context,
                new MaterializationBeginGenerationRequest(
                    materializationId: plan.Materialization.Definition.Id,
                    generationId: generation,
                    definitionFingerprint: plan.Materialization.DefinitionFingerprint,
                    workerFence: MaterializationWorkerFence.Initial,
                    createdAtUtc: attempt.StartedAtUtc))
            .ConfigureAwait(false);
        if (begun.Disposition is not (MaterializationTargetOperationDisposition.Applied
                or MaterializationTargetOperationDisposition.Replayed)
            || begun.Generation is not { } candidate
            || candidate.GenerationId != generation
            || candidate.MaterializationId != plan.Materialization.Definition.Id
            || candidate.DefinitionFingerprint != plan.Materialization.DefinitionFingerprint
            || candidate.State != MaterializationGenerationState.Loading)
        {
            return new(
                MaterializationRebuildInitializationDisposition.TargetRejected,
                generation,
                begun.Generation,
                diagnostics: [Diagnostic(
                    MaterializationRebuildDiagnosticCodes.RuntimeBindingMismatch,
                    $"Candidate allocation was rejected with '{begun.Disposition}'.",
                    subject: generation.Value)]);
        }

        var progress = ImmutableArray.CreateBuilder<MaterializationProgressSnapshot>(plan.Shards.Length);
        foreach (var shard in plan.Shards)
        {
            var binding = resolved.GetShard(shard.Id);
            var key = ProgressKey(plan, generation, shard);
            var owner = Owner(attempt, shard);
            var current = await resolved.ProgressStore.LoadAsync(context, key).ConfigureAwait(false);
            if (current is null || !string.Equals(current.FenceOwner, owner, StringComparison.Ordinal))
            {
                var acquired = await resolved.ProgressStore.AcquireFenceAsync(
                        context,
                        key,
                        MaterializationRebuildIdentities.Fence(plan, attempt, shard.Id, current?.Revision),
                        current?.Revision,
                        owner)
                    .ConfigureAwait(false);
                if (acquired.Disposition is not (MaterializationProgressMutationDisposition.Applied
                    or MaterializationProgressMutationDisposition.Replayed))
                {
                    return RejectedInitialization(
                        generation,
                        begun.Generation,
                        progress,
                        acquired.Diagnostics,
                        $"Could not acquire progress for shard '{shard.Id.Value}'.");
                }
                current = acquired.Snapshot!;
            }

            if (current.LatestChangeCheckpoint is null)
            {
                var position = await binding.Source.CaptureCurrentPositionAsync(context, shard.Scope)
                    .ConfigureAwait(false);
                var checkpoint = new MaterializationApplicationCheckpoint(
                    id: MaterializationRebuildIdentities.ChangeCut(plan, attempt, shard.Id),
                    kind: MaterializationCheckpointKind.ChangeProgress,
                    continuation: null,
                    completion: null,
                    position: position,
                    appliedDeliveries: [],
                    committedAtUtc: context.UtcNow,
                    evidenceReference: shard.ChangeChannel?.Value,
                    channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(position));
                var saved = await resolved.ProgressStore.SaveCheckpointAsync(
                        context,
                        key,
                        MaterializationRebuildIdentities.ChangeCutMutation(plan, attempt, shard.Id),
                        current.Revision,
                        owner,
                        current.Fence,
                        checkpoint)
                    .ConfigureAwait(false);
                if (saved.Disposition is not (MaterializationProgressMutationDisposition.Applied
                    or MaterializationProgressMutationDisposition.Replayed))
                {
                    return RejectedInitialization(
                        generation,
                        begun.Generation,
                        progress,
                        saved.Diagnostics,
                        $"Could not persist the initial change cut for shard '{shard.Id.Value}'.");
                }
                current = saved.Snapshot!;
            }
            progress.Add(current);
        }

        return new(
            MaterializationRebuildInitializationDisposition.Ready,
            generation,
            begun.Generation,
            progress.MoveToImmutable());
    }

    /// <summary>Runs or resumes one shard through its authoritative baseline completion boundary.</summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="attempt">Exact Process attempt owning the candidate.</param>
    /// <param name="shardId">Stable persisted shard identity.</param>
    /// <returns>Completed, failed, fenced, or boundary-exceeded terminal evidence.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="shardId"/> is absent from the plan.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationRebuildShardResult> RunShardAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildShardId shardId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        context.ThrowIfCancellationRequested();
        var plan = resolved.Plan;
        var binding = resolved.GetShard(shardId);
        var shard = binding.Shard;
        var generation = MaterializationRebuildIdentities.Generation(plan, attempt);
        var key = ProgressKey(plan, generation, shard);
        var owner = Owner(attempt, shard);
        var progress = await resolved.ProgressStore.LoadAsync(context, key).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Rebuild shard '{shard.Id.Value}' was not initialized for generation '{generation.Value}'.");
        if (!string.Equals(progress.FenceOwner, owner, StringComparison.Ordinal)
            || progress.LatestChangeCheckpoint is null)
        {
            return Failure(
                MaterializationRebuildShardDisposition.Fenced,
                shard,
                generation,
                pages: 0,
                outputs: 0,
                progress,
                MaterializationRebuildDiagnosticCodes.ProgressFenced,
                "The shard progress fence or initial Channel cut does not belong to this Process attempt.");
        }
        if (progress.LatestBatchCheckpoint?.Kind == MaterializationCheckpointKind.BatchCompleted)
        {
            return MaterializationRebuildProgressSemantics.IsExactCompletedBaseline(
                plan,
                shard,
                generation,
                progress)
                ? Success(shard, generation, pages: 0, outputs: 0, progress)
                : Failure(
                    MaterializationRebuildShardDisposition.Fenced,
                    shard,
                    generation,
                    pages: 0,
                    outputs: 0,
                    progress,
                    MaterializationRebuildDiagnosticCodes.ProgressFenced,
                    "Retained baseline completion does not match the exact shard read or operating boundary.");
        }

        var pages = 0;
        long outputCount = 0;
        var crashOccurrence = 0;
        while ((progress.LatestBatchCheckpoint?.BatchPageOrdinal ?? 0)
            < plan.Limits.MaximumPagesPerShard)
        {
            var continuation = progress.LatestBatchCheckpoint?.Continuation;
            var pageIdentity = MaterializationRebuildIdentities.Page(plan, attempt, shard, continuation);
            MaterializationSourcePage page;
            try
            {
                page = await binding.Source.ReadPageAsync(
                        context,
                        new MaterializationSourcePageRequest(
                            read: shard.Read,
                            scope: shard.Scope,
                            continuation: continuation,
                            maximumItems: plan.Limits.MaximumPageItems,
                            maximumBytes: plan.Limits.MaximumPageBytes))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Failure(
                    MaterializationRebuildShardDisposition.SourceOrHydrationFailed,
                    shard,
                    generation,
                    pages,
                    outputCount,
                    progress,
                    MaterializationRebuildDiagnosticCodes.HydrationIncomplete,
                    exception.Message);
            }
            if (page.State == MaterializationSourcePageState.Exhausted
                && page.Read.State == RelationQuerySourceReadState.Partial)
            {
                return Failure(
                    MaterializationRebuildShardDisposition.BoundaryExceeded,
                    shard,
                    generation,
                    pages,
                    outputCount,
                    progress,
                    MaterializationRebuildDiagnosticCodes.OperatingBoundaryExceeded,
                    "The source exhausted its continuation before proving authoritative read completeness.",
                    page.Diagnostics);
            }
            if (page.Read.State is RelationQuerySourceReadState.Failed
                    or RelationQuerySourceReadState.Inconclusive
                || page.State == MaterializationSourcePageState.MoreAvailable
                    && page.Read.State != RelationQuerySourceReadState.Partial)
            {
                return Failure(
                    MaterializationRebuildShardDisposition.SourceOrHydrationFailed,
                    shard,
                    generation,
                    pages,
                    outputCount,
                    progress,
                    MaterializationRebuildDiagnosticCodes.HydrationIncomplete,
                    $"The source returned incompatible '{page.Read.State}' evidence for a '{page.State}' page.",
                    page.Diagnostics);
            }
            await ObserveCrashAsync(
                    context,
                    attempt,
                    generation,
                    shard.Id,
                    pageIdentity,
                    MaterializationRebuildCrashPoint.AfterScan,
                    crashOccurrence++)
                .ConfigureAwait(false);

            MaterializationRebuildHydrationResult hydrated;
            try
            {
                hydrated = await binding.Hydrator.HydrateAsync(
                        context,
                        new MaterializationRebuildHydrationRequest(
                            evaluation: new RelationQueryEvaluationId(pageIdentity),
                            shard: shard,
                            page: page))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return Failure(
                    MaterializationRebuildShardDisposition.SourceOrHydrationFailed,
                    shard,
                    generation,
                    pages,
                    outputCount,
                    progress,
                    MaterializationRebuildDiagnosticCodes.HydrationIncomplete,
                    exception.Message);
            }
            await ObserveCrashAsync(
                    context,
                    attempt,
                    generation,
                    shard.Id,
                    pageIdentity,
                    MaterializationRebuildCrashPoint.AfterHydration,
                    crashOccurrence++)
                .ConfigureAwait(false);

            if (!TryProjectMutations(pageIdentity, hydrated.Rows, out var mutations, out var projectionMessage))
            {
                return Failure(
                    MaterializationRebuildShardDisposition.SourceOrHydrationFailed,
                    shard,
                    generation,
                    pages,
                    outputCount,
                    progress,
                    MaterializationRebuildDiagnosticCodes.OutputIdentityMissing,
                    projectionMessage!);
            }

            var write = await ApplyMutationsAsync(
                    context,
                    attempt,
                    shard,
                    generation,
                    pageIdentity,
                    mutations,
                    crashOccurrence)
                .ConfigureAwait(false);
            crashOccurrence = write.NextCrashOccurrence;
            if (write.FailureDisposition is { } failureDisposition)
            {
                return Failure(
                    failureDisposition,
                    shard,
                    generation,
                    pages,
                    outputCount,
                    progress,
                    failureDisposition switch
                    {
                        MaterializationRebuildShardDisposition.BoundaryExceeded =>
                            MaterializationRebuildDiagnosticCodes.OperatingBoundaryExceeded,
                        MaterializationRebuildShardDisposition.RestartRequired =>
                            MaterializationRebuildDiagnosticCodes.SourceReplayDrift,
                        MaterializationRebuildShardDisposition.Fenced =>
                            MaterializationRebuildDiagnosticCodes.ProgressFenced,
                        _ => MaterializationRebuildDiagnosticCodes.TargetMutationFailed
                    },
                    write.Message!);
            }

            var batchPageOrdinal = checked((progress.LatestBatchCheckpoint?.BatchPageOrdinal ?? 0) + 1);
            var checkpoint = page.State switch
            {
                MaterializationSourcePageState.MoreAvailable => new MaterializationApplicationCheckpoint(
                    id: MaterializationRebuildIdentities.BaselineCheckpoint(pageIdentity),
                    kind: MaterializationCheckpointKind.BatchContinuation,
                    continuation: page.Continuation,
                    completion: null,
                    position: null,
                    appliedDeliveries: [],
                    committedAtUtc: context.UtcNow,
                    evidenceReference: hydrated.EvidenceReference,
                    batchPageOrdinal: batchPageOrdinal),
                MaterializationSourcePageState.Exhausted => new MaterializationApplicationCheckpoint(
                    id: MaterializationRebuildIdentities.BaselineCheckpoint(pageIdentity),
                    kind: MaterializationCheckpointKind.BatchCompleted,
                    continuation: null,
                    completion: MaterializationSourceReadCompletion.FromPage(page),
                    position: null,
                    appliedDeliveries: [],
                    committedAtUtc: context.UtcNow,
                    evidenceReference: hydrated.EvidenceReference,
                    batchPageOrdinal: batchPageOrdinal),
                _ => throw new InvalidOperationException($"Unsupported source page state '{page.State}'.")
            };
            var saved = await resolved.ProgressStore.SaveCheckpointAsync(
                    context,
                    key,
                    MaterializationRebuildIdentities.BaselineCheckpointMutation(pageIdentity),
                    progress.Revision,
                    owner,
                    progress.Fence,
                    checkpoint)
                .ConfigureAwait(false);
            if (saved.Disposition is not (MaterializationProgressMutationDisposition.Applied
                or MaterializationProgressMutationDisposition.Replayed))
            {
                return Failure(
                    MaterializationRebuildShardDisposition.Fenced,
                    shard,
                    generation,
                    pages,
                    outputCount,
                    saved.Snapshot ?? progress,
                    MaterializationRebuildDiagnosticCodes.ProgressFenced,
                    $"Baseline checkpoint was rejected with '{saved.Disposition}'.");
            }
            progress = saved.Snapshot!;
            pages++;
            outputCount = checked(outputCount + mutations.Length);
            await ObserveCrashAsync(
                    context,
                    attempt,
                    generation,
                    shard.Id,
                    pageIdentity,
                    MaterializationRebuildCrashPoint.AfterCheckpoint,
                    crashOccurrence++)
                .ConfigureAwait(false);
            if (checkpoint.Kind == MaterializationCheckpointKind.BatchCompleted)
                return Success(shard, generation, pages, outputCount, progress);
        }

        return Failure(
            MaterializationRebuildShardDisposition.BoundaryExceeded,
            shard,
            generation,
            pages,
            outputCount,
            progress,
            MaterializationRebuildDiagnosticCodes.OperatingBoundaryExceeded,
            $"Shard exceeded its finite {plan.Limits.MaximumPagesPerShard}-page boundary.");
    }

    /// <summary>Projects durable baseline-complete/catch-up-required evidence when every shard is complete.</summary>
    /// <param name="context">Explicit cancellation and tracing context.</param>
    /// <param name="attempt">Exact owning Process attempt.</param>
    /// <returns>Typed readiness evidence, or <see langword="null"/> while any shard remains incomplete.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<MaterializationBaselineCompleteCatchUpRequired?> InspectReadinessAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        context.ThrowIfCancellationRequested();
        var plan = resolved.Plan;
        var generation = MaterializationRebuildIdentities.Generation(plan, attempt);
        var generationSnapshot = await resolved.Target.InspectGenerationAsync(context, generation)
            .ConfigureAwait(false);
        if (generationSnapshot?.State != MaterializationGenerationState.Loading
            || generationSnapshot.MaterializationId != plan.Materialization.Definition.Id
            || generationSnapshot.DefinitionFingerprint != plan.Materialization.DefinitionFingerprint)
        {
            return null;
        }
        var snapshots = ImmutableArray.CreateBuilder<MaterializationProgressSnapshot>(plan.Shards.Length);
        foreach (var shard in plan.Shards)
        {
            var snapshot = await resolved.ProgressStore.LoadAsync(
                    context,
                    ProgressKey(plan, generation, shard))
                .ConfigureAwait(false);
            if (snapshot is null
                || !MaterializationRebuildProgressSemantics.IsExactCompletedBaseline(
                    plan,
                    shard,
                    generation,
                    snapshot))
            {
                return null;
            }
            snapshots.Add(snapshot);
        }
        return new(attempt, plan, generation, snapshots.MoveToImmutable());
    }

    /// <summary>
    /// Idempotently abandons an attempt's generation identity, retiring a retained candidate or installing a durable
    /// tombstone before a delayed candidate begin can revive it.
    /// </summary>
    /// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
    /// <param name="attempt">Abandoned Process attempt.</param>
    /// <param name="abandonedAtUtc">Stable UTC restart/abandonment command time retained across retry.</param>
    /// <returns><see langword="true"/> when durable abandonment evidence was applied or replayed.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="abandonedAtUtc"/> is not UTC.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled.</exception>
    public async Task<bool> AbandonAttemptAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        DateTimeOffset abandonedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(attempt);
        MaterializationContract.RequireUtc(abandonedAtUtc, nameof(abandonedAtUtc));
        context.ThrowIfCancellationRequested();
        var plan = resolved.Plan;
        var generation = MaterializationRebuildIdentities.Generation(plan, attempt);
        var abandoned = await resolved.Target.AbandonGenerationAsync(
                context,
                new MaterializationAbandonGenerationRequest(
                    abandonmentId: MaterializationRebuildIdentities.Abandonment(plan, attempt),
                    generationId: generation,
                    abandonedAtUtc: abandonedAtUtc))
            .ConfigureAwait(false);
        return abandoned.Disposition is MaterializationTargetOperationDisposition.Applied
                or MaterializationTargetOperationDisposition.Replayed
            && abandoned.Receipt is { } receipt
            && receipt.AbandonmentId == MaterializationRebuildIdentities.Abandonment(plan, attempt)
            && receipt.GenerationId == generation
            && receipt.AbandonedAtUtc == abandonedAtUtc;
    }

    async Task<(
        MaterializationRebuildShardDisposition? FailureDisposition,
        string? Message,
        int NextCrashOccurrence)> ApplyMutationsAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildShardPlan shard,
        MaterializationGenerationId generation,
        string pageIdentity,
        ImmutableArray<MaterializationItemMutation> mutations,
        int crashOccurrence)
    {
        if (mutations.IsDefaultOrEmpty)
            return (null, null, crashOccurrence);
        var offset = 0;
        var chunkIndex = 0;
        var maximumAttempts = resolved.Plan.Materialization.Definition.FailurePolicy.MaximumAttempts;
        while (offset < mutations.Length)
        {
            var lowerCount = 1;
            var upperCount = Math.Min(resolved.Plan.Limits.MaximumBulkItems, mutations.Length - offset);
            ImmutableArray<MaterializationItemMutation>? selected = null;
            while (lowerCount <= upperCount)
            {
                var count = lowerCount + ((upperCount - lowerCount) / 2);
                var candidate = mutations.Slice(offset, count);
                var request = new MaterializationApplyBatchRequest(
                    batchId: MaterializationRebuildIdentities.Batch(pageIdentity, chunkIndex, retry: 0),
                    generationId: generation,
                    workerFence: MaterializationWorkerFence.Initial,
                    mutations: candidate);
                if (MaterializationTargetIntentFingerprinter.TryAnalyzeBatch(
                        request,
                        resolved.Plan.Limits.MaximumBulkBytes,
                        out _,
                        out _))
                {
                    selected = candidate;
                    lowerCount = count + 1;
                }
                else
                {
                    upperCount = count - 1;
                }
            }
            if (selected is null)
            {
                return (
                    MaterializationRebuildShardDisposition.BoundaryExceeded,
                    $"One output mutation cannot fit the {resolved.Plan.Limits.MaximumBulkBytes}-byte target bound.",
                    crashOccurrence);
            }
            var chunk = selected.Value;
            var pending = chunk;
            for (var retry = 0; retry < maximumAttempts; retry++)
            {
                var request = new MaterializationApplyBatchRequest(
                    batchId: MaterializationRebuildIdentities.Batch(pageIdentity, chunkIndex, retry),
                    generationId: generation,
                    workerFence: MaterializationWorkerFence.Initial,
                    mutations: pending);
                var result = await resolved.Target.ApplyBatchAsync(context, request).ConfigureAwait(false);
                try
                {
                    result.ValidateAgainst(request);
                }
                catch (ArgumentException exception)
                {
                    return (
                        MaterializationRebuildShardDisposition.TargetFailed,
                        $"The target returned inexact per-item evidence: {exception.Message}",
                        crashOccurrence);
                }
                await ObserveCrashAsync(
                        context,
                        attempt,
                        generation,
                        shard.Id,
                        pageIdentity,
                        MaterializationRebuildCrashPoint.AfterBulk,
                        crashOccurrence++)
                    .ConfigureAwait(false);
                if (result.Disposition == MaterializationBatchDisposition.IdentityConflict)
                {
                    return (
                        MaterializationRebuildShardDisposition.RestartRequired,
                        $"The target found different canonical content for replayed page '{pageIdentity}' "
                        + $"and batch '{request.BatchId.Value}'; continuing this Process attempt is unsafe.",
                        crashOccurrence);
                }
                if (result.Disposition == MaterializationBatchDisposition.StaleFence)
                {
                    return (
                        MaterializationRebuildShardDisposition.Fenced,
                        "The target rejected a stale generation worker fence.",
                        crashOccurrence);
                }

                HashSet<MaterializationItemMutationId>? retryable = null;
                StringBuilder? permanentFailures = null;
                foreach (var outcome in result.Outcomes)
                {
                    if (outcome.Disposition is MaterializationItemOutcomeDisposition.Applied
                        or MaterializationItemOutcomeDisposition.Replayed)
                    {
                        continue;
                    }
                    if (outcome.Disposition == MaterializationItemOutcomeDisposition.RetryableRejected)
                    {
                        retryable ??= new(result.Outcomes.Length);
                        retryable.Add(outcome.MutationId);
                        continue;
                    }

                    permanentFailures ??= new();
                    if (permanentFailures.Length > 0)
                        permanentFailures.Append(' ');
                    permanentFailures.Append(outcome.Code).Append(": ").Append(outcome.Message);
                }

                if (retryable is null && permanentFailures is null)
                    break;
                if (permanentFailures is not null)
                {
                    return (
                        MaterializationRebuildShardDisposition.TargetFailed,
                        permanentFailures.ToString(),
                        crashOccurrence);
                }
                if (retry + 1 >= maximumAttempts)
                {
                    return (
                        MaterializationRebuildShardDisposition.TargetFailed,
                        "The target retry budget was exhausted for one or more output mutations.",
                        crashOccurrence);
                }
                var retryBuilder = ImmutableArray.CreateBuilder<MaterializationItemMutation>(retryable!.Count);
                foreach (var mutation in pending)
                {
                    if (retryable.Contains(mutation.MutationId))
                        retryBuilder.Add(mutation);
                }
                pending = retryBuilder.MoveToImmutable();
            }
            offset += chunk.Length;
            chunkIndex++;
        }
        return (null, null, crashOccurrence);
    }

    static bool TryProjectMutations(
        string pageIdentity,
        ImmutableArray<RelationQueryOutputRow> rows,
        out ImmutableArray<MaterializationItemMutation> mutations,
        out string? message)
    {
        if (rows.IsDefaultOrEmpty)
        {
            mutations = [];
            message = null;
            return true;
        }
        var builder = ImmutableArray.CreateBuilder<MaterializationItemMutation>(rows.Length);
        HashSet<MaterializationItemId> items = new(rows.Length);
        foreach (var row in rows)
        {
            if (row.Identity is not { } identity
                || identity.Kind is ObservationValueKind.Undefined
                    or ObservationValueKind.Null
                    or ObservationValueKind.Array
                    or ObservationValueKind.Object)
            {
                mutations = [];
                message = "Every materialized Relations row requires one concrete scalar identity.";
                return false;
            }
            var encodedIdentity = EncodeIdentity(identity);
            var item = new MaterializationItemId(encodedIdentity);
            if (!items.Add(item))
            {
                mutations = [];
                message = $"Materialized output identity '{encodedIdentity}' occurred more than once in one page.";
                return false;
            }
            builder.Add(new MaterializationUpsert(
                itemId: item,
                mutationId: MaterializationRebuildIdentities.Mutation(pageIdentity, encodedIdentity),
                version: MaterializationRebuildIdentities.BaselineItemVersion,
                value: row.Value));
        }
        mutations = builder.MoveToImmutable();
        message = null;
        return true;
    }

    static string EncodeIdentity(ObservationValue identity)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new IdentityContent(identity),
            IdentityJsonOptions);
        return $"materialization-rebuild-item/v1/{Convert.ToHexStringLower(SHA256.HashData(canonical))}";
    }

    sealed record IdentityContent(ObservationValue Value);

    async ValueTask ObserveCrashAsync(
        OperationContext context,
        MaterializationRebuildAttempt attempt,
        MaterializationGenerationId generation,
        MaterializationRebuildShardId shard,
        string pageIdentity,
        MaterializationRebuildCrashPoint point,
        int occurrence) =>
        await crashInjector.ObserveAsync(
                context,
                new(attempt, generation, shard, pageIdentity, point, occurrence))
            .ConfigureAwait(false);

    static MaterializationProgressKey ProgressKey(
        MaterializationRebuildPlan plan,
        MaterializationGenerationId generation,
        MaterializationRebuildShardPlan shard) =>
        new(
            materialization: plan.Materialization.Definition.Id,
            definitionFingerprint: plan.Materialization.DefinitionFingerprint,
            generation: generation,
            scope: shard.Scope);

    static string Owner(MaterializationRebuildAttempt attempt, MaterializationRebuildShardPlan shard) =>
        $"{attempt.Continuation.ProcessInstanceId.Value}/{attempt.Continuation.ProcessAttemptId.Value}/{shard.Id.Value}";

    MaterializationRebuildInitializationResult RejectedInitialization(
        MaterializationGenerationId generation,
        MaterializationGenerationSnapshot? generationSnapshot,
        ImmutableArray<MaterializationProgressSnapshot>.Builder progress,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics,
        string message)
    {
        var normalized = diagnostics.IsDefaultOrEmpty
            ? [Diagnostic(
                MaterializationRebuildDiagnosticCodes.ProgressFenced,
                message,
                subject: generation.Value)]
            : diagnostics;
        return new(
            MaterializationRebuildInitializationDisposition.ProgressRejected,
            generation,
            generationSnapshot,
            progress.ToImmutable(),
            normalized);
    }

    static MaterializationRebuildShardResult Success(
        MaterializationRebuildShardPlan shard,
        MaterializationGenerationId generation,
        int pages,
        long outputs,
        MaterializationProgressSnapshot progress) =>
        new(
            MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
            shard.Id,
            generation,
            pages,
            outputs,
            progress);

    MaterializationRebuildShardResult Failure(
        MaterializationRebuildShardDisposition disposition,
        MaterializationRebuildShardPlan shard,
        MaterializationGenerationId generation,
        int pages,
        long outputs,
        MaterializationProgressSnapshot progress,
        string code,
        string message) =>
        new(
            disposition,
            shard.Id,
            generation,
            pages,
            outputs,
            progress,
            [Diagnostic(code, message, subject: $"{generation.Value}/{shard.Id.Value}")]);

    MaterializationRebuildShardResult Failure(
        MaterializationRebuildShardDisposition disposition,
        MaterializationRebuildShardPlan shard,
        MaterializationGenerationId generation,
        int pages,
        long outputs,
        MaterializationProgressSnapshot progress,
        string code,
        string message,
        ImmutableArray<DocumentValidationDiagnostic> sourceDiagnostics)
    {
        var normalized = sourceDiagnostics.IsDefault ? [] : sourceDiagnostics;
        var diagnostics = ImmutableArray.CreateBuilder<DocumentValidationDiagnostic>(normalized.Length + 1);
        diagnostics.Add(Diagnostic(code, message, subject: $"{generation.Value}/{shard.Id.Value}"));
        diagnostics.AddRange(normalized);
        return new(
            disposition,
            shard.Id,
            generation,
            pages,
            outputs,
            progress,
            diagnostics.MoveToImmutable());
    }

    DocumentValidationDiagnostic Diagnostic(string code, string message, string subject) =>
        MaterializationContract.CreateDiagnostic(
            code,
            DiagnosticSeverity.Error,
            message,
            "/rebuild",
            "materialization-rebuild-reference-executor",
            subject,
            [resolved.Plan.Provenance.Source.Reference],
            "operation preserves exact pinned rebuild semantics",
            "operation was rejected");
}
