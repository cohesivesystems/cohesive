using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionDefinitionCompatibilityTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void OpenCompatibilityIdentities_RoundTripAsValidatedFlatScalars()
    {
        var kind = new ExecutionDefinitionKind("materialization");
        var extensionId = new ExecutionExtensionId("cohesive.control");
        var extensionVersion = new ExecutionExtensionSchemaVersion("cohesive-control/v1");

        Assert.Equal("\"materialization\"", JsonSerializer.Serialize(kind, JsonOptions));
        Assert.Equal(kind, RoundTrip(kind));
        Assert.Equal(extensionId, RoundTrip(extensionId));
        Assert.Equal(extensionVersion, RoundTrip(extensionVersion));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionKind(" "));
        Assert.Throws<ArgumentException>(() => new ExecutionExtensionId(" "));
        Assert.Throws<ArgumentException>(() => new ExecutionExtensionSchemaVersion(" "));
    }

    [Fact]
    public void ExtensionCompatibility_NormalizesAndSupportsOnlyExactVersions()
    {
        var first = new ExecutionExtensionSchemaVersion("extension/v1");
        var second = new ExecutionExtensionSchemaVersion("extension/v2");
        var compatibility = new ExecutionDefinitionExtensionCompatibilityDeclaration(
            new("example.extension"),
            [second, first]);

        Assert.Equal(new[] { first, second }, compatibility.SupportedSchemaVersions);
        Assert.True(compatibility.Supports(first));
        Assert.True(compatibility.Supports(second));
        Assert.False(compatibility.Supports(new("extension/v3")));
        Assert.Equal(compatibility, RoundTrip(compatibility));
    }

    [Fact]
    public void DefinitionCompatibility_NormalizesEveryExactAdmissionSet()
    {
        var transition = new ExecutionDefinitionKind("transition");
        var process = new ExecutionDefinitionKind("process");
        var firstDefinition = Reference("definition/a", "revision/1", "01");
        var secondDefinition = Reference("definition/b", "revision/2", "02");
        var firstExtension = Extension("a.extension", "v2", "v1");
        var secondExtension = Extension("b.extension", "v1");
        var compatibility = new ExecutionDefinitionCompatibilityDeclaration(
            SchemaCompatibility(),
            [transition, process],
            [secondDefinition, firstDefinition],
            [secondExtension, firstExtension]);

        Assert.Equal(new[] { process, transition }, compatibility.SupportedKinds);
        Assert.Equal(new[] { firstDefinition, secondDefinition }, compatibility.SupportedDefinitions);
        Assert.Equal(new[] { firstExtension, secondExtension }, compatibility.SupportedExtensions);
        Assert.True(compatibility.Supports(process));
        Assert.True(compatibility.TryGetExtension(firstExtension.Id, out var actualExtension));
        Assert.Equal(firstExtension, actualExtension);
        Assert.False(compatibility.TryGetExtension(new("unknown.extension"), out _));

        var equivalent = new ExecutionDefinitionCompatibilityDeclaration(
            SchemaCompatibility(),
            [process, transition],
            [firstDefinition, secondDefinition],
            [firstExtension, secondExtension]);
        Assert.Equal(compatibility, equivalent);
        Assert.Equal(compatibility.GetHashCode(), equivalent.GetHashCode());
        Assert.Equal(compatibility, RoundTrip(compatibility));
    }

    [Fact]
    public void CompatibilityDeclarations_RejectIncompleteOrAmbiguousSets()
    {
        var definition = Reference("definition/a", "revision/1", "01");
        var conflictingDefinition = new ExecutionDefinitionReference(
            definition.DefinitionId,
            definition.RevisionId,
            DifferentFingerprint(definition.Fingerprint));
        var extension = Extension("example.extension", "v1");

        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionExtensionCompatibilityDeclaration(
            new("example.extension"),
            []));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionExtensionCompatibilityDeclaration(
            new("example.extension"),
            [new("v1"), new("v1")]));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionCompatibilityDeclaration(
            SchemaCompatibility(),
            [],
            [definition]));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionCompatibilityDeclaration(
            SchemaCompatibility(),
            [new("process"), new("process")],
            [definition]));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionCompatibilityDeclaration(
            SchemaCompatibility(),
            [new("process")],
            []));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionCompatibilityDeclaration(
            SchemaCompatibility(),
            [new("process")],
            [definition, definition]));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionCompatibilityDeclaration(
            SchemaCompatibility(),
            [new("process")],
            [definition, conflictingDefinition]));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionCompatibilityDeclaration(
            SchemaCompatibility(),
            [new("process")],
            [definition],
            [extension, extension]));
    }

    [Fact]
    public void Validator_AcceptsOnlyAnExactDefinitionAndExtensionAdmissionMatch()
    {
        var document = Document(
            "definition/rebuild",
            "revision/1",
            "process",
            [DefinitionExtension("cohesive.control", "control/v1")]);
        var compatibility = CompatibilityFor(document);

        var validation = ExecutionDefinitionCompatibilityValidator.Validate(document, compatibility);

        Assert.True(validation.IsValid);
        Assert.Empty(validation.Diagnostics);
    }

    [Fact]
    public void Validator_DistinguishesUnknownIdentityUnsupportedRevisionAndIncompatibleFingerprint()
    {
        var document = Document("definition/rebuild", "revision/2", "process");
        var unknownIdentity = CompatibilityFor(
            document,
            supportedDefinitions: [Reference("definition/other", "revision/2", document.Metadata.Fingerprint.Value)]);
        var unsupportedRevision = CompatibilityFor(
            document,
            supportedDefinitions: [Reference("definition/rebuild", "revision/1", document.Metadata.Fingerprint.Value)]);
        var incompatibleFingerprint = CompatibilityFor(
            document,
            supportedDefinitions:
            [
                new(
                    document.Metadata.DefinitionId,
                    document.Metadata.RevisionId,
                    DifferentFingerprint(document.Metadata.Fingerprint))
            ]);

        var identityDiagnostic = Assert.Single(
            ExecutionDefinitionCompatibilityValidator.Validate(document, unknownIdentity).Diagnostics);
        var revisionDiagnostic = Assert.Single(
            ExecutionDefinitionCompatibilityValidator.Validate(document, unsupportedRevision).Diagnostics);
        var fingerprintDiagnostic = Assert.Single(
            ExecutionDefinitionCompatibilityValidator.Validate(document, incompatibleFingerprint).Diagnostics);

        Assert.Equal(ExecutionDefinitionDiagnosticCodes.DefinitionIdentityUnknown, identityDiagnostic.Code);
        Assert.Equal("/metadata/definitionId", identityDiagnostic.Location);
        Assert.Equal(ExecutionDefinitionDiagnosticCodes.RevisionUnsupported, revisionDiagnostic.Code);
        Assert.Equal("/metadata/revisionId", revisionDiagnostic.Location);
        Assert.Equal(ExecutionDefinitionDiagnosticCodes.FingerprintIncompatible, fingerprintDiagnostic.Code);
        Assert.Equal("/metadata/fingerprint", fingerprintDiagnostic.Location);
    }

    [Fact]
    public void Validator_ReportsSchemaAndKindMismatchesBeforeDefinitionReferenceDiagnostics()
    {
        var document = Document("definition/rebuild", "revision/1", "process");
        var compatibility = new ExecutionDefinitionCompatibilityDeclaration(
            new([new("cohesive-execution/v1")]),
            [new("transition")],
            [Reference("definition/other", "revision/1", document.Metadata.Fingerprint.Value)]);

        var validation = ExecutionDefinitionCompatibilityValidator.Validate(document, compatibility);

        Assert.Equal(
            [
                ExecutionDefinitionDiagnosticCodes.SchemaVersionUnsupported,
                ExecutionDefinitionDiagnosticCodes.KindUnsupported,
                ExecutionDefinitionDiagnosticCodes.DefinitionIdentityUnknown
            ],
            validation.Diagnostics.Select(static diagnostic => diagnostic.Code));
        Assert.Equal(
            ["/metadata/schemaVersion", "/kind", "/metadata/definitionId"],
            validation.Diagnostics.Select(static diagnostic => diagnostic.Location));
    }

    [Fact]
    public void Validator_AttributesUnknownAndUnsupportedExtensionsInNormalizedDocumentOrder()
    {
        var document = Document(
            "definition/rebuild",
            "revision/1",
            "process",
            [
                DefinitionExtension("z.unknown", "unknown/v1"),
                DefinitionExtension("a.known", "known/v2")
            ]);
        var compatibility = CompatibilityFor(
            document,
            supportedExtensions: [Extension("a.known", "known/v1")]);

        var validation = ExecutionDefinitionCompatibilityValidator.Validate(document, compatibility);

        Assert.Equal(
            [
                ExecutionDefinitionDiagnosticCodes.ExtensionSchemaVersionUnsupported,
                ExecutionDefinitionDiagnosticCodes.ExtensionUnknown
            ],
            validation.Diagnostics.Select(static diagnostic => diagnostic.Code));
        Assert.Equal(
            ["/extensions/0/schemaVersion", "/extensions/1/id"],
            validation.Diagnostics.Select(static diagnostic => diagnostic.Location));
    }

    static ExecutionDefinitionReference Reference(
        string definitionId,
        string revisionId,
        string fingerprint) =>
        new(
            new(definitionId),
            new(revisionId),
            new(
                ExecutionDefinitionFingerprinter.Algorithm,
                ExecutionDefinitionFingerprinter.Canonicalization,
                fingerprint));

    static ExecutionDefinitionDocument Document(
        string definitionId,
        string revisionId,
        string kind,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default)
    {
        using var parsed = JsonDocument.Parse("""{"root":"start"}""");
        var definition = parsed.RootElement;
        var definitionKind = new ExecutionDefinitionKind(kind);
        var fingerprint = ExecutionDefinitionFingerprinter.Compute(
            ExecutionDefinitionDocument.CurrentSchemaVersion,
            definitionKind,
            definition,
            extensions);
        var metadata = new ExecutionDefinitionMetadata(
            new(definitionId),
            new(revisionId),
            ExecutionDefinitionDocument.CurrentSchemaVersion,
            fingerprint,
            new(
                new("compatibility-tests"),
                new("tests/execution-definition-compatibility"),
                DocumentOrigin.Generated));
        return new(definitionKind, metadata, definition, extensions);
    }

    static ExecutionDefinitionExtension DefinitionExtension(string id, string schemaVersion) =>
        new(
            new(id),
            new(schemaVersion),
            PortableValue.Concrete(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                ObservationValue.FromString("enabled")));

    static ExecutionDefinitionCompatibilityDeclaration CompatibilityFor(
        ExecutionDefinitionDocument document,
        ImmutableArray<ExecutionDefinitionReference> supportedDefinitions = default,
        ImmutableArray<ExecutionDefinitionExtensionCompatibilityDeclaration> supportedExtensions = default) =>
        new(
            new([document.Metadata.SchemaVersion]),
            [document.Kind],
            supportedDefinitions.IsDefault
                ? [new(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint)]
                : supportedDefinitions,
            supportedExtensions.IsDefault
                ?
                [
                    .. document.Extensions.Select(static extension =>
                        new ExecutionDefinitionExtensionCompatibilityDeclaration(
                            extension.Id,
                            [extension.SchemaVersion]))
                ]
                : supportedExtensions);

    static ExecutionDefinitionFingerprint DifferentFingerprint(ExecutionDefinitionFingerprint fingerprint) =>
        new(
            fingerprint.Algorithm,
            fingerprint.Canonicalization,
            string.Equals(
                fingerprint.Value,
                "0000000000000000000000000000000000000000000000000000000000000000",
                StringComparison.Ordinal)
                ? "1111111111111111111111111111111111111111111111111111111111111111"
                : "0000000000000000000000000000000000000000000000000000000000000000");

    static ExecutionDefinitionExtensionCompatibilityDeclaration Extension(
        string id,
        params string[] versions) =>
        new(new(id), [.. versions.Select(static version => new ExecutionExtensionSchemaVersion(version))]);

    static ExecutionIrSchemaCompatibilityDeclaration SchemaCompatibility() =>
        new([ExecutionDefinitionDocument.CurrentSchemaVersion]);

    static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}
