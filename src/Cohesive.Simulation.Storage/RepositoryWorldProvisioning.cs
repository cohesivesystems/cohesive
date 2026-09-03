using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Simulation.Provisioning;
using Cohesive.Storage;
using Cohesive.Storage.Seeding;
using Cohesive.Transitions.Model;

namespace Cohesive.Simulation.Storage;

/// <summary>Explicit binding from one generated world population to one entity repository.</summary>
public sealed class RepositoryWorldPopulationBinding
{
    /// <summary>Creates a population-to-repository binding.</summary>
    /// <param name="populationId">Stable world population identity.</param>
    /// <param name="repository">Repository accepting generated entity snapshots.</param>
    /// <param name="stateVersion">Non-negative entity-state version assigned to generated snapshots.</param>
    /// <param name="atomicity">Batch atomicity required from the repository.</param>
    /// <exception cref="ArgumentNullException"><paramref name="repository"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="populationId"/> is empty or white-space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="stateVersion"/> is negative or <paramref name="atomicity"/> is unknown.
    /// </exception>
    public RepositoryWorldPopulationBinding(
        string populationId,
        IEntityRepository repository,
        long stateVersion = 0,
        EntityBatchAtomicity atomicity = EntityBatchAtomicity.None)
    {
        PopulationId = Guard.RequireNotNullOrWhiteSpace(populationId);
        Repository = Guard.RequireNotNull(repository);
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

    /// <summary>Gets the entity-state version assigned to generated snapshots.</summary>
    public long StateVersion { get; }

    /// <summary>Gets the batch atomicity required from the repository.</summary>
    public EntityBatchAtomicity Atomicity { get; }
}

/// <summary>Deterministic target-identity convention for a repository provisioning profile.</summary>
public static class RepositoryWorldProvisioningTargetConvention
{
    /// <summary>Stable identity of the current repository target-profile convention.</summary>
    public const string Identity = "cohesive-simulation-storage-target/v2";

    /// <summary>Derives an exact provisioning target identity from a destination and normalized repository bindings.</summary>
    /// <param name="destinationId">Stable logical identity of the physical repository destination.</param>
    /// <param name="bindings">Population bindings whose effective repository policy participates in target identity.</param>
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
            writer.Append(binding.StateVersion);
            writer.Append((int)binding.Atomicity);
        }

        return $"{destinationId}@csimrepo2_{writer.Complete()}";
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
/// with the same world and seed converge the same entity identities and generated state. Entity identities are
/// supplied by the canonical world artifact rather than selected by this adapter. The generic repository contract has no durable batch
/// ledger, so this sink returns <see cref="WorldProvisioningBatchDisposition.Committed"/> rather than claiming
/// <see cref="WorldProvisioningBatchDisposition.AlreadyCommitted"/>. Repository failures preserve their original
/// exception because a non-atomic batch may have an unknown partial outcome.
/// </remarks>
public sealed class RepositoryWorldProvisioningSink : IWorldProvisioningSink
{
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
    /// Binding, capability, shape, duplicate-id, and entity-semantic failures are rejected before repository
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
        HashSet<string> entityIds = new(StringComparer.Ordinal);
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
            var entityId = generated.EntityId;
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
        }

        var context = operationContext.WithCancellationToken(cancellationToken);
        var result = await seedWriter.Seed(
                context,
                writes,
                new(
                    SkipExisting: false,
                    Atomicity: binding.Atomicity))
            .ConfigureAwait(false);
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
