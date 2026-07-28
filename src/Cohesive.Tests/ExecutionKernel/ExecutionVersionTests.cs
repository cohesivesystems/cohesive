using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionVersionTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SchemaVersion_IsAnOpaqueFlatScalar()
    {
        var version = new ExecutionIrSchemaVersion("cohesive-execution/v1");

        var json = JsonSerializer.Serialize(version, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ExecutionIrSchemaVersion>(json, JsonOptions);

        Assert.Equal("\"cohesive-execution/v1\"", json);
        Assert.Equal(version, roundTrip);
        Assert.Throws<ArgumentNullException>(() => new ExecutionIrSchemaVersion(null!));
        Assert.Throws<ArgumentException>(() => new ExecutionIrSchemaVersion(" "));
    }

    [Fact]
    public void CompatibilityDeclaration_NormalizesAndSupportsOnlyExactVersions()
    {
        var version1 = new ExecutionIrSchemaVersion("cohesive-execution/v1");
        var version2 = new ExecutionIrSchemaVersion("cohesive-execution/v2");
        var declaration = new ExecutionIrSchemaCompatibilityDeclaration([version2, version1]);

        Assert.Collection(
            declaration.SupportedSchemaVersions,
            actual => Assert.Equal(version1, actual),
            actual => Assert.Equal(version2, actual));
        Assert.True(declaration.Supports(version1));
        Assert.True(declaration.Supports(version2));
        Assert.False(declaration.Supports(new("cohesive-execution/v1-preview")));
        Assert.False(declaration.Supports(new("cohesive-execution/v3")));
        Assert.Throws<ArgumentException>(() => declaration.Supports(default));
    }

    [Fact]
    public void CompatibilityDeclaration_HasStructuralEqualityAndDeterministicJson()
    {
        var first = new ExecutionIrSchemaCompatibilityDeclaration(
            ImmutableArray.Create(
                new ExecutionIrSchemaVersion("cohesive-execution/v2"),
                new ExecutionIrSchemaVersion("cohesive-execution/v1")));
        var second = new ExecutionIrSchemaCompatibilityDeclaration(
            ImmutableArray.Create(
                new ExecutionIrSchemaVersion("cohesive-execution/v1"),
                new ExecutionIrSchemaVersion("cohesive-execution/v2")));

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.Equal(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonSerializer.Serialize(second, JsonOptions));

        var json = JsonSerializer.Serialize(first, JsonOptions);
        Assert.Equal(first, JsonSerializer.Deserialize<ExecutionIrSchemaCompatibilityDeclaration>(json, JsonOptions));
    }

    [Fact]
    public void CompatibilityDeclaration_RejectsIncompleteOrAmbiguousSets()
    {
        var version = new ExecutionIrSchemaVersion("cohesive-execution/v1");

        Assert.Throws<ArgumentException>(() => new ExecutionIrSchemaCompatibilityDeclaration(default));
        Assert.Throws<ArgumentException>(() => new ExecutionIrSchemaCompatibilityDeclaration([]));
        Assert.Throws<ArgumentException>(() => new ExecutionIrSchemaCompatibilityDeclaration([default]));
        Assert.Throws<ArgumentException>(() => new ExecutionIrSchemaCompatibilityDeclaration([version, version]));
    }

    [Fact]
    public void DefinitionMetadata_RequiresAndRoundTripsPinnedIdentityRevisionSchemaFingerprintAndProvenance()
    {
        var metadata = CreateDefinitionMetadata();

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<ExecutionDefinitionMetadata>(json, JsonOptions);

        Assert.Equal(metadata, roundTrip);
        Assert.Equal(metadata.GetHashCode(), roundTrip?.GetHashCode());
    }

    [Fact]
    public void DefinitionMetadata_RejectsDefaultIdentityOrVersionAndMissingReferences()
    {
        var metadata = CreateDefinitionMetadata();

        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionMetadata(
            default,
            metadata.RevisionId,
            metadata.SchemaVersion,
            metadata.Fingerprint,
            metadata.Provenance));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionMetadata(
            metadata.DefinitionId,
            default,
            metadata.SchemaVersion,
            metadata.Fingerprint,
            metadata.Provenance));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionMetadata(
            metadata.DefinitionId,
            metadata.RevisionId,
            default,
            metadata.Fingerprint,
            metadata.Provenance));
        Assert.Throws<ArgumentNullException>(() => new ExecutionDefinitionMetadata(
            metadata.DefinitionId,
            metadata.RevisionId,
            metadata.SchemaVersion,
            null!,
            metadata.Provenance));
        Assert.Throws<ArgumentNullException>(() => new ExecutionDefinitionMetadata(
            metadata.DefinitionId,
            metadata.RevisionId,
            metadata.SchemaVersion,
            metadata.Fingerprint,
            null!));
    }

    [Fact]
    public void DefinitionFingerprint_RequiresInterpretationMetadataButDoesNotComputeContent()
    {
        var fingerprint = new ExecutionDefinitionFingerprint(
            algorithm: "sha256",
            canonicalization: "execution-ir/v1",
            value: "0123456789abcdef");

        Assert.Equal("sha256", fingerprint.Algorithm);
        Assert.Equal("execution-ir/v1", fingerprint.Canonicalization);
        Assert.Equal("0123456789abcdef", fingerprint.Value);
        Assert.Throws<ArgumentNullException>(() => new ExecutionDefinitionFingerprint(null!, "canonical", "value"));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionFingerprint("sha256", " ", "value"));
        Assert.Throws<ArgumentException>(() => new ExecutionDefinitionFingerprint("sha256", "canonical", " "));
    }

    static ExecutionDefinitionMetadata CreateDefinitionMetadata() => new(
        definitionId: new("definition/index-rebuild"),
        revisionId: new("revision/2026-07-27"),
        schemaVersion: new("cohesive-execution/v1"),
        fingerprint: new(
            algorithm: "sha256",
            canonicalization: "execution-ir/v1",
            value: "0123456789abcdef"),
        provenance: new(
            producer: new("cohesive-csharp", version: "0.1.0"),
            source: new(
                reference: "src/IndexRebuild.cs",
                semanticPath: ExecutionSemanticPath.From("index-rebuild")),
            origin: DocumentOrigin.Compiled));
}
