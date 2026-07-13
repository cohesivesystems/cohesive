using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Tests the strict portable serialization, semantic fingerprint, and document validation contracts
/// of canonical relationship catalogs.
/// </summary>
public sealed class RelationshipCatalogPersistenceTests
{
    static readonly GraphId DomainGraphId = new("domain/v1");
    static readonly QualifiedShapeId LoadShapeId = new(DomainGraphId, new("Load"));
    static readonly QualifiedShapeId CustomerShapeId = new(DomainGraphId, new("Customer"));
    static readonly QualifiedShapeId EquipmentShapeId = new(DomainGraphId, new("Equipment"));

    [Fact]
    public void Document_RoundTrip_PreservesCanonicalCatalogAndClosedTargetKey()
    {
        var document = RelationshipCatalogDocument.FromCatalog(CreateCatalog());

        var json = RelationshipCatalogJsonSerializer.Serialize(document, indented: false);
        var roundTripped = RelationshipCatalogJsonSerializer.Deserialize(json);
        var roundTrippedJson = RelationshipCatalogJsonSerializer.Serialize(roundTripped, indented: false);

        Assert.Equal(2, roundTripped.Catalog.Count);
        Assert.All(
            roundTripped.Catalog.Relationships,
            static relationship => Assert.IsType<ObservationIdentityRelationshipTargetKey>(relationship.TargetKey));
        Assert.Equal(document.CatalogFingerprint, roundTripped.CatalogFingerprint);
        Assert.True(RelationshipCatalogDocumentSemanticValidator.Validate(roundTripped).IsValid);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(roundTrippedJson)));
        Assert.Contains("\"$targetKey\":\"observationIdentity\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"count\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"inverseCardinality\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Deserialize_RejectsUnknownRootAndNestedProperties()
    {
        var json = SerializeCurrentDocument();

        var rootWithUnknownProperty = JsonNode.Parse(json)!.AsObject();
        rootWithUnknownProperty["unexpected"] = true;
        Assert.Throws<JsonException>(() =>
            RelationshipCatalogJsonSerializer.Deserialize(rootWithUnknownProperty.ToJsonString()));

        var relationshipWithUnknownProperty = JsonNode.Parse(json)!.AsObject();
        relationshipWithUnknownProperty["catalog"]!["relationships"]![0]!["unexpected"] = true;
        Assert.Throws<JsonException>(() =>
            RelationshipCatalogJsonSerializer.Deserialize(relationshipWithUnknownProperty.ToJsonString()));

        var targetKeyWithUnknownProperty = JsonNode.Parse(json)!.AsObject();
        targetKeyWithUnknownProperty["catalog"]!["relationships"]![0]!["targetKey"]!["unexpected"] = true;
        Assert.Throws<JsonException>(() =>
            RelationshipCatalogJsonSerializer.Deserialize(targetKeyWithUnknownProperty.ToJsonString()));
    }

    [Fact]
    public void Deserialize_RejectsUnknownTargetKeyDiscriminatorAndNumericEnums()
    {
        var json = SerializeCurrentDocument();

        var missingTargetKeyDiscriminator = JsonNode.Parse(json)!.AsObject();
        missingTargetKeyDiscriminator["catalog"]!["relationships"]![0]!["targetKey"]!
            .AsObject()
            .Remove("$targetKey");
        Assert.Throws<JsonException>(() =>
            RelationshipCatalogJsonSerializer.Deserialize(missingTargetKeyDiscriminator.ToJsonString()));

        var unknownTargetKey = JsonNode.Parse(json)!.AsObject();
        unknownTargetKey["catalog"]!["relationships"]![0]!["targetKey"]!["$targetKey"] = "alternateField";
        Assert.Throws<JsonException>(() =>
            RelationshipCatalogJsonSerializer.Deserialize(unknownTargetKey.ToJsonString()));

        var numericUniqueness = JsonNode.Parse(json)!.AsObject();
        numericUniqueness["catalog"]!["relationships"]![0]!["sourceReferenceUniqueness"] =
            (int)SourceReferenceUniqueness.GloballyUnique;
        Assert.Throws<JsonException>(() =>
            RelationshipCatalogJsonSerializer.Deserialize(numericUniqueness.ToJsonString()));

        var numericPathSegment = JsonNode.Parse(json)!.AsObject();
        numericPathSegment["catalog"]!["relationships"]![0]!["sourceReference"]!["segments"]![0]!["kind"] =
            (int)SegmentKind.Field;
        Assert.Throws<JsonException>(() =>
            RelationshipCatalogJsonSerializer.Deserialize(numericPathSegment.ToJsonString()));

        var incorrectlyCasedEnum = JsonNode.Parse(json)!.AsObject();
        incorrectlyCasedEnum["catalog"]!["relationships"]![0]!["sourceReferenceUniqueness"] =
            "notguaranteed";
        Assert.Throws<JsonException>(() =>
            RelationshipCatalogJsonSerializer.Deserialize(incorrectlyCasedEnum.ToJsonString()));
    }

    [Fact]
    public void Deserialize_RejectsLegacyNestedSingleValueWrappers()
    {
        var document = JsonNode.Parse(SerializeCurrentDocument())!.AsObject();
        var relationship = document["catalog"]!["relationships"]![0]!.AsObject();
        var id = relationship["id"]!.GetValue<string>();
        relationship["id"] = new JsonObject
        {
            ["value"] = id,
            ["unexpected"] = true
        };

        Assert.Throws<JsonException>(() =>
            RelationshipCatalogJsonSerializer.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void TryDeserialize_RejectsEmptyInvalidAndNonObjectJson()
    {
        AssertDiagnostic(
            RelationshipCatalogJsonSerializer.TryDeserialize(" ", out var emptyDocument),
            "relationshipCatalog.json.empty");
        Assert.Null(emptyDocument);

        AssertDiagnostic(
            RelationshipCatalogJsonSerializer.TryDeserialize("{", out var invalidDocument),
            "relationshipCatalog.json.invalid");
        Assert.Null(invalidDocument);

        AssertDiagnostic(
            RelationshipCatalogJsonSerializer.TryDeserialize("[]", out var arrayDocument),
            "relationshipCatalog.document.rootInvalid");
        Assert.Null(arrayDocument);
    }

    [Fact]
    public void Fingerprint_IgnoresDocumentMetadata()
    {
        var catalog = CreateCatalog();
        var first = RelationshipCatalogDocument.FromCatalog(
            catalog,
            new RelationshipCatalogDocumentMetadata(
                origin: DocumentOrigin.User,
                name: "Domain relationships",
                producer: "relations-dsl",
                createdAtUtc: new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero)));
        var second = RelationshipCatalogDocument.FromCatalog(
            catalog,
            new RelationshipCatalogDocumentMetadata(
                origin: DocumentOrigin.Generated,
                name: "Ari relationship proposal",
                producer: "ari",
                createdAtUtc: new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero)));

        Assert.NotEqual(
            RelationshipCatalogJsonSerializer.Serialize(first, indented: false),
            RelationshipCatalogJsonSerializer.Serialize(second, indented: false));
        Assert.Equal(first.CatalogFingerprint, second.CatalogFingerprint);
    }

    [Fact]
    public void Fingerprint_ChangesWhenRelationshipSemanticsChange()
    {
        var relationship = CreateCustomerRelationship();
        var nonUnique = new RelationshipCatalog([relationship]);
        var unique = new RelationshipCatalog([
            relationship with { SourceReferenceUniqueness = SourceReferenceUniqueness.GloballyUnique }
        ]);

        Assert.NotEqual(
            RelationshipCatalogFingerprinter.Compute(nonUnique).Value,
            RelationshipCatalogFingerprinter.Compute(unique).Value);
    }

    [Fact]
    public void FingerprintAndSerialization_NormalizeRelationshipDeclarationOrder()
    {
        var customer = CreateCustomerRelationship();
        var equipment = CreateEquipmentRelationship();
        var first = RelationshipCatalogDocument.FromCatalog(new([customer, equipment]));
        var second = RelationshipCatalogDocument.FromCatalog(new([equipment, customer]));

        Assert.Equal(first.CatalogFingerprint, second.CatalogFingerprint);
        Assert.Equal(
            RelationshipCatalogJsonSerializer.Serialize(first, indented: false),
            RelationshipCatalogJsonSerializer.Serialize(second, indented: false));
    }

    [Fact]
    public void Fingerprint_MatchesKnownCanonicalizationVector()
    {
        var fingerprint = RelationshipCatalogFingerprinter.Compute(CreateCatalog());

        Assert.Equal("relationship-catalog/v1-c14n/v1", fingerprint.Canonicalization);
        Assert.Equal("9fde0e2e70dfc329915f805c50083fd7ec9e76f56d8756649a17b88d684e1d13", fingerprint.Value);
    }

    [Fact]
    public void Fingerprint_CanonicalizesUnicodeSemanticText()
    {
        var graphId = new GraphId("domaine/café/雪");
        var relationship = new RelationshipDefinition(
            id: new("Chargement.Client/café/雪"),
            sourceShape: new(graphId, new("Chargement")),
            sourceReference: FieldPath.FromField("ClientÉtrangerId"),
            targetShape: new(graphId, new("Client/雪")),
            targetKey: ObservationIdentityRelationshipTargetKey.Instance);

        var first = RelationshipCatalogFingerprinter.Compute(new([relationship]));
        var second = RelationshipCatalogFingerprinter.Compute(new([relationship]));

        Assert.Equal(first, second);
        Assert.Equal(64, first.Value.Length);
    }

    [Fact]
    public void TryDeserialize_ReturnsStructuredVersionFingerprintAndCatalogDiagnostics()
    {
        var json = SerializeCurrentDocument();

        var tampered = JsonNode.Parse(json)!.AsObject();
        tampered["catalogFingerprint"]!["value"] = new string('0', 64);
        var tamperedResult = RelationshipCatalogJsonSerializer.TryDeserialize(
            tampered.ToJsonString(),
            out var tamperedDocument);
        Assert.NotNull(tamperedDocument);
        AssertDiagnostic(tamperedResult, "relationshipCatalog.fingerprint.mismatch");

        var missingCatalog = JsonNode.Parse(json)!.AsObject();
        missingCatalog.Remove("catalog");
        var missingCatalogResult = RelationshipCatalogJsonSerializer.TryDeserialize(
            missingCatalog.ToJsonString(),
            out var missingCatalogDocument);
        Assert.Null(missingCatalogDocument);
        AssertDiagnostic(missingCatalogResult, "relationshipCatalog.catalog.missing");

        var missingFingerprint = JsonNode.Parse(json)!.AsObject();
        missingFingerprint.Remove("catalogFingerprint");
        var missingFingerprintResult = RelationshipCatalogJsonSerializer.TryDeserialize(
            missingFingerprint.ToJsonString(),
            out var missingFingerprintDocument);
        Assert.Null(missingFingerprintDocument);
        AssertDiagnostic(missingFingerprintResult, "relationshipCatalog.fingerprint.missing");

        var unsupportedVersion = JsonNode.Parse(json)!.AsObject();
        unsupportedVersion["schemaVersion"] = "relationship-catalog/v99";
        var unsupportedVersionResult = RelationshipCatalogJsonSerializer.TryDeserialize(
            unsupportedVersion.ToJsonString(),
            out var unsupportedVersionDocument);
        Assert.Null(unsupportedVersionDocument);
        AssertDiagnostic(unsupportedVersionResult, "relationshipCatalog.schemaVersion.unsupported");

        var unversionedResult = RelationshipCatalogJsonSerializer.TryDeserialize(
            """{"catalog":{}}""",
            out var unversionedDocument);
        Assert.Null(unversionedDocument);
        AssertDiagnostic(unversionedResult, "relationshipCatalog.schemaVersion.missing");
    }

    [Fact]
    public void TryDeserialize_RejectsDuplicateJsonObjectPropertiesRecursively()
    {
        var json = SerializeCurrentDocument();
        var duplicate = json.Replace(
            "\"$targetKey\":",
            "\"$targetKey\":\"observationIdentity\",\"$targetKey\":",
            StringComparison.Ordinal);

        var result = RelationshipCatalogJsonSerializer.TryDeserialize(duplicate, out var document);

        Assert.Null(document);
        AssertDiagnostic(result, "relationshipCatalog.json.duplicateProperty");
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Location?.Contains("/catalog/relationships/0/targetKey/$targetKey", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void DocumentValidator_RejectsUnsupportedVersionProfileDigestAndTampering()
    {
        var document = RelationshipCatalogDocument.FromCatalog(CreateCatalog());
        var unsupportedVersion = document with { SchemaVersion = "relationship-catalog/v99" };
        var unsupportedProfile = document with
        {
            CatalogFingerprint = document.CatalogFingerprint with { Canonicalization = "relationship-catalog/v99-c14n/v1" }
        };
        var unsupportedAlgorithm = document with
        {
            CatalogFingerprint = document.CatalogFingerprint with { Algorithm = "sha512" }
        };
        var invalidDigest = document with
        {
            CatalogFingerprint = document.CatalogFingerprint with { Value = new string('A', 64) }
        };
        var tamperedCatalog = document with
        {
            Catalog = new RelationshipCatalog([
                CreateCustomerRelationship() with
                {
                    SourceReferenceUniqueness = SourceReferenceUniqueness.GloballyUnique
                }
            ])
        };

        AssertDiagnostic(
            RelationshipCatalogDocumentSemanticValidator.Validate(unsupportedVersion),
            "relationshipCatalog.schemaVersion.unsupported");
        AssertDiagnostic(
            RelationshipCatalogDocumentSemanticValidator.Validate(unsupportedProfile),
            "relationshipCatalog.fingerprint.profileUnsupported");
        AssertDiagnostic(
            RelationshipCatalogDocumentSemanticValidator.Validate(unsupportedAlgorithm),
            "relationshipCatalog.fingerprint.profileUnsupported");
        AssertDiagnostic(
            RelationshipCatalogDocumentSemanticValidator.Validate(invalidDigest),
            "relationshipCatalog.fingerprint.valueInvalid");
        AssertDiagnostic(
            RelationshipCatalogDocumentSemanticValidator.Validate(tamperedCatalog),
            "relationshipCatalog.fingerprint.mismatch");
    }

    [Fact]
    public void DocumentValidator_UsesCatalogLocalValidationWithoutAmbientShapeResolution()
    {
        var catalog = CreateCatalog();
        var document = RelationshipCatalogDocument.FromCatalog(catalog);

        var result = RelationshipCatalogDocumentSemanticValidator.Validate(document);

        Assert.True(result.IsValid);
        Assert.DoesNotContain(
            result.Diagnostics,
            static diagnostic => diagnostic.Code.Contains("endpointGraphMissing", StringComparison.Ordinal));

        var customer = CreateCustomerRelationship() with { Id = new("duplicate") };
        var equipment = CreateEquipmentRelationship() with { Id = new("duplicate") };
        var invalidCatalog = new RelationshipCatalog([customer, equipment]);
        var invalidDocument = new RelationshipCatalogDocument(
            RelationshipCatalogDocument.CurrentSchemaVersion,
            invalidCatalog,
            RelationshipCatalogFingerprinter.Compute(invalidCatalog));

        var invalidResult = RelationshipCatalogDocumentSemanticValidator.Validate(invalidDocument);

        AssertDiagnostic(invalidResult, "relationshipCatalog.relationship.duplicateId");
        Assert.Contains(
            invalidResult.Diagnostics,
            static diagnostic => diagnostic.Location?.StartsWith("/catalog/", StringComparison.Ordinal) == true);
        Assert.Throws<ArgumentException>(() => RelationshipCatalogDocument.FromCatalog(invalidCatalog));
    }

    [Fact]
    public void DocumentValidator_ReturnsInvalidEnumDiagnosticsBeforeFingerprinting()
    {
        var document = RelationshipCatalogDocument.FromCatalog(CreateCatalog());
        var relationship = CreateCustomerRelationship() with
        {
            SourceReferenceUniqueness = (SourceReferenceUniqueness)999
        };
        var invalid = document with { Catalog = new([relationship]) };

        var result = RelationshipCatalogDocumentSemanticValidator.Validate(invalid);

        AssertDiagnostic(
            result,
            "relationshipCatalog.relationship.sourceReferenceUniquenessInvalid");
    }

    static string SerializeCurrentDocument() =>
        RelationshipCatalogJsonSerializer.Serialize(
            RelationshipCatalogDocument.FromCatalog(CreateCatalog()),
            indented: false);

    static RelationshipCatalog CreateCatalog() =>
        new([CreateEquipmentRelationship(), CreateCustomerRelationship()]);

    static RelationshipDefinition CreateCustomerRelationship() => new(
        id: new("Load.Customer"),
        sourceShape: LoadShapeId,
        sourceReference: FieldPath.FromField("CustomerId"),
        targetShape: CustomerShapeId,
        targetKey: ObservationIdentityRelationshipTargetKey.Instance);

    static RelationshipDefinition CreateEquipmentRelationship() => new(
        id: new("Load.Equipment"),
        sourceShape: LoadShapeId,
        sourceReference: FieldPath.FromField("EquipmentId"),
        targetShape: EquipmentShapeId,
        targetKey: ObservationIdentityRelationshipTargetKey.Instance);

    static void AssertDiagnostic(DocumentValidationResult result, string code) =>
        Assert.Contains(result.Diagnostics, diagnostic =>
            string.Equals(diagnostic.Code, code, StringComparison.Ordinal));
}
