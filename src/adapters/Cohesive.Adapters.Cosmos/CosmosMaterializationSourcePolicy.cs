namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Explicit hard operating boundaries and deployment evidence for one Cosmos materialization source.
/// </summary>
/// <remarks>
/// Full-fidelity change delivery requires account-level continuous backup whose configured retention bounds the
/// all-versions-and-deletes feed, plus evidence that the feed supplies the previous images required by this adapter.
/// These settings are deployment evidence supplied by the caller; the adapter does not mutate account policy.
/// Baseline-plus-catch-up additionally requires evidence that the account is configured for Strong consistency.
/// Baseline queries request Strong explicitly, while the change-feed client must inherit or explicitly retain that
/// account policy rather than weakening it.
/// </remarks>
public sealed record CosmosMaterializationSourcePolicy
{
    /// <summary>Conventional maximum observations returned by one baseline page.</summary>
    public const int DefaultMaximumScanPageItems = 1_000;

    /// <summary>Conventional maximum canonical observation bytes returned by one baseline page.</summary>
    public const long DefaultMaximumScanPageBytes = 4L * 1024 * 1024;

    /// <summary>Conventional maximum deliveries returned by one change page.</summary>
    public const int DefaultMaximumChangePageItems = 1_000;

    /// <summary>Conventional maximum canonical delivery bytes returned by one change page.</summary>
    public const long DefaultMaximumChangePageBytes = 4L * 1024 * 1024;

    /// <summary>Conventional Cosmos SDK page-size hint.</summary>
    public const int DefaultMaximumProviderPageItems = 1_000;

    /// <summary>Conventional maximum encoded cursor characters.</summary>
    public const int DefaultMaximumCursorCharacters = 4 * 1024 * 1024;

    /// <summary>Conventional maximum operations admitted for one physical container.</summary>
    public const int DefaultMaximumContainerParallelism = 4;

    /// <summary>Conventional maximum operations admitted for one fixed logical partition.</summary>
    public const int DefaultMaximumPartitionParallelism = 1;

    /// <summary>Creates explicit Cosmos materialization operating policy.</summary>
    /// <param name="fullFidelityRetention">
    /// Positive account-level continuous-backup retention horizon for all-versions-and-deletes change feed.
    /// </param>
    /// <param name="continuousBackupEvidenceReference">
    /// Non-sensitive deployment evidence proving that continuous backup is enabled for the Cosmos account.
    /// </param>
    /// <param name="previousImageEvidenceReference">
    /// Non-sensitive deployment evidence proving that the full-fidelity feed supplies previous images for replace
    /// and delete changes.
    /// </param>
    /// <param name="strongConsistencyEvidenceReference">
    /// Non-sensitive deployment evidence proving that the Cosmos account is configured for Strong consistency.
    /// The adapter requests Strong on every baseline query and rejects an explicitly weaker change-feed client.
    /// </param>
    /// <param name="maximumScanPageItems">Positive maximum observations returned by one baseline page.</param>
    /// <param name="maximumScanPageBytes">Positive maximum canonical observation bytes returned by one baseline page.</param>
    /// <param name="maximumChangePageItems">Positive maximum deliveries returned by one change page.</param>
    /// <param name="maximumChangePageBytes">Positive maximum canonical delivery bytes returned by one change page.</param>
    /// <param name="maximumProviderPageItems">
    /// Positive SDK page-size hint. Cosmos may exceed this hint to preserve one transactional change batch; the
    /// adapter retains an intra-page cursor rather than dropping the response suffix.
    /// </param>
    /// <param name="maximumCursorCharacters">Positive maximum encoded continuation or position characters.</param>
    /// <param name="maximumContainerParallelism">
    /// Positive maximum operations admitted across source instances bound to the same account, database, and
    /// container in one runtime-owned admission index.
    /// </param>
    /// <param name="maximumPartitionParallelism">
    /// Positive maximum operations admitted across source instances bound to the same fixed logical partition in
    /// one runtime-owned admission index. Whole-container sources use only the container bound.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="continuousBackupEvidenceReference"/>, <paramref name="previousImageEvidenceReference"/>, or
    /// <paramref name="strongConsistencyEvidenceReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A deployment evidence reference is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A duration or numeric bound is not positive.</exception>
    public CosmosMaterializationSourcePolicy(
        TimeSpan fullFidelityRetention,
        string continuousBackupEvidenceReference,
        string previousImageEvidenceReference,
        string strongConsistencyEvidenceReference,
        int maximumScanPageItems = DefaultMaximumScanPageItems,
        long maximumScanPageBytes = DefaultMaximumScanPageBytes,
        int maximumChangePageItems = DefaultMaximumChangePageItems,
        long maximumChangePageBytes = DefaultMaximumChangePageBytes,
        int maximumProviderPageItems = DefaultMaximumProviderPageItems,
        int maximumCursorCharacters = DefaultMaximumCursorCharacters,
        int maximumContainerParallelism = DefaultMaximumContainerParallelism,
        int maximumPartitionParallelism = DefaultMaximumPartitionParallelism)
    {
        if (fullFidelityRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(fullFidelityRetention), fullFidelityRetention, "Full-fidelity retention must be positive.");
        continuousBackupEvidenceReference = Guard.RequireNotNullOrWhiteSpace(continuousBackupEvidenceReference);
        previousImageEvidenceReference = Guard.RequireNotNullOrWhiteSpace(previousImageEvidenceReference);
        strongConsistencyEvidenceReference = Guard.RequireNotNullOrWhiteSpace(
            strongConsistencyEvidenceReference);
        if (maximumScanPageItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumScanPageItems), maximumScanPageItems, "A scan page item bound must be positive.");
        if (maximumScanPageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumScanPageBytes), maximumScanPageBytes, "A scan page byte bound must be positive.");
        if (maximumChangePageItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumChangePageItems), maximumChangePageItems, "A change page item bound must be positive.");
        if (maximumChangePageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumChangePageBytes), maximumChangePageBytes, "A change page byte bound must be positive.");
        if (maximumProviderPageItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumProviderPageItems), maximumProviderPageItems, "A provider page hint must be positive.");
        if (maximumCursorCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCursorCharacters), maximumCursorCharacters, "A cursor character bound must be positive.");
        if (maximumContainerParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumContainerParallelism),
                maximumContainerParallelism,
                "Container source parallelism must be positive.");
        }
        if (maximumPartitionParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPartitionParallelism),
                maximumPartitionParallelism,
                "Partition source parallelism must be positive.");
        }

        FullFidelityRetention = fullFidelityRetention;
        ContinuousBackupEvidenceReference = continuousBackupEvidenceReference;
        PreviousImageEvidenceReference = previousImageEvidenceReference;
        StrongConsistencyEvidenceReference = strongConsistencyEvidenceReference;
        MaximumScanPageItems = maximumScanPageItems;
        MaximumScanPageBytes = maximumScanPageBytes;
        MaximumChangePageItems = maximumChangePageItems;
        MaximumChangePageBytes = maximumChangePageBytes;
        MaximumProviderPageItems = maximumProviderPageItems;
        MaximumCursorCharacters = maximumCursorCharacters;
        MaximumContainerParallelism = maximumContainerParallelism;
        MaximumPartitionParallelism = maximumPartitionParallelism;
    }

    /// <summary>Caller-attested account continuous-backup retention horizon for the full-fidelity change feed.</summary>
    public TimeSpan FullFidelityRetention { get; }

    /// <summary>Caller-supplied deployment evidence that account-level continuous backup is enabled.</summary>
    public string ContinuousBackupEvidenceReference { get; }

    /// <summary>Caller-supplied evidence that full-fidelity replace and delete records provide previous images.</summary>
    public string PreviousImageEvidenceReference { get; }

    /// <summary>Caller-supplied deployment evidence that the account is configured for Strong consistency.</summary>
    public string StrongConsistencyEvidenceReference { get; }

    /// <summary>Maximum observations returned by one baseline page.</summary>
    public int MaximumScanPageItems { get; }

    /// <summary>Maximum canonical observation bytes returned by one baseline page.</summary>
    public long MaximumScanPageBytes { get; }

    /// <summary>Maximum deliveries returned by one change page.</summary>
    public int MaximumChangePageItems { get; }

    /// <summary>Maximum canonical delivery bytes returned by one change page.</summary>
    public long MaximumChangePageBytes { get; }

    /// <summary>Maximum item-count hint supplied to a Cosmos SDK page request.</summary>
    public int MaximumProviderPageItems { get; }

    /// <summary>Maximum encoded continuation or source-position characters accepted by the adapter.</summary>
    public int MaximumCursorCharacters { get; }

    /// <summary>Maximum admitted source operations for one physical container.</summary>
    public int MaximumContainerParallelism { get; }

    /// <summary>Maximum admitted source operations for one fixed logical partition.</summary>
    public int MaximumPartitionParallelism { get; }
}
