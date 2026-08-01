using System.Text.Json.Serialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>PostgreSQL replica-identity mode expected by one logical-replication source binding.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresLogicalReplicationReplicaIdentityKind
{
    /// <summary>The table primary key is the replica identity.</summary>
    Default = 0,

    /// <summary>Every table column is included in old-row logical-replication tuples.</summary>
    Full = 1,

    /// <summary>One explicitly named unique index is the replica identity.</summary>
    Index = 2
}

/// <summary>Whether a logical-replication binding requires complete before images.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostgresLogicalReplicationBeforeImageRequirement
{
    /// <summary>Complete before images are not required by the binding.</summary>
    NotRequired = 0,

    /// <summary>Every update and delete must carry a complete before image.</summary>
    Required = 1
}

/// <summary>Exact expected PostgreSQL replica-identity configuration for one published table.</summary>
public sealed record PostgresLogicalReplicationReplicaIdentityBinding
{
    /// <summary>Creates expected replica-identity evidence.</summary>
    /// <param name="kind">Expected PostgreSQL replica-identity mode.</param>
    /// <param name="indexName">
    /// Exact PostgreSQL replica-identity index when <paramref name="kind"/> is
    /// <see cref="PostgresLogicalReplicationReplicaIdentityKind.Index"/>; otherwise <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="indexName"/> is <see langword="null"/> for index replica identity.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="indexName"/> is absent or invalid for index replica identity, or is present for another
    /// replica-identity mode.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public PostgresLogicalReplicationReplicaIdentityBinding(
        PostgresLogicalReplicationReplicaIdentityKind kind,
        string? indexName = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unsupported PostgreSQL logical-replication replica identity.");
        }

        if (kind == PostgresLogicalReplicationReplicaIdentityKind.Index)
        {
            IndexName = PostgresRelationQueryStorageBinding.RequireIdentifier(
                value: indexName!,
                parameterName: nameof(indexName));
        }
        else if (indexName is not null)
        {
            throw new ArgumentException(
                "Only index replica identity may declare a replica-identity index name.",
                nameof(indexName));
        }

        Kind = kind;
    }

    /// <summary>Expected PostgreSQL replica-identity mode.</summary>
    public PostgresLogicalReplicationReplicaIdentityKind Kind { get; }

    /// <summary>Exact replica-identity index name, or <see langword="null"/> for default or full identity.</summary>
    public string? IndexName { get; }

    /// <summary>Whether this configuration supplies complete old rows for updates and deletes.</summary>
    [JsonIgnore]
    public bool ProvidesCompleteBeforeImage => Kind == PostgresLogicalReplicationReplicaIdentityKind.Full;
}

/// <summary>
/// Exact publication and dedicated logical slot binding for one PostgreSQL materialization change source.
/// </summary>
/// <remarks>
/// The slot is exclusively owned by one exact materialization source placement. Sharing it between independently
/// checkpointed consumers would allow one consumer to release WAL still required by another. PostgreSQL does not
/// expose a durable slot-incarnation identity, so the deployment owner supplies <see cref="SlotGeneration"/> and
/// rotates it whenever the slot is dropped and recreated, even when <see cref="SlotName"/> is reused.
/// </remarks>
public sealed record PostgresLogicalReplicationBinding
{
    /// <summary>Maximum characters in one operator-owned slot-generation identity.</summary>
    public const int MaximumSlotGenerationCharacters = 256;

    /// <summary>Creates one exact publication and dedicated-slot binding.</summary>
    /// <param name="publicationName">Exact PostgreSQL publication identifier.</param>
    /// <param name="slotName">
    /// Exact dedicated logical-replication slot name using PostgreSQL's lowercase letter, digit, and underscore
    /// grammar and 63-byte identifier limit.
    /// </param>
    /// <param name="slotGeneration">
    /// Stable operator-owned slot-incarnation identity, rotated whenever the physical slot is recreated.
    /// </param>
    /// <param name="expectedReplicaIdentity">Exact replica identity expected on the one published table.</param>
    /// <param name="beforeImageRequirement">Whether complete update and delete before images are mandatory.</param>
    /// <exception cref="ArgumentNullException">A required reference or string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identifier is empty or invalid, a slot name violates PostgreSQL's exact replication-slot grammar,
    /// <paramref name="slotGeneration"/> is empty, oversized, or not a canonical non-secret ASCII identity, or
    /// complete before images are required without full replica identity.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="beforeImageRequirement"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public PostgresLogicalReplicationBinding(
        string publicationName,
        string slotName,
        string slotGeneration,
        PostgresLogicalReplicationReplicaIdentityBinding expectedReplicaIdentity,
        PostgresLogicalReplicationBeforeImageRequirement beforeImageRequirement =
            PostgresLogicalReplicationBeforeImageRequirement.NotRequired)
    {
        PublicationName = PostgresRelationQueryStorageBinding.RequireIdentifier(
            value: publicationName,
            parameterName: nameof(publicationName));
        SlotName = RequireSlotName(value: slotName, parameterName: nameof(slotName));
        SlotGeneration = RequireSlotGeneration(
            value: slotGeneration,
            parameterName: nameof(slotGeneration));
        ExpectedReplicaIdentity = Guard.RequireNotNull(expectedReplicaIdentity);
        if (!Enum.IsDefined(beforeImageRequirement))
        {
            throw new ArgumentOutOfRangeException(
                nameof(beforeImageRequirement),
                beforeImageRequirement,
                "Unsupported PostgreSQL before-image requirement.");
        }
        if (beforeImageRequirement == PostgresLogicalReplicationBeforeImageRequirement.Required
            && !expectedReplicaIdentity.ProvidesCompleteBeforeImage)
        {
            throw new ArgumentException(
                "Complete logical-replication before images require PostgreSQL REPLICA IDENTITY FULL.",
                nameof(expectedReplicaIdentity));
        }

        BeforeImageRequirement = beforeImageRequirement;
    }

    /// <summary>Exact PostgreSQL publication identifier.</summary>
    public string PublicationName { get; }

    /// <summary>Exact dedicated PostgreSQL logical-replication slot name.</summary>
    public string SlotName { get; }

    /// <summary>Operator-owned physical slot-incarnation identity.</summary>
    public string SlotGeneration { get; }

    /// <summary>Exact replica identity expected on the published table.</summary>
    public PostgresLogicalReplicationReplicaIdentityBinding ExpectedReplicaIdentity { get; }

    /// <summary>Whether complete update and delete before images are mandatory.</summary>
    public PostgresLogicalReplicationBeforeImageRequirement BeforeImageRequirement { get; }

    internal static string RequireSlotName(string value, string parameterName)
    {
        value = Guard.RequireNotNullOrWhiteSpace(value, parameterName);
        if (value.Length > PostgresSqlIdentifier.StandardMaxUtf8ByteLength
            || value.Any(static character => character is not (>= 'a' and <= 'z')
                && character is not (>= '0' and <= '9')
                && character != '_'))
        {
            throw new ArgumentException(
                "A PostgreSQL replication-slot name must contain only lowercase ASCII letters, digits, and underscores and cannot exceed 63 bytes.",
                parameterName);
        }

        return value;
    }

    static string RequireSlotGeneration(string value, string parameterName)
    {
        value = Guard.RequireNotNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaximumSlotGenerationCharacters
            || value.Any(static character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('/' or '.' or '_' or '-' or ':' or '@')))
        {
            throw new ArgumentException(
                $"A PostgreSQL slot generation must be a non-secret ASCII identity of at most {MaximumSlotGenerationCharacters} characters.",
                parameterName);
        }

        return value;
    }
}
