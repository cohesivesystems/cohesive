using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// One exact-versioned semantic extension attached to a canonical execution definition.
/// </summary>
/// <remarks>
/// Extension payloads use <see cref="PortableValue"/> so an interpreter can validate their
/// type and value semantics without loading producer CLR types. Every extension contributes to
/// the semantic content fingerprint and therefore must be explicitly understood before activation.
/// For a <see cref="PortableValueState.Failed"/> value, the fingerprint retains the semantic state,
/// contract, and stable machine-readable failure code while excluding diagnostic prose and locations.
/// </remarks>
public sealed record ExecutionDefinitionExtension
{
    /// <summary>Creates an exact-versioned execution-definition extension.</summary>
    /// <param name="id">Stable extension identity.</param>
    /// <param name="schemaVersion">Exact portable schema version of the extension payload.</param>
    /// <param name="value">Typed portable extension payload.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="schemaVersion"/> is a default uninitialized value.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ExecutionDefinitionExtension(
        ExecutionExtensionId id,
        ExecutionExtensionSchemaVersion schemaVersion,
        PortableValue value)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An execution extension requires a non-default identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
        {
            throw new ArgumentException(
                "An execution extension requires a non-default schema version.",
                nameof(schemaVersion));
        }

        Id = id;
        SchemaVersion = schemaVersion;
        Value = Guard.RequireNotNull(value);
    }

    /// <summary>Stable extension identity.</summary>
    public ExecutionExtensionId Id { get; }

    /// <summary>Exact portable schema version of the extension payload.</summary>
    public ExecutionExtensionSchemaVersion SchemaVersion { get; }

    /// <summary>Typed portable extension payload.</summary>
    public PortableValue Value { get; }
}

/// <summary>
/// Portable, versioned envelope for one canonical execution definition.
/// </summary>
/// <remarks>
/// <para>
/// The normalized definition payload is the persisted semantic authority. It remains independently
/// inspectable and can be dispatched by schema and kind before the original producer assembly is
/// available. Block-specific CLR types are producers and projections of this canonical IR, not a
/// parallel source of truth.
/// </para>
/// <para>
/// Object properties and exact decimal-rational JSON numbers are normalized by this shared boundary.
/// Arrays are always order-bearing here; a block producer must normalize any kind-specific set-like
/// collection before materializing the document.
/// </para>
/// <para>
/// Semantic fingerprints cover the schema version, definition kind, canonical definition payload,
/// and extensions. Definition and revision identities, descriptive metadata, provenance, source maps,
/// retained metadata diagnostics, and failed-value diagnostic prose and locations remain durable in the
/// document but do not affect semantic content identity. A failed extension's state and machine-readable
/// diagnostic code remain semantic.
/// </para>
/// </remarks>
public sealed record ExecutionDefinitionDocument
{
    /// <summary>Current shared execution-definition document schema version.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } = new("cohesive-execution/v2");

    /// <summary>Creates a portable execution-definition document.</summary>
    /// <param name="kind">Stable semantic family of the definition payload.</param>
    /// <param name="metadata">Required definition identity, revision, fingerprint, and attribution.</param>
    /// <param name="definition">Canonical block-specific definition encoded as a JSON object.</param>
    /// <param name="extensions">Exact-versioned semantic extensions attached to the definition.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="kind"/> is default; <paramref name="definition"/> is not a JSON object or contains
    /// duplicate properties; or
    /// <paramref name="extensions"/> contains a <see langword="null"/> entry, default identity or
    /// version, or duplicate extension identity.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="definition"/> cannot be normalized as canonical JSON.
    /// </exception>
    /// <exception cref="JsonException"><paramref name="definition"/> contains invalid JSON content.</exception>
    [JsonConstructor]
    public ExecutionDefinitionDocument(
        ExecutionDefinitionKind kind,
        ExecutionDefinitionMetadata metadata,
        JsonElement definition,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default)
        : this(
            kind,
            metadata,
            (
                Definition: ExecutionDefinitionFingerprinter.NormalizeDefinition(definition),
                Extensions: NormalizeExtensions(extensions)))
    {
    }

    ExecutionDefinitionDocument(
        ExecutionDefinitionKind kind,
        ExecutionDefinitionMetadata metadata,
        (JsonElement Definition, ImmutableArray<ExecutionDefinitionExtension> Extensions) canonicalContent)
    {
        if (string.IsNullOrWhiteSpace(kind.Value))
            throw new ArgumentException("An execution definition requires a non-default kind.", nameof(kind));

        Kind = kind;
        Metadata = Guard.RequireNotNull(metadata);
        Definition = canonicalContent.Definition;
        Extensions = canonicalContent.Extensions;
    }

    /// <summary>Stable semantic family of the definition payload.</summary>
    public ExecutionDefinitionKind Kind { get; }

    /// <summary>Required definition identity, revision, fingerprint, and attribution.</summary>
    public ExecutionDefinitionMetadata Metadata { get; }

    /// <summary>Immutable JSON representation of the canonical block-specific definition.</summary>
    public JsonElement Definition { get; }

    /// <summary>Semantic extensions in deterministic extension-identity order.</summary>
    public ImmutableArray<ExecutionDefinitionExtension> Extensions { get; }

    /// <summary>
    /// Projects a typed canonical definition into a current shared execution-definition document.
    /// </summary>
    /// <typeparam name="TDefinition">Portable block-specific definition type.</typeparam>
    /// <param name="kind">Stable semantic family of the definition.</param>
    /// <param name="definitionId">Stable identity shared by all revisions of the definition.</param>
    /// <param name="revisionId">Stable identity of this accepted semantic revision.</param>
    /// <param name="definition">Typed block-specific canonical definition.</param>
    /// <param name="provenance">Required producer and root source attribution.</param>
    /// <param name="extensions">Optional exact-versioned semantic extensions.</param>
    /// <param name="displayName">Optional human-facing name excluded from semantic fingerprinting.</param>
    /// <param name="description">Optional human-facing description excluded from semantic fingerprinting.</param>
    /// <param name="sourceMap">Optional normalized per-construct source map.</param>
    /// <param name="diagnostics">Optional persisted authoring or validation diagnostics.</param>
    /// <returns>A fingerprinted current-version portable execution-definition document.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, the typed definition does not serialize as a JSON object or contains
    /// duplicate properties, or an extension or metadata value violates its structural contract.
    /// </exception>
    /// <exception cref="JsonException">The typed definition cannot be encoded using the strict wire contract.</exception>
    /// <exception cref="InvalidOperationException">The typed definition has no canonical JSON representation.</exception>
    /// <exception cref="NotSupportedException">The typed definition contains an unsupported runtime type.</exception>
    public static ExecutionDefinitionDocument Create<TDefinition>(
        ExecutionDefinitionKind kind,
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        TDefinition definition,
        ExecutionProvenance provenance,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default,
        string? displayName = null,
        string? description = null,
        ExecutionSourceMap? sourceMap = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(provenance);

        var normalizedExtensions = NormalizeExtensions(extensions);
        var definitionElement = ExecutionDefinitionFingerprinter.NormalizeDefinition(
            JsonSerializer.SerializeToElement(
                definition,
                ExecutionDefinitionJsonSerializer.CreateOptions()));

        var fingerprint = ExecutionDefinitionFingerprinter.ComputeNormalized(
            CurrentSchemaVersion,
            kind,
            definitionElement,
            normalizedExtensions);
        var metadata = new ExecutionDefinitionMetadata(
            definitionId,
            revisionId,
            CurrentSchemaVersion,
            fingerprint,
            provenance,
            displayName,
            description,
            sourceMap,
            diagnostics);
        return new(
            kind,
            metadata,
            (Definition: definitionElement, Extensions: normalizedExtensions));
    }

    /// <summary>Deserializes the canonical payload as a block-specific definition type.</summary>
    /// <typeparam name="TDefinition">Portable block-specific definition type.</typeparam>
    /// <returns>The typed canonical definition represented by <see cref="Definition"/>.</returns>
    /// <exception cref="JsonException">
    /// The payload cannot be decoded as <typeparamref name="TDefinition"/> or produces a null value.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TDefinition"/> is not supported by the strict execution JSON contract.
    /// </exception>
    public TDefinition GetDefinition<TDefinition>() =>
        ExecutionDefinitionJsonSerializer.DeserializeDefinition<TDefinition>(this);

    /// <summary>Compares documents by normalized persisted content.</summary>
    /// <param name="other">Document to compare with this value.</param>
    /// <returns>
    /// <see langword="true"/> when metadata, canonical definition content, and extensions are equal;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool Equals(ExecutionDefinitionDocument? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null
            || Kind != other.Kind
            || Metadata != other.Metadata
            || Extensions.Length != other.Extensions.Length
            || !string.Equals(Definition.GetRawText(), other.Definition.GetRawText(), StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = 0; index < Extensions.Length; index++)
        {
            if (Extensions[index] != other.Extensions[index])
                return false;
        }

        return true;
    }

    /// <summary>Returns a structural hash code for normalized persisted content.</summary>
    /// <returns>A hash code derived from metadata, canonical definition content, and extensions.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(Metadata);
        hash.Add(Definition.GetRawText(), StringComparer.Ordinal);
        foreach (var extension in Extensions)
            hash.Add(extension);
        return hash.ToHashCode();
    }

    internal static ImmutableArray<ExecutionDefinitionExtension> NormalizeExtensions(
        ImmutableArray<ExecutionDefinitionExtension> extensions)
    {
        if (extensions.IsDefaultOrEmpty)
            return [];

        var isCanonical = true;
        string? previousId = null;
        foreach (var extension in extensions)
        {
            if (extension is null)
                throw new ArgumentException("Execution extensions cannot contain null entries.", nameof(extensions));
            if (string.IsNullOrWhiteSpace(extension.Id.Value)
                || string.IsNullOrWhiteSpace(extension.SchemaVersion.Value))
            {
                throw new ArgumentException(
                    "Execution extensions require non-default identities and schema versions.",
                    nameof(extensions));
            }

            if (previousId is not null)
            {
                var comparison = StringComparer.Ordinal.Compare(previousId, extension.Id.Value);
                if (comparison == 0)
                {
                    throw new ArgumentException(
                        $"Execution extension '{extension.Id.Value}' is declared more than once.",
                        nameof(extensions));
                }

                isCanonical &= comparison < 0;
            }

            previousId = extension.Id.Value;
        }

        if (isCanonical)
            return extensions;

        HashSet<ExecutionExtensionId> observed = [];
        foreach (var extension in extensions)
        {
            if (!observed.Add(extension.Id))
            {
                throw new ArgumentException(
                    $"Execution extension '{extension.Id.Value}' is declared more than once.",
                    nameof(extensions));
            }
        }

        var normalized = ImmutableArray.CreateBuilder<ExecutionDefinitionExtension>(extensions.Length);
        normalized.AddRange(extensions);
        normalized.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value));
        return normalized.MoveToImmutable();
    }
}
