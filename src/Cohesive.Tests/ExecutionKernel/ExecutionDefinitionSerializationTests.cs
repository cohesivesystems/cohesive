using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionDefinitionSerializationTests
{
    static readonly ExecutionDefinitionKind TransitionKind = new("transition");

    [Fact]
    public void NormalizedSemanticContent_MatchesKnownCanonicalBytesAndFingerprint()
    {
        var document = CreateDocument();
        const string Expected =
            "{\"definition\":{\"entry\":\"start\",\"orderedSteps\":[\"reserve\",\"commit\"],\"semanticObject\":{\"alpha\":1,\"zeta\":2}},\"extensions\":[],\"kind\":\"transition\",\"schemaVersion\":\"cohesive-execution/v2\"}";

        var normalized = ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document);

        Assert.Equal(Expected, Encoding.UTF8.GetString(normalized));
        Assert.Equal(ExecutionDefinitionFingerprinter.Algorithm, document.Metadata.Fingerprint.Algorithm);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.Canonicalization,
            document.Metadata.Fingerprint.Canonicalization);
        Assert.Equal(
            "2aad02b26fbb921b257feae707ff9d0990c53217983b32fcc1ed2334ca806424",
            document.Metadata.Fingerprint.Value);
        Assert.Equal(document.Metadata.Fingerprint, ExecutionDefinitionFingerprinter.Compute(document));
    }

    [Fact]
    public void SemanticBodySequenceAndExtensionChanges_AlterNormalizedBytesAndFingerprint()
    {
        ImmutableArray<ExecutionDefinitionExtension> extensions =
        [
            StringExtension("example.mode", "adaptive")
        ];
        var baseline = CreateDocument(extensions: extensions);
        var changedBody = CreateDocument(
            definition: Definition(entry: "resume"),
            extensions: extensions);
        var changedSequence = CreateDocument(
            definition: Definition(orderedSteps: ["commit", "reserve"]),
            extensions: extensions);
        var changedExtension = CreateDocument(
            extensions: [StringExtension("example.mode", "fixed")]);

        AssertDifferentSemantics(baseline, changedBody);
        AssertDifferentSemantics(baseline, changedSequence);
        AssertDifferentSemantics(baseline, changedExtension);
    }

    [Fact]
    public void LifecycleAndAttributionMetadata_DoNotAffectSemanticContentButRemainDurable()
    {
        var baseline = CreateDocument(
            definitionId: "definition/baseline",
            revisionId: "revision/1",
            displayName: "Baseline transition",
            description: "First human description",
            provenance: Provenance(
                producer: "first-producer",
                sourceReference: "src/FirstTransition.cs",
                sourcePath: new(["transition", "first"])),
            sourceMap: new([
                new(
                    "src/FirstTransition.cs:12",
                    new(["steps", "reserve"]),
                    "First source location")
            ]),
            diagnostics:
            [
                new(
                    "authoring.first",
                    DiagnosticSeverity.Info,
                    "First authoring diagnostic.",
                    "/definition/entry")
            ]);
        var changed = CreateDocument(
            definitionId: "definition/renamed",
            revisionId: "revision/99",
            displayName: "Renamed transition",
            description: "Different human description",
            provenance: Provenance(
                producer: "second-producer",
                sourceReference: "notion:execution/transition",
                sourcePath: new(["transition", "second"])),
            sourceMap: new([
                new(
                    "notion:execution/transition#commit",
                    new(["steps", "commit"]),
                    "Different source location")
            ]),
            diagnostics:
            [
                new(
                    "authoring.second",
                    DiagnosticSeverity.Warning,
                    "Different authoring diagnostic.",
                    "/definition/orderedSteps/1")
            ]);

        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(baseline),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(changed));
        Assert.Equal(baseline.Metadata.Fingerprint, changed.Metadata.Fingerprint);
        Assert.NotEqual(
            ExecutionDefinitionJsonSerializer.Serialize(baseline),
            ExecutionDefinitionJsonSerializer.Serialize(changed));

        var roundTrip = ExecutionDefinitionJsonSerializer.Deserialize(
            ExecutionDefinitionJsonSerializer.Serialize(changed));

        Assert.Equal(new ExecutionDefinitionId("definition/renamed"), roundTrip.Metadata.DefinitionId);
        Assert.Equal(new ExecutionRevisionId("revision/99"), roundTrip.Metadata.RevisionId);
        Assert.Equal("Renamed transition", roundTrip.Metadata.DisplayName);
        Assert.Equal("Different human description", roundTrip.Metadata.Description);
        Assert.Equal(changed.Metadata.Provenance, roundTrip.Metadata.Provenance);
        Assert.Equal(changed.Metadata.SourceMap, roundTrip.Metadata.SourceMap);
        Assert.True(changed.Metadata.Diagnostics.SequenceEqual(roundTrip.Metadata.Diagnostics));
    }

    [Fact]
    public void TypeRichExtensionSourceMapAndTypedBody_RoundTripToIdenticalCanonicalBytes()
    {
        var extension = TypeRichExtension();
        var sourceMap = new ExecutionSourceMap([
            new(
                "src/Transition.cs:48",
                new(["steps", "reserve/inventory"]),
                "Reserve inventory effect"),
            new(
                "notion:execution-kernel#a~b",
                new(["steps", "a~b"]),
                "Escaped source-map path")
        ]);
        var document = CreateDocument(
            extensions: [extension],
            displayName: "Inventory transition",
            description: "Representative durable transition.",
            provenance: Provenance(
                producer: "cohesive-csharp",
                sourceReference: "src/Transition.cs",
                sourcePath: new(["transition", "inventory"])),
            sourceMap: sourceMap,
            diagnostics:
            [
                new(
                    "authoring.reviewed",
                    DiagnosticSeverity.Info,
                    "The transition was reviewed.",
                    "/definition/orderedSteps/0",
                    "#/properties/orderedSteps/items")
            ]);

        var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(document);
        var restored = ExecutionDefinitionJsonSerializer.Deserialize(Encoding.UTF8.GetString(canonical));
        var restoredCanonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(restored);
        var typed = restored.GetDefinition<TransitionDefinition>();

        Assert.Equal(canonical, restoredCanonical);
        Assert.Equal(document, restored);
        Assert.Equal(document.GetHashCode(), restored.GetHashCode());
        Assert.Equal(document.Metadata.Fingerprint, restored.Metadata.Fingerprint);
        Assert.Equal(document.Metadata.Provenance, restored.Metadata.Provenance);
        Assert.Equal(sourceMap, restored.Metadata.SourceMap);
        Assert.Equal(
            ["/steps/a~0b", "/steps/reserve~1inventory"],
            restored.Metadata.SourceMap.Entries.Select(static entry => entry.SemanticPath!.Value.ToString()));
        Assert.True(document.Metadata.Diagnostics.SequenceEqual(restored.Metadata.Diagnostics));
        Assert.Equal("start", typed.Entry);
        Assert.Equal(["reserve", "commit"], typed.OrderedSteps.ToArray());
        Assert.Equal(1, typed.SemanticObject["alpha"]);
        Assert.Equal(2, typed.SemanticObject["zeta"]);

        var restoredExtension = Assert.Single(restored.Extensions);
        Assert.Equal(extension.Id, restoredExtension.Id);
        Assert.Equal(extension.SchemaVersion, restoredExtension.SchemaVersion);
        var objectType = Assert.IsType<ObjectTypeRef>(restoredExtension.Value.Contract.Type);
        Assert.Collection(
            objectType.Fields,
            field =>
            {
                Assert.Equal("mode", field.Name);
                var enumType = Assert.IsType<EnumTypeRef>(field.Type);
                Assert.Equal(["adaptive", "fixed"], enumType.Members.ToArray());
            },
            field =>
            {
                Assert.Equal("batchSizes", field.Name);
                Assert.Equal(FieldCardinality.Many, field.Cardinality);
                Assert.Equal(FieldPresence.Optional, field.Presence);
                Assert.Equal(FieldNullability.Nullable, field.Nullability);
            });
        var extensionFields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, ObservationValue>>(
            restoredExtension.Value.Value!.Value.Fields);
        Assert.Equal("adaptive", extensionFields["mode"].String);
        Assert.Equal(
            [32L, 64L],
            extensionFields["batchSizes"].Array.Select(static item => item.Int64));
    }

    [Fact]
    public void NamedExtensionContract_RoundTripsWithGraphWhileGraphlessValidationReportsUnresolvedType()
    {
        TypeId settingsTypeId = new("execution/control-settings");
        var graph = new ShapeGraph(
            new("execution/serialization-tests"),
            [],
            [
                new TypeDefinition.Structural(
                    settingsTypeId,
                    [
                        new(
                            new("mode"),
                            new ScalarTypeRef(ScalarTypeKind.String))
                    ])
            ]);
        var extension = new ExecutionDefinitionExtension(
            new("cohesive.control.settings"),
            new("cohesive-control-settings/v1"),
            PortableValue.Concrete(
                new(new NamedTypeRef(settingsTypeId)),
                ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["mode"] = ObservationValue.FromString("adaptive")
                })));
        var json = ExecutionDefinitionJsonSerializer.Serialize(
            CreateDocument(extensions: [extension]));

        var graphlessValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            json,
            out var graphlessDocument);
        var graphValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            json,
            graph,
            out var graphDocument);
        var restored = ExecutionDefinitionJsonSerializer.Deserialize(json, graph);

        Assert.NotNull(graphlessDocument);
        Assert.False(graphlessValidation.IsValid);
        AssertDiagnostic(
            graphlessValidation,
            PortableExecutionDiagnosticCodes.UnresolvedType,
            "/extensions/0/value/contract/type");
        Assert.NotNull(graphDocument);
        Assert.True(graphValidation.IsValid);
        Assert.Empty(graphValidation.Diagnostics);
        var restoredType = Assert.IsType<NamedTypeRef>(
            Assert.Single(restored.Extensions).Value.Contract.Type);
        Assert.Equal(settingsTypeId, restoredType.TypeId);
    }

    [Fact]
    public void Deserialize_RejectsUnknownAndDuplicateJsonPropertiesAtStrictBoundaries()
    {
        var document = CreateDocument(extensions: [StringExtension("example.mode", "adaptive")]);
        var json = ExecutionDefinitionJsonSerializer.Serialize(document);

        var unknownRoot = JsonNode.Parse(json)!.AsObject();
        unknownRoot["unexpected"] = true;
        var unknownRootValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            unknownRoot.ToJsonString(),
            out var unknownRootDocument);

        Assert.Null(unknownRootDocument);
        AssertDiagnostic(unknownRootValidation, ExecutionDefinitionDiagnosticCodes.DeserializationInvalid);

        var unknownPortableValue = JsonNode.Parse(json)!.AsObject();
        unknownPortableValue["extensions"]![0]!["value"]!["unexpected"] = true;
        var unknownPortableValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            unknownPortableValue.ToJsonString(),
            out var unknownPortableDocument);

        Assert.Null(unknownPortableDocument);
        AssertDiagnostic(unknownPortableValidation, ExecutionDefinitionDiagnosticCodes.DeserializationInvalid);

        var duplicate = json.Replace(
            "\"entry\":\"start\"",
            "\"entry\":\"start\",\"entry\":\"start\"",
            StringComparison.Ordinal);
        var duplicateValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            duplicate,
            out var duplicateDocument);

        Assert.Null(duplicateDocument);
        AssertDiagnostic(
            duplicateValidation,
            ExecutionDefinitionDiagnosticCodes.JsonDuplicateProperty,
            "/definition/entry");

        using var directDuplicate = JsonDocument.Parse(
            """
            {
              "entry": "start",
              "nested": {
                "value": 1,
                "value": 2
              }
            }
            """);
        var directException = Assert.Throws<ArgumentException>(
            () => DocumentFromJson(directDuplicate.RootElement));
        Assert.Contains("/nested/value", directException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedProjection_RejectsUnknownDefinitionMembers()
    {
        using var parsed = JsonDocument.Parse(
            """
            {
              "entry": "start",
              "orderedSteps": ["reserve", "commit"],
              "semanticObject": {"alpha": 1, "zeta": 2},
              "unexpected": true
            }
            """);
        var document = DocumentFromJson(parsed.RootElement);

        Assert.Throws<JsonException>(() => document.GetDefinition<TransitionDefinition>());
    }

    [Fact]
    public void TypedProjection_RejectsQuotedJsonNumbersUnderStrictOptions()
    {
        using var parsed = JsonDocument.Parse(
            """
            {
              "entry": "start",
              "orderedSteps": ["reserve", "commit"],
              "semanticObject": {"alpha": "1", "zeta": 2}
            }
            """);
        var document = DocumentFromJson(parsed.RootElement);

        Assert.Throws<JsonException>(() => document.GetDefinition<TransitionDefinition>());
    }

    [Fact]
    public void ExactJsonNumbers_NormalizeEquivalentSpellingsAndDistinguishHugeValues()
    {
        using var integerJson = JsonDocument.Parse(DefinitionJson("1"));
        using var equivalentDecimalJson = JsonDocument.Parse(DefinitionJson("1.0e0"));
        using var hugeJson = JsonDocument.Parse(DefinitionJson("1e400"));
        const string OtherHugeLiteral = "1.0000000000000000000000000000000000000001e400";
        using var otherHugeJson = JsonDocument.Parse(DefinitionJson(OtherHugeLiteral));
        var integer = DocumentFromJson(integerJson.RootElement);
        var equivalentDecimal = DocumentFromJson(equivalentDecimalJson.RootElement);
        var huge = DocumentFromJson(hugeJson.RootElement);
        var otherHuge = DocumentFromJson(otherHugeJson.RootElement);

        Assert.Equal(integer.Metadata.Fingerprint, equivalentDecimal.Metadata.Fingerprint);
        Assert.Equal("1", integer.Definition.GetProperty("semanticObject").GetProperty("alpha").GetRawText());
        Assert.Equal("1", equivalentDecimal.Definition.GetProperty("semanticObject").GetProperty("alpha").GetRawText());
        Assert.NotEqual(huge.Metadata.Fingerprint, otherHuge.Metadata.Fingerprint);

        var hugeValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(huge),
            out var restoredHuge);
        var otherHugeValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(otherHuge),
            out var restoredOtherHuge);

        Assert.True(hugeValidation.IsValid);
        Assert.True(otherHugeValidation.IsValid);
        Assert.Equal(huge, restoredHuge);
        Assert.Equal(otherHuge, restoredOtherHuge);
        Assert.Equal("1e400", restoredHuge!.Definition.GetProperty("semanticObject").GetProperty("alpha").GetRawText());
        Assert.Equal(
            OtherHugeLiteral,
            restoredOtherHuge!.Definition.GetProperty("semanticObject").GetProperty("alpha").GetRawText());
    }

    [Theory]
    [InlineData("-0", "0", "0")]
    [InlineData("1000", "1e3", "1000")]
    [InlineData("0.0000010", "1e-6", "0.000001")]
    public void EquivalentExactJsonNumberSpellings_NormalizeToOnePersistedValue(
        string leftLiteral,
        string rightLiteral,
        string expected)
    {
        using var leftJson = JsonDocument.Parse(DefinitionJson(leftLiteral));
        using var rightJson = JsonDocument.Parse(DefinitionJson(rightLiteral));
        var left = DocumentFromJson(leftJson.RootElement);
        var right = DocumentFromJson(rightJson.RootElement);

        Assert.Equal(left.Metadata.Fingerprint, right.Metadata.Fingerprint);
        Assert.Equal(
            expected,
            left.Definition.GetProperty("semanticObject").GetProperty("alpha").GetRawText());
        Assert.Equal(left, right);
    }

    [Fact]
    public void CanonicalWriter_RejectsMalformedObjectSetItems()
    {
        JsonObject content = new()
        {
            ["items"] = new JsonArray(
                new JsonObject { ["id"] = "valid" },
                new JsonObject { ["value"] = 1 })
        };

        Assert.Throws<InvalidOperationException>(() => CanonicalJsonWriter.GetCanonicalBytes(
            content,
            ExecutionDefinitionJsonSerializer.CreateOptions(),
            static path => path.Value == "/items"
                ? CanonicalJsonArrayOrdering.ObjectSet("id")
                : CanonicalJsonArrayOrdering.Sequence));
    }

    [Fact]
    public void ExactNumberCanonicalization_NormalizesProgrammaticJsonValues()
    {
        JsonObject programmatic = new()
        {
            ["decimal"] = JsonValue.Create(1.0m),
            ["negativeZero"] = JsonValue.Create(-0d)
        };
        var canonical = CanonicalJsonWriter.GetCanonicalBytes(
            programmatic,
            ExecutionDefinitionJsonSerializer.CreateOptions(),
            static _ => CanonicalJsonArrayOrdering.Sequence,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);

        Assert.Equal("{\"decimal\":1,\"negativeZero\":0}", Encoding.UTF8.GetString(canonical));
    }

    [Fact]
    public void TryDeserialize_DistinguishesUnsupportedSchemaAndFingerprintFailures()
    {
        var json = ExecutionDefinitionJsonSerializer.Serialize(CreateDocument());

        var priorSchema = JsonNode.Parse(json)!.AsObject();
        priorSchema["metadata"]!["schemaVersion"] = "cohesive-execution/v1";
        var priorSchemaValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            priorSchema.ToJsonString(),
            out var priorSchemaDocument);

        Assert.Null(priorSchemaDocument);
        AssertDiagnostic(
            priorSchemaValidation,
            ExecutionDefinitionDiagnosticCodes.SchemaVersionUnsupported,
            "/metadata/schemaVersion");

        var unsupportedSchema = JsonNode.Parse(json)!.AsObject();
        unsupportedSchema["metadata"]!["schemaVersion"] = "cohesive-execution/v99";
        var unsupportedSchemaValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            unsupportedSchema.ToJsonString(),
            out var unsupportedSchemaDocument);

        Assert.Null(unsupportedSchemaDocument);
        AssertDiagnostic(
            unsupportedSchemaValidation,
            ExecutionDefinitionDiagnosticCodes.SchemaVersionUnsupported,
            "/metadata/schemaVersion");

        var originalDocument = CreateDocument();
        var futureCompatibility = new ExecutionDefinitionCompatibilityDeclaration(
            new([new("cohesive-execution/v99")]),
            [originalDocument.Kind],
            [
                new(
                    originalDocument.Metadata.DefinitionId,
                    originalDocument.Metadata.RevisionId,
                    originalDocument.Metadata.Fingerprint)
            ]);
        var unsupportedCodecValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            unsupportedSchema.ToJsonString(),
            futureCompatibility,
            out var unsupportedCodecDocument);

        Assert.Null(unsupportedCodecDocument);
        AssertDiagnostic(
            unsupportedCodecValidation,
            ExecutionDefinitionDiagnosticCodes.SchemaVersionUnsupported,
            "/metadata/schemaVersion");

        var unsupportedProfile = JsonNode.Parse(json)!.AsObject();
        unsupportedProfile["metadata"]!["fingerprint"]!["canonicalization"] =
            "cohesive-execution-definition/v99-c14n/v1";
        var unsupportedProfileValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            unsupportedProfile.ToJsonString(),
            out var unsupportedProfileDocument);

        Assert.NotNull(unsupportedProfileDocument);
        AssertDiagnostic(
            unsupportedProfileValidation,
            ExecutionDefinitionDiagnosticCodes.FingerprintProfileUnsupported,
            "/metadata/fingerprint");

        var malformedFingerprint = JsonNode.Parse(json)!.AsObject();
        malformedFingerprint["metadata"]!["fingerprint"]!["value"] = new string('A', 64);
        var malformedFingerprintValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            malformedFingerprint.ToJsonString(),
            out var malformedFingerprintDocument);

        Assert.NotNull(malformedFingerprintDocument);
        AssertDiagnostic(
            malformedFingerprintValidation,
            ExecutionDefinitionDiagnosticCodes.FingerprintValueInvalid,
            "/metadata/fingerprint/value");

        var mismatchedFingerprint = JsonNode.Parse(json)!.AsObject();
        mismatchedFingerprint["metadata"]!["fingerprint"]!["value"] = new string('0', 64);
        var mismatchedFingerprintValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            mismatchedFingerprint.ToJsonString(),
            out var mismatchedFingerprintDocument);

        Assert.NotNull(mismatchedFingerprintDocument);
        AssertDiagnostic(
            mismatchedFingerprintValidation,
            ExecutionDefinitionDiagnosticCodes.FingerprintMismatch,
            "/metadata/fingerprint/value");
    }

    [Fact]
    public void InvalidPortableExtension_PreservesGranularDiagnosticCodeAndPrefixedLocation()
    {
        var extension = new ExecutionDefinitionExtension(
            new("example.required-mode"),
            new("example-required-mode/v1"),
            PortableValue.Missing(new(new ScalarTypeRef(ScalarTypeKind.String))));
        var document = CreateDocument(extensions: [extension]);

        var validation = ExecutionDefinitionDocumentValidator.Validate(document);

        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal(PortableExecutionDiagnosticCodes.PresenceMismatch, diagnostic.Code);
        Assert.Equal("/extensions/0/value/state", diagnostic.Location);
    }

    [Fact]
    public void FailedPortableExtension_FingerprintRetainsStableFailureSemanticsButExcludesAttribution()
    {
        var baseline = CreateDocument(extensions:
        [
            FailedExtension(
                code: "source.timeout",
                message: "Worker 17 timed out.",
                location: "/hosts/worker-17/request-42",
                schemaLocation: "#/hostFailures/timeout")
        ]);
        var changedAttribution = CreateDocument(extensions:
        [
            FailedExtension(
                code: "source.timeout",
                message: "A differently worded timeout from another producer.",
                location: "/hosts/worker-99/request-8",
                schemaLocation: "#/producerSpecific/timeout")
        ]);
        var changedFailureCode = CreateDocument(extensions:
        [
            FailedExtension(
                code: "source.permissionDenied",
                message: "Worker 17 timed out.",
                location: "/hosts/worker-17/request-42",
                schemaLocation: "#/hostFailures/timeout")
        ]);
        var unknown = CreateDocument(extensions:
        [
            new(
                new("example.runtime-failure"),
                new("example-runtime-failure/v1"),
                PortableValue.Unknown(new(new ScalarTypeRef(ScalarTypeKind.String))))
        ]);

        Assert.Equal(baseline.Metadata.Fingerprint, changedAttribution.Metadata.Fingerprint);
        Assert.Equal(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(baseline),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(changedAttribution));
        Assert.NotEqual(baseline.Metadata.Fingerprint, changedFailureCode.Metadata.Fingerprint);
        Assert.NotEqual(baseline.Metadata.Fingerprint, unknown.Metadata.Fingerprint);
        var normalized = Encoding.UTF8.GetString(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(baseline));
        Assert.Contains("\"failure\":{\"code\":\"source.timeout\"}", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"message\"", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"location\"", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"schemaLocation\"", normalized, StringComparison.Ordinal);
        Assert.NotEqual(
            ExecutionDefinitionJsonSerializer.Serialize(baseline),
            ExecutionDefinitionJsonSerializer.Serialize(changedAttribution));

        var changedWire = JsonNode.Parse(ExecutionDefinitionJsonSerializer.Serialize(baseline))!.AsObject();
        var failure = changedWire["extensions"]![0]!["value"]!["failure"]!.AsObject();
        failure["message"] = "Wire-specific timeout prose.";
        failure["location"] = "/hosts/worker-wire/request-23";
        failure["schemaLocation"] = "#/wireSpecific/timeout";

        var validation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            changedWire.ToJsonString(),
            out var restored);

        Assert.NotNull(restored);
        Assert.True(validation.IsValid);
        Assert.Equal(baseline.Metadata.Fingerprint, restored.Metadata.Fingerprint);
        var restoredFailure = Assert.Single(restored.Extensions).Value.Failure;
        Assert.NotNull(restoredFailure);
        Assert.Equal("Wire-specific timeout prose.", restoredFailure.Message);
        Assert.Equal("/hosts/worker-wire/request-23", restoredFailure.Location);
        Assert.Equal("#/wireSpecific/timeout", restoredFailure.SchemaLocation);
    }

    [Fact]
    public void RetainedDiagnostics_CannotChangeIntegrityOrActivationWithoutFingerprintEvidence()
    {
        var baseline = CreateDocument();
        var withRetainedError = CreateDocument(
            diagnostics:
            [
                new(
                    "authoring.external-error",
                    DiagnosticSeverity.Error,
                    "A retained producer observation is not authoritative activation evidence.",
                    "/definition/entry")
            ]);
        var compatibility = CompatibilityFor(baseline);

        Assert.Equal(baseline.Metadata.Fingerprint, withRetainedError.Metadata.Fingerprint);
        var baselineValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(baseline),
            compatibility,
            out var baselineDocument);
        var retainedValidation = ExecutionDefinitionJsonSerializer.TryDeserialize(
            ExecutionDefinitionJsonSerializer.Serialize(withRetainedError),
            compatibility,
            out var retainedDocument);

        Assert.NotNull(baselineDocument);
        Assert.NotNull(retainedDocument);
        Assert.True(baselineValidation.IsValid);
        Assert.True(retainedValidation.IsValid);
        Assert.Empty(retainedValidation.Diagnostics);
        var retainedDiagnostic = Assert.Single(retainedDocument.Metadata.Diagnostics);
        Assert.Equal("authoring.external-error", retainedDiagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, retainedDiagnostic.Severity);
    }

    [Fact]
    public void ExtensionInputOrder_ExhaustivelyNormalizesAndRemainsIdempotent()
    {
        ImmutableArray<ExecutionDefinitionExtension> extensions =
        [
            StringExtension("z.extension", "z"),
            StringExtension("a.extension", "a"),
            StringExtension("m.extension", "m")
        ];
        var expected = CreateDocument(extensions: extensions);
        var expectedNormalized = ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(expected);
        var expectedCanonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(expected);

        var permutationCount = 0;
        foreach (var permutation in Permutations(extensions))
        {
            permutationCount++;
            var actual = CreateDocument(extensions: permutation);
            var actualNormalized = ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(actual);
            var actualCanonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(actual);
            var roundTrip = ExecutionDefinitionJsonSerializer.Deserialize(Encoding.UTF8.GetString(actualCanonical));

            Assert.Equal(
                ["a.extension", "m.extension", "z.extension"],
                actual.Extensions.Select(static extension => extension.Id.Value));
            Assert.Equal(expectedNormalized, actualNormalized);
            Assert.Equal(expected.Metadata.Fingerprint, actual.Metadata.Fingerprint);
            Assert.Equal(expectedCanonical, actualCanonical);
            Assert.Equal(
                actualNormalized,
                ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(roundTrip));
            Assert.Equal(actualCanonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(roundTrip));
        }

        Assert.Equal(6, permutationCount);
    }

    [Fact]
    public void FixedSeedSemanticObjectOrder_IsFingerprintInvariantAndNormalizationIsIdempotent()
    {
        const int PropertySeed = 155_108;
        string[] keys = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot"];
        var expected = CreateDocument(definition: Definition(semanticObject: keys.ToDictionary(
            static key => key,
            static key => key.Length,
            StringComparer.Ordinal)));
        var expectedNormalized = ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(expected);
        var expectedFingerprint = expected.Metadata.Fingerprint;
        var random = new Random(PropertySeed);

        for (var iteration = 0; iteration < 64; iteration++)
        {
            var shuffled = keys.ToArray();
            Shuffle(shuffled, random);
            Dictionary<string, int> semanticObject = new(StringComparer.Ordinal);
            foreach (var key in shuffled)
            {
                semanticObject.Add(key, key.Length);
            }

            var actual = CreateDocument(definition: Definition(semanticObject: semanticObject));
            var normalized = ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(actual);
            var canonical = ExecutionDefinitionJsonSerializer.GetCanonicalBytes(actual);
            var roundTrip = ExecutionDefinitionJsonSerializer.Deserialize(Encoding.UTF8.GetString(canonical));

            Assert.True(
                expectedNormalized.AsSpan().SequenceEqual(normalized),
                $"Canonical semantic bytes changed for property seed {PropertySeed}, iteration {iteration}.");
            Assert.Equal(expectedFingerprint, actual.Metadata.Fingerprint);
            Assert.Equal(
                normalized,
                ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(roundTrip));
            Assert.Equal(canonical, ExecutionDefinitionJsonSerializer.GetCanonicalBytes(roundTrip));
        }
    }

    static ExecutionDefinitionDocument CreateDocument(
        TransitionDefinition? definition = null,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default,
        string definitionId = "definition/inventory-transition",
        string revisionId = "revision/1",
        string? displayName = null,
        string? description = null,
        ExecutionProvenance? provenance = null,
        ExecutionSourceMap? sourceMap = null,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics = default) =>
        ExecutionDefinitionDocument.Create(
            TransitionKind,
            new(definitionId),
            new(revisionId),
            definition ?? Definition(),
            provenance ?? Provenance(),
            extensions,
            displayName,
            description,
            sourceMap,
            diagnostics);

    static TransitionDefinition Definition(
        string entry = "start",
        ImmutableArray<string> orderedSteps = default,
        Dictionary<string, int>? semanticObject = null) =>
        new(
            entry,
            orderedSteps.IsDefault ? ["reserve", "commit"] : orderedSteps,
            semanticObject ?? new(StringComparer.Ordinal)
            {
                ["zeta"] = 2,
                ["alpha"] = 1
            });

    static string DefinitionJson(string alpha) =>
        $$"""
        {
          "entry": "start",
          "orderedSteps": ["reserve", "commit"],
          "semanticObject": {"alpha": {{alpha}}, "zeta": 2}
        }
        """;

    static ExecutionProvenance Provenance(
        string producer = "execution-tests",
        string sourceReference = "tests/execution-definition",
        ExecutionSemanticPath? sourcePath = null) =>
        new(
            new(producer, "1.0"),
            new(sourceReference, sourcePath ?? new(["transition", "inventory"])),
            DocumentOrigin.Compiled);

    static ExecutionDefinitionExtension StringExtension(string id, string value) =>
        new(
            new(id),
            new("example-string/v1"),
            PortableValue.Concrete(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                ObservationValue.FromString(value)));

    static ExecutionDefinitionExtension FailedExtension(
        string code,
        string message,
        string? location,
        string? schemaLocation) =>
        new(
            new("example.runtime-failure"),
            new("example-runtime-failure/v1"),
            PortableValue.Failed(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                new(code, DiagnosticSeverity.Error, message, location, schemaLocation)));

    static ExecutionDefinitionExtension TypeRichExtension()
    {
        var contract = new ValueContract(new ObjectTypeRef([
            new(
                "mode",
                new EnumTypeRef("ThrottleMode", ["adaptive", "fixed"])),
            new(
                "batchSizes",
                new ScalarTypeRef(ScalarTypeKind.Int64),
                cardinality: FieldCardinality.Many,
                presence: FieldPresence.Optional,
                nullability: FieldNullability.Nullable)
        ]));
        var value = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
        {
            ["batchSizes"] = ObservationValue.FromArray([
                ObservationValue.FromInt64(32),
                ObservationValue.FromInt64(64)
            ]),
            ["mode"] = ObservationValue.FromString("adaptive")
        });
        return new(
            new("cohesive.control.profile"),
            new("cohesive-control-profile/v1"),
            PortableValue.Concrete(contract, value));
    }

    static ExecutionDefinitionDocument DocumentFromJson(JsonElement definition)
    {
        var fingerprint = ExecutionDefinitionFingerprinter.Compute(
            ExecutionDefinitionDocument.CurrentSchemaVersion,
            TransitionKind,
            definition);
        var metadata = new ExecutionDefinitionMetadata(
            new("definition/typed-projection"),
            new("revision/1"),
            ExecutionDefinitionDocument.CurrentSchemaVersion,
            fingerprint,
            Provenance());
        return new(TransitionKind, metadata, definition);
    }

    static ExecutionDefinitionCompatibilityDeclaration CompatibilityFor(
        ExecutionDefinitionDocument document) =>
        new(
            new([document.Metadata.SchemaVersion]),
            [document.Kind],
            [
                new(
                    document.Metadata.DefinitionId,
                    document.Metadata.RevisionId,
                    document.Metadata.Fingerprint)
            ]);

    static void AssertDifferentSemantics(
        ExecutionDefinitionDocument expected,
        ExecutionDefinitionDocument actual)
    {
        Assert.NotEqual(
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(expected),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(actual));
        Assert.NotEqual(expected.Metadata.Fingerprint, actual.Metadata.Fingerprint);
    }

    static void AssertDiagnostic(
        DocumentValidationResult validation,
        string code,
        string? location = null)
    {
        var diagnostic = Assert.Single(
            validation.Diagnostics,
            candidate => string.Equals(candidate.Code, code, StringComparison.Ordinal));
        if (location is not null)
        {
            Assert.Equal(location, diagnostic.Location);
        }
    }

    static IEnumerable<ImmutableArray<T>> Permutations<T>(ImmutableArray<T> values)
    {
        var buffer = values.ToArray();
        return Permute(index: 0);

        IEnumerable<ImmutableArray<T>> Permute(int index)
        {
            if (index == buffer.Length)
            {
                yield return [.. buffer];
                yield break;
            }

            for (var candidate = index; candidate < buffer.Length; candidate++)
            {
                (buffer[index], buffer[candidate]) = (buffer[candidate], buffer[index]);
                foreach (var permutation in Permute(index + 1))
                {
                    yield return permutation;
                }
                (buffer[index], buffer[candidate]) = (buffer[candidate], buffer[index]);
            }
        }
    }

    static void Shuffle<T>(T[] values, Random random)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var candidate = random.Next(index + 1);
            (values[index], values[candidate]) = (values[candidate], values[index]);
        }
    }

    sealed record TransitionDefinition(
        string Entry,
        ImmutableArray<string> OrderedSteps,
        Dictionary<string, int> SemanticObject);
}
