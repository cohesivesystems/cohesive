using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Exact portable schema version of canonical execution IR.
/// </summary>
/// <remarks>
/// The value is intentionally opaque. Compatibility is declared by exact identity rather than by
/// an implicit numeric range, so schema families can choose their own deterministic version syntax.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ExecutionIrSchemaVersion
{
    /// <summary>Creates an execution IR schema version.</summary>
    /// <param name="value">Exact portable schema-version identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ExecutionIrSchemaVersion(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Exact portable schema-version identity.</summary>
    public string Value { get; }

    /// <summary>Returns the exact portable schema-version identity.</summary>
    /// <returns>The value supplied when this schema version was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Deterministic declaration of the exact execution IR schema versions an interpreter supports.
/// </summary>
/// <remarks>
/// Versions are normalized into ordinal order for stable serialization. The declaration intentionally
/// has no range, minimum-version, or best-effort semantics: a version is supported only when its exact
/// identity appears in <see cref="SupportedSchemaVersions"/>.
/// </remarks>
public sealed record ExecutionIrSchemaCompatibilityDeclaration
{
    /// <summary>Creates an exact execution IR schema-compatibility declaration.</summary>
    /// <param name="supportedSchemaVersions">
    /// Non-empty set of exact portable schema versions supported by the declaring interpreter.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="supportedSchemaVersions"/> is default or empty, contains a default version,
    /// or contains a duplicate exact version.
    /// </exception>
    [JsonConstructor]
    public ExecutionIrSchemaCompatibilityDeclaration(
        ImmutableArray<ExecutionIrSchemaVersion> supportedSchemaVersions)
    {
        if (supportedSchemaVersions.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "At least one exact execution IR schema version must be declared.",
                nameof(supportedSchemaVersions));
        }

        var normalized = ImmutableArray.CreateBuilder<ExecutionIrSchemaVersion>(supportedSchemaVersions.Length);
        var observed = new HashSet<ExecutionIrSchemaVersion>();
        foreach (var schemaVersion in supportedSchemaVersions)
        {
            if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            {
                throw new ArgumentException(
                    "Execution IR schema compatibility cannot contain a default schema version.",
                    nameof(supportedSchemaVersions));
            }

            if (!observed.Add(schemaVersion))
            {
                throw new ArgumentException(
                    $"Execution IR schema version '{schemaVersion.Value}' is declared more than once.",
                    nameof(supportedSchemaVersions));
            }

            normalized.Add(schemaVersion);
        }

        normalized.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Value, right.Value));
        SupportedSchemaVersions = normalized.MoveToImmutable();
    }

    /// <summary>Exact supported schema versions in deterministic ordinal order.</summary>
    public ImmutableArray<ExecutionIrSchemaVersion> SupportedSchemaVersions { get; }

    /// <summary>Determines whether an exact schema version is declared as supported.</summary>
    /// <param name="schemaVersion">Exact schema version to test.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="schemaVersion"/> is present in the declaration;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is a default uninitialized value.
    /// </exception>
    public bool Supports(ExecutionIrSchemaVersion schemaVersion)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("A default execution IR schema version cannot be tested.", nameof(schemaVersion));

        var low = 0;
        var high = SupportedSchemaVersions.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = StringComparer.Ordinal.Compare(
                SupportedSchemaVersions[middle].Value,
                schemaVersion.Value);
            if (comparison == 0)
                return true;
            if (comparison < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return false;
    }

    /// <summary>Compares two declarations by their normalized exact version sets.</summary>
    /// <param name="other">Declaration to compare with this value.</param>
    /// <returns>
    /// <see langword="true"/> when both declarations contain the same exact versions;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool Equals(ExecutionIrSchemaCompatibilityDeclaration? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null || SupportedSchemaVersions.Length != other.SupportedSchemaVersions.Length)
            return false;

        for (var index = 0; index < SupportedSchemaVersions.Length; index++)
        {
            if (SupportedSchemaVersions[index] != other.SupportedSchemaVersions[index])
                return false;
        }

        return true;
    }

    /// <summary>Returns a structural hash code for the normalized exact version set.</summary>
    /// <returns>A hash code derived from each exact schema version in canonical order.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var schemaVersion in SupportedSchemaVersions)
            hash.Add(schemaVersion);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Versioned cryptographic fingerprint metadata for canonical execution-definition content.
/// </summary>
/// <remarks>
/// This type carries a computed fingerprint and its interpretation metadata. Canonicalization and
/// fingerprint computation are intentionally supplied by a separate interpretation.
/// </remarks>
public sealed record ExecutionDefinitionFingerprint
{
    /// <summary>Creates execution-definition fingerprint metadata.</summary>
    /// <param name="algorithm">Hash-algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization-profile identity.</param>
    /// <param name="value">Fingerprint value emitted by the named algorithm.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/>
    /// is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ExecutionDefinitionFingerprint(string algorithm, string canonicalization, string value)
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
}

/// <summary>
/// Required identity, revision, schema, fingerprint, and provenance metadata for a persisted
/// execution definition.
/// </summary>
public sealed record ExecutionDefinitionMetadata
{
    /// <summary>Creates persisted execution-definition metadata.</summary>
    /// <param name="definitionId">Stable identity shared by all revisions of the definition.</param>
    /// <param name="revisionId">Stable identity of this semantic revision.</param>
    /// <param name="schemaVersion">Portable schema version used by this definition.</param>
    /// <param name="fingerprint">Fingerprint of canonical semantic definition content.</param>
    /// <param name="provenance">Required producer and source attribution.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="definitionId"/>, <paramref name="revisionId"/>, or
    /// <paramref name="schemaVersion"/> is a default uninitialized value.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="fingerprint"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ExecutionDefinitionMetadata(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionFingerprint fingerprint,
        ExecutionProvenance provenance)
    {
        if (string.IsNullOrWhiteSpace(definitionId.Value))
            throw new ArgumentException("Definition metadata requires a non-default definition identity.", nameof(definitionId));
        if (string.IsNullOrWhiteSpace(revisionId.Value))
            throw new ArgumentException("Definition metadata requires a non-default revision identity.", nameof(revisionId));
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("Definition metadata requires a non-default IR schema version.", nameof(schemaVersion));

        DefinitionId = definitionId;
        RevisionId = revisionId;
        SchemaVersion = schemaVersion;
        Fingerprint = Guard.RequireNotNull(fingerprint);
        Provenance = Guard.RequireNotNull(provenance);
    }

    /// <summary>Stable identity shared by all revisions of the definition.</summary>
    public ExecutionDefinitionId DefinitionId { get; }

    /// <summary>Stable identity of this semantic revision.</summary>
    public ExecutionRevisionId RevisionId { get; }

    /// <summary>Portable schema version used by this definition.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Fingerprint of canonical semantic definition content.</summary>
    public ExecutionDefinitionFingerprint Fingerprint { get; }

    /// <summary>Required producer and source attribution.</summary>
    public ExecutionProvenance Provenance { get; }
}
