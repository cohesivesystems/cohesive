using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Stable identity of an execution-definition family.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ExecutionDefinitionKind
{
    /// <summary>Creates an execution-definition kind.</summary>
    /// <param name="value">
    /// Stable family identity, such as an entity transition, process, or materialization definition kind.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ExecutionDefinitionKind(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Stable execution-definition family identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable execution-definition family identity.</summary>
    /// <returns>The value supplied when this kind was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity of an explicitly declared execution IR extension.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ExecutionExtensionId
{
    /// <summary>Creates an execution-extension identity.</summary>
    /// <param name="value">Stable extension identity assigned by the extension authority.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ExecutionExtensionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Stable extension identity assigned by the extension authority.</summary>
    public string Value { get; }

    /// <summary>Returns the stable extension identity.</summary>
    /// <returns>The value supplied when this identity was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Exact portable schema version of one execution IR extension.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ExecutionExtensionSchemaVersion
{
    /// <summary>Creates an exact execution-extension schema version.</summary>
    /// <param name="value">Opaque exact schema-version identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is empty or consists only of white-space characters.
    /// </exception>
    [JsonConstructor]
    public ExecutionExtensionSchemaVersion(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Opaque exact schema-version identity.</summary>
    public string Value { get; }

    /// <summary>Returns the exact extension schema-version identity.</summary>
    /// <returns>The value supplied when this schema version was constructed.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Exact definition revision and content fingerprint admitted by an execution interpreter.
/// </summary>
public sealed record ExecutionDefinitionReference
{
    /// <summary>Creates an exact execution-definition reference.</summary>
    /// <param name="definitionId">Stable identity shared by all revisions of the definition.</param>
    /// <param name="revisionId">Exact semantic revision admitted by this reference.</param>
    /// <param name="fingerprint">Exact canonical content fingerprint admitted by this reference.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="definitionId"/> or <paramref name="revisionId"/> is a default uninitialized identity.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="fingerprint"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public ExecutionDefinitionReference(
        ExecutionDefinitionId definitionId,
        ExecutionRevisionId revisionId,
        ExecutionDefinitionFingerprint fingerprint)
    {
        if (string.IsNullOrWhiteSpace(definitionId.Value))
        {
            throw new ArgumentException(
                "An execution-definition reference requires a non-default definition identity.",
                nameof(definitionId));
        }

        if (string.IsNullOrWhiteSpace(revisionId.Value))
        {
            throw new ArgumentException(
                "An execution-definition reference requires a non-default revision identity.",
                nameof(revisionId));
        }

        DefinitionId = definitionId;
        RevisionId = revisionId;
        Fingerprint = Guard.RequireNotNull(fingerprint);
    }

    /// <summary>Stable identity shared by all revisions of the definition.</summary>
    public ExecutionDefinitionId DefinitionId { get; }

    /// <summary>Exact semantic revision admitted by this reference.</summary>
    public ExecutionRevisionId RevisionId { get; }

    /// <summary>Exact canonical content fingerprint admitted by this reference.</summary>
    public ExecutionDefinitionFingerprint Fingerprint { get; }

    /// <summary>Compares exact references in canonical identity, revision, and fingerprint order.</summary>
    /// <param name="left">First exact reference.</param>
    /// <param name="right">Second exact reference.</param>
    /// <returns>
    /// A negative value when <paramref name="left"/> precedes <paramref name="right"/>, zero when their
    /// canonical components are equal, or a positive value when <paramref name="left"/> follows
    /// <paramref name="right"/>.
    /// </returns>
    internal static int CompareCanonical(
        ExecutionDefinitionReference left,
        ExecutionDefinitionReference right)
    {
        var comparison = StringComparer.Ordinal.Compare(left.DefinitionId.Value, right.DefinitionId.Value);
        if (comparison != 0)
            return comparison;

        comparison = StringComparer.Ordinal.Compare(left.RevisionId.Value, right.RevisionId.Value);
        if (comparison != 0)
            return comparison;

        comparison = StringComparer.Ordinal.Compare(left.Fingerprint.Algorithm, right.Fingerprint.Algorithm);
        if (comparison != 0)
            return comparison;

        comparison = StringComparer.Ordinal.Compare(
            left.Fingerprint.Canonicalization,
            right.Fingerprint.Canonicalization);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Fingerprint.Value, right.Fingerprint.Value);
    }
}

/// <summary>
/// Exact schema-version compatibility declaration for one execution IR extension.
/// </summary>
public sealed record ExecutionDefinitionExtensionCompatibilityDeclaration
{
    /// <summary>Creates an exact extension compatibility declaration.</summary>
    /// <param name="id">Stable extension identity.</param>
    /// <param name="supportedSchemaVersions">
    /// Non-empty set of exact extension schema versions supported by the declaring interpreter.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default; or <paramref name="supportedSchemaVersions"/> is default or empty,
    /// contains a default version, or contains a duplicate exact version.
    /// </exception>
    [JsonConstructor]
    public ExecutionDefinitionExtensionCompatibilityDeclaration(
        ExecutionExtensionId id,
        ImmutableArray<ExecutionExtensionSchemaVersion> supportedSchemaVersions)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException(
                "An extension compatibility declaration requires a non-default extension identity.",
                nameof(id));
        }

        if (supportedSchemaVersions.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "At least one exact execution-extension schema version must be declared.",
                nameof(supportedSchemaVersions));
        }

        var observed = new HashSet<ExecutionExtensionSchemaVersion>();
        foreach (var schemaVersion in supportedSchemaVersions)
        {
            if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            {
                throw new ArgumentException(
                    "Extension compatibility cannot contain a default schema version.",
                    nameof(supportedSchemaVersions));
            }

            if (!observed.Add(schemaVersion))
            {
                throw new ArgumentException(
                    $"Execution-extension schema version '{schemaVersion.Value}' is declared more than once.",
                    nameof(supportedSchemaVersions));
            }

        }

        Id = id;
        SupportedSchemaVersions = CanonicalDocumentCollections.SortIfNeeded(
            supportedSchemaVersions,
            static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
    }

    /// <summary>Stable extension identity.</summary>
    public ExecutionExtensionId Id { get; }

    /// <summary>Exact supported schema versions in deterministic ordinal order.</summary>
    public ImmutableArray<ExecutionExtensionSchemaVersion> SupportedSchemaVersions { get; }

    /// <summary>Determines whether an exact extension schema version is supported.</summary>
    /// <param name="schemaVersion">Exact extension schema version to test.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="schemaVersion"/> is present in the declaration;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> is a default uninitialized value.
    /// </exception>
    public bool Supports(ExecutionExtensionSchemaVersion schemaVersion)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
        {
            throw new ArgumentException(
                "A default execution-extension schema version cannot be tested.",
                nameof(schemaVersion));
        }

        return CanonicalDocumentCollections.BinarySearchIndex(
            SupportedSchemaVersions,
            schemaVersion,
            static (candidate, requested) =>
                StringComparer.Ordinal.Compare(candidate.Value, requested.Value)) >= 0;
    }

    /// <summary>Compares declarations by extension identity and normalized exact version set.</summary>
    /// <param name="other">Declaration to compare with this value.</param>
    /// <returns>
    /// <see langword="true"/> when both declarations identify the same extension and exact versions;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool Equals(ExecutionDefinitionExtensionCompatibilityDeclaration? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null
            || Id != other.Id
            || SupportedSchemaVersions.Length != other.SupportedSchemaVersions.Length)
        {
            return false;
        }

        for (var index = 0; index < SupportedSchemaVersions.Length; index++)
        {
            if (SupportedSchemaVersions[index] != other.SupportedSchemaVersions[index])
                return false;
        }

        return true;
    }

    /// <summary>Returns a structural hash code for the extension identity and exact version set.</summary>
    /// <returns>A hash code derived from the extension identity and every version in canonical order.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        foreach (var schemaVersion in SupportedSchemaVersions)
            hash.Add(schemaVersion);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Exact execution-definition admission contract declared by an interpreter or runtime.
/// </summary>
/// <remarks>
/// Compatibility is fail-closed. Schema versions, definition references, and extension versions are admitted
/// only through exact identity; this declaration does not imply ranges, best-effort fallback, or semantic
/// weakening.
/// </remarks>
public sealed record ExecutionDefinitionCompatibilityDeclaration
{
    /// <summary>Creates an exact execution-definition compatibility declaration.</summary>
    /// <param name="schemaCompatibility">Exact execution IR schema versions supported by the interpreter.</param>
    /// <param name="supportedKinds">Non-empty set of supported execution-definition families.</param>
    /// <param name="supportedDefinitions">
    /// Non-empty set of exact definition, revision, and fingerprint references admitted by the interpreter.
    /// </param>
    /// <param name="supportedExtensions">
    /// Exact supported extension declarations, or a default or empty collection when no extensions are supported.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaCompatibility"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="supportedKinds"/> or <paramref name="supportedDefinitions"/> is default or empty;
    /// a supplied collection contains an unsupported, default, null, or duplicate entry; more than one
    /// definition reference has the same identity and revision; or more than one extension declaration has
    /// the same identity.
    /// </exception>
    [JsonConstructor]
    public ExecutionDefinitionCompatibilityDeclaration(
        ExecutionIrSchemaCompatibilityDeclaration schemaCompatibility,
        ImmutableArray<ExecutionDefinitionKind> supportedKinds,
        ImmutableArray<ExecutionDefinitionReference> supportedDefinitions,
        ImmutableArray<ExecutionDefinitionExtensionCompatibilityDeclaration> supportedExtensions = default)
    {
        SchemaCompatibility = Guard.RequireNotNull(schemaCompatibility);
        SupportedKinds = NormalizeKinds(supportedKinds);
        SupportedDefinitions = NormalizeDefinitions(supportedDefinitions);
        SupportedExtensions = NormalizeExtensions(supportedExtensions);
    }

    /// <summary>Exact execution IR schema versions supported by the interpreter.</summary>
    public ExecutionIrSchemaCompatibilityDeclaration SchemaCompatibility { get; }

    /// <summary>Supported execution-definition families in deterministic ordinal identity order.</summary>
    public ImmutableArray<ExecutionDefinitionKind> SupportedKinds { get; }

    /// <summary>Admitted exact definition references in deterministic ordinal order.</summary>
    public ImmutableArray<ExecutionDefinitionReference> SupportedDefinitions { get; }

    /// <summary>Supported extension declarations in deterministic extension-identity order.</summary>
    public ImmutableArray<ExecutionDefinitionExtensionCompatibilityDeclaration> SupportedExtensions { get; }

    /// <summary>Determines whether an execution-definition family is supported.</summary>
    /// <param name="kind">Definition family to test.</param>
    /// <returns><see langword="true"/> when the family is declared as supported; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is a default uninitialized value.</exception>
    public bool Supports(ExecutionDefinitionKind kind)
    {
        if (string.IsNullOrWhiteSpace(kind.Value))
            throw new ArgumentException("A default execution-definition kind cannot be tested.", nameof(kind));

        return CanonicalDocumentCollections.BinarySearchIndex(
            SupportedKinds,
            kind,
            static (candidate, requested) =>
                StringComparer.Ordinal.Compare(candidate.Value, requested.Value)) >= 0;
    }

    /// <summary>Attempts to find the compatibility declaration for an extension identity.</summary>
    /// <param name="id">Stable extension identity to find.</param>
    /// <param name="compatibility">
    /// Receives the matching extension declaration, or <see langword="null"/> when the extension is unknown.
    /// </param>
    /// <returns><see langword="true"/> when the extension is declared; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> is a default uninitialized identity.</exception>
    public bool TryGetExtension(
        ExecutionExtensionId id,
        [NotNullWhen(true)] out ExecutionDefinitionExtensionCompatibilityDeclaration? compatibility)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A default execution-extension identity cannot be tested.", nameof(id));

        var index = CanonicalDocumentCollections.BinarySearchIndex(
            SupportedExtensions,
            id,
            static (candidate, requested) =>
                StringComparer.Ordinal.Compare(candidate.Id.Value, requested.Value));
        compatibility = index >= 0 ? SupportedExtensions[index] : null;
        return compatibility is not null;
    }

    /// <summary>Compares declarations by their normalized exact compatibility sets.</summary>
    /// <param name="other">Declaration to compare with this value.</param>
    /// <returns>
    /// <see langword="true"/> when both declarations contain the same exact schema, kinds, definitions, and
    /// extensions; otherwise <see langword="false"/>.
    /// </returns>
    public bool Equals(ExecutionDefinitionCompatibilityDeclaration? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null
            || SchemaCompatibility != other.SchemaCompatibility
            || !SequenceEqual(SupportedKinds, other.SupportedKinds)
            || !SequenceEqual(SupportedDefinitions, other.SupportedDefinitions)
            || !SequenceEqual(SupportedExtensions, other.SupportedExtensions))
        {
            return false;
        }

        return true;
    }

    /// <summary>Returns a structural hash code for all normalized compatibility sets.</summary>
    /// <returns>A hash code derived from exact schema, kind, definition, and extension declarations.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaCompatibility);
        Add(ref hash, SupportedKinds);
        Add(ref hash, SupportedDefinitions);
        Add(ref hash, SupportedExtensions);
        return hash.ToHashCode();
    }

    static ImmutableArray<ExecutionDefinitionKind> NormalizeKinds(
        ImmutableArray<ExecutionDefinitionKind> supportedKinds)
    {
        if (supportedKinds.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "At least one execution-definition kind must be supported.",
                nameof(supportedKinds));
        }

        var observed = new HashSet<ExecutionDefinitionKind>();
        foreach (var kind in supportedKinds)
        {
            if (string.IsNullOrWhiteSpace(kind.Value))
            {
                throw new ArgumentException(
                    "Supported execution-definition kinds cannot contain a default identity.",
                    nameof(supportedKinds));
            }

            if (!observed.Add(kind))
            {
                throw new ArgumentException(
                    $"Execution-definition kind '{kind}' is declared more than once.",
                    nameof(supportedKinds));
            }

        }

        return CanonicalDocumentCollections.SortIfNeeded(
            supportedKinds,
            static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
    }

    static ImmutableArray<ExecutionDefinitionReference> NormalizeDefinitions(
        ImmutableArray<ExecutionDefinitionReference> supportedDefinitions)
    {
        if (supportedDefinitions.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "At least one exact execution-definition reference must be supported.",
                nameof(supportedDefinitions));
        }

        var observed = new HashSet<(ExecutionDefinitionId DefinitionId, ExecutionRevisionId RevisionId)>();
        foreach (var definition in supportedDefinitions)
        {
            if (definition is null)
            {
                throw new ArgumentException(
                    "Supported execution-definition references cannot contain null entries.",
                    nameof(supportedDefinitions));
            }

            if (!observed.Add((definition.DefinitionId, definition.RevisionId)))
            {
                throw new ArgumentException(
                    $"Execution-definition revision '{definition.DefinitionId.Value}/{definition.RevisionId.Value}' has more than one compatibility reference.",
                    nameof(supportedDefinitions));
            }

        }

        return CanonicalDocumentCollections.SortIfNeeded(
            supportedDefinitions,
            ExecutionDefinitionReference.CompareCanonical);
    }

    static ImmutableArray<ExecutionDefinitionExtensionCompatibilityDeclaration> NormalizeExtensions(
        ImmutableArray<ExecutionDefinitionExtensionCompatibilityDeclaration> supportedExtensions)
    {
        if (supportedExtensions.IsDefaultOrEmpty)
            return [];

        var observed = new HashSet<ExecutionExtensionId>();
        foreach (var extension in supportedExtensions)
        {
            if (extension is null)
            {
                throw new ArgumentException(
                    "Supported execution-extension declarations cannot contain null entries.",
                    nameof(supportedExtensions));
            }

            if (!observed.Add(extension.Id))
            {
                throw new ArgumentException(
                    $"Execution extension '{extension.Id.Value}' has more than one compatibility declaration.",
                    nameof(supportedExtensions));
            }

        }

        return CanonicalDocumentCollections.SortIfNeeded(
            supportedExtensions,
            static (left, right) => StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value));
    }

    static bool SequenceEqual<T>(ImmutableArray<T> left, ImmutableArray<T> right)
    {
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[index], right[index]))
                return false;
        }

        return true;
    }

    static void Add<T>(ref HashCode hash, ImmutableArray<T> values)
    {
        foreach (var value in values)
            hash.Add(value);
    }
}

/// <summary>Stable diagnostic codes emitted while reading, cataloging, and validating execution definitions.</summary>
public static class ExecutionDefinitionDiagnosticCodes
{
    /// <summary>A definition-catalog input is null and cannot establish canonical definition evidence.</summary>
    public const string CatalogDocumentInvalid = "execution.definition.catalog.document.invalid";

    /// <summary>More than one catalog document occupies the same definition identity and semantic revision.</summary>
    public const string CatalogRevisionDuplicate = "execution.definition.catalog.revision.duplicate";

    /// <summary>The supplied execution-definition JSON is empty.</summary>
    public const string JsonEmpty = "execution.definition.json.empty";

    /// <summary>The supplied text is not valid JSON.</summary>
    public const string JsonInvalid = "execution.definition.json.invalid";

    /// <summary>The document root is not a JSON object.</summary>
    public const string DocumentRootInvalid = "execution.definition.document.rootInvalid";

    /// <summary>The document contains a duplicate JSON object property.</summary>
    public const string JsonDuplicateProperty = "execution.definition.json.duplicateProperty";

    /// <summary>The document does not declare a definition kind.</summary>
    public const string KindMissing = "execution.definition.kind.missing";

    /// <summary>The declared definition kind is structurally invalid.</summary>
    public const string KindInvalid = "execution.definition.kind.invalid";

    /// <summary>The document uses an execution IR schema version unsupported by the interpreter.</summary>
    public const string SchemaVersionUnsupported = "execution.definition.schemaVersion.unsupported";

    /// <summary>The document does not contain required definition metadata.</summary>
    public const string MetadataMissing = "execution.definition.metadata.missing";

    /// <summary>The definition metadata is structurally invalid.</summary>
    public const string MetadataInvalid = "execution.definition.metadata.invalid";

    /// <summary>The definition metadata does not declare an execution IR schema version.</summary>
    public const string SchemaVersionMissing = "execution.definition.schemaVersion.missing";

    /// <summary>The declared execution IR schema version is structurally invalid.</summary>
    public const string SchemaVersionInvalid = "execution.definition.schemaVersion.invalid";

    /// <summary>The document belongs to an execution-definition family unsupported by the interpreter.</summary>
    public const string KindUnsupported = "execution.definition.kind.unsupported";

    /// <summary>The document identity is unknown to the interpreter.</summary>
    public const string DefinitionIdentityUnknown = "execution.definition.identity.unknown";

    /// <summary>The document revision is not admitted for its definition identity.</summary>
    public const string RevisionUnsupported = "execution.definition.revision.unsupported";

    /// <summary>The exact document fingerprint is not admitted for its definition and revision.</summary>
    public const string FingerprintIncompatible = "execution.definition.fingerprint.incompatible";

    /// <summary>The declared fingerprint algorithm or canonicalization profile is unsupported.</summary>
    public const string FingerprintProfileUnsupported =
        "execution.definition.fingerprint.profileUnsupported";

    /// <summary>The declared fingerprint value is not a valid digest for its profile.</summary>
    public const string FingerprintValueInvalid = "execution.definition.fingerprint.valueInvalid";

    /// <summary>The declared fingerprint does not match normalized semantic definition content.</summary>
    public const string FingerprintMismatch = "execution.definition.fingerprint.mismatch";

    /// <summary>The definition metadata does not contain a semantic content fingerprint.</summary>
    public const string FingerprintMissing = "execution.definition.fingerprint.missing";

    /// <summary>The definition metadata does not contain required producer and source provenance.</summary>
    public const string ProvenanceMissing = "execution.definition.provenance.missing";

    /// <summary>The document does not contain canonical definition content.</summary>
    public const string ContentMissing = "execution.definition.content.missing";

    /// <summary>The canonical definition content is structurally invalid.</summary>
    public const string ContentInvalid = "execution.definition.content.invalid";

    /// <summary>The document does not contain its explicit extension collection.</summary>
    public const string ExtensionsMissing = "execution.definition.extensions.missing";

    /// <summary>The explicit extension collection is structurally invalid.</summary>
    public const string ExtensionsInvalid = "execution.definition.extensions.invalid";

    /// <summary>JSON deserialization unexpectedly produced a null execution-definition document.</summary>
    public const string DeserializationNull = "execution.definition.deserialize.null";

    /// <summary>The document cannot be deserialized under the closed execution-definition contract.</summary>
    public const string DeserializationInvalid = "execution.definition.deserialize.invalid";

    /// <summary>The document declares an extension unknown to the interpreter.</summary>
    public const string ExtensionUnknown = "execution.definition.extension.unknown";

    /// <summary>The document uses an unsupported exact schema version of a known extension.</summary>
    public const string ExtensionSchemaVersionUnsupported =
        "execution.definition.extension.schemaVersion.unsupported";
}

/// <summary>
/// Validates whether a structurally valid execution-definition document may be activated by an interpreter.
/// </summary>
/// <remarks>
/// This validator checks only exact activation compatibility. Document integrity, fingerprint recomputation, and
/// portable-value validation are independent checks and are intentionally outside this validator.
/// </remarks>
public static class ExecutionDefinitionCompatibilityValidator
{
    /// <summary>Validates an execution definition against an exact interpreter compatibility declaration.</summary>
    /// <param name="document">Structurally valid execution-definition document to check.</param>
    /// <param name="declaration">Exact compatibility declaration of the admitting interpreter.</param>
    /// <returns>Deterministically ordered structured activation-compatibility diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="document"/> or <paramref name="declaration"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult Validate(
        ExecutionDefinitionDocument document,
        ExecutionDefinitionCompatibilityDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(declaration);

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (!declaration.SchemaCompatibility.Supports(document.Metadata.SchemaVersion))
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.SchemaVersionUnsupported,
                $"Execution IR schema version '{document.Metadata.SchemaVersion.Value}' is not supported by the admitting interpreter.",
                "/metadata/schemaVersion"));
        }

        if (!declaration.Supports(document.Kind))
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.KindUnsupported,
                $"Execution-definition kind '{document.Kind.Value}' is not supported by the admitting interpreter.",
                "/kind"));
        }

        ValidateDefinitionReference(document, declaration, diagnostics);
        ValidateExtensions(document, declaration, diagnostics);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateDefinitionReference(
        ExecutionDefinitionDocument document,
        ExecutionDefinitionCompatibilityDeclaration declaration,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var definitions = declaration.SupportedDefinitions;
        var identityStart = LowerBound(definitions, document.Metadata.DefinitionId);
        if (identityStart == definitions.Length
            || definitions[identityStart].DefinitionId != document.Metadata.DefinitionId)
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.DefinitionIdentityUnknown,
                $"Execution definition '{document.Metadata.DefinitionId.Value}' is unknown to the admitting interpreter.",
                "/metadata/definitionId"));
            return;
        }

        var revisionFound = false;
        for (var index = identityStart;
             index < definitions.Length && definitions[index].DefinitionId == document.Metadata.DefinitionId;
             index++)
        {
            var candidate = definitions[index];
            if (candidate.RevisionId != document.Metadata.RevisionId)
                continue;

            revisionFound = true;
            if (candidate.Fingerprint == document.Metadata.Fingerprint)
                return;
        }

        if (!revisionFound)
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.RevisionUnsupported,
                $"Execution revision '{document.Metadata.RevisionId.Value}' is not supported for definition '{document.Metadata.DefinitionId.Value}'.",
                "/metadata/revisionId"));
            return;
        }

        diagnostics.Add(Error(
            ExecutionDefinitionDiagnosticCodes.FingerprintIncompatible,
            "The execution-definition fingerprint is not admitted for the exact definition revision.",
            "/metadata/fingerprint"));
    }

    static void ValidateExtensions(
        ExecutionDefinitionDocument document,
        ExecutionDefinitionCompatibilityDeclaration declaration,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        for (var index = 0; index < document.Extensions.Length; index++)
        {
            var extension = document.Extensions[index];
            var location = $"/extensions/{index}";
            if (!declaration.TryGetExtension(extension.Id, out var compatibility))
            {
                diagnostics.Add(Error(
                    ExecutionDefinitionDiagnosticCodes.ExtensionUnknown,
                    $"Execution extension '{extension.Id.Value}' is unknown to the admitting interpreter.",
                    $"{location}/id"));
                continue;
            }

            if (!compatibility.Supports(extension.SchemaVersion))
            {
                diagnostics.Add(Error(
                    ExecutionDefinitionDiagnosticCodes.ExtensionSchemaVersionUnsupported,
                    $"Schema version '{extension.SchemaVersion.Value}' of execution extension '{extension.Id.Value}' is not supported by the admitting interpreter.",
                    $"{location}/schemaVersion"));
            }
        }
    }

    static int LowerBound(
        ImmutableArray<ExecutionDefinitionReference> definitions,
        ExecutionDefinitionId definitionId)
    {
        var low = 0;
        var high = definitions.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (StringComparer.Ordinal.Compare(definitions[middle].DefinitionId.Value, definitionId.Value) < 0)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location);
}
