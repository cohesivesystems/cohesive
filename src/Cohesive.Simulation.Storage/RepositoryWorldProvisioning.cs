using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Storage;
using Cohesive.Storage.Seeding;
using Cohesive.Transitions.Model;

namespace Cohesive.Simulation.Storage;

/// <summary>Source of stable entity identity for one generated world population.</summary>
public enum WorldEntityIdentitySource
{
    /// <summary>Derive stable entity slots from the population scope and generated sequence index.</summary>
    PopulationSequence,

    /// <summary>Read the entity identity from a scalar field asserted unique across the complete population.</summary>
    UniqueObservationField
}

/// <summary>Deterministic entity identity policy for generated observations.</summary>
public sealed class WorldEntityIdentityPolicy
{
    WorldEntityIdentityPolicy(
        WorldEntityIdentitySource source,
        FieldPath? observationField)
    {
        Source = source;
        ObservationField = observationField;
    }

    /// <summary>Gets the conventional stable population-sequence identity policy.</summary>
    public static WorldEntityIdentityPolicy PopulationSequence { get; } = new(
        WorldEntityIdentitySource.PopulationSequence,
        observationField: null);

    /// <summary>Gets the configured source of entity identity.</summary>
    public WorldEntityIdentitySource Source { get; }

    /// <summary>Gets the observation field path when <see cref="Source"/> is <see cref="WorldEntityIdentitySource.UniqueObservationField"/>.</summary>
    public FieldPath? ObservationField { get; }

    /// <summary>Creates a policy asserting that one scalar observation field is unique across the population.</summary>
    /// <param name="path">Non-empty field-only path to the population-unique identity value.</param>
    /// <returns>An immutable unique-observation-field identity policy.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is default or contains collection navigation.</exception>
    public static WorldEntityIdentityPolicy FromUniqueObservationField(FieldPath path)
    {
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("An entity identity field path is required.", nameof(path));
        if (path.Segments.Any(static segment => segment.Kind == SegmentKind.Element))
            throw new ArgumentException("An entity identity field path cannot contain collection navigation.", nameof(path));
        return new(WorldEntityIdentitySource.UniqueObservationField, path);
    }

    /// <summary>Creates a policy asserting that one top-level scalar field is unique across the population.</summary>
    /// <param name="fieldIdentity">Canonical population-unique top-level field identity.</param>
    /// <returns>An immutable unique-observation-field identity policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty or white-space.</exception>
    public static WorldEntityIdentityPolicy FromUniqueObservationField(string fieldIdentity) =>
        FromUniqueObservationField(FieldPath.FromField(fieldIdentity));

    internal bool TryResolve(
        WorldProvisioningBatch batch,
        GeneratedObservation generated,
        out EntityId entityId,
        out string? error)
    {
        switch (Source)
        {
            case WorldEntityIdentitySource.PopulationSequence:
                entityId = WorldEntitySequenceIdentityConvention.Create(
                    batch.PopulationScope,
                    generated.Replay.SequenceIndex);
                error = null;
                return true;
            case WorldEntityIdentitySource.UniqueObservationField:
                return TryResolveField(generated, out entityId, out error);
            default:
                entityId = default;
                error = $"Unknown world entity identity source '{Source}'.";
                return false;
        }
    }

    bool TryResolveField(
        GeneratedObservation generated,
        out EntityId entityId,
        out string? error)
    {
        var path = ObservationField!.Value;
        if (!generated.Observation.TryGetField(path, out var value))
        {
            entityId = default;
            error = $"Generated observation at sequence index '{generated.Replay.SequenceIndex}' "
                + $"does not contain entity identity path '{path}'.";
            return false;
        }

        var text = value.Kind switch
        {
            ObservationValueKind.String
                or ObservationValueKind.DateTimeOffset
                or ObservationValueKind.DateOnly
                or ObservationValueKind.TimeOnly
                or ObservationValueKind.TimeSpan => value.GetString(),
            ObservationValueKind.Int64 => value.GetInt64().ToString(CultureInfo.InvariantCulture),
            ObservationValueKind.Decimal => value.GetDecimal().ToString(CultureInfo.InvariantCulture),
            ObservationValueKind.Double when double.IsFinite(value.GetDouble()) =>
                value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            ObservationValueKind.Bool => value.GetBoolean() ? "true" : "false",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            entityId = default;
            error = $"Generated observation at sequence index '{generated.Replay.SequenceIndex}' has entity identity "
                + $"path '{path}' with unsupported or empty value kind '{value.Kind}'.";
            return false;
        }

        entityId = new(text);
        error = null;
        return true;
    }
}

/// <summary>Stable convention for assigning entity slots to world population sequence positions.</summary>
public static class WorldEntitySequenceIdentityConvention
{
    /// <summary>Stable identity of the current population-sequence entity identity convention.</summary>
    public const string Identity = "cohesive-simulation-storage-entity-sequence/v1";

    /// <summary>Derives a stable entity identity from an exact population scope and sequence index.</summary>
    /// <param name="populationScope">Exact isolated world-population scope.</param>
    /// <param name="sequenceIndex">Non-negative generated sequence index.</param>
    /// <returns>An entity identity stable across seeds and world revisions that retain the same world and population identities.</returns>
    /// <exception cref="ArgumentException"><paramref name="populationScope"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is negative.</exception>
    public static EntityId Create(GenerationScope populationScope, long sequenceIndex)
    {
        if (string.IsNullOrWhiteSpace(populationScope.Value))
            throw new ArgumentException("A population scope is required for sequence entity identity.", nameof(populationScope));
        if (sequenceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceIndex), sequenceIndex, "Sequence index cannot be negative.");

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Identity);
        Append(hash, populationScope.Value);
        var scopeFingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
        return new($"csimentity1_{scopeFingerprint}_{sequenceIndex.ToString(CultureInfo.InvariantCulture)}");
    }

    static void Append(IncrementalHash hash, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, byteCount);
        hash.AppendData(length);
        if (byteCount == 0)
            return;

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, rented);
            hash.AppendData(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>Explicit binding from one generated world population to one entity repository.</summary>
public sealed class RepositoryWorldPopulationBinding
{
    /// <summary>Creates a population-to-repository binding.</summary>
    /// <param name="populationId">Stable world population identity.</param>
    /// <param name="repository">Repository accepting generated entity snapshots.</param>
    /// <param name="entityIdentity">Explicit policy assigning entity identity to each generated observation.</param>
    /// <param name="stateVersion">Non-negative entity-state version assigned to generated snapshots.</param>
    /// <param name="atomicity">Batch atomicity required from the repository.</param>
    /// <exception cref="ArgumentNullException"><paramref name="repository"/> or <paramref name="entityIdentity"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="populationId"/> is empty or white-space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="stateVersion"/> is negative or <paramref name="atomicity"/> is unknown.
    /// </exception>
    public RepositoryWorldPopulationBinding(
        string populationId,
        IEntityRepository repository,
        WorldEntityIdentityPolicy entityIdentity,
        long stateVersion = 0,
        EntityBatchAtomicity atomicity = EntityBatchAtomicity.None)
    {
        PopulationId = Guard.RequireNotNullOrWhiteSpace(populationId);
        Repository = Guard.RequireNotNull(repository);
        EntityIdentity = Guard.RequireNotNull(entityIdentity);
        if (stateVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(stateVersion), stateVersion, "Entity-state version cannot be negative.");
        if (!Enum.IsDefined(atomicity))
            throw new ArgumentOutOfRangeException(nameof(atomicity), atomicity, "Unknown entity batch atomicity.");
        StateVersion = stateVersion;
        Atomicity = atomicity;
    }

    /// <summary>Gets the stable world population identity.</summary>
    public string PopulationId { get; }

    /// <summary>Gets the target entity repository.</summary>
    public IEntityRepository Repository { get; }

    /// <summary>Gets the explicit entity identity policy.</summary>
    public WorldEntityIdentityPolicy EntityIdentity { get; }

    /// <summary>Gets the entity-state version assigned to generated snapshots.</summary>
    public long StateVersion { get; }

    /// <summary>Gets the batch atomicity required from the repository.</summary>
    public EntityBatchAtomicity Atomicity { get; }
}

/// <summary>Deterministic target-identity convention for a repository provisioning profile.</summary>
public static class RepositoryWorldProvisioningTargetConvention
{
    /// <summary>Stable identity of the current repository target-profile convention.</summary>
    public const string Identity = "cohesive-simulation-storage-target/v1";

    /// <summary>Derives an exact provisioning target identity from a destination and normalized repository bindings.</summary>
    /// <param name="destinationId">Stable logical identity of the physical repository destination.</param>
    /// <param name="bindings">Population bindings whose effective policy participates in target identity.</param>
    /// <returns>A target identity that changes when entity mapping, shape, version, or atomicity changes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="destinationId"/> is empty, <paramref name="bindings"/> is empty or contains null, or population
    /// identities repeat.
    /// </exception>
    public static string Create(
        string destinationId,
        IReadOnlyList<RepositoryWorldPopulationBinding> bindings)
    {
        destinationId = Guard.RequireNotNullOrWhiteSpace(destinationId);
        ArgumentNullException.ThrowIfNull(bindings);
        if (bindings.Count == 0)
            throw new ArgumentException("A repository provisioning target requires a population binding.", nameof(bindings));

        RepositoryWorldPopulationBinding[] normalized = new RepositoryWorldPopulationBinding[bindings.Count];
        for (var index = 0; index < bindings.Count; index++)
            normalized[index] = bindings[index] ?? throw new ArgumentException("A repository binding cannot be null.", nameof(bindings));
        Array.Sort(
            normalized,
            static (left, right) => StringComparer.Ordinal.Compare(left.PopulationId, right.PopulationId));
        for (var index = 1; index < normalized.Length; index++)
        {
            if (string.Equals(normalized[index - 1].PopulationId, normalized[index].PopulationId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Population identity '{normalized[index].PopulationId}' has more than one repository binding.",
                    nameof(bindings));
            }
        }

        using TargetFingerprintWriter writer = new();
        writer.Append(Identity);
        writer.Append(destinationId);
        writer.Append(normalized.Length);
        foreach (var binding in normalized)
        {
            writer.Append(binding.PopulationId);
            writer.Append(binding.Repository.EntityDefinition.Name.Value);
            writer.Append(binding.Repository.EntityDefinition.StateShape.Graph.Id.Value);
            writer.Append(binding.Repository.EntityDefinition.StateShape.ShapeId.Value);
            writer.Append((int)binding.EntityIdentity.Source);
            if (binding.EntityIdentity.ObservationField is { } path)
            {
                writer.Append(path.Segments.Length);
                foreach (var segment in path.Segments)
                {
                    writer.Append((int)segment.Kind);
                    writer.Append(segment.Segment ?? string.Empty);
                }
            }
            else
            {
                writer.Append(0);
            }
            writer.Append(binding.StateVersion);
            writer.Append((int)binding.Atomicity);
        }

        return $"{destinationId}@csimrepo1_{writer.Complete()}";
    }

    sealed class TargetFingerprintWriter : IDisposable
    {
        readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        readonly byte[] numberBuffer = new byte[sizeof(long)];

        public void Append(string value)
        {
            var byteCount = Encoding.UTF8.GetByteCount(value);
            BinaryPrimitives.WriteInt32BigEndian(numberBuffer, byteCount);
            hash.AppendData(numberBuffer.AsSpan(0, sizeof(int)));
            if (byteCount == 0)
                return;

            var rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var written = Encoding.UTF8.GetBytes(value, rented);
                hash.AppendData(rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public void Append(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(numberBuffer, value);
            hash.AppendData(numberBuffer.AsSpan(0, sizeof(int)));
        }

        public void Append(long value)
        {
            BinaryPrimitives.WriteInt64BigEndian(numberBuffer, value);
            hash.AppendData(numberBuffer);
        }

        public string Complete() => Convert.ToHexStringLower(hash.GetHashAndReset());

        public void Dispose() => hash.Dispose();
    }
}

/// <summary>Entity-repository sink for deterministic generated world populations.</summary>
/// <remarks>
/// The sink performs deterministic upserts through the shared <see cref="RepositorySeedWriter"/>. Repeated runs
/// with the same world and seed converge the same entity identities and generated state. The population-sequence
/// policy additionally retains stable entity slots across seeds. The generic repository contract has no durable batch
/// ledger, so this sink returns <see cref="WorldProvisioningBatchDisposition.Committed"/> rather than claiming
/// <see cref="WorldProvisioningBatchDisposition.AlreadyCommitted"/>. Repository failures preserve their original
/// exception because a non-atomic batch may have an unknown partial outcome.
/// Unique-field policies retain resolved identities for an incomplete run so duplicates across batches can be
/// rejected. Reservations survive unknown repository failures so the same run can resume, and are released when the
/// population's final batch commits.
/// </remarks>
public sealed class RepositoryWorldProvisioningSink : IWorldProvisioningSink
{
    readonly Lock identityGate = new();
    readonly Dictionary<(WorldProvisioningRunId RunId, string PopulationId), Dictionary<string, long>>
        activeUniqueEntitySequences = [];
    readonly ImmutableDictionary<string, RepositoryWorldPopulationBinding> bindingsByPopulation;
    readonly OperationContext operationContext;
    readonly RepositorySeedWriter seedWriter;

    /// <summary>Creates an entity-repository world provisioning sink.</summary>
    /// <param name="destinationId">Stable logical identity of the physical repository destination.</param>
    /// <param name="operationContext">Base operation context used for repository reads and writes.</param>
    /// <param name="bindings">Explicit population-to-repository bindings.</param>
    /// <param name="seedWriter">Optional shared seed writer; a stateless writer is created when omitted.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="operationContext"/> or <paramref name="bindings"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="destinationId"/> is empty, <paramref name="bindings"/> is empty, or population identities repeat.
    /// </exception>
    public RepositoryWorldProvisioningSink(
        string destinationId,
        OperationContext operationContext,
        IReadOnlyList<RepositoryWorldPopulationBinding> bindings,
        RepositorySeedWriter? seedWriter = null)
    {
        DestinationId = Guard.RequireNotNullOrWhiteSpace(destinationId);
        this.operationContext = Guard.RequireNotNull(operationContext);
        ArgumentNullException.ThrowIfNull(bindings);
        TargetId = RepositoryWorldProvisioningTargetConvention.Create(destinationId, bindings);
        Bindings = [.. bindings.OrderBy(static binding => binding.PopulationId, StringComparer.Ordinal)];
        bindingsByPopulation = Bindings.ToImmutableDictionary(
            static binding => binding.PopulationId,
            StringComparer.Ordinal);
        this.seedWriter = seedWriter ?? new();
    }

    /// <summary>Gets the stable logical identity of the physical repository destination.</summary>
    public string DestinationId { get; }

    /// <inheritdoc />
    public string TargetId { get; }

    /// <summary>Gets normalized population bindings in stable identity order.</summary>
    public ImmutableArray<RepositoryWorldPopulationBinding> Bindings { get; }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="batch"/> names another exact repository target profile.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    /// <remarks>
    /// Binding, capability, shape, identity, duplicate-id, and entity-semantic failures are rejected before repository
    /// writes begin. Exceptions from repository reads or writes are preserved as unknown outcomes.
    /// </remarks>
    public async ValueTask<WorldProvisioningBatchReceipt> CommitAsync(
        WorldProvisioningBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!string.Equals(TargetId, batch.TargetId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Repository sink target '{TargetId}' cannot commit batch target '{batch.TargetId}'.",
                nameof(batch));
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (!bindingsByPopulation.TryGetValue(batch.PopulationId, out var binding))
            return Rejected(batch, $"Population '{batch.PopulationId}' has no repository binding.");

        var capabilities = binding.Repository.BatchCapabilities;
        if (!capabilities.SupportsAtomicity(binding.Atomicity))
        {
            return Rejected(
                batch,
                $"Repository '{binding.Repository.EntityType}' does not support required batch atomicity '{binding.Atomicity}'.");
        }
        if (capabilities.MaxItemsPerBatch is { } maximum && batch.Items.Length > maximum)
        {
            return Rejected(
                batch,
                $"Repository '{binding.Repository.EntityType}' accepts at most '{maximum}' items per batch, "
                + $"but provisioning supplied '{batch.Items.Length}'.");
        }

        var writes = new RepositorySeedWrite[batch.Items.Length];
        var resolvedEntityIds = new EntityId[batch.Items.Length];
        HashSet<string> entityIds = new(StringComparer.Ordinal);
        var uniqueIdentityKey = (batch.RunId, batch.PopulationId);
        for (var index = 0; index < batch.Items.Length; index++)
        {
            var generated = batch.Items[index];
            if (generated.Observation.ShapeId != binding.Repository.EntityDefinition.StateShape.QualifiedId)
            {
                return Rejected(
                    batch,
                    $"Population '{batch.PopulationId}' observation shape '{generated.Observation.ShapeId}' does not match "
                    + $"repository entity shape '{binding.Repository.EntityDefinition.StateShape.QualifiedId}'.");
            }
            if (!binding.EntityIdentity.TryResolve(batch, generated, out var entityId, out var identityError))
                return Rejected(batch, identityError!);
            if (!entityIds.Add(entityId.Value))
            {
                return Rejected(
                    batch,
                    $"Population '{batch.PopulationId}' resolves entity identity '{entityId.Value}' more than once in one batch.");
            }
            EntityObservationSnapshot snapshot = new(
                entityId,
                binding.StateVersion,
                generated.Observation);
            try
            {
                binding.Repository.EntityDefinition.ValidateState(new EntityState(snapshot));
            }
            catch (SemanticRuleViolationException exception)
            {
                return Rejected(
                    batch,
                    $"Generated entity '{entityId.Value}' violates repository entity semantics: {exception.Message}");
            }

            writes[index] = new(
                Type: binding.PopulationId,
                Repository: binding.Repository,
                Write: new(snapshot));
            resolvedEntityIds[index] = entityId;
        }

        if (binding.EntityIdentity.Source == WorldEntityIdentitySource.UniqueObservationField)
        {
            lock (identityGate)
            {
                if (!activeUniqueEntitySequences.TryGetValue(uniqueIdentityKey, out var acceptedUniqueEntitySequences))
                {
                    acceptedUniqueEntitySequences = new(StringComparer.Ordinal);
                    activeUniqueEntitySequences.Add(uniqueIdentityKey, acceptedUniqueEntitySequences);
                }

                for (var index = 0; index < resolvedEntityIds.Length; index++)
                {
                    var entityId = resolvedEntityIds[index].Value;
                    var sequenceIndex = batch.Items[index].Replay.SequenceIndex;
                    if (acceptedUniqueEntitySequences.TryGetValue(entityId, out var acceptedSequence)
                        && acceptedSequence != sequenceIndex)
                    {
                        return Rejected(
                            batch,
                            $"Population '{batch.PopulationId}' resolves unique entity identity '{entityId}' at both "
                            + $"sequence indices '{acceptedSequence}' and '{sequenceIndex}'.");
                    }
                }

                for (var index = 0; index < resolvedEntityIds.Length; index++)
                {
                    acceptedUniqueEntitySequences[resolvedEntityIds[index].Value] =
                        batch.Items[index].Replay.SequenceIndex;
                }
            }
        }

        var context = operationContext.WithCancellationToken(cancellationToken);
        var result = await seedWriter.Seed(
                context,
                writes,
                new(
                    SkipExisting: false,
                    Atomicity: binding.Atomicity))
            .ConfigureAwait(false);
        if (binding.EntityIdentity.Source == WorldEntityIdentitySource.UniqueObservationField)
        {
            if (batch.StartSequenceIndex + batch.Items.Length == batch.PopulationCount)
            {
                lock (identityGate)
                    activeUniqueEntitySequences.Remove(uniqueIdentityKey);
            }
        }

        var replacedCount = result.Items.Count(static item => item.Status == RepositorySeedItemStatuses.Replaced);
        return new(
            batch.Id,
            WorldProvisioningBatchDisposition.Committed,
            $"created={result.WrittenCount - replacedCount};replaced={replacedCount}");
    }

    static WorldProvisioningBatchReceipt Rejected(
        WorldProvisioningBatch batch,
        string detail) =>
        new(batch.Id, WorldProvisioningBatchDisposition.Rejected, detail);
}
