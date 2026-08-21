using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra;

/// <summary>Versioned deterministic fingerprint of one canonical infrastructure definition.</summary>
/// <remarks>
/// The current fingerprint identifies the exact definition identity, revision, schema, and normalized semantic
/// topology. A topology-only comparison, if introduced later, must use a separately named semantic-content digest.
/// </remarks>
public sealed record InfrastructureDefinitionFingerprint
{
    /// <summary>Cryptographic hash algorithm used by the current infrastructure definition profile.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current infrastructure definition fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infrastructure-definition/v1-c14n/v1";

    /// <summary>Creates infrastructure-definition fingerprint metadata.</summary>
    /// <param name="algorithm">Hash-algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization-profile identity.</param>
    /// <param name="value">Fingerprint value emitted by the named algorithm.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureDefinitionFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash-algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization-profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Fingerprint value emitted by <see cref="Algorithm"/>.</summary>
    public string Value { get; }

    /// <summary>Computes the current deterministic fingerprint for a canonical infrastructure definition.</summary>
    /// <param name="definition">Normalized canonical infrastructure definition.</param>
    /// <returns>SHA-256 metadata fencing the current schema and normalized semantic topology.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Definition content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    public static InfrastructureDefinitionFingerprint Compute(InfrastructureDefinition definition) =>
        InfrastructureDefinitionFingerprinting.Compute(
            InfrastructureDefinitionDocument.CurrentSchemaVersion,
            definition);
}

/// <summary>Portable envelope fencing one canonical infrastructure definition with exact content integrity.</summary>
public sealed record InfrastructureDefinitionDocument
{
    /// <summary>Current portable infrastructure-definition document schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-infrastructure/v1";

    /// <summary>Creates or restores an exactly fingerprinted infrastructure-definition document.</summary>
    /// <param name="schemaVersion">Exact portable infrastructure schema version.</param>
    /// <param name="definition">Canonical provider-neutral infrastructure definition.</param>
    /// <param name="fingerprint">Persisted exact definition fingerprint.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="definition"/>, or <paramref name="fingerprint"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is empty or unsupported, or <paramref name="fingerprint"/> does not match
    /// the canonical schema and definition content.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">Definition content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    [JsonConstructor]
    public InfrastructureDefinitionDocument(
        string schemaVersion,
        InfrastructureDefinition definition,
        InfrastructureDefinitionFingerprint fingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Infrastructure schema '{SchemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        Definition = Guard.RequireNotNull(definition);
        Fingerprint = Guard.RequireNotNull(fingerprint);
        var computed = InfrastructureDefinitionFingerprinting.Compute(SchemaVersion, Definition);
        if (Fingerprint != computed)
        {
            throw new ArgumentException(
                "The supplied infrastructure-definition fingerprint does not match canonical content.",
                nameof(fingerprint));
        }
    }

    /// <summary>Exact portable infrastructure-definition schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Canonical provider-neutral infrastructure definition.</summary>
    public InfrastructureDefinition Definition { get; }

    /// <summary>
    /// Deterministic fingerprint of the exact schema, definition identity, revision, and canonical semantic topology.
    /// </summary>
    public InfrastructureDefinitionFingerprint Fingerprint { get; }

    /// <summary>Fences a canonical infrastructure definition in a current-version portable document.</summary>
    /// <param name="definition">Definition to persist.</param>
    /// <returns>A current-version document with a computed deterministic SHA-256 fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">Definition content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    public static InfrastructureDefinitionDocument FromDefinition(InfrastructureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(
            CurrentSchemaVersion,
            definition,
            InfrastructureDefinitionFingerprinting.Compute(CurrentSchemaVersion, definition));
    }
}

static class InfrastructureDefinitionFingerprinting
{
    internal static InfrastructureDefinitionFingerprint Compute(
        string schemaVersion,
        InfrastructureDefinition definition)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        ArgumentNullException.ThrowIfNull(definition);
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                schemaVersion,
                definition.Id,
                definition.Revision,
                definition.Workloads,
                definition.Resources,
                definition.Bindings),
            StrictDocumentJson.CreateOptions());
        return new(
            InfrastructureDefinitionFingerprint.CurrentAlgorithm,
            InfrastructureDefinitionFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        InfrastructureDefinitionId Id,
        InfrastructureRevisionId Revision,
        ImmutableArray<InfrastructureWorkloadDefinition> Workloads,
        ImmutableArray<InfrastructureResourceDefinition> Resources,
        ImmutableArray<InfrastructureBindingDefinition> Bindings);
}
