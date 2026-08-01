using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationBackendPoolJsonSerializerTests
{
    static readonly MaterializationId Materialization = new("materialization/backend-pool-json");
    static readonly ExecutionDefinitionFingerprint MaterializationFingerprint = new(
        MaterializationDefinitionFingerprinter.Algorithm,
        MaterializationDefinitionFingerprinter.Canonicalization,
        "0123456789abcdef");

    [Fact]
    public void Document_RoundTripsThroughCanonicalJsonWithNormalizedMembersAndExactFingerprint()
    {
        var document = CreateDocument(reverseMembers: true);

        var canonical = MaterializationBackendPoolJsonSerializer.GetCanonicalBytes(document);
        var json = MaterializationBackendPoolJsonSerializer.Serialize(
            document,
            PortableDocumentJsonFormatting.Compact);
        var restored = MaterializationBackendPoolJsonSerializer.Deserialize(json);

        Assert.Equal(Encoding.UTF8.GetString(canonical), json);
        Assert.Equal(document.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(document.DefinitionFingerprint, restored.DefinitionFingerprint);
        Assert.Equal(
            document.DefinitionFingerprint,
            MaterializationBackendPoolFingerprinter.Compute(restored.Definition));
        Assert.Equal(
            ["target/backend-a", "target/backend-b"],
            restored.Definition.Members.Select(static member => member.Id.Value));
        Assert.Equal(canonical, MaterializationBackendPoolJsonSerializer.GetCanonicalBytes(restored));
    }

    [Fact]
    public void Deserialize_RejectsUnknownAndDuplicateProperties()
    {
        var document = CreateDocument();
        var json = MaterializationBackendPoolJsonSerializer.Serialize(
            document,
            PortableDocumentJsonFormatting.Compact);
        var unknown = json.Insert(startIndex: 1, "\"unknown\":true,");
        var schema = $"\"schemaVersion\":\"{MaterializationBackendPoolDocument.CurrentSchemaVersion}\"";
        var duplicate = json.Replace(schema, $"{schema},{schema}", StringComparison.Ordinal);

        Assert.Throws<JsonException>(() =>
            MaterializationBackendPoolJsonSerializer.Deserialize(unknown));
        Assert.Throws<JsonException>(() =>
            MaterializationBackendPoolJsonSerializer.Deserialize(duplicate));
    }

    [Fact]
    public void Deserialize_RejectsUnsupportedSchemaVersion()
    {
        var document = CreateDocument();
        var json = MaterializationBackendPoolJsonSerializer.Serialize(
            document,
            PortableDocumentJsonFormatting.Compact);
        var currentSchema = $"\"schemaVersion\":\"{MaterializationBackendPoolDocument.CurrentSchemaVersion}\"";
        const string ForgedSchema = "\"schemaVersion\":\"cohesive-materialization-backend-pool/v2\"";
        var forged = json.Replace(currentSchema, ForgedSchema, StringComparison.Ordinal);

        Assert.NotEqual(json, forged);
        Assert.Throws<JsonException>(() =>
            MaterializationBackendPoolJsonSerializer.Deserialize(forged));
    }

    [Fact]
    public void Deserialize_RejectsForgedDefinitionFingerprint()
    {
        var document = CreateDocument();
        var json = MaterializationBackendPoolJsonSerializer.Serialize(
            document,
            PortableDocumentJsonFormatting.Compact);
        var forged = json.Replace(
            document.DefinitionFingerprint.Value,
            new string('0', document.DefinitionFingerprint.Value.Length),
            StringComparison.Ordinal);

        Assert.NotEqual(json, forged);
        Assert.Throws<JsonException>(() =>
            MaterializationBackendPoolJsonSerializer.Deserialize(forged));
    }

    static MaterializationBackendPoolDocument CreateDocument(bool reverseMembers = false)
    {
        var first = Descriptor("target/backend-a");
        var second = Descriptor("target/backend-b");
        MaterializationBackendPoolDefinition definition = new(
            new("pool/backend-pool-json"),
            Materialization,
            MaterializationFingerprint,
            reverseMembers ? [second, first] : [first, second],
            defaultTarget: first.Id,
            provenance: new(
                new("cohesive-tests", "1"),
                new("tests/materialization-backend-pool-json"),
                DocumentOrigin.Generated));
        return MaterializationBackendPoolDocument.FromDefinition(definition);
    }

    static MaterializationTargetDescriptor Descriptor(string id)
    {
        MaterializationTargetId targetId = new(id);
        return new(
            targetId,
            Materialization,
            new(
                new($"profile/{id}"),
                MaterializationEndpointRole.Target,
                targetId.Value,
                []));
    }
}
