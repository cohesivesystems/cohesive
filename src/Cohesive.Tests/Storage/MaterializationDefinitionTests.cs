using System.Collections.Immutable;
using System.Text;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.TestFixtures;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationDefinitionTests
{
    const long EvidenceReadBytes = 4_096;
    const long DefinitionWriteItems = 100;
    const long DefinitionWriteBytes = 1_000_000;

    [Fact]
    public void OneDefinition_ExpressesRebuildAndIncrementalRequirements()
    {
        var definition = CreateDefinition(reverseDeclarations: false);

        Assert.Equal(MaterializationSynchronizationMode.All, definition.UpdatePolicy.SupportedModes);
        Assert.Contains(
            definition.GetSourceCapabilities(MaterializationSynchronizationMode.Rebuild),
            static requirement => requirement.Capability == MaterializationCapabilityKind.SourceBoundedEnumeration);
        Assert.Contains(
            definition.GetSourceCapabilities(MaterializationSynchronizationMode.Incremental),
            static requirement => requirement.Capability == MaterializationCapabilityKind.SourceChangeDelivery);
        Assert.Contains(
            definition.GetTargetCapabilities(MaterializationSynchronizationMode.Rebuild),
            static requirement => requirement.Capability == MaterializationCapabilityKind.TargetFencedPromotion);
        Assert.Contains(
            definition.GetTargetCapabilities(MaterializationSynchronizationMode.Incremental),
            static requirement => requirement.Capability == MaterializationCapabilityKind.TargetBulkDelete);
        Assert.True(MaterializationDefinitionValidator.Validate(definition).IsValid);
    }

    [Fact]
    public void Document_RoundTripsExactRelationsProvenanceAndDependencyAuthority()
    {
        var document = MaterializationDocument.FromDefinition(CreateDefinition(reverseDeclarations: false));
        var canonical = MaterializationJsonSerializer.GetCanonicalBytes(document);

        var restored = MaterializationJsonSerializer.Deserialize(Encoding.UTF8.GetString(canonical));
        var restoredCanonical = MaterializationJsonSerializer.GetCanonicalBytes(restored);
        var compilation = restored.Definition.Relation.Compile();

        Assert.Equal(canonical, restoredCanonical);
        Assert.Equal(document.DefinitionFingerprint, restored.DefinitionFingerprint);
        Assert.True(compilation.IsSuccessful);
        Assert.NotNull(compilation.Plan);
        Assert.NotEmpty(compilation.Plan!.DependencyManifest.Entries);
        Assert.Equal(
            document.Definition.Relation.CompiledPlanFingerprint,
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(compilation.Plan)));
        Assert.Contains(
            compilation.Plan.RequirementGraph.Outputs,
            output => output == restored.Definition.Relation.Output);
    }

    [Fact]
    public void Fingerprint_IsIndependentOfSetLikeDeclarationOrder()
    {
        var forward = MaterializationDocument.FromDefinition(CreateDefinition(reverseDeclarations: false));
        var reverse = MaterializationDocument.FromDefinition(CreateDefinition(reverseDeclarations: true));

        Assert.Equal(forward.DefinitionFingerprint, reverse.DefinitionFingerprint);
        Assert.Equal(
            MaterializationJsonSerializer.GetCanonicalBytes(forward),
            MaterializationJsonSerializer.GetCanonicalBytes(reverse));
    }

    [Fact]
    public void Validator_RejectsStaleCompiledPlanFingerprint()
    {
        var valid = CreateDefinition(reverseDeclarations: false);
        MaterializationRelationReference relation = new(
            valid.Relation.CompilationRequest,
            valid.Relation.CompiledPlan,
            new(
                RelationQueryCompiledPlanReferenceFingerprinter.Algorithm,
                RelationQueryCompiledPlanReferenceFingerprinter.Canonicalization,
                new string('0', 64)),
            valid.Relation.Output);
        MaterializationDefinition stale = new(
            valid.Id,
            relation,
            valid.Sources,
            valid.TargetCapabilities,
            valid.UpdatePolicy,
            valid.FailurePolicy,
            valid.FreshnessPolicy,
            valid.ControlLoops,
            valid.Provenance);

        var validation = MaterializationDefinitionValidator.Validate(stale);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.PlanFingerprintMismatch);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.PlanReferenceMismatch);
    }

    [Fact]
    public void StrictReader_RejectsUnknownAndDuplicateProperties()
    {
        var document = MaterializationDocument.FromDefinition(CreateDefinition(reverseDeclarations: false));
        var json = MaterializationJsonSerializer.Serialize(document, PortableDocumentJsonFormatting.Compact);
        var unknown = json.Insert(1, "\"unknown\":true,");
        var duplicate = json.Replace(
            "\"schemaVersion\":\"cohesive-materialization/v1\"",
            "\"schemaVersion\":\"cohesive-materialization/v1\",\"schemaVersion\":\"cohesive-materialization/v1\"",
            StringComparison.Ordinal);

        var unknownValidation = MaterializationJsonSerializer.TryDeserialize(unknown, out _);
        var duplicateValidation = MaterializationJsonSerializer.TryDeserialize(duplicate, out _);

        Assert.False(unknownValidation.IsValid);
        Assert.Contains(
            unknownValidation.Diagnostics,
            static diagnostic => diagnostic.Code == "materialization.json.deserializationInvalid");
        Assert.False(duplicateValidation.IsValid);
        Assert.Contains(
            duplicateValidation.Diagnostics,
            static diagnostic => diagnostic.Code == "materialization.json.duplicateProperty");
    }

    [Fact]
    public void StrictReader_DispatchesUnsupportedSchemaBeforeCurrentContractDeserialization()
    {
        const string DocumentJson = """
            {"schemaVersion":"cohesive-materialization/v2","futureField":true}
            """;

        var validation = MaterializationJsonSerializer.TryDeserialize(DocumentJson, out var restored);

        Assert.Null(restored);
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal("materialization.schemaVersion.unsupported", diagnostic.Code);
        Assert.Equal("/schemaVersion", diagnostic.Location);
    }

    [Fact]
    public void CapabilityMatcher_RequiresGuaranteesAndSufficientHardLimits()
    {
        MaterializationCapabilityRequirement requirement = new(
            new("source/enumeration"),
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            [MaterializationGuaranteeKind.StableOrdering],
            [new(MaterializationLimitKind.ReadItems, 100)],
            MaterializationSynchronizationMode.Rebuild);
        var exact = Profile(
            new(
                new("evidence/enumeration"),
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                MaterializationCapabilityRealizationKind.Constrained,
                [MaterializationGuaranteeKind.StableOrdering],
                [
                    new(MaterializationLimitKind.ReadItems, 128),
                    new(MaterializationLimitKind.ReadBytes, EvidenceReadBytes)
                ],
                ["adapter/postgres/v1"]));
        var tooSmall = Profile(
            new(
                new("evidence/enumeration"),
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                MaterializationCapabilityRealizationKind.Constrained,
                [MaterializationGuaranteeKind.StableOrdering],
                [
                    new(MaterializationLimitKind.ReadItems, 64),
                    new(MaterializationLimitKind.ReadBytes, EvidenceReadBytes)
                ],
                ["adapter/postgres/v1"]));

        var accepted = MaterializationCapabilityMatcher.MatchForMode(
            [requirement],
            exact,
            MaterializationSynchronizationMode.Rebuild);
        var rejected = MaterializationCapabilityMatcher.MatchForMode(
            [requirement],
            tooSmall,
            MaterializationSynchronizationMode.Rebuild);

        Assert.True(accepted.IsSatisfied);
        Assert.Equal(MaterializationCapabilityRealizationKind.Constrained, accepted.Decisions[0].Realization);
        Assert.False(rejected.IsSatisfied);
        Assert.Contains(
            rejected.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationCapabilityDiagnosticCodes.LimitExceeded);
    }

    [Fact]
    public void CapabilityContracts_RejectDimensionsThatDoNotApplyToTheCapability()
    {
        Assert.False(MaterializationCapabilityCatalog.AllowsGuarantee(
            MaterializationCapabilityKind.SourceSettlement,
            MaterializationGuaranteeKind.AtomicPromotion));
        Assert.False(MaterializationCapabilityCatalog.AllowsLimit(
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            MaterializationLimitKind.WriteItems));

        var requirementGuarantee = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityRequirement(
                new("source/settlement"),
                MaterializationCapabilityKind.SourceSettlement,
                [MaterializationGuaranteeKind.AtomicPromotion]));
        var requirementLimit = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityRequirement(
                new("source/enumeration"),
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                operatingLimits: [new(MaterializationLimitKind.WriteItems, 100)]));
        var evidenceGuarantee = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityEvidence(
                new("evidence/settlement"),
                MaterializationCapabilityKind.SourceSettlement,
                MaterializationCapabilityRealizationKind.Native,
                [MaterializationGuaranteeKind.AtomicPromotion],
                [],
                ["adapter/source/v1"]));
        var evidenceLimit = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityEvidence(
                new("evidence/enumeration"),
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                MaterializationCapabilityRealizationKind.Native,
                [],
                [new(MaterializationLimitKind.WriteItems, 100)],
                ["adapter/source/v1"]));

        Assert.Equal("guarantees", requirementGuarantee.ParamName);
        Assert.Equal("operatingLimits", requirementLimit.ParamName);
        Assert.Equal("guarantees", evidenceGuarantee.ParamName);
        Assert.Equal("operatingLimits", evidenceLimit.ParamName);
    }

    [Theory]
    [InlineData(MaterializationCapabilityKind.SourceBatchedPointRead, MaterializationLimitKind.ReadItems)]
    [InlineData(MaterializationCapabilityKind.SourceParameterizedPredicateQuery, MaterializationLimitKind.ReadBytes)]
    [InlineData(MaterializationCapabilityKind.SourceBoundedEnumeration, MaterializationLimitKind.ReadBytes)]
    [InlineData(MaterializationCapabilityKind.SourceChangeDelivery, MaterializationLimitKind.ChangeItems)]
    [InlineData(MaterializationCapabilityKind.SourceChangeDelivery, MaterializationLimitKind.ReadBytes)]
    [InlineData(MaterializationCapabilityKind.TargetBulkUpsert, MaterializationLimitKind.WriteBytes)]
    [InlineData(MaterializationCapabilityKind.TargetBulkDelete, MaterializationLimitKind.WriteItems)]
    [InlineData(MaterializationCapabilityKind.TargetPerItemOutcomes, MaterializationLimitKind.WriteBytes)]
    public void CapabilityEvidence_RequiresEveryHardOperationBound(
        MaterializationCapabilityKind capability,
        MaterializationLimitKind omittedLimit)
    {
        var incompleteLimits = RequiredEvidenceLimits(capability)
            .Where(limit => limit != omittedLimit)
            .Select(static limit => new MaterializationOperatingLimit(limit, 100))
            .ToImmutableArray();

        var exception = Assert.Throws<ArgumentException>(() => new MaterializationCapabilityEvidence(
            new("evidence/incomplete"),
            capability,
            MaterializationCapabilityRealizationKind.Constrained,
            [],
            incompleteLimits,
            ["adapter/incomplete/v1"]));

        Assert.Equal("operatingLimits", exception.ParamName);
        Assert.Contains(omittedLimit.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PerItemOutcomeRequirement_DeclaresTheBulkRequestBoundsItCovers()
    {
        var exception = Assert.Throws<ArgumentException>(() => new MaterializationCapabilityRequirement(
            new("target/outcomes"),
            MaterializationCapabilityKind.TargetPerItemOutcomes,
            [MaterializationGuaranteeKind.ExactPerItemOutcome],
            [new(MaterializationLimitKind.WriteItems, DefinitionWriteItems)]));

        Assert.Equal("operatingLimits", exception.ParamName);
        Assert.Contains(nameof(MaterializationLimitKind.WriteBytes), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityMatcher_ReportsClosestAttributableEvidence()
    {
        MaterializationCapabilityRequirement requirement = new(
            new("source/enumeration"),
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            [
                MaterializationGuaranteeKind.StableOrdering,
                MaterializationGuaranteeKind.RequestLocalCompleteness
            ],
            [new(MaterializationLimitKind.ReadItems, 100)],
            MaterializationSynchronizationMode.Rebuild);
        MaterializationCapabilityProfile profile = new(
            new("profile/source/v1"),
            MaterializationEndpointRole.Source,
            "source/postgres",
            [
                new(
                    new("evidence/a-distant"),
                    MaterializationCapabilityKind.SourceBoundedEnumeration,
                    MaterializationCapabilityRealizationKind.Constrained,
                    guarantees: [],
                    operatingLimits:
                    [
                        new(MaterializationLimitKind.ReadItems, 1),
                        new(MaterializationLimitKind.ReadBytes, 1)
                    ],
                    sourceReferences: ["adapter/postgres/distant"]),
                new(
                    new("evidence/z-closest"),
                    MaterializationCapabilityKind.SourceBoundedEnumeration,
                    MaterializationCapabilityRealizationKind.Constrained,
                    [
                        MaterializationGuaranteeKind.StableOrdering,
                        MaterializationGuaranteeKind.RequestLocalCompleteness
                    ],
                    [
                        new(MaterializationLimitKind.ReadItems, 64),
                        new(MaterializationLimitKind.ReadBytes, EvidenceReadBytes)
                    ],
                    ["adapter/postgres/closest"])
            ]);

        var match = MaterializationCapabilityMatcher.MatchForMode(
            [requirement],
            profile,
            MaterializationSynchronizationMode.Rebuild);

        var diagnostic = Assert.Single(match.Validation.Diagnostics);
        Assert.Equal(MaterializationCapabilityDiagnosticCodes.LimitExceeded, diagnostic.Code);
        Assert.Contains("adapter/postgres/closest", diagnostic.Evidence!.SourceReferences);
        AssertCompleteDiagnostic(diagnostic);
    }

    [Fact]
    public void CapabilityMatch_NormalizesCallerSuppliedDiagnostics()
    {
        var later = CompleteDiagnostic("tests.capability.z", "/capabilities/z");
        var earlier = CompleteDiagnostic("tests.capability.a", "/capabilities/a");

        MaterializationCapabilityMatch match = new(
            [],
            new DocumentValidationResult([later, earlier]));

        Assert.Equal(
            ["tests.capability.a", "tests.capability.z"],
            match.Validation.Diagnostics.Select(static diagnostic => diagnostic.Code));
    }

    [Fact]
    public void CapabilityMatch_RejectsIncompleteCallerDiagnostics()
    {
        DocumentValidationDiagnostic incomplete = new(
            "tests.capability.incomplete",
            DiagnosticSeverity.Warning,
            "The diagnostic has no attributable evidence.");

        var exception = Assert.Throws<ArgumentException>(() => new MaterializationCapabilityMatch(
            [],
            new DocumentValidationResult([incomplete])));

        Assert.Equal("validation", exception.ParamName);
    }

    [Fact]
    public void Validator_RequiresEveryAcquisitionSourceAndItsOwnProtocolClosure()
    {
        var valid = CreateDefinition(reverseDeclarations: false);
        var first = valid.Sources[0];
        MaterializationSourceRequirement incomplete = new(
            first.Input,
            [.. first.Capabilities.Where(static requirement =>
                requirement.Capability != MaterializationCapabilityKind.SourceContinuation)]);
        MaterializationDefinition invalid = new(
            valid.Id,
            valid.Relation,
            [incomplete, .. valid.Sources.Skip(1)],
            valid.TargetCapabilities,
            valid.UpdatePolicy,
            valid.FailurePolicy,
            valid.FreshnessPolicy,
            valid.ControlLoops,
            valid.Provenance);

        var closureValidation = MaterializationDefinitionValidator.Validate(invalid);

        MaterializationSourceRequirement unrelated = new(
            new("input/not-in-plan"),
            first.Capabilities);
        MaterializationDefinition wrongSource = new(
            valid.Id,
            valid.Relation,
            [unrelated, .. valid.Sources.Skip(1)],
            valid.TargetCapabilities,
            valid.UpdatePolicy,
            valid.FailurePolicy,
            valid.FreshnessPolicy,
            valid.ControlLoops,
            valid.Provenance);
        var sourceValidation = MaterializationDefinitionValidator.Validate(wrongSource);

        Assert.False(closureValidation.IsValid);
        Assert.Contains(
            closureValidation.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.ProtocolCapabilityMissing
                && diagnostic.Location?.Contains(Uri.EscapeDataString(first.Input.Value), StringComparison.Ordinal) == true);
        Assert.False(sourceValidation.IsValid);
        Assert.Contains(
            sourceValidation.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.SourceRequirementMissing
                && diagnostic.Location?.Contains(Uri.EscapeDataString(first.Input.Value), StringComparison.Ordinal) == true);
        Assert.Contains(
            sourceValidation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.SourceInputMissing);
    }

    [Fact]
    public void Validator_IncludesTraversalAcquisitionsAndDerivesTheirReadCapability()
    {
        var valid = CreateDefinition(reverseDeclarations: false);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(valid.Relation.Compile().Plan);
        Assert.NotEmpty(plan.InputContract.Traversals);
        foreach (var traversal in plan.InputContract.Traversals)
        {
            var requirement = Assert.Single(valid.Sources, source => source.Input == traversal.Input.Id);
            var expected = traversal.Input.Direction == RelationshipTraversalDirection.Forward
                ? MaterializationCapabilityKind.SourceBatchedPointRead
                : MaterializationCapabilityKind.SourceParameterizedPredicateQuery;
            Assert.Contains(requirement.Capabilities, candidate => candidate.Capability == expected);
        }

        var omitted = plan.InputContract.Traversals[0].Input.Id;
        MaterializationDefinition incomplete = new(
            valid.Id,
            valid.Relation,
            [.. valid.Sources.Where(source => source.Input != omitted)],
            valid.TargetCapabilities,
            valid.UpdatePolicy,
            valid.FailurePolicy,
            valid.FreshnessPolicy,
            valid.ControlLoops,
            valid.Provenance);

        var validation = MaterializationDefinitionValidator.Validate(incomplete);

        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.SourceRequirementMissing
                && diagnostic.Location?.Contains(Uri.EscapeDataString(omitted.Value), StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Validator_RequiresSemanticGuaranteesForProtocolCapabilities()
    {
        var valid = CreateDefinition(reverseDeclarations: false);
        var promotion = Assert.Single(
            valid.TargetCapabilities,
            static requirement => requirement.Capability == MaterializationCapabilityKind.TargetFencedPromotion);
        MaterializationCapabilityRequirement weakened = new(
            promotion.Id,
            promotion.Capability,
            guarantees: [],
            promotion.OperatingLimits,
            promotion.Modes);
        MaterializationDefinition invalid = new(
            valid.Id,
            valid.Relation,
            valid.Sources,
            [.. valid.TargetCapabilities.Select(requirement => requirement == promotion ? weakened : requirement)],
            valid.UpdatePolicy,
            valid.FailurePolicy,
            valid.FreshnessPolicy,
            valid.ControlLoops,
            valid.Provenance);

        var validation = MaterializationDefinitionValidator.Validate(invalid);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.ProtocolGuaranteeMissing
                && diagnostic.Message.Contains(nameof(MaterializationGuaranteeKind.AtomicPromotion), StringComparison.Ordinal));
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.ProtocolGuaranteeMissing
                && diagnostic.Message.Contains(nameof(MaterializationGuaranteeKind.FencedPromotion), StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RequiresCatchUpSourceAndDeleteCapabilitiesDuringRebuild()
    {
        var valid = CreateDefinition(reverseDeclarations: false);
        var weakenedSources = valid.Sources.Select(source => new MaterializationSourceRequirement(
            source.Input,
            [.. source.Capabilities.Select(requirement =>
                requirement.Capability is MaterializationCapabilityKind.SourceChangeDelivery
                    or MaterializationCapabilityKind.SourceSettlement
                    ? new MaterializationCapabilityRequirement(
                        requirement.Id,
                        requirement.Capability,
                        requirement.Guarantees,
                        requirement.OperatingLimits,
                        MaterializationSynchronizationMode.Incremental)
                    : requirement)])).ToImmutableArray();
        var weakenedTarget = valid.TargetCapabilities.Select(requirement =>
            requirement.Capability == MaterializationCapabilityKind.TargetBulkDelete
                ? new MaterializationCapabilityRequirement(
                    requirement.Id,
                    requirement.Capability,
                    requirement.Guarantees,
                    requirement.OperatingLimits,
                    MaterializationSynchronizationMode.Incremental)
                : requirement).ToImmutableArray();
        MaterializationDefinition invalid = new(
            valid.Id,
            valid.Relation,
            weakenedSources,
            weakenedTarget,
            valid.UpdatePolicy,
            valid.FailurePolicy,
            valid.FreshnessPolicy,
            valid.ControlLoops,
            valid.Provenance);

        var validation = MaterializationDefinitionValidator.Validate(invalid);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.ProtocolCapabilityMissing
                && diagnostic.Message.Contains(nameof(MaterializationCapabilityKind.SourceChangeDelivery), StringComparison.Ordinal));
        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.ProtocolCapabilityMissing
                && diagnostic.Message.Contains(nameof(MaterializationCapabilityKind.SourceSettlement), StringComparison.Ordinal));
        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.ProtocolCapabilityMissing
                && diagnostic.Message.Contains(nameof(MaterializationCapabilityKind.TargetBulkDelete), StringComparison.Ordinal));
    }

    [Fact]
    public void JsonOptions_RejectUnsupportedFormatting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MaterializationJsonSerializer.CreateOptions((PortableDocumentJsonFormatting)999));
    }

    static MaterializationCapabilityProfile Profile(MaterializationCapabilityEvidence evidence) =>
        new(
            new("profile/source/v1"),
            MaterializationEndpointRole.Source,
            "source/postgres",
            [evidence]);

    static MaterializationDefinition CreateDefinition(bool reverseDeclarations)
    {
        RelationQueryCompilationRequest request = new(
            FederatedLoadRelationFixture.RelationDocument,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument);
        var compilation = RelationQueryStaticCompiler.Compile(request);
        var plan = Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
        var output = Assert.Single(plan.RequirementGraph.Outputs, static candidate => candidate.Field is null);
        var relation = MaterializationRelationReference.From(request, output.Id);
        ImmutableArray<MaterializationSourceRequirement> sourceRequirements =
        [
            .. plan.InputContract.Sources.Select(source => SourceRequirement(
                source.Input.Id,
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                reverseDeclarations)),
            .. plan.InputContract.Traversals.Select(traversal => SourceRequirement(
                traversal.Input.Id,
                traversal.Input.Direction == RelationshipTraversalDirection.Forward
                    ? MaterializationCapabilityKind.SourceBatchedPointRead
                    : MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                reverseDeclarations))
        ];
        ImmutableArray<MaterializationCapabilityRequirement> targetCapabilities =
        [
            Requirement("target/isolation", MaterializationCapabilityKind.TargetGenerationIsolation, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/upsert", MaterializationCapabilityKind.TargetBulkUpsert, MaterializationSynchronizationMode.All),
            Requirement("target/delete", MaterializationCapabilityKind.TargetBulkDelete, MaterializationSynchronizationMode.All),
            Requirement("target/outcomes", MaterializationCapabilityKind.TargetPerItemOutcomes, MaterializationSynchronizationMode.All),
            Requirement("target/seal", MaterializationCapabilityKind.TargetSeal, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/validation", MaterializationCapabilityKind.TargetValidation, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/promotion", MaterializationCapabilityKind.TargetFencedPromotion, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/retirement", MaterializationCapabilityKind.TargetRetirement, MaterializationSynchronizationMode.Rebuild),
            Requirement("target/cleanup", MaterializationCapabilityKind.TargetCleanup, MaterializationSynchronizationMode.Rebuild)
        ];
        if (reverseDeclarations)
        {
            targetCapabilities = targetCapabilities.Reverse().ToImmutableArray();
            sourceRequirements = sourceRequirements.Reverse().ToImmutableArray();
        }

        return new(
            new("loads/search"),
            relation,
            sourceRequirements,
            targetCapabilities,
            new(
                MaterializationSynchronizationMode.All,
                MaterializationConsistencyKind.BaselinePlusCatchUp,
                MaterializationIdempotencyKind.StableOutputIdentityAndVersion),
            new(maximumAttempts: 5, MaterializationFailureDisposition.Stop),
            new(maximumLagMilliseconds: 30_000, maximumUnsettledMilliseconds: 10_000),
            [],
            new(
                new("tests/materialization"),
                new("tests/materialization-definition"),
                DocumentOrigin.User));
    }

    static MaterializationSourceRequirement SourceRequirement(
        RelationQueryInputId input,
        MaterializationCapabilityKind rebuildRead,
        bool reverseDeclarations)
    {
        ImmutableArray<MaterializationCapabilityRequirement> capabilities =
        [
            Requirement($"{input.Value}/read", rebuildRead, MaterializationSynchronizationMode.Rebuild),
            Requirement($"{input.Value}/continuation", MaterializationCapabilityKind.SourceContinuation, MaterializationSynchronizationMode.Rebuild),
            Requirement($"{input.Value}/changes", MaterializationCapabilityKind.SourceChangeDelivery, MaterializationSynchronizationMode.All),
            Requirement($"{input.Value}/settlement", MaterializationCapabilityKind.SourceSettlement, MaterializationSynchronizationMode.All)
        ];
        if (reverseDeclarations)
        {
            capabilities = capabilities.Reverse().ToImmutableArray();
        }

        return new(input, capabilities);
    }

    static MaterializationCapabilityRequirement Requirement(
        string id,
        MaterializationCapabilityKind capability,
        MaterializationSynchronizationMode modes) =>
        new(new(id), capability, Guarantees(capability), OperatingLimits(capability), modes);

    static ImmutableArray<MaterializationOperatingLimit> OperatingLimits(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    new(MaterializationLimitKind.ReadItems, 100),
                    new(MaterializationLimitKind.ReadBytes, EvidenceReadBytes)
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    new(MaterializationLimitKind.ChangeItems, 100),
                    new(MaterializationLimitKind.ReadBytes, EvidenceReadBytes)
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [
                    new(MaterializationLimitKind.WriteItems, DefinitionWriteItems),
                    new(MaterializationLimitKind.WriteBytes, DefinitionWriteBytes)
                ],
            _ => []
        };

    static ImmutableArray<MaterializationLimitKind> RequiredEvidenceLimits(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [MaterializationLimitKind.ReadItems, MaterializationLimitKind.ReadBytes],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [MaterializationLimitKind.ChangeItems, MaterializationLimitKind.ReadBytes],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [MaterializationLimitKind.WriteItems, MaterializationLimitKind.WriteBytes],
            _ => []
        };

    static DocumentValidationDiagnostic CompleteDiagnostic(string code, string location) => new(
        code,
        DiagnosticSeverity.Warning,
        $"Diagnostic {code}.",
        location,
        Evidence: new DocumentDiagnosticEvidence(
            stage: "tests-materialization-capability",
            subject: code,
            sourceReferences: ["tests/materialization-capability/v1"],
            expected: "capability requirement satisfied",
            observed: "capability requirement mismatch"));

    static void AssertCompleteDiagnostic(DocumentValidationDiagnostic diagnostic)
    {
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Location));
        var evidence = Assert.IsType<DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Stage));
        Assert.False(string.IsNullOrWhiteSpace(evidence.Subject));
        Assert.NotEmpty(evidence.SourceReferences);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Expected));
        Assert.False(string.IsNullOrWhiteSpace(evidence.Observed));
    }

    static ImmutableArray<MaterializationGuaranteeKind> Guarantees(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    MaterializationGuaranteeKind.StableOrdering,
                MaterializationGuaranteeKind.RequestLocalCompleteness
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    MaterializationGuaranteeKind.StableOrdering,
                MaterializationGuaranteeKind.AtLeastOnceDelivery,
                MaterializationGuaranteeKind.BaselinePlusCatchUp
                ],
            MaterializationCapabilityKind.SourceSettlement => [MaterializationGuaranteeKind.ExplicitSettlement],
            MaterializationCapabilityKind.TargetGenerationIsolation =>
                [
                    MaterializationGuaranteeKind.GenerationIsolation,
                    MaterializationGuaranteeKind.FencedMutation
                ],
            MaterializationCapabilityKind.TargetBulkUpsert or MaterializationCapabilityKind.TargetBulkDelete =>
                [
                    MaterializationGuaranteeKind.IdempotentWrite,
                MaterializationGuaranteeKind.FencedMutation,
                MaterializationGuaranteeKind.VersionConditionalWrite
                ],
            MaterializationCapabilityKind.TargetPerItemOutcomes => [MaterializationGuaranteeKind.ExactPerItemOutcome],
            MaterializationCapabilityKind.TargetFencedPromotion =>
                [
                    MaterializationGuaranteeKind.AtomicPromotion,
                MaterializationGuaranteeKind.FencedPromotion
                ],
            MaterializationCapabilityKind.TargetSeal
                or MaterializationCapabilityKind.TargetValidation
                or MaterializationCapabilityKind.TargetRetirement
                or MaterializationCapabilityKind.TargetCleanup =>
                [MaterializationGuaranteeKind.FencedMutation],
            _ => []
        };
}
