namespace Cohesive.Storage;

/// <summary>
/// Resolves repository partition keys from semantic operation context, entity ids, and observation state.
/// </summary>
/// <remarks>
/// Partition policies are the repository-level placement contract. They can encode tenant scope,
/// bucket modulus, date-window placement, or any other strategy that can produce an exact partition
/// key for writes and, when possible, point reads.
/// </remarks>
public sealed class EntityPartitionKeyPolicy
{
    readonly Func<OperationContext, EntityObservationSnapshot, string> writePartitionKeyResolver;
    readonly Func<OperationContext, string, string?>? pointReadPartitionKeyResolver;

    /// <summary>
    /// Creates a partition-key policy.
    /// </summary>
    /// <param name="description">Human-readable policy description used in diagnostics.</param>
    /// <param name="writePartitionKeyResolver">Resolver used for write placement.</param>
    /// <param name="pointReadPartitionKeyResolver">Optional resolver used for exact point-read placement.</param>
    public EntityPartitionKeyPolicy(
        string description,
        Func<OperationContext, EntityObservationSnapshot, string> writePartitionKeyResolver,
        Func<OperationContext, string, string?>? pointReadPartitionKeyResolver = null
        )
    {
        Description = Guard.RequireNotNullOrWhiteSpace(description);
        this.writePartitionKeyResolver = Guard.RequireNotNull(writePartitionKeyResolver);
        this.pointReadPartitionKeyResolver = pointReadPartitionKeyResolver;
    }

    /// <summary>
    /// Human-readable policy description used in diagnostics.
    /// </summary>
    public string Description { get; }

    /// <summary>Fixed placement evidence, present only for a policy created with Fixed.</summary>
    public string? FixedPartitionKey { get; private init; }

    /// <summary>Uses one partition for every point read and write, optionally checking an observation field.</summary>
    /// <param name="partitionKey">Required trusted partition value.</param>
    /// <param name="observationField">If supplied, writes must carry this same value in that field.</param>
    /// <returns>An immutable policy exposing fixed placement to repository capabilities.</returns>
    /// <exception cref="ArgumentException">A supplied name or partition is empty, or the partition has surrounding whitespace.</exception>
    public static EntityPartitionKeyPolicy Fixed(string partitionKey, string? observationField = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        if (partitionKey != partitionKey.Trim()) throw new ArgumentException("Fixed partition must not have surrounding whitespace.", nameof(partitionKey));
        if (observationField is not null) ArgumentException.ThrowIfNullOrWhiteSpace(observationField);
        return new("fixed partition", (_, snapshot) =>
        {
            if (observationField is not null && snapshot.Observation.GetField(observationField).GetRequiredString() != partitionKey)
                throw new ArgumentException("Observation differs from the fixed storage partition.");
            return partitionKey;
        }, (_, _) => partitionKey) { FixedPartitionKey = partitionKey };
    }

    /// <summary>
    /// Default policy that uses the observation id as the partition key for writes and reads.
    /// </summary>
    public static EntityPartitionKeyPolicy ObservationId { get; } = new(
        description: "observation id",
        writePartitionKeyResolver: static (_, snapshot) => snapshot.EntityId.Value,
        pointReadPartitionKeyResolver: static (_, id) => id
        );

    /// <summary>
    /// Creates a policy that reads the write partition key from one observation field.
    /// </summary>
    public static EntityPartitionKeyPolicy FromField(string fieldName)
    {
        var normalizedFieldName = Guard.RequireNotNullOrWhiteSpace(fieldName);
        return new(
            description: $"field '{normalizedFieldName}'",
            writePartitionKeyResolver: (_, snapshot) => snapshot.Observation.GetField(normalizedFieldName).GetRequiredString()
            );
    }

    /// <summary>
    /// Creates a policy from an entity-snapshot write selector.
    /// </summary>
    public static EntityPartitionKeyPolicy FromObservation(
        Func<EntityObservationSnapshot, string> partitionKeySelector,
        string description = "the configured partition-key selector",
        Func<string, string?>? pointReadPartitionKeySelector = null
        )
    {
        ArgumentNullException.ThrowIfNull(partitionKeySelector);
        return new(
            description,
            (_, observation) => partitionKeySelector(observation),
            pointReadPartitionKeySelector is null ? null : (_, id) => pointReadPartitionKeySelector(id)
            );
    }

    /// <summary>
    /// Resolves a non-empty partition key for writing one observation.
    /// </summary>
    public string ResolveWritePartitionKey(OperationContext context, EntityObservationSnapshot observation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observation);
        return Guard.RequireNotNullOrWhiteSpace(writePartitionKeyResolver(context, observation)).Trim();
    }

    /// <summary>
    /// Attempts to resolve an exact partition key for loading one observation by id.
    /// </summary>
    public string? TryResolvePointReadPartitionKey(OperationContext context, string id)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var partitionKey = pointReadPartitionKeyResolver?.Invoke(context, id);
        return string.IsNullOrWhiteSpace(partitionKey) ? null : partitionKey.Trim();
    }
}
