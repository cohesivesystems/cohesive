using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Processes;

/// <summary>One retained write-once local-mutation fingerprint.</summary>
public sealed record ProcessDurableLocalMutationReceipt
{
    /// <summary>Creates retained local-mutation replay evidence.</summary>
    /// <param name="identity">Stable write-once mutation identity.</param>
    /// <param name="fingerprint">Canonical fingerprint of the exact mutation intent.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    [JsonConstructor]
    public ProcessDurableLocalMutationReceipt(
        string identity,
        ProcessCommitFingerprint fingerprint)
    {
        Identity = Guard.RequireNotNullOrWhiteSpace(identity);
        ProcessCheckpointRequirements.RequireIdentity(fingerprint.Value, nameof(fingerprint));
        Fingerprint = fingerprint;
    }

    /// <summary>Stable write-once mutation identity.</summary>
    public string Identity { get; }

    /// <summary>Canonical fingerprint of the exact mutation intent.</summary>
    public ProcessCommitFingerprint Fingerprint { get; }
}

/// <summary>One retained Process commit receipt and its exact replay snapshot.</summary>
public sealed record ProcessDurableCommitReceiptDocument
{
    /// <summary>Creates durable commit replay evidence.</summary>
    /// <param name="id">Stable write-once commit identity.</param>
    /// <param name="fingerprint">Canonical fingerprint of the exact commit intent.</param>
    /// <param name="snapshot">Exact aggregate snapshot returned by a replay.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is empty.</exception>
    [JsonConstructor]
    public ProcessDurableCommitReceiptDocument(
        ProcessCommitId id,
        ProcessCommitFingerprint fingerprint,
        ProcessDurableStoreSnapshot snapshot)
    {
        ProcessCheckpointRequirements.RequireIdentity(id.Value, nameof(id));
        ProcessCheckpointRequirements.RequireIdentity(fingerprint.Value, nameof(fingerprint));
        Id = id;
        Fingerprint = fingerprint;
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    /// <summary>Stable write-once commit identity.</summary>
    public ProcessCommitId Id { get; }

    /// <summary>Canonical fingerprint of the exact commit intent.</summary>
    public ProcessCommitFingerprint Fingerprint { get; }

    /// <summary>Exact aggregate snapshot returned by a replay.</summary>
    public ProcessDurableStoreSnapshot Snapshot { get; }
}

/// <summary>Complete provider-neutral durable state for one Process instance.</summary>
public sealed record ProcessDurableAggregateDocument
{
    /// <summary>Creates one complete Process aggregate document.</summary>
    /// <param name="checkpoint">Complete canonical Process checkpoint.</param>
    /// <param name="revision">Current physical compare-and-swap revision.</param>
    /// <param name="workerLease">Current or expired worker lease.</param>
    /// <param name="latestWorkerFence">Greatest worker fence ever issued for the instance.</param>
    /// <param name="localState">Local state in canonical resource order.</param>
    /// <param name="localMutationReceipts">Local-mutation receipts in canonical identity order.</param>
    /// <param name="commitReceipts">Commit receipts in canonical identity order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="latestWorkerFence"/> is negative.</exception>
    /// <exception cref="ArgumentException">
    /// Revision, lease, local state, or receipt evidence is malformed, duplicated, unordered, or belongs to
    /// another Process instance.
    /// </exception>
    [JsonConstructor]
    public ProcessDurableAggregateDocument(
        ProcessDurableCheckpoint checkpoint,
        ProcessStorageRevision revision,
        ProcessWorkerLease? workerLease,
        long latestWorkerFence,
        ImmutableArray<ProcessLocalState> localState,
        ImmutableArray<ProcessDurableLocalMutationReceipt> localMutationReceipts,
        ImmutableArray<ProcessDurableCommitReceiptDocument> commitReceipts)
    {
        Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
        ProcessCheckpointRequirements.RequireIdentity(revision.Value, nameof(revision));
        if (latestWorkerFence < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latestWorkerFence),
                latestWorkerFence,
                "The latest Process worker fence cannot be negative.");
        }

        if (workerLease is not null
            && (!long.TryParse(
                    workerLease.Fence.Value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var retainedFence)
                || retainedFence != latestWorkerFence))
        {
            throw new ArgumentException(
                "The retained worker lease must carry the latest numeric Process worker fence.",
                nameof(workerLease));
        }

        var normalizedLocalState = localState.IsDefault ? [] : localState;
        _ = new ProcessDurableStoreSnapshot(checkpoint, revision, workerLease, normalizedLocalState);
        LocalMutationReceipts = ValidateOrdered(
            localMutationReceipts,
            static receipt => receipt.Identity,
            nameof(localMutationReceipts));
        CommitReceipts = ValidateOrdered(
            commitReceipts,
            static receipt => receipt.Id.Value,
            nameof(commitReceipts));
        if (CommitReceipts.Any(receipt =>
            receipt.Snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId != InstanceId))
        {
            throw new ArgumentException(
                "A Process commit receipt snapshot must belong to its aggregate instance.",
                nameof(commitReceipts));
        }

        Revision = revision;
        WorkerLease = workerLease;
        LatestWorkerFence = latestWorkerFence;
        LocalState = normalizedLocalState;
    }

    /// <summary>Logical Process instance stored by this aggregate.</summary>
    [JsonIgnore]
    public ProcessInstanceId InstanceId => Checkpoint.ContinuationIdentity.ProcessInstanceId;

    /// <summary>Complete canonical Process checkpoint.</summary>
    public ProcessDurableCheckpoint Checkpoint { get; }

    /// <summary>Current physical compare-and-swap revision.</summary>
    public ProcessStorageRevision Revision { get; }

    /// <summary>Current or expired worker lease.</summary>
    public ProcessWorkerLease? WorkerLease { get; }

    /// <summary>Greatest worker fence ever issued for the instance.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long LatestWorkerFence { get; }

    /// <summary>Local state in canonical resource order.</summary>
    public ImmutableArray<ProcessLocalState> LocalState { get; }

    /// <summary>Local-mutation receipts in canonical identity order.</summary>
    public ImmutableArray<ProcessDurableLocalMutationReceipt> LocalMutationReceipts { get; }

    /// <summary>Commit receipts in canonical identity order.</summary>
    public ImmutableArray<ProcessDurableCommitReceiptDocument> CommitReceipts { get; }

    static ImmutableArray<T> ValidateOrdered<T>(
        ImmutableArray<T> values,
        Func<T, string> identity,
        string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        string? prior = null;
        foreach (var value in normalized)
        {
            if (value is null)
            {
                throw new ArgumentException("Durable Process receipts cannot contain null entries.", parameterName);
            }

            var current = identity(value);
            if (prior is not null && StringComparer.Ordinal.Compare(prior, current) >= 0)
            {
                throw new ArgumentException(
                    "Durable Process receipts must be unique and ordered by identity.",
                    parameterName);
            }
            prior = current;
        }
        return normalized;
    }
}

/// <summary>Complete provider-neutral document for one atomic Process durability authority.</summary>
/// <remarks>
/// Providers may persist this document as one aggregate or project it into equivalent transactional rows. It is
/// the reference store's durable conformance format and remains independent of a particular database.
/// </remarks>
public sealed record ProcessDurableStoreDocument
{
    /// <summary>Current portable Process durable-store document schema.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } =
        new("cohesive-process-durable-store/v1");

    /// <summary>Creates a complete durable-store authority document.</summary>
    /// <param name="schemaVersion">Exact portable document schema.</param>
    /// <param name="aggregates">Process aggregates in canonical instance order.</param>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported or aggregates are null, duplicated, or unordered.
    /// </exception>
    [JsonConstructor]
    public ProcessDurableStoreDocument(
        ExecutionIrSchemaVersion schemaVersion,
        ImmutableArray<ProcessDurableAggregateDocument> aggregates)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentException("Unsupported Process durable-store schema version.", nameof(schemaVersion));
        }

        var normalized = aggregates.IsDefault ? [] : aggregates;
        string? prior = null;
        foreach (var aggregate in normalized)
        {
            if (aggregate is null)
            {
                throw new ArgumentException("Process durable-store aggregates cannot contain null entries.", nameof(aggregates));
            }

            var current = aggregate.InstanceId.Value;
            if (prior is not null && StringComparer.Ordinal.Compare(prior, current) >= 0)
            {
                throw new ArgumentException(
                    "Process durable-store aggregates must be unique and ordered by instance identity.",
                    nameof(aggregates));
            }
            prior = current;
        }

        SchemaVersion = schemaVersion;
        Aggregates = normalized;
    }

    /// <summary>Exact portable document schema.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Process aggregates in canonical instance order.</summary>
    public ImmutableArray<ProcessDurableAggregateDocument> Aggregates { get; }

    /// <summary>Creates an empty current-version authority document.</summary>
    /// <returns>An empty portable Process durable-store document.</returns>
    public static ProcessDurableStoreDocument Empty() => new(CurrentSchemaVersion, []);
}

/// <summary>Strict portable JSON wire contract for Process durable-store documents.</summary>
public static class ProcessDurableStoreJsonSerializer
{
    static readonly JsonSerializerOptions CompactOptions = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one complete Process durable-store authority document.</summary>
    /// <param name="document">Exact provider-neutral durable-store state.</param>
    /// <returns>Strict portable JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document violates the strict JSON contract.</exception>
    public static string Serialize(ProcessDurableStoreDocument document) =>
        JsonSerializer.Serialize(
            document ?? throw new ArgumentNullException(nameof(document)),
            CompactOptions);

    /// <summary>Deserializes one complete Process durable-store authority document.</summary>
    /// <param name="json">Strict portable JSON.</param>
    /// <returns>The validated provider-neutral durable-store state.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">JSON or semantic constructor invariants are invalid.</exception>
    public static ProcessDurableStoreDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<ProcessDurableStoreDocument>(
            json ?? throw new ArgumentNullException(nameof(json)),
            CompactOptions) ?? throw new JsonException("A Process durable-store document cannot be null.");
}
