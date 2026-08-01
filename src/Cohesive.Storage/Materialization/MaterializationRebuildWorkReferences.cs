using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Portable exact reference to one persisted materialization rebuild plan.</summary>
/// <remarks>
/// The reference is the coordinator Process input. It deliberately carries no runtime object, credential, or
/// mutable generation identity; each coordinator attempt resolves it against current exact runtime bindings.
/// </remarks>
public sealed record MaterializationRebuildPlanReference
{
    /// <summary>Current durable plan-reference wire schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-plan-reference/v1";

    /// <summary>Creates a current-version reference to one exact rebuild plan.</summary>
    /// <param name="plan">Exact persisted rebuild-plan fingerprint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public MaterializationRebuildPlanReference(MaterializationRebuildPlanFingerprint plan)
        : this(CurrentSchemaVersion, plan)
    {
    }

    /// <summary>Creates or deserializes one versioned exact rebuild-plan reference.</summary>
    /// <param name="schemaVersion">Exact durable reference schema.</param>
    /// <param name="plan">Exact persisted rebuild-plan fingerprint.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/> or <paramref name="plan"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is empty, white space, or not the current schema.
    /// </exception>
    [JsonConstructor]
    public MaterializationRebuildPlanReference(
        string schemaVersion,
        MaterializationRebuildPlanFingerprint plan)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Rebuild plan-reference schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        Plan = Guard.RequireNotNull(plan);
    }

    /// <summary>Exact durable reference schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact persisted rebuild-plan fingerprint.</summary>
    public MaterializationRebuildPlanFingerprint Plan { get; }
}

/// <summary>Portable durable identity of one attempt-bound materialization rebuild shard operation.</summary>
/// <remarks>
/// Initialization creates these references only after the current coordinator attempt has been established. A
/// restarted coordinator therefore emits references for its replacement attempt and generation, while retries and
/// reconciliation of one retained Request continue to address the original exact attempt and shard.
/// </remarks>
public sealed record MaterializationRebuildShardWorkReference
{
    /// <summary>Current durable shard-work reference wire schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-shard-work-reference/v1";

    /// <summary>Creates a current-version durable shard-work reference.</summary>
    /// <param name="plan">Exact persisted rebuild-plan fingerprint.</param>
    /// <param name="attempt">Exact owning coordinator continuation and stable UTC start time.</param>
    /// <param name="shard">Stable shard identity within <paramref name="plan"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/> or <paramref name="attempt"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="shard"/> is default.</exception>
    public MaterializationRebuildShardWorkReference(
        MaterializationRebuildPlanFingerprint plan,
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildShardId shard)
        : this(CurrentSchemaVersion, plan, attempt, shard)
    {
    }

    /// <summary>Creates or deserializes one versioned durable shard-work reference.</summary>
    /// <param name="schemaVersion">Exact durable work-reference schema.</param>
    /// <param name="plan">Exact persisted rebuild-plan fingerprint.</param>
    /// <param name="attempt">Exact owning coordinator continuation and stable UTC start time.</param>
    /// <param name="shard">Stable shard identity within <paramref name="plan"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="plan"/>, or <paramref name="attempt"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is empty, white space, or not the current schema; or
    /// <paramref name="shard"/> is default.
    /// </exception>
    [JsonConstructor]
    public MaterializationRebuildShardWorkReference(
        string schemaVersion,
        MaterializationRebuildPlanFingerprint plan,
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildShardId shard)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Rebuild shard-work schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        Plan = Guard.RequireNotNull(plan);
        Attempt = Guard.RequireNotNull(attempt);
        MaterializationContract.RequireDefinedIdentity(shard.Value, nameof(shard));
        Shard = shard;
    }

    /// <summary>Exact durable work-reference schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact persisted rebuild-plan fingerprint.</summary>
    public MaterializationRebuildPlanFingerprint Plan { get; }

    /// <summary>Exact owning coordinator continuation and stable UTC start time.</summary>
    public MaterializationRebuildAttempt Attempt { get; }

    /// <summary>Stable shard identity within <see cref="Plan"/>.</summary>
    public MaterializationRebuildShardId Shard { get; }
}

/// <summary>Strict canonical JSON codec for durable materialization rebuild work references.</summary>
public static class MaterializationRebuildWorkReferenceJsonSerializer
{
    static readonly JsonSerializerOptions Options = StrictDocumentJson.CreateOptions();

    /// <summary>Serializes one exact plan reference to deterministic compact JSON.</summary>
    /// <param name="reference">Plan reference to encode.</param>
    /// <returns>Canonical compact JSON suitable for the coordinator Process String input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The reference cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The reference has no canonical JSON representation.</exception>
    public static string SerializePlan(MaterializationRebuildPlanReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(reference, Options));
    }

    /// <summary>Serializes one attempt-bound shard reference to deterministic compact JSON.</summary>
    /// <param name="reference">Shard-work reference to encode.</param>
    /// <returns>Canonical compact JSON suitable for a worker Process String input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The reference cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The reference has no canonical JSON representation.</exception>
    public static string SerializeShard(MaterializationRebuildShardWorkReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(reference, Options));
    }

    /// <summary>Deserializes and verifies one current-version exact rebuild-plan reference.</summary>
    /// <param name="json">Canonical plan-reference JSON.</param>
    /// <returns>The validated exact plan reference.</returns>
    /// <exception cref="JsonException">The JSON is invalid, open, noncanonical, or uses an unsupported schema.</exception>
    public static MaterializationRebuildPlanReference DeserializePlan(string json)
    {
        if (!TryDeserializePlan(json, out var reference, out var error) || reference is null)
            throw new JsonException(error.Message);
        return reference;
    }

    /// <summary>Deserializes and verifies one current-version attempt-bound shard-work reference.</summary>
    /// <param name="json">Canonical shard-work JSON.</param>
    /// <returns>The validated attempt-bound shard-work reference.</returns>
    /// <exception cref="JsonException">The JSON is invalid, open, noncanonical, or uses an unsupported schema.</exception>
    public static MaterializationRebuildShardWorkReference DeserializeShard(string json)
    {
        if (!TryDeserializeShard(json, out var reference, out var error) || reference is null)
            throw new JsonException(error.Message);
        return reference;
    }

    internal static bool TryDeserializePlan(
        string json,
        out MaterializationRebuildPlanReference? reference,
        out StrictDocumentJsonReadError error) => StrictDocumentJson.TryReadCanonicalObject(
            json,
            Options,
            "materialization rebuild plan reference",
            out reference,
            out error);

    internal static bool TryDeserializeShard(
        string json,
        out MaterializationRebuildShardWorkReference? reference,
        out StrictDocumentJsonReadError error) => StrictDocumentJson.TryReadCanonicalObject(
            json,
            Options,
            "materialization rebuild shard-work reference",
            out reference,
            out error);
}
