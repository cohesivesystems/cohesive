using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable identity of one declared pool of interchangeable materialization targets.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationBackendPoolId
{
    /// <summary>Creates a materialization backend-pool identity.</summary>
    /// <param name="value">Stable identity independent of a selected target or runtime route.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty, white-space, or contains ill-formed Unicode.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendPoolId(string value) =>
        Value = MaterializationContract.RequireUnicodeIdentity(value, nameof(value));

    /// <summary>Gets the stable backend-pool identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable backend-pool identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Exact portable reference to one canonical materialization backend-pool definition.</summary>
public sealed record MaterializationBackendPoolReference
{
    /// <summary>Current durable backend-pool reference schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-backend-pool-reference/v1";

    /// <summary>Creates or deserializes one exact backend-pool reference.</summary>
    /// <param name="schemaVersion">Exact durable reference schema.</param>
    /// <param name="pool">Stable backend-pool identity.</param>
    /// <param name="materialization">Exact materialization definition served by the pool.</param>
    /// <param name="definitionFingerprint">Fingerprint of the complete canonical pool definition.</param>
    /// <exception cref="ArgumentNullException">A required reference component is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema or pool identity is invalid.</exception>
    [JsonConstructor]
    public MaterializationBackendPoolReference(
        string schemaVersion,
        MaterializationBackendPoolId pool,
        MaterializationDefinitionReference materialization,
        ExecutionDefinitionFingerprint definitionFingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Backend-pool reference schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        MaterializationContract.RequireDefinedIdentity(pool.Value, nameof(pool));
        Pool = pool;
        Materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));
        DefinitionFingerprint = definitionFingerprint ?? throw new ArgumentNullException(nameof(definitionFingerprint));
    }

    /// <summary>Exact durable backend-pool reference schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable backend-pool identity.</summary>
    public MaterializationBackendPoolId Pool { get; }

    /// <summary>Exact materialization definition served by the pool.</summary>
    public MaterializationDefinitionReference Materialization { get; }

    /// <summary>Fingerprint of the complete canonical pool definition.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Creates an exact reference to a verified backend-pool document.</summary>
    /// <param name="document">Canonical backend-pool document.</param>
    /// <returns>A reference fencing the pool identity, materialization definition, and pool definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static MaterializationBackendPoolReference FromDocument(MaterializationBackendPoolDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(
            CurrentSchemaVersion,
            document.Definition.Id,
            new(
                MaterializationDefinitionReference.CurrentSchemaVersion,
                document.Definition.MaterializationId,
                document.Definition.DefinitionFingerprint),
            document.DefinitionFingerprint);
    }
}

/// <summary>Canonical static IR declaring the targets available to one materialization backend pool.</summary>
/// <remarks>
/// This definition owns pool membership and the optional safe framework-default target. Runtime read/write
/// selection, generation lifecycle, revisions, and fences are separate mutable interpretations of this static IR.
/// </remarks>
public sealed record MaterializationBackendPoolDefinition
{
    /// <summary>Creates a canonical materialization backend-pool definition.</summary>
    /// <param name="id">Stable pool identity.</param>
    /// <param name="materializationId">Logical materialization served by every pool member.</param>
    /// <param name="definitionFingerprint">Exact canonical materialization-definition fingerprint.</param>
    /// <param name="members">Complete target descriptors available for exact dependency resolution.</param>
    /// <param name="defaultTarget">
    /// Optional safe framework-default target; <see langword="null"/> requires higher-precedence routing settings.
    /// </param>
    /// <param name="provenance">Required producer and source attribution for the pool declaration.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definitionFingerprint"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity is default; members are absent, null, duplicated, or belong to another materialization; or the
    /// supplied default target is not a declared member.
    /// </exception>
    [JsonConstructor]
    public MaterializationBackendPoolDefinition(
        MaterializationBackendPoolId id,
        MaterializationId materializationId,
        ExecutionDefinitionFingerprint definitionFingerprint,
        ImmutableArray<MaterializationTargetDescriptor> members,
        MaterializationTargetId? defaultTarget,
        ExecutionProvenance provenance)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        MaterializationContract.RequireDefinedIdentity(materializationId.Value, nameof(materializationId));
        Id = id;
        MaterializationId = materializationId;
        DefinitionFingerprint = definitionFingerprint ?? throw new ArgumentNullException(nameof(definitionFingerprint));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));

        if (members.IsDefaultOrEmpty || members.Any(static member => member is null))
        {
            throw new ArgumentException(
                "A materialization backend pool requires one or more non-null target descriptors.",
                nameof(members));
        }

        var identities = new HashSet<MaterializationTargetId>();
        var isCanonical = true;
        for (var index = 0; index < members.Length; index++)
        {
            var member = members[index];
            if (member.MaterializationId != materializationId)
            {
                throw new ArgumentException(
                    "Every backend-pool member must serve the pool's exact materialization identity.",
                    nameof(members));
            }

            if (!identities.Add(member.Id))
            {
                throw new ArgumentException(
                    "A materialization backend pool cannot repeat a target identity.",
                    nameof(members));
            }

            if (index > 0
                && string.Compare(members[index - 1].Id.Value, member.Id.Value, StringComparison.Ordinal) > 0)
            {
                isCanonical = false;
            }
        }

        if (defaultTarget is { } selectedDefault)
        {
            MaterializationContract.RequireDefinedIdentity(selectedDefault.Value, nameof(defaultTarget));
            if (!identities.Contains(selectedDefault))
            {
                throw new ArgumentException(
                    "A backend-pool default target must name one exact declared member.",
                    nameof(defaultTarget));
            }
        }

        if (isCanonical)
        {
            Members = members;
        }
        else
        {
            var normalized = ImmutableArray.CreateBuilder<MaterializationTargetDescriptor>(members.Length);
            normalized.AddRange(members);
            normalized.Sort(static (left, right) =>
                string.Compare(left.Id.Value, right.Id.Value, StringComparison.Ordinal));
            Members = normalized.MoveToImmutable();
        }

        DefaultTarget = defaultTarget;
    }

    /// <summary>Stable pool identity.</summary>
    public MaterializationBackendPoolId Id { get; }

    /// <summary>Logical materialization served by every pool member.</summary>
    public MaterializationId MaterializationId { get; }

    /// <summary>Exact canonical materialization-definition fingerprint shared by pool generations.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Complete target descriptors in canonical target-identity order.</summary>
    public ImmutableArray<MaterializationTargetDescriptor> Members { get; }

    /// <summary>Optional safe framework-default target, or <see langword="null"/> when none is declared.</summary>
    public MaterializationTargetId? DefaultTarget { get; }

    /// <summary>Required producer and source attribution for this pool declaration.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Compares definitions structurally, including canonical members and provenance.</summary>
    /// <param name="other">Definition to compare.</param>
    /// <returns><see langword="true"/> when every semantic field is equal.</returns>
    public bool Equals(MaterializationBackendPoolDefinition? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && MaterializationBackendPoolFingerprinter.Compute(this)
            == MaterializationBackendPoolFingerprinter.Compute(other);

    /// <summary>Returns a structural hash code for every semantic field.</summary>
    /// <returns>A hash derived from pool identity, materialization, members, default, and provenance.</returns>
    public override int GetHashCode() => MaterializationBackendPoolFingerprinter.Compute(this).GetHashCode();
}

/// <summary>Portable envelope fencing one canonical materialization backend-pool definition.</summary>
public sealed record MaterializationBackendPoolDocument
{
    /// <summary>Current portable backend-pool document schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-backend-pool/v1";

    /// <summary>Creates a materialization backend-pool document.</summary>
    /// <param name="schemaVersion">Exact portable backend-pool schema version.</param>
    /// <param name="definition">Canonical backend-pool definition.</param>
    /// <param name="definitionFingerprint">Fingerprint of the complete canonical pool definition.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="definition"/>, or
    /// <paramref name="definitionFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is empty, unsupported, or the supplied fingerprint differs from the
    /// canonical definition content.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">Definition content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    [JsonConstructor]
    public MaterializationBackendPoolDocument(
        string schemaVersion,
        MaterializationBackendPoolDefinition definition,
        ExecutionDefinitionFingerprint definitionFingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        DefinitionFingerprint = definitionFingerprint ?? throw new ArgumentNullException(nameof(definitionFingerprint));
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported materialization backend-pool schema version '{schemaVersion}'.",
                nameof(schemaVersion));
        }

        var expectedFingerprint = MaterializationBackendPoolFingerprinter.Compute(definition);
        if (definitionFingerprint != expectedFingerprint)
        {
            throw new ArgumentException(
                "The backend-pool definition fingerprint does not match canonical content.",
                nameof(definitionFingerprint));
        }
    }

    /// <summary>Exact portable backend-pool schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Canonical backend-pool definition.</summary>
    public MaterializationBackendPoolDefinition Definition { get; }

    /// <summary>Fingerprint of the complete canonical backend-pool definition.</summary>
    public ExecutionDefinitionFingerprint DefinitionFingerprint { get; }

    /// <summary>Fences one canonical backend-pool definition with its current schema and content fingerprint.</summary>
    /// <param name="definition">Canonical backend-pool definition to persist.</param>
    /// <returns>A current-version document containing the exact normalized definition.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Definition content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    public static MaterializationBackendPoolDocument FromDefinition(
        MaterializationBackendPoolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(CurrentSchemaVersion, definition, MaterializationBackendPoolFingerprinter.Compute(definition));
    }
}

/// <summary>Computes an exact portable-content fence for a canonical backend-pool definition.</summary>
public static class MaterializationBackendPoolFingerprinter
{
    /// <summary>Cryptographic hash algorithm used by the v1 profile.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by the v1 backend-pool definition fence.</summary>
    public const string Canonicalization = "cohesive-materialization-backend-pool/v1-c14n/v1";

    /// <summary>Computes the fingerprint of every canonical pool declaration and provenance field.</summary>
    /// <param name="definition">Canonical backend-pool definition to fingerprint.</param>
    /// <returns>Versioned SHA-256 metadata fencing the complete definition content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Definition content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    public static ExecutionDefinitionFingerprint Compute(MaterializationBackendPoolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                SchemaVersion: MaterializationBackendPoolDocument.CurrentSchemaVersion,
                Id: definition.Id,
                MaterializationId: definition.MaterializationId,
                MaterializationDefinitionFingerprint: definition.DefinitionFingerprint,
                Members: definition.Members,
                DefaultTarget: definition.DefaultTarget,
                Provenance: definition.Provenance),
            MaterializationJsonSerializer.CreateOptions());
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        MaterializationBackendPoolId Id,
        MaterializationId MaterializationId,
        ExecutionDefinitionFingerprint MaterializationDefinitionFingerprint,
        ImmutableArray<MaterializationTargetDescriptor> Members,
        MaterializationTargetId? DefaultTarget,
        ExecutionProvenance Provenance);
}
