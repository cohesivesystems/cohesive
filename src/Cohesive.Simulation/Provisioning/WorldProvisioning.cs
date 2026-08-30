using System.Collections.Immutable;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Provisioning;

/// <summary>Stable identity of one exact world-provisioning run.</summary>
public readonly record struct WorldProvisioningRunId
{
    /// <summary>Creates a world-provisioning run identity.</summary>
    /// <param name="value">Non-empty deterministic identity value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    public WorldProvisioningRunId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Gets the deterministic identity value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    internal static void Validate(WorldProvisioningRunId id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A world-provisioning run identity is required.", parameterName);
    }
}

/// <summary>Stable identity of one exact world-provisioning batch.</summary>
public readonly record struct WorldProvisioningBatchId
{
    /// <summary>Creates a world-provisioning batch identity.</summary>
    /// <param name="value">Non-empty deterministic identity value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    public WorldProvisioningBatchId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Gets the deterministic identity value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    internal static void Validate(WorldProvisioningBatchId id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A world-provisioning batch identity is required.", parameterName);
    }
}

/// <summary>Deterministic convention for provisioning run and batch identities.</summary>
/// <remarks>
/// A run identity names one exact compiled world, root seed, batching policy, reference interpreter, and logical sink
/// target. Batch identities additionally name a population and contiguous sequence range. The generated observations
/// need not be hashed again because those coordinates completely determine them under the versioned interpreter
/// contract.
/// </remarks>
public static class WorldProvisioningIdentityConvention
{
    /// <summary>Stable identity of the current provisioning identity convention.</summary>
    public const string Identity = "cohesive-simulation-world-provisioning/v1";

    /// <summary>Derives the run identity for one exact world, seed, batching policy, and logical sink target.</summary>
    /// <param name="world">Exact compiled world to provision.</param>
    /// <param name="rootSeed">Deterministic root seed shared by every population.</param>
    /// <param name="targetId">Stable logical identity of the sink target.</param>
    /// <param name="batchSize">Positive maximum number of observations delivered in one sink call.</param>
    /// <returns>A deterministic content-addressed run identity.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="targetId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="targetId"/> is empty or white-space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is not positive.</exception>
    public static WorldProvisioningRunId CreateRunId(
        CompiledWorldPlan world,
        long rootSeed,
        string targetId,
        int batchSize = WorldProvisioningOptions.DefaultBatchSize)
    {
        ArgumentNullException.ThrowIfNull(world);
        targetId = Guard.RequireNotNullOrWhiteSpace(targetId);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Provisioning batch size must be positive.");
        using SimulationFingerprintWriter writer = new();
        writer.Append(Identity);
        writer.Append(world.Definition.Id);
        writer.Append(world.Definition.Revision);
        writer.Append(world.FingerprintAlgorithm);
        writer.Append(world.FingerprintCanonicalization);
        writer.Append(world.Fingerprint);
        writer.Append(rootSeed);
        writer.Append(batchSize);
        writer.Append(ReferenceGenerationInterpreter.Identity);
        writer.Append(ReferenceGenerationInterpreter.EntropyAlgorithm);
        writer.Append(targetId);
        return new($"csimrun1_{writer.Complete()}");
    }

    /// <summary>Derives the identity of one contiguous population batch.</summary>
    /// <param name="runId">Exact owning provisioning run.</param>
    /// <param name="populationId">Stable population identity.</param>
    /// <param name="scope">Exact generation scope assigned to the population.</param>
    /// <param name="batchOrdinal">Zero-based batch ordinal within the population.</param>
    /// <param name="startSequenceIndex">Zero-based sequence index of the first item.</param>
    /// <param name="itemCount">Number of contiguous generated items in the batch.</param>
    /// <returns>A deterministic content-addressed batch identity.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="runId"/> or <paramref name="scope"/> is default, or <paramref name="populationId"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="batchOrdinal"/> or <paramref name="startSequenceIndex"/> is negative, or
    /// <paramref name="itemCount"/> is not positive.
    /// </exception>
    public static WorldProvisioningBatchId CreateBatchId(
        WorldProvisioningRunId runId,
        string populationId,
        GenerationScope scope,
        int batchOrdinal,
        long startSequenceIndex,
        int itemCount)
    {
        WorldProvisioningRunId.Validate(runId, nameof(runId));
        populationId = Guard.RequireNotNullOrWhiteSpace(populationId);
        GenerationScope.Validate(scope, nameof(scope));
        if (batchOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(batchOrdinal), batchOrdinal, "Batch ordinal cannot be negative.");
        if (startSequenceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startSequenceIndex),
                startSequenceIndex,
                "Batch start sequence index cannot be negative.");
        }
        if (itemCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(itemCount), itemCount, "A provisioning batch must contain an item.");

        using SimulationFingerprintWriter writer = new();
        writer.Append(Identity);
        writer.Append(runId.Value);
        writer.Append(populationId);
        writer.Append(scope.Value);
        writer.Append(batchOrdinal);
        writer.Append(startSequenceIndex);
        writer.Append(itemCount);
        return new($"csimbatch1_{writer.Complete()}");
    }
}

/// <summary>Controls bounded world provisioning.</summary>
public sealed record WorldProvisioningOptions
{
    /// <summary>Default maximum number of observations delivered in one sink call.</summary>
    public const int DefaultBatchSize = 100;

    /// <summary>Creates world-provisioning options.</summary>
    /// <param name="batchSize">Positive maximum number of observations delivered in one sink call.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is not positive.</exception>
    public WorldProvisioningOptions(int batchSize = DefaultBatchSize)
    {
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize), batchSize, "Provisioning batch size must be positive.");
        BatchSize = batchSize;
    }

    /// <summary>Gets the maximum number of observations delivered in one sink call.</summary>
    public int BatchSize { get; }
}

/// <summary>One deterministic contiguous batch delivered to a provisioning sink.</summary>
public sealed class WorldProvisioningBatch
{
    internal WorldProvisioningBatch(
        WorldProvisioningBatchId id,
        WorldProvisioningRunId runId,
        string targetId,
        CompiledWorldPlan world,
        CompiledWorldPopulation population,
        long rootSeed,
        int ordinal,
        long startSequenceIndex,
        ImmutableArray<GeneratedObservation> items)
    {
        Id = id;
        RunId = runId;
        TargetId = targetId;
        WorldId = world.Definition.Id;
        WorldRevision = world.Definition.Revision;
        WorldFingerprint = world.Fingerprint;
        WorldFingerprintAlgorithm = world.FingerprintAlgorithm;
        WorldFingerprintCanonicalization = world.FingerprintCanonicalization;
        PopulationId = population.Definition.Id;
        PopulationCount = population.Definition.Count;
        PopulationScope = population.Scope;
        RootSeed = rootSeed;
        Ordinal = ordinal;
        StartSequenceIndex = startSequenceIndex;
        Items = items;
    }

    /// <summary>Gets the deterministic batch identity.</summary>
    public WorldProvisioningBatchId Id { get; }

    /// <summary>Gets the deterministic owning run identity.</summary>
    public WorldProvisioningRunId RunId { get; }

    /// <summary>Gets the stable logical sink target identity.</summary>
    public string TargetId { get; }

    /// <summary>Gets the stable logical world identity.</summary>
    public string WorldId { get; }

    /// <summary>Gets the exact authored world revision.</summary>
    public string WorldRevision { get; }

    /// <summary>Gets the exact compiled world fingerprint.</summary>
    public string WorldFingerprint { get; }

    /// <summary>Gets the world fingerprint algorithm identity.</summary>
    public string WorldFingerprintAlgorithm { get; }

    /// <summary>Gets the world fingerprint canonicalization profile.</summary>
    public string WorldFingerprintCanonicalization { get; }

    /// <summary>Gets the stable population identity.</summary>
    public string PopulationId { get; }

    /// <summary>Gets the total declared number of items in the population.</summary>
    public int PopulationCount { get; }

    /// <summary>Gets the exact isolated generation scope for the population.</summary>
    public GenerationScope PopulationScope { get; }

    /// <summary>Gets the deterministic root seed.</summary>
    public long RootSeed { get; }

    /// <summary>Gets the zero-based batch ordinal within the population.</summary>
    public int Ordinal { get; }

    /// <summary>Gets the zero-based sequence index of the first item.</summary>
    public long StartSequenceIndex { get; }

    /// <summary>Gets generated observations in ascending contiguous sequence-index order.</summary>
    public ImmutableArray<GeneratedObservation> Items { get; }
}

/// <summary>Outcome asserted by a sink for one complete provisioning batch.</summary>
public enum WorldProvisioningBatchDisposition
{
    /// <summary>The complete batch was committed under its deterministic identity.</summary>
    Committed,

    /// <summary>The sink had already committed the same complete deterministic batch.</summary>
    AlreadyCommitted,

    /// <summary>The sink rejected the complete batch without acknowledging it as committed.</summary>
    Rejected
}

/// <summary>Sink acknowledgement for one complete deterministic provisioning batch.</summary>
public sealed record WorldProvisioningBatchReceipt
{
    /// <summary>Creates a sink acknowledgement.</summary>
    /// <param name="batchId">Exact batch identity being acknowledged.</param>
    /// <param name="disposition">Complete-batch outcome asserted by the sink.</param>
    /// <param name="detail">Optional diagnostic detail; required when the batch is rejected.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="batchId"/> is default or a rejected receipt has no diagnostic detail.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unknown.</exception>
    public WorldProvisioningBatchReceipt(
        WorldProvisioningBatchId batchId,
        WorldProvisioningBatchDisposition disposition,
        string? detail = null)
    {
        WorldProvisioningBatchId.Validate(batchId, nameof(batchId));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unknown provisioning disposition.");
        if (disposition == WorldProvisioningBatchDisposition.Rejected && string.IsNullOrWhiteSpace(detail))
            throw new ArgumentException("A rejected provisioning batch requires diagnostic detail.", nameof(detail));

        BatchId = batchId;
        Disposition = disposition;
        Detail = detail;
    }

    /// <summary>Gets the exact acknowledged batch identity.</summary>
    public WorldProvisioningBatchId BatchId { get; }

    /// <summary>Gets the complete-batch outcome asserted by the sink.</summary>
    public WorldProvisioningBatchDisposition Disposition { get; }

    /// <summary>Gets optional sink-provided diagnostic detail.</summary>
    public string? Detail { get; }
}

/// <summary>Provider-neutral destination for deterministic world-provisioning batches.</summary>
/// <remarks>
/// Calls are sequential and ordered by population identity, then sequence index. A successful receipt must name the
/// supplied batch and asserts an outcome for the complete batch. A sink exception leaves the outcome unknown; the
/// reference provisioner preserves that exception and never retries automatically. Durable adapters should use
/// <see cref="WorldProvisioningBatch.Id"/> as an idempotency key and return
/// <see cref="WorldProvisioningBatchDisposition.AlreadyCommitted"/> after verifying a prior exact commit.
/// </remarks>
public interface IWorldProvisioningSink
{
    /// <summary>Gets the stable logical identity of the destination participating in run identity.</summary>
    string TargetId { get; }

    /// <summary>Attempts to commit one complete deterministic batch.</summary>
    /// <param name="batch">Contiguous generated population batch.</param>
    /// <param name="cancellationToken">Token requesting cancellation before acknowledgement.</param>
    /// <returns>An acknowledgement naming the exact supplied batch.</returns>
    ValueTask<WorldProvisioningBatchReceipt> CommitAsync(
        WorldProvisioningBatch batch,
        CancellationToken cancellationToken = default);
}

/// <summary>Completion summary for one provisioned population.</summary>
public sealed record WorldProvisionedPopulation
{
    internal WorldProvisionedPopulation(
        string populationId,
        int itemCount,
        int batchCount,
        int alreadyCommittedBatchCount)
    {
        PopulationId = populationId;
        ItemCount = itemCount;
        BatchCount = batchCount;
        AlreadyCommittedBatchCount = alreadyCommittedBatchCount;
    }

    /// <summary>Gets the stable population identity.</summary>
    public string PopulationId { get; }

    /// <summary>Gets the number of generated items acknowledged as committed.</summary>
    public int ItemCount { get; }

    /// <summary>Gets the number of acknowledged batches.</summary>
    public int BatchCount { get; }

    /// <summary>Gets how many batches were acknowledged as already committed.</summary>
    public int AlreadyCommittedBatchCount { get; }
}

/// <summary>Deterministic completion evidence for a fully provisioned world.</summary>
public sealed class WorldProvisioningResult
{
    internal WorldProvisioningResult(
        WorldProvisioningRunId runId,
        string targetId,
        CompiledWorldPlan world,
        long rootSeed,
        int batchSize,
        ImmutableArray<WorldProvisionedPopulation> populations)
    {
        RunId = runId;
        TargetId = targetId;
        WorldId = world.Definition.Id;
        WorldRevision = world.Definition.Revision;
        WorldFingerprint = world.Fingerprint;
        WorldFingerprintAlgorithm = world.FingerprintAlgorithm;
        WorldFingerprintCanonicalization = world.FingerprintCanonicalization;
        RootSeed = rootSeed;
        BatchSize = batchSize;
        Populations = populations;
    }

    /// <summary>Gets the deterministic run identity.</summary>
    public WorldProvisioningRunId RunId { get; }

    /// <summary>Gets the stable logical sink target identity.</summary>
    public string TargetId { get; }

    /// <summary>Gets the stable logical world identity.</summary>
    public string WorldId { get; }

    /// <summary>Gets the exact authored world revision.</summary>
    public string WorldRevision { get; }

    /// <summary>Gets the exact compiled world fingerprint.</summary>
    public string WorldFingerprint { get; }

    /// <summary>Gets the world fingerprint algorithm identity.</summary>
    public string WorldFingerprintAlgorithm { get; }

    /// <summary>Gets the world fingerprint canonicalization profile.</summary>
    public string WorldFingerprintCanonicalization { get; }

    /// <summary>Gets the deterministic root seed.</summary>
    public long RootSeed { get; }

    /// <summary>Gets the maximum number of observations per delivered batch.</summary>
    public int BatchSize { get; }

    /// <summary>Gets completion summaries in stable population identity order.</summary>
    public ImmutableArray<WorldProvisionedPopulation> Populations { get; }

    /// <summary>Gets the total number of generated items acknowledged as committed.</summary>
    public long ItemCount => Populations.Sum(static population => (long)population.ItemCount);

    /// <summary>Gets the total number of acknowledged batches.</summary>
    public long BatchCount => Populations.Sum(static population => (long)population.BatchCount);

    /// <summary>Gets the number of batches acknowledged as already committed.</summary>
    public long AlreadyCommittedBatchCount =>
        Populations.Sum(static population => (long)population.AlreadyCommittedBatchCount);
}

/// <summary>Failure raised when a sink explicitly rejects a deterministic provisioning batch.</summary>
public sealed class WorldProvisioningRejectedException : InvalidOperationException
{
    /// <summary>Creates an explicit provisioning rejection failure.</summary>
    /// <param name="batch">Exact rejected batch.</param>
    /// <param name="receipt">Sink rejection receipt.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="batch"/> or <paramref name="receipt"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The receipt is not a rejection or names another batch.
    /// </exception>
    public WorldProvisioningRejectedException(
        WorldProvisioningBatch batch,
        WorldProvisioningBatchReceipt receipt)
        : base(CreateMessage(batch, receipt))
    {
        Batch = batch;
        Receipt = receipt;
    }

    /// <summary>Gets the exact rejected batch.</summary>
    public WorldProvisioningBatch Batch { get; }

    /// <summary>Gets the sink rejection receipt.</summary>
    public WorldProvisioningBatchReceipt Receipt { get; }

    static string CreateMessage(
        WorldProvisioningBatch batch,
        WorldProvisioningBatchReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.BatchId != batch.Id)
            throw new ArgumentException("A rejection receipt must name the rejected batch.", nameof(receipt));
        if (receipt.Disposition != WorldProvisioningBatchDisposition.Rejected)
            throw new ArgumentException("A provisioning rejection exception requires a rejected receipt.", nameof(receipt));
        return $"Provisioning sink rejected batch '{batch.Id.Value}': {receipt.Detail}";
    }
}

/// <summary>Reference executor for bounded deterministic world provisioning.</summary>
public static class WorldProvisioner
{
    /// <summary>Provisions every world population through a provider-neutral sink.</summary>
    /// <param name="world">Exact compiled world to generate.</param>
    /// <param name="rootSeed">Deterministic root seed shared by every population.</param>
    /// <param name="sink">Destination receiving sequential deterministic batches.</param>
    /// <param name="options">Optional batching policy; defaults to <see cref="WorldProvisioningOptions.DefaultBatchSize"/>.</param>
    /// <param name="cancellationToken">Token requesting cancellation before generation or sink acknowledgement.</param>
    /// <returns>Completion evidence after every batch has been acknowledged as committed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> or <paramref name="sink"/> is null.</exception>
    /// <exception cref="ArgumentException">The sink target identity is empty.</exception>
    /// <exception cref="InvalidOperationException">The sink returns no receipt or a receipt naming another batch.</exception>
    /// <exception cref="WorldProvisioningRejectedException">The sink explicitly rejects a batch.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    /// <remarks>
    /// Populations are visited in compiled identity order and each population is visited by ascending sequence index.
    /// At most one configured batch of generated observations is retained by this method. Sink exceptions are not
    /// wrapped because they may describe an unknown commit outcome; no automatic retry is attempted.
    /// </remarks>
    public static Task<WorldProvisioningResult> ProvisionAsync(
        CompiledWorldPlan world,
        long rootSeed,
        IWorldProvisioningSink sink,
        WorldProvisioningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(sink);
        var targetId = Guard.RequireNotNullOrWhiteSpace(sink.TargetId);
        options ??= new();
        return ProvisionCoreAsync(world, rootSeed, sink, targetId, options, cancellationToken);
    }

    static async Task<WorldProvisioningResult> ProvisionCoreAsync(
        CompiledWorldPlan world,
        long rootSeed,
        IWorldProvisioningSink sink,
        string targetId,
        WorldProvisioningOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runId = WorldProvisioningIdentityConvention.CreateRunId(
            world,
            rootSeed,
            targetId,
            options.BatchSize);
        var populationResults = ImmutableArray.CreateBuilder<WorldProvisionedPopulation>(world.Populations.Length);

        foreach (var population in world.Populations)
        {
            var batchCount = 0;
            var alreadyCommittedCount = 0;
            for (long start = 0; start < population.Definition.Count; start += options.BatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var itemCount = (int)Math.Min(options.BatchSize, population.Definition.Count - start);
                var items = ImmutableArray.CreateBuilder<GeneratedObservation>(itemCount);
                for (var offset = 0; offset < itemCount; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    items.Add(ReferenceGenerationInterpreter.Generate(
                        population.GenerationPlan,
                        rootSeed,
                        population.Scope,
                        sequenceIndex: (long)start + offset));
                }

                var batchId = WorldProvisioningIdentityConvention.CreateBatchId(
                    runId,
                    population.Definition.Id,
                    population.Scope,
                    batchOrdinal: batchCount,
                    startSequenceIndex: start,
                    itemCount: itemCount);
                var batch = new WorldProvisioningBatch(
                    batchId,
                    runId,
                    targetId,
                    world,
                    population,
                    rootSeed,
                    ordinal: batchCount,
                    startSequenceIndex: start,
                    items.MoveToImmutable());
                var receipt = await sink.CommitAsync(batch, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Provisioning sink '{targetId}' returned no receipt for batch '{batch.Id.Value}'.");
                if (receipt.BatchId != batch.Id)
                {
                    throw new InvalidOperationException(
                        $"Provisioning sink '{targetId}' acknowledged batch '{receipt.BatchId.Value}' "
                        + $"while batch '{batch.Id.Value}' was supplied.");
                }
                if (receipt.Disposition == WorldProvisioningBatchDisposition.Rejected)
                    throw new WorldProvisioningRejectedException(batch, receipt);
                if (receipt.Disposition == WorldProvisioningBatchDisposition.AlreadyCommitted)
                    alreadyCommittedCount++;
                batchCount++;
            }

            populationResults.Add(new(
                population.Definition.Id,
                population.Definition.Count,
                batchCount,
                alreadyCommittedCount));
        }

        return new(
            runId,
            targetId,
            world,
            rootSeed,
            options.BatchSize,
            populationResults.MoveToImmutable());
    }
}
