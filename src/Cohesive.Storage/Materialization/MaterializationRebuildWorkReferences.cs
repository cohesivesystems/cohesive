using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Portable exact reference to one persisted materialization rebuild plan.</summary>
/// <remarks>
/// The reference is the leaf component of <see cref="MaterializationRebuildLeafExecutionAuthority"/>. It deliberately
/// carries no runtime object, credential, or mutable generation identity.
/// </remarks>
public sealed record MaterializationRebuildPlanReference
{
    /// <summary>Current durable plan-reference wire schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-plan-reference/v2";

    /// <summary>Creates a current-version reference to one exact rebuild plan.</summary>
    /// <param name="plan">Exact persisted rebuild-plan fingerprint.</param>
    /// <param name="placementSlice">Exact placement-slice fingerprint carried by the plan.</param>
    /// <exception cref="ArgumentNullException">A required fingerprint is <see langword="null"/>.</exception>
    public MaterializationRebuildPlanReference(
        MaterializationRebuildPlanFingerprint plan,
        MaterializationPlacementSliceFingerprint placementSlice)
        : this(CurrentSchemaVersion, plan, placementSlice)
    {
    }

    /// <summary>Creates or deserializes one versioned exact rebuild-plan reference.</summary>
    /// <param name="schemaVersion">Exact durable reference schema.</param>
    /// <param name="plan">Exact persisted rebuild-plan fingerprint.</param>
    /// <param name="placementSlice">Exact placement-slice fingerprint carried by the plan.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="plan"/>, or <paramref name="placementSlice"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is empty, white space, or not the current schema.
    /// </exception>
    [JsonConstructor]
    public MaterializationRebuildPlanReference(
        string schemaVersion,
        MaterializationRebuildPlanFingerprint plan,
        MaterializationPlacementSliceFingerprint placementSlice)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Rebuild plan-reference schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        Plan = Guard.RequireNotNull(plan);
        PlacementSlice = placementSlice ?? throw new ArgumentNullException(nameof(placementSlice));
    }

    /// <summary>Exact durable reference schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact persisted rebuild-plan fingerprint.</summary>
    public MaterializationRebuildPlanFingerprint Plan { get; }

    /// <summary>Exact placement-slice authority carried by the persisted leaf plan.</summary>
    public MaterializationPlacementSliceFingerprint PlacementSlice { get; }

    /// <summary>Creates the exact durable reference to one constructor-verified rebuild leaf.</summary>
    /// <param name="plan">Canonical persisted rebuild leaf.</param>
    /// <returns>A reference fencing both leaf content and placement authority.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    public static MaterializationRebuildPlanReference FromPlan(MaterializationRebuildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(plan.Fingerprint, plan.PlacementSlice.Fingerprint);
    }
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
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-shard-work-reference/v2";

    /// <summary>Creates a current-version durable shard-work reference.</summary>
    /// <param name="authority">Exact linked plan-set, leaf-plan, and placement-slice authority.</param>
    /// <param name="attempt">Exact owning coordinator continuation and stable UTC start time.</param>
    /// <param name="shard">Stable shard identity within <paramref name="authority"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authority"/> or <paramref name="attempt"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="shard"/> is default.</exception>
    public MaterializationRebuildShardWorkReference(
        MaterializationRebuildLeafExecutionAuthority authority,
        MaterializationRebuildAttempt attempt,
        MaterializationRebuildShardId shard)
        : this(CurrentSchemaVersion, authority, attempt, shard)
    {
    }

    /// <summary>Creates or deserializes one versioned durable shard-work reference.</summary>
    /// <param name="schemaVersion">Exact durable work-reference schema.</param>
    /// <param name="authority">Exact linked plan-set, leaf-plan, and placement-slice authority.</param>
    /// <param name="attempt">Exact owning coordinator continuation and stable UTC start time.</param>
    /// <param name="shard">Stable shard identity within <paramref name="authority"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="authority"/>, or <paramref name="attempt"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is empty, white space, or not the current schema; or
    /// <paramref name="shard"/> is default.
    /// </exception>
    [JsonConstructor]
    public MaterializationRebuildShardWorkReference(
        string schemaVersion,
        MaterializationRebuildLeafExecutionAuthority authority,
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

        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        Attempt = Guard.RequireNotNull(attempt);
        MaterializationContract.RequireDefinedIdentity(shard.Value, nameof(shard));
        Shard = shard;
    }

    /// <summary>Exact durable work-reference schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact linked plan-set, leaf-plan, and full placement-slice execution authority.</summary>
    public MaterializationRebuildLeafExecutionAuthority Authority { get; }

    /// <summary>Exact persisted rebuild-plan fingerprint projected from <see cref="Authority"/>.</summary>
    [JsonIgnore]
    public MaterializationRebuildPlanFingerprint Plan => Authority.LeafPlan.Plan;

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
    /// <returns>Canonical compact JSON suitable for embedding in a durable planning or execution authority.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reference"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The reference cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The reference has no canonical JSON representation.</exception>
    public static string SerializePlan(MaterializationRebuildPlanReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(reference, Options));
    }

    /// <summary>Serializes one exact linked leaf execution authority to deterministic compact JSON.</summary>
    /// <param name="authority">Execution authority to encode.</param>
    /// <returns>Canonical compact JSON suitable for the coordinator Process String input.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="authority"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The authority cannot be serialized under its strict wire contract.</exception>
    /// <exception cref="NotSupportedException">A contained value has no supported JSON representation.</exception>
    /// <exception cref="InvalidOperationException">The authority has no canonical JSON representation.</exception>
    public static string SerializeAuthority(MaterializationRebuildLeafExecutionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(authority, Options));
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

    /// <summary>Deserializes one current-version internally consistent linked-leaf execution claim.</summary>
    /// <param name="json">Canonical execution-authority JSON.</param>
    /// <returns>The self-consistent claim, which must still be resolved against its full plan set before target I/O.</returns>
    /// <exception cref="JsonException">The JSON is invalid, open, noncanonical, or uses an unsupported schema.</exception>
    public static MaterializationRebuildLeafExecutionAuthority DeserializeAuthority(string json)
    {
        if (!TryDeserializeAuthority(json, out var authority, out var error) || authority is null)
            throw new JsonException(error.Message);
        return authority;
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

    internal static bool TryDeserializeAuthority(
        string json,
        out MaterializationRebuildLeafExecutionAuthority? authority,
        out StrictDocumentJsonReadError error) => StrictDocumentJson.TryReadCanonicalObject(
            json,
            Options,
            "materialization rebuild leaf execution authority",
            out authority,
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
