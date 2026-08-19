using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Whether bounded canonical source reads may query more than one Cosmos logical partition.</summary>
public enum CosmosRelationQueryCrossPartitionPolicy
{
    /// <summary>
    /// Requires one explicitly configured logical partition. Reader construction rejects this policy when no fixed
    /// partition key is supplied.
    /// </summary>
    Prohibit = 0,

    /// <summary>Allows bounded queries across logical partitions.</summary>
    AllowBoundedQueries = 1
}

/// <summary>
/// Explicit physical policy for bounded relation/query acquisition from one Cosmos container.
/// </summary>
/// <remarks>
/// The partition selector and fixed partition key are explicit caller assertions about the physical container;
/// the reader does not infer them from identity or semantic field names and does not compare them with Cosmos
/// container metadata. An incorrect fixed key can make an empty physical query appear authoritatively absent, and
/// an incorrect selector can invalidate cross-partition identity-conflict evidence.
/// </remarks>
/// <remarks>
/// V1 models one property-only scalar partition coordinate for cross-partition attribution. It does not model a
/// hierarchical partition-key tuple. A fixed SDK <see cref="PartitionKey"/> may scope I/O, but callers must still
/// ensure that its declared source scope is complete. Without a fixed-partition assertion, reads are issued only
/// when <see cref="CrossPartitionPolicy"/> explicitly allows bounded cross-partition queries.
/// </remarks>
public sealed record CosmosRelationQuerySourcePolicy
{
    /// <summary>
    /// Maximum structurally safe relationship predicate width accepted before SQL construction and request-size
    /// validation.
    /// </summary>
    public const int MaximumSupportedKeysPerQuery = 4_096;

    /// <summary>Creates explicit Cosmos source-acquisition policy.</summary>
    /// <param name="logicalPartition">
    /// Provider-neutral identity shared by every supplied source and reader participating in this logical partition.
    /// </param>
    /// <param name="partitionSourceSelector">
    /// Caller-declared property-only path to one scalar Cosmos partition coordinate. The reader does not verify it
    /// against container metadata; hierarchical partition-key tuples are not represented in v1.
    /// </param>
    /// <param name="crossPartitionPolicy">Whether bounded reads without a fixed partition may cross partitions.</param>
    /// <param name="fixedPartitionKey">
    /// Optional caller assertion that this logical partition contains every document exposed by the registration.
    /// An incorrect assertion can make authoritative absence results unsound.
    /// </param>
    /// <param name="maximumEnumerationRows">Maximum rows one bounded enumeration may return.</param>
    /// <param name="maximumKeysPerQuery">Maximum lookup keys placed in one Cosmos SQL query.</param>
    /// <param name="maximumQueryChunks">Maximum Cosmos SQL queries used to realize one batched lookup.</param>
    /// <param name="maximumSdkPageSize">Maximum item count requested for one Cosmos SDK feed page.</param>
    /// <param name="readConsistencyLevel">
    /// Optional request-level Cosmos consistency. Materialization sources require
    /// <see cref="Microsoft.Azure.Cosmos.ConsistencyLevel.Strong"/> so a captured change-feed cut and
    /// the subsequent baseline cannot leave a pre-cut write permanently unseen.
    /// </param>
    /// <param name="requestSizeLimits">
    /// Explicit pre-I/O SQL-text and complete-request size boundaries, or <see langword="null"/> for Cosmos
    /// conventions.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitionSourceSelector"/> or <paramref name="logicalPartition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="partitionSourceSelector"/> is empty or is not a property-only path.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="crossPartitionPolicy"/> or <paramref name="readConsistencyLevel"/> is unsupported, a
    /// numeric limit is not positive, or <paramref name="maximumKeysPerQuery"/> exceeds
    /// <see cref="MaximumSupportedKeysPerQuery"/>.
    /// </exception>
    public CosmosRelationQuerySourcePolicy(
        string partitionSourceSelector,
        RelationQueryLogicalPartitionIdentity logicalPartition,
        CosmosRelationQueryCrossPartitionPolicy crossPartitionPolicy = CosmosRelationQueryCrossPartitionPolicy.Prohibit,
        PartitionKey? fixedPartitionKey = null,
        int maximumEnumerationRows = 10_000,
        int maximumKeysPerQuery = 100,
        int maximumQueryChunks = 16,
        int maximumSdkPageSize = 256,
        ConsistencyLevel? readConsistencyLevel = null,
        CosmosQueryRequestSizeLimits? requestSizeLimits = null)
    {
        if (!Enum.IsDefined(crossPartitionPolicy))
        {
            throw new ArgumentOutOfRangeException(
                nameof(crossPartitionPolicy),
                crossPartitionPolicy,
                "Unsupported Cosmos cross-partition query policy.");
        }
        if (maximumEnumerationRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEnumerationRows), maximumEnumerationRows, "The enumeration row limit must be positive.");
        if (maximumKeysPerQuery is <= 0 or > MaximumSupportedKeysPerQuery)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumKeysPerQuery),
                maximumKeysPerQuery,
                $"The query key limit must be between 1 and {MaximumSupportedKeysPerQuery}.");
        }
        if (maximumQueryChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumQueryChunks), maximumQueryChunks, "The query chunk limit must be positive.");
        if (maximumSdkPageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSdkPageSize), maximumSdkPageSize, "The SDK page-size limit must be positive.");
        if (readConsistencyLevel is { } consistencyLevel && !Enum.IsDefined(consistencyLevel))
        {
            throw new ArgumentOutOfRangeException(
                nameof(readConsistencyLevel),
                readConsistencyLevel,
                "Unsupported Cosmos read-consistency level.");
        }

        LogicalPartition = Guard.RequireNotNull(logicalPartition);
        PartitionSourceSelector = CosmosRelationQuerySourceSelectors.RequirePropertyPath(
            partitionSourceSelector,
            nameof(partitionSourceSelector)).ToString();
        CrossPartitionPolicy = crossPartitionPolicy;
        FixedPartitionKey = fixedPartitionKey;
        MaximumEnumerationRows = maximumEnumerationRows;
        MaximumKeysPerQuery = maximumKeysPerQuery;
        MaximumQueryChunks = maximumQueryChunks;
        MaximumSdkPageSize = maximumSdkPageSize;
        ReadConsistencyLevel = readConsistencyLevel;
        RequestSizeLimits = requestSizeLimits ?? new();
    }

    /// <summary>
    /// Caller-declared property-only path to one scalar Cosmos partition coordinate; not verified against container
    /// metadata.
    /// </summary>
    public string PartitionSourceSelector { get; }

    /// <summary>Provider-neutral logical partition implemented by every read under this policy.</summary>
    public RelationQueryLogicalPartitionIdentity LogicalPartition { get; }

    /// <summary>Whether bounded reads without a fixed partition may query multiple logical partitions.</summary>
    public CosmosRelationQueryCrossPartitionPolicy CrossPartitionPolicy { get; }

    /// <summary>
    /// Caller-asserted fixed logical partition for this source, or <see langword="null"/> for no fixed-partition
    /// assertion.
    /// </summary>
    public PartitionKey? FixedPartitionKey { get; }

    /// <summary>Maximum rows one bounded enumeration may return.</summary>
    public int MaximumEnumerationRows { get; }

    /// <summary>Maximum lookup keys placed in one Cosmos SQL query.</summary>
    public int MaximumKeysPerQuery { get; }

    /// <summary>Maximum Cosmos SQL queries used to realize one batched lookup.</summary>
    public int MaximumQueryChunks { get; }

    /// <summary>Maximum item count requested for one Cosmos SDK feed page.</summary>
    public int MaximumSdkPageSize { get; }

    /// <summary>
    /// Request-level Cosmos read consistency, or <see langword="null"/> to inherit the client/account policy.
    /// </summary>
    public ConsistencyLevel? ReadConsistencyLevel { get; }

    /// <summary>Pre-I/O SQL-text and conservative complete-request size boundaries.</summary>
    public CosmosQueryRequestSizeLimits RequestSizeLimits { get; }

    /// <summary>Derives planner-visible placement limits that this physical policy can honor conclusively.</summary>
    /// <param name="configuredLimits">Explicit or conventional source placement limits to constrain.</param>
    /// <returns>
    /// <paramref name="configuredLimits"/> when already within every policy and runtime boundary; otherwise a new
    /// limit snapshot with batch size constrained by key/query chunking and row buffering constrained by enumeration
    /// and runtime-array limits.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuredLimits"/> is <see langword="null"/>.</exception>
    public RelationQuerySourcePlacementLimits GetEffectivePlacementLimits(
        RelationQuerySourcePlacementLimits configuredLimits)
    {
        ArgumentNullException.ThrowIfNull(configuredLimits);
        var policyBatchLimit = checked((long)MaximumKeysPerQuery * MaximumQueryChunks);
        var effectiveBatchSize = Math.Min(configuredLimits.MaximumBatchSize, policyBatchLimit);
        var effectiveBufferedRows = Math.Min(
            configuredLimits.MaximumBufferedRows,
            Math.Min(MaximumEnumerationRows, Array.MaxLength - 1L));
        return effectiveBatchSize == configuredLimits.MaximumBatchSize
            && effectiveBufferedRows == configuredLimits.MaximumBufferedRows
                ? configuredLimits
                : new RelationQuerySourcePlacementLimits(
                    effectiveBatchSize,
                    effectiveBufferedRows,
                    configuredLimits.MaximumFanOut,
                    configuredLimits.MaximumConcurrency);
    }
}

static class CosmosRelationQuerySourceSelectors
{
    internal static FieldPath RequirePropertyPath(string selector, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector, parameterName);
        try
        {
            return CosmosSqlNames.RequirePropertyPath(FieldPath.Parse(selector), parameterName);
        }
        catch (ArgumentException exception) when (!string.Equals(exception.ParamName, parameterName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A Cosmos relation/query source selector must be a non-empty property-only path.",
                parameterName,
                exception);
        }
    }
}
