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
        Assert.DoesNotContain(
            definition.TargetCapabilities,
            static requirement => requirement.Capability == MaterializationCapabilityKind.TargetContributorLedger);
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
                CapabilityRealizationKind.Constrained,
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
                CapabilityRealizationKind.Constrained,
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
        Assert.Equal(CapabilityRealizationKind.Constrained, accepted.Decisions[0].Realization);
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
                CapabilityRealizationKind.Native,
                [MaterializationGuaranteeKind.AtomicPromotion],
                [],
                ["adapter/source/v1"]));
        var evidenceLimit = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityEvidence(
                new("evidence/enumeration"),
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                CapabilityRealizationKind.Native,
                [],
                [new(MaterializationLimitKind.WriteItems, 100)],
                ["adapter/source/v1"]));

        Assert.Equal("guarantees", requirementGuarantee.ParamName);
        Assert.Equal("operatingLimits", requirementLimit.ParamName);
        Assert.Equal("guarantees", evidenceGuarantee.ParamName);
        Assert.Equal("operatingLimits", evidenceLimit.ParamName);
    }

    [Fact]
    public void ChangeCoverageGuarantees_AreSourceChangeSpecificAndDoNotSatisfyEachOther()
    {
        Assert.True(MaterializationCapabilityCatalog.AllowsGuarantee(
            MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationGuaranteeKind.CompleteMutationDelivery));
        Assert.True(MaterializationCapabilityCatalog.AllowsGuarantee(
            MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationGuaranteeKind.LatestVersionUpsertDelivery));
        Assert.False(MaterializationCapabilityCatalog.AllowsGuarantee(
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            MaterializationGuaranteeKind.CompleteMutationDelivery));
        Assert.False(MaterializationCapabilityCatalog.AllowsGuarantee(
            MaterializationCapabilityKind.TargetBulkUpsert,
            MaterializationGuaranteeKind.LatestVersionUpsertDelivery));

        MaterializationCapabilityEvidence completeMutations = new(
            new("evidence/complete-mutations"),
            MaterializationCapabilityKind.SourceChangeDelivery,
            CapabilityRealizationKind.Native,
            [MaterializationGuaranteeKind.CompleteMutationDelivery],
            operatingLimits: [],
            sourceReferences: ["adapter/complete-mutations/v1"]);
        MaterializationCapabilityEvidence latestUpserts = new(
            new("evidence/latest-upserts"),
            MaterializationCapabilityKind.SourceChangeDelivery,
            CapabilityRealizationKind.Native,
            [MaterializationGuaranteeKind.LatestVersionUpsertDelivery],
            operatingLimits: [],
            sourceReferences: ["adapter/latest-upserts/v1"]);
        MaterializationCapabilityRequirement requiresCompleteMutations = new(
            new("source/complete-mutations"),
            MaterializationCapabilityKind.SourceChangeDelivery,
            [MaterializationGuaranteeKind.CompleteMutationDelivery]);
        MaterializationCapabilityRequirement requiresLatestUpserts = new(
            new("source/latest-upserts"),
            MaterializationCapabilityKind.SourceChangeDelivery,
            [MaterializationGuaranteeKind.LatestVersionUpsertDelivery]);

        Assert.True(MaterializationCapabilityMatcher.Match(
            [requiresCompleteMutations],
            Profile(completeMutations)).IsSatisfied);
        Assert.True(MaterializationCapabilityMatcher.Match(
            [requiresLatestUpserts],
            Profile(latestUpserts)).IsSatisfied);
        Assert.False(MaterializationCapabilityMatcher.Match(
            [requiresCompleteMutations],
            Profile(latestUpserts)).IsSatisfied);
        Assert.False(MaterializationCapabilityMatcher.Match(
            [requiresLatestUpserts],
            Profile(completeMutations)).IsSatisfied);
    }

    [Fact]
    public void ChangeDeliveryContracts_RequireExactlyOneCoverageGuarantee()
    {
        var missingRequirement = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityRequirement(
                new("source/missing-coverage"),
                MaterializationCapabilityKind.SourceChangeDelivery,
                [MaterializationGuaranteeKind.AtLeastOnceDelivery]));
        var ambiguousRequirement = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityRequirement(
                new("source/ambiguous-coverage"),
                MaterializationCapabilityKind.SourceChangeDelivery,
                [
                    MaterializationGuaranteeKind.CompleteMutationDelivery,
                    MaterializationGuaranteeKind.LatestVersionUpsertDelivery
                ]));
        var missingEvidence = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityEvidence(
                new("evidence/missing-coverage"),
                MaterializationCapabilityKind.SourceChangeDelivery,
                CapabilityRealizationKind.Native,
                [MaterializationGuaranteeKind.AtLeastOnceDelivery],
                operatingLimits: [],
                sourceReferences: ["adapter/missing-coverage/v1"]));
        var ambiguousEvidence = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityEvidence(
                new("evidence/ambiguous-coverage"),
                MaterializationCapabilityKind.SourceChangeDelivery,
                CapabilityRealizationKind.Native,
                [
                    MaterializationGuaranteeKind.CompleteMutationDelivery,
                    MaterializationGuaranteeKind.LatestVersionUpsertDelivery
                ],
                operatingLimits: [],
                sourceReferences: ["adapter/ambiguous-coverage/v1"]));

        Assert.Equal("guarantees", missingRequirement.ParamName);
        Assert.Equal("guarantees", ambiguousRequirement.ParamName);
        Assert.Equal("guarantees", missingEvidence.ParamName);
        Assert.Equal("guarantees", ambiguousEvidence.ParamName);
        Assert.Contains("exactly one", missingRequirement.Message, StringComparison.Ordinal);
        Assert.Contains("exactly one", ambiguousRequirement.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionAlignedDimensions_AreOwnedOnlyBySourceChangeDelivery()
    {
        Assert.True(MaterializationCapabilityCatalog.AllowsGuarantee(
            MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationGuaranteeKind.TransactionAlignedDelivery));
        Assert.True(MaterializationCapabilityCatalog.AllowsLimit(
            MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationLimitKind.TransactionItems));
        Assert.True(MaterializationCapabilityCatalog.AllowsLimit(
            MaterializationCapabilityKind.SourceChangeDelivery,
            MaterializationLimitKind.TransactionBytes));
        Assert.False(MaterializationCapabilityCatalog.AllowsGuarantee(
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            MaterializationGuaranteeKind.TransactionAlignedDelivery));
        Assert.False(MaterializationCapabilityCatalog.AllowsLimit(
            MaterializationCapabilityKind.SourceSettlement,
            MaterializationLimitKind.TransactionItems));
        Assert.False(MaterializationCapabilityCatalog.AllowsLimit(
            MaterializationCapabilityKind.TargetBulkUpsert,
            MaterializationLimitKind.TransactionBytes));

        var guaranteeException = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityRequirement(
                id: new("source/enumeration/transaction-aligned"),
                capability: MaterializationCapabilityKind.SourceBoundedEnumeration,
                guarantees: [MaterializationGuaranteeKind.TransactionAlignedDelivery]));
        var limitException = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityRequirement(
                id: new("source/settlement/transaction-limit"),
                capability: MaterializationCapabilityKind.SourceSettlement,
                operatingLimits:
                [
                    new(
                        kind: MaterializationLimitKind.TransactionItems,
                        maximum: 100)
                ]));

        Assert.Equal("guarantees", guaranteeException.ParamName);
        Assert.Equal("operatingLimits", limitException.ParamName);
    }

    [Fact]
    public void TransactionAlignedEvidence_RequiresPairedHardTransactionSafetyLimits()
    {
        var missingLimits = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityEvidence(
                id: new("evidence/transaction-aligned/missing-limits"),
                capability: MaterializationCapabilityKind.SourceChangeDelivery,
                realization: CapabilityRealizationKind.Constrained,
                guarantees:
                [
                    MaterializationGuaranteeKind.CompleteMutationDelivery,
                    MaterializationGuaranteeKind.TransactionAlignedDelivery
                ],
                operatingLimits: [],
                sourceReferences: ["adapter/transaction-aligned/v1"]));
        var incompleteLimits = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityEvidence(
                id: new("evidence/transaction-aligned/incomplete-limits"),
                capability: MaterializationCapabilityKind.SourceChangeDelivery,
                realization: CapabilityRealizationKind.Constrained,
                guarantees:
                [
                    MaterializationGuaranteeKind.CompleteMutationDelivery,
                    MaterializationGuaranteeKind.TransactionAlignedDelivery
                ],
                operatingLimits:
                [
                    new(
                        kind: MaterializationLimitKind.TransactionItems,
                        maximum: 100)
                ],
                sourceReferences: ["adapter/transaction-aligned/v1"]));
        var ordinaryWithTransactionLimits = Assert.Throws<ArgumentException>(() =>
            new MaterializationCapabilityEvidence(
                id: new("evidence/ordinary/transaction-limits"),
                capability: MaterializationCapabilityKind.SourceChangeDelivery,
                realization: CapabilityRealizationKind.Constrained,
                guarantees: [MaterializationGuaranteeKind.CompleteMutationDelivery],
                operatingLimits:
                [
                    new(
                        kind: MaterializationLimitKind.TransactionItems,
                        maximum: 100),
                    new(
                        kind: MaterializationLimitKind.TransactionBytes,
                        maximum: 10_000)
                ],
                sourceReferences: ["adapter/ordinary/v1"]));

        Assert.Equal("operatingLimits", missingLimits.ParamName);
        Assert.Contains(nameof(MaterializationLimitKind.TransactionItems), missingLimits.Message, StringComparison.Ordinal);
        Assert.Equal("operatingLimits", incompleteLimits.ParamName);
        Assert.Contains(nameof(MaterializationLimitKind.TransactionBytes), incompleteLimits.Message, StringComparison.Ordinal);
        Assert.Equal("operatingLimits", ordinaryWithTransactionLimits.ParamName);
        Assert.Contains(nameof(MaterializationGuaranteeKind.TransactionAlignedDelivery), ordinaryWithTransactionLimits.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransactionAlignedMatching_KeepsPageBudgetsDistinctFromTransactionSafetyLimits()
    {
        const long PreferredPageItems = 10;
        const long PreferredPageBytes = 1_000;
        const long RequiredTransactionItems = 100;
        const long RequiredTransactionBytes = 100_000;
        MaterializationCapabilityRequirement requirement = new(
            id: new("source/transaction-aligned"),
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            guarantees:
            [
                MaterializationGuaranteeKind.CompleteMutationDelivery,
                MaterializationGuaranteeKind.TransactionAlignedDelivery
            ],
            operatingLimits:
            [
                new(
                    kind: MaterializationLimitKind.ChangeItems,
                    maximum: PreferredPageItems),
                new(
                    kind: MaterializationLimitKind.ReadBytes,
                    maximum: PreferredPageBytes),
                new(
                    kind: MaterializationLimitKind.TransactionItems,
                    maximum: RequiredTransactionItems),
                new(
                    kind: MaterializationLimitKind.TransactionBytes,
                    maximum: RequiredTransactionBytes)
            ]);
        MaterializationCapabilityEvidence sufficient = TransactionAlignedEvidence(
            id: "evidence/transaction-aligned/sufficient",
            maximumTransactionItems: RequiredTransactionItems,
            maximumTransactionBytes: RequiredTransactionBytes);
        MaterializationCapabilityEvidence insufficient = TransactionAlignedEvidence(
            id: "evidence/transaction-aligned/insufficient",
            maximumTransactionItems: RequiredTransactionItems - 1,
            maximumTransactionBytes: RequiredTransactionBytes);

        var accepted = MaterializationCapabilityMatcher.Match([requirement], Profile(sufficient));
        var rejected = MaterializationCapabilityMatcher.Match([requirement], Profile(insufficient));

        Assert.True(accepted.IsSatisfied);
        Assert.True(PreferredPageItems < RequiredTransactionItems);
        Assert.True(PreferredPageBytes < RequiredTransactionBytes);
        Assert.False(rejected.IsSatisfied);
        var diagnostic = Assert.Single(rejected.Validation.Diagnostics);
        Assert.Equal(MaterializationCapabilityDiagnosticCodes.LimitExceeded, diagnostic.Code);
        var evidence = Assert.IsType<DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.Equal(
            $"{MaterializationLimitKind.TransactionItems}>={RequiredTransactionItems}",
            evidence.Expected);
    }

    [Fact]
    public void OrdinaryChangeDelivery_PageBudgetsRemainHardMatchingBounds()
    {
        MaterializationCapabilityEvidence ordinary = new(
            id: new("evidence/ordinary/bounded"),
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            realization: CapabilityRealizationKind.Constrained,
            guarantees: [MaterializationGuaranteeKind.CompleteMutationDelivery],
            operatingLimits:
            [
                new(
                    kind: MaterializationLimitKind.ChangeItems,
                    maximum: 10),
                new(
                    kind: MaterializationLimitKind.ReadBytes,
                    maximum: 1_000)
            ],
            sourceReferences: ["adapter/ordinary/v1"]);
        var profile = Profile(ordinary);

        Assert.True(MaterializationCapabilityLimits.SupportsBounds(
            profile: profile,
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            itemLimitKind: MaterializationLimitKind.ChangeItems,
            requestedItems: 10,
            byteLimitKind: MaterializationLimitKind.ReadBytes,
            requestedBytes: 1_000));
        Assert.False(MaterializationCapabilityLimits.SupportsBounds(
            profile: profile,
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            itemLimitKind: MaterializationLimitKind.ChangeItems,
            requestedItems: 11,
            byteLimitKind: MaterializationLimitKind.ReadBytes,
            requestedBytes: 1_000));
        Assert.False(MaterializationCapabilityLimits.SupportsBounds(
            profile: profile,
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            itemLimitKind: MaterializationLimitKind.ChangeItems,
            requestedItems: 10,
            byteLimitKind: MaterializationLimitKind.ReadBytes,
            requestedBytes: 1_001));
    }

    [Fact]
    public void ContributorLedgerCapability_OwnsItsBoundedReadAndWriteDimensions()
    {
        const MaterializationCapabilityKind Capability =
            MaterializationCapabilityKind.TargetContributorLedger;

        Assert.Equal(MaterializationEndpointRole.Target, MaterializationCapabilityCatalog.RoleOf(Capability));
        Assert.True(MaterializationCapabilityCatalog.AllowsGuarantee(
            Capability,
            MaterializationGuaranteeKind.RequestLocalCompleteness));
        Assert.True(MaterializationCapabilityCatalog.AllowsGuarantee(
            Capability,
            MaterializationGuaranteeKind.IdempotentWrite));
        Assert.True(MaterializationCapabilityCatalog.AllowsGuarantee(
            Capability,
            MaterializationGuaranteeKind.VersionConditionalWrite));
        Assert.True(MaterializationCapabilityCatalog.AllowsGuarantee(
            Capability,
            MaterializationGuaranteeKind.FencedMutation));
        Assert.True(MaterializationCapabilityCatalog.AllowsGuarantee(
            Capability,
            MaterializationGuaranteeKind.AtomicWithMaterializationMutation));
        Assert.True(MaterializationCapabilityCatalog.AllowsLimit(
            Capability,
            MaterializationLimitKind.ReadItems));
        Assert.True(MaterializationCapabilityCatalog.AllowsLimit(
            Capability,
            MaterializationLimitKind.WriteItems));
        Assert.True(MaterializationCapabilityCatalog.AllowsLimit(
            Capability,
            MaterializationLimitKind.Parallelism));
        Assert.True(MaterializationCapabilityCatalog.AllowsLimit(
            Capability,
            MaterializationLimitKind.IndexedIdentityCharacters));
    }

    [Theory]
    [InlineData(MaterializationCapabilityKind.SourceBatchedPointRead, MaterializationLimitKind.ReadItems)]
    [InlineData(MaterializationCapabilityKind.SourceParameterizedPredicateQuery, MaterializationLimitKind.ReadBytes)]
    [InlineData(MaterializationCapabilityKind.SourceBoundedEnumeration, MaterializationLimitKind.ReadBytes)]
    [InlineData(MaterializationCapabilityKind.TargetBulkUpsert, MaterializationLimitKind.WriteBytes)]
    [InlineData(MaterializationCapabilityKind.TargetBulkDelete, MaterializationLimitKind.WriteItems)]
    [InlineData(MaterializationCapabilityKind.TargetPerItemOutcomes, MaterializationLimitKind.WriteBytes)]
    [InlineData(MaterializationCapabilityKind.TargetContributorLedger, MaterializationLimitKind.ReadItems)]
    [InlineData(MaterializationCapabilityKind.TargetContributorLedger, MaterializationLimitKind.ReadBytes)]
    [InlineData(MaterializationCapabilityKind.TargetContributorLedger, MaterializationLimitKind.WriteItems)]
    [InlineData(MaterializationCapabilityKind.TargetContributorLedger, MaterializationLimitKind.WriteBytes)]
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
            CapabilityRealizationKind.Constrained,
            [],
            incompleteLimits,
            ["adapter/incomplete/v1"]));

        Assert.Equal("operatingLimits", exception.ParamName);
        Assert.Contains(omittedLimit.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityRealization_RejectsUnknownEvidenceAndDecision()
    {
        var evidenceException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MaterializationCapabilityEvidence(
                id: new("evidence/unknown"),
                capability: MaterializationCapabilityKind.TargetGenerationIsolation,
                realization: CapabilityRealizationKind.Unknown,
                guarantees: [],
                operatingLimits: [],
                sourceReferences: ["adapter/unknown/v1"]));
        Assert.Equal("realization", evidenceException.ParamName);

        MaterializationCapabilityRequirement requirement = new(
            id: new("target/generation-isolation"),
            capability: MaterializationCapabilityKind.TargetGenerationIsolation);
        var decisionException = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new MaterializationCapabilityDecision(
                requirement,
                realization: CapabilityRealizationKind.Unknown));
        Assert.Equal("realization", decisionException.ParamName);
    }

    [Fact]
    public void ChangeDeliveryEvidence_MayOmitAdvisoryBatchLimits_ButCannotSatisfyBoundedRequirements()
    {
        MaterializationCapabilityEvidence managed = new(
            new("evidence/managed-changes"),
            MaterializationCapabilityKind.SourceChangeDelivery,
            CapabilityRealizationKind.Native,
            [
                MaterializationGuaranteeKind.AtLeastOnceDelivery,
                MaterializationGuaranteeKind.LatestVersionUpsertDelivery
            ],
            operatingLimits: [],
            sourceReferences: ["adapter/managed-source/v1"]);
        var profile = Profile(managed);
        MaterializationCapabilityRequirement unbounded = new(
            new("source/managed-changes"),
            MaterializationCapabilityKind.SourceChangeDelivery,
            [
                MaterializationGuaranteeKind.AtLeastOnceDelivery,
                MaterializationGuaranteeKind.LatestVersionUpsertDelivery
            ]);
        MaterializationCapabilityRequirement bounded = new(
            new("source/pull-changes"),
            MaterializationCapabilityKind.SourceChangeDelivery,
            [
                MaterializationGuaranteeKind.AtLeastOnceDelivery,
                MaterializationGuaranteeKind.LatestVersionUpsertDelivery
            ],
            [
                new(MaterializationLimitKind.ChangeItems, 100),
                new(MaterializationLimitKind.ReadBytes, EvidenceReadBytes)
            ]);

        var unboundedMatch = MaterializationCapabilityMatcher.Match([unbounded], profile);
        var boundedMatch = MaterializationCapabilityMatcher.Match([bounded], profile);

        Assert.True(unboundedMatch.IsSatisfied);
        Assert.False(boundedMatch.IsSatisfied);
        Assert.Collection(
            boundedMatch.Validation.Diagnostics,
            diagnostic => Assert.Equal(MaterializationCapabilityDiagnosticCodes.LimitUnavailable, diagnostic.Code),
            diagnostic => Assert.Equal(MaterializationCapabilityDiagnosticCodes.LimitUnavailable, diagnostic.Code));
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
    public void ContributorLedgerRequirement_MatchesOneCompleteTargetFacility()
    {
        MaterializationCapabilityRequirement requirement = new(
            id: new("target/contributor-ledger"),
            capability: MaterializationCapabilityKind.TargetContributorLedger,
            guarantees:
            [
                MaterializationGuaranteeKind.RequestLocalCompleteness,
                MaterializationGuaranteeKind.IdempotentWrite,
                MaterializationGuaranteeKind.VersionConditionalWrite,
                MaterializationGuaranteeKind.FencedMutation,
                MaterializationGuaranteeKind.AtomicWithMaterializationMutation
            ],
            operatingLimits:
            [
                new(MaterializationLimitKind.ReadItems, 100),
                new(MaterializationLimitKind.WriteItems, 100)
            ],
            modes: MaterializationSynchronizationMode.Incremental);
        MaterializationCapabilityEvidence evidence = new(
            id: new("evidence/contributor-ledger"),
            capability: MaterializationCapabilityKind.TargetContributorLedger,
            realization: CapabilityRealizationKind.Native,
            guarantees:
            [
                MaterializationGuaranteeKind.RequestLocalCompleteness,
                MaterializationGuaranteeKind.IdempotentWrite,
                MaterializationGuaranteeKind.VersionConditionalWrite,
                MaterializationGuaranteeKind.FencedMutation,
                MaterializationGuaranteeKind.AtomicWithMaterializationMutation
            ],
            operatingLimits:
            [
                new(MaterializationLimitKind.ReadItems, 128),
                new(MaterializationLimitKind.ReadBytes, EvidenceReadBytes),
                new(MaterializationLimitKind.WriteItems, 128),
                new(MaterializationLimitKind.WriteBytes, DefinitionWriteBytes)
            ],
            sourceReferences: ["adapter/target/contributor-ledger/v1"]);
        MaterializationCapabilityProfile profile = new(
            id: new("profile/target/v1"),
            role: MaterializationEndpointRole.Target,
            subject: "target/search",
            evidence: [evidence]);

        var match = MaterializationCapabilityMatcher.MatchForMode(
            requirements: [requirement],
            profile,
            mode: MaterializationSynchronizationMode.Incremental);
        MaterializationCapabilityRequirement excessiveRequirement = new(
            id: new("target/contributor-ledger/excessive"),
            capability: MaterializationCapabilityKind.TargetContributorLedger,
            guarantees: requirement.Guarantees,
            operatingLimits:
            [
                new(MaterializationLimitKind.ReadItems, 3_000),
                new(MaterializationLimitKind.WriteItems, 100)
            ],
            modes: MaterializationSynchronizationMode.Incremental);
        var rejected = MaterializationCapabilityMatcher.MatchForMode(
            requirements: [excessiveRequirement],
            profile,
            mode: MaterializationSynchronizationMode.Incremental);

        Assert.True(match.IsSatisfied);
        var decision = Assert.Single(match.Decisions);
        Assert.Same(evidence, decision.Evidence);
        Assert.False(rejected.IsSatisfied);
        Assert.Contains(
            rejected.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == MaterializationCapabilityDiagnosticCodes.LimitExceeded
                && diagnostic.Evidence?.Subject == nameof(MaterializationCapabilityKind.TargetContributorLedger));
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
                    CapabilityRealizationKind.Constrained,
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
                    CapabilityRealizationKind.Constrained,
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

    [Theory]
    [InlineData(MaterializationGuaranteeKind.RequestLocalCompleteness)]
    [InlineData(MaterializationGuaranteeKind.IdempotentWrite)]
    [InlineData(MaterializationGuaranteeKind.VersionConditionalWrite)]
    [InlineData(MaterializationGuaranteeKind.FencedMutation)]
    [InlineData(MaterializationGuaranteeKind.AtomicWithMaterializationMutation)]
    public void Validator_RejectsContributorLedgerWhenMandatoryGuaranteeIsOmitted(
        MaterializationGuaranteeKind omittedGuarantee)
    {
        var valid = CreateDefinition(reverseDeclarations: false);
        var completeLedger = Requirement(
            id: "target/contributor-ledger",
            capability: MaterializationCapabilityKind.TargetContributorLedger,
            modes: MaterializationSynchronizationMode.Incremental);
        MaterializationCapabilityRequirement weakenedLedger = new(
            id: completeLedger.Id,
            capability: completeLedger.Capability,
            guarantees:
            [
                .. completeLedger.Guarantees.Where(guarantee => guarantee != omittedGuarantee)
            ],
            operatingLimits: completeLedger.OperatingLimits,
            modes: completeLedger.Modes);
        MaterializationDefinition invalid = new(
            id: valid.Id,
            relation: valid.Relation,
            sources: valid.Sources,
            targetCapabilities: [.. valid.TargetCapabilities, weakenedLedger],
            updatePolicy: valid.UpdatePolicy,
            failurePolicy: valid.FailurePolicy,
            freshnessPolicy: valid.FreshnessPolicy,
            controlLoops: valid.ControlLoops,
            provenance: valid.Provenance);

        var validation = MaterializationDefinitionValidator.Validate(invalid);

        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.ProtocolGuaranteeMissing
                && diagnostic.Message.Contains(omittedGuarantee.ToString(), StringComparison.Ordinal)
                && diagnostic.Location?.Contains(
                    Uri.EscapeDataString(completeLedger.Id.Value),
                    StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData(
        MaterializationConsistencyKind.CoordinatedSnapshot,
        MaterializationGuaranteeKind.CoordinatedSnapshot)]
    [InlineData(
        MaterializationConsistencyKind.Reconciliation,
        MaterializationGuaranteeKind.Reconciliation)]
    public void Validator_RejectsOptionalIncrementalPredicateWithoutPolicyConsistencyGuarantee(
        MaterializationConsistencyKind consistency,
        MaterializationGuaranteeKind requiredGuarantee)
    {
        var complete = CreateDefinitionWithOptionalIncrementalPredicate(
            consistency: consistency,
            includePolicyGuarantee: true);
        var validValidation = MaterializationDefinitionValidator.Validate(complete);
        var incomplete = CreateDefinitionWithOptionalIncrementalPredicate(
            consistency: consistency,
            includePolicyGuarantee: false);

        var validation = MaterializationDefinitionValidator.Validate(incomplete);

        Assert.True(
            validValidation.IsValid,
            string.Join(Environment.NewLine, validValidation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == MaterializationDefinitionDiagnosticCodes.ProtocolGuaranteeMissing
                && diagnostic.Message.Contains(requiredGuarantee.ToString(), StringComparison.Ordinal)
                && diagnostic.Message.Contains(
                    nameof(MaterializationCapabilityKind.SourceParameterizedPredicateQuery),
                    StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_AllowsSourcesWithoutOptionalSettlementCapability()
    {
        var valid = CreateDefinition(reverseDeclarations: false);
        var sourcesWithoutSettlement = valid.Sources.Select(source => new MaterializationSourceRequirement(
            source.Input,
            [.. source.Capabilities.Where(static requirement =>
                requirement.Capability != MaterializationCapabilityKind.SourceSettlement)])).ToImmutableArray();
        MaterializationDefinition withoutSettlement = new(
            valid.Id,
            valid.Relation,
            sourcesWithoutSettlement,
            valid.TargetCapabilities,
            valid.UpdatePolicy,
            valid.FailurePolicy,
            valid.FreshnessPolicy,
            valid.ControlLoops,
            valid.Provenance);

        var validation = MaterializationDefinitionValidator.Validate(withoutSettlement);

        Assert.True(validation.IsValid);
        Assert.DoesNotContain(
            validation.Diagnostics,
            static diagnostic => diagnostic.Message.Contains(
                nameof(MaterializationCapabilityKind.SourceSettlement),
                StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RequiresExplicitGuaranteeWhenOptionalSettlementIsDeclared()
    {
        var valid = CreateDefinition(reverseDeclarations: false);
        var selectedSource = valid.Sources[0];
        var weakenedSource = new MaterializationSourceRequirement(
            selectedSource.Input,
            [.. selectedSource.Capabilities.Select(static requirement =>
                requirement.Capability == MaterializationCapabilityKind.SourceSettlement
                    ? new MaterializationCapabilityRequirement(
                        requirement.Id,
                        requirement.Capability,
                        guarantees: [],
                        requirement.OperatingLimits,
                        requirement.Modes)
                    : requirement)]);
        MaterializationDefinition invalid = new(
            valid.Id,
            valid.Relation,
            [.. valid.Sources.Select(source => source == selectedSource ? weakenedSource : source)],
            valid.TargetCapabilities,
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
                && diagnostic.Message.Contains(nameof(MaterializationGuaranteeKind.ExplicitSettlement), StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RequiresCatchUpChangeAndDeleteCapabilitiesDuringRebuild()
    {
        var valid = CreateDefinition(reverseDeclarations: false);
        var weakenedSources = valid.Sources.Select(source => new MaterializationSourceRequirement(
            source.Input,
            [.. source.Capabilities.Select(requirement =>
                requirement.Capability == MaterializationCapabilityKind.SourceChangeDelivery
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
                && diagnostic.Message.Contains(nameof(MaterializationCapabilityKind.TargetBulkDelete), StringComparison.Ordinal));
    }

    [Fact]
    public void JsonOptions_RejectUnsupportedFormatting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MaterializationJsonSerializer.CreateOptions((PortableDocumentJsonFormatting)999));
    }

    static MaterializationCapabilityEvidence TransactionAlignedEvidence(
        string id,
        long maximumTransactionItems,
        long maximumTransactionBytes) =>
        new(
            id: new(id),
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            realization: CapabilityRealizationKind.Constrained,
            guarantees:
            [
                MaterializationGuaranteeKind.CompleteMutationDelivery,
                MaterializationGuaranteeKind.TransactionAlignedDelivery
            ],
            operatingLimits:
            [
                new(
                    kind: MaterializationLimitKind.ChangeItems,
                    maximum: 10),
                new(
                    kind: MaterializationLimitKind.ReadBytes,
                    maximum: 1_000),
                new(
                    kind: MaterializationLimitKind.TransactionItems,
                    maximum: maximumTransactionItems),
                new(
                    kind: MaterializationLimitKind.TransactionBytes,
                    maximum: maximumTransactionBytes)
            ],
            sourceReferences: ["adapter/transaction-aligned/v1"]);

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

    static MaterializationDefinition CreateDefinitionWithOptionalIncrementalPredicate(
        MaterializationConsistencyKind consistency,
        bool includePolicyGuarantee)
    {
        var template = CreateDefinition(reverseDeclarations: false);
        var requiredGuarantee = consistency switch
        {
            MaterializationConsistencyKind.CoordinatedSnapshot =>
                MaterializationGuaranteeKind.CoordinatedSnapshot,
            MaterializationConsistencyKind.Reconciliation =>
                MaterializationGuaranteeKind.Reconciliation,
            _ => throw new ArgumentOutOfRangeException(
                nameof(consistency),
                consistency,
                "The optional-predicate test requires a read-consistency guarantee.")
        };
        var rootSource = Assert.Single(
            template.Sources,
            static source => source.Capabilities.Any(static requirement =>
                requirement.Capability == MaterializationCapabilityKind.SourceBoundedEnumeration));
        var sources = template.Sources.Select(source =>
        {
            ImmutableArray<MaterializationCapabilityRequirement> capabilities =
            [
                .. source.Capabilities.Select(requirement =>
                    IsBoundedSourceRead(requirement.Capability)
                        ? new MaterializationCapabilityRequirement(
                            id: requirement.Id,
                            capability: requirement.Capability,
                            guarantees: requirement.Guarantees.Contains(requiredGuarantee)
                                ? requirement.Guarantees
                                : [.. requirement.Guarantees, requiredGuarantee],
                            operatingLimits: requirement.OperatingLimits,
                            modes: requirement.Modes)
                        : requirement)
            ];
            if (source.Input == rootSource.Input)
            {
                ImmutableArray<MaterializationGuaranteeKind> predicateGuarantees =
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.RequestLocalCompleteness
                ];
                if (includePolicyGuarantee)
                {
                    predicateGuarantees = [.. predicateGuarantees, requiredGuarantee];
                }

                capabilities = capabilities.Add(new(
                    id: new($"{source.Input.Value}/impact-predicate"),
                    capability: MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                    guarantees: predicateGuarantees,
                    operatingLimits: OperatingLimits(
                        MaterializationCapabilityKind.SourceParameterizedPredicateQuery),
                    modes: MaterializationSynchronizationMode.Incremental));
            }

            return new MaterializationSourceRequirement(
                input: source.Input,
                capabilities: capabilities);
        }).ToImmutableArray();

        return new(
            id: template.Id,
            relation: template.Relation,
            sources: sources,
            targetCapabilities: template.TargetCapabilities,
            updatePolicy: new(
                supportedModes: template.UpdatePolicy.SupportedModes,
                consistency: consistency,
                idempotency: template.UpdatePolicy.Idempotency),
            failurePolicy: template.FailurePolicy,
            freshnessPolicy: template.FreshnessPolicy,
            controlLoops: template.ControlLoops,
            provenance: template.Provenance);
    }

    static bool IsBoundedSourceRead(MaterializationCapabilityKind capability) => capability is
        MaterializationCapabilityKind.SourceBatchedPointRead
        or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
        or MaterializationCapabilityKind.SourceBoundedEnumeration;

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
            MaterializationCapabilityKind.TargetContributorLedger =>
                [
                    new(MaterializationLimitKind.ReadItems, 100),
                    new(MaterializationLimitKind.ReadBytes, EvidenceReadBytes),
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
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [MaterializationLimitKind.WriteItems, MaterializationLimitKind.WriteBytes],
            MaterializationCapabilityKind.TargetContributorLedger =>
                [
                    MaterializationLimitKind.ReadItems,
                    MaterializationLimitKind.ReadBytes,
                    MaterializationLimitKind.WriteItems,
                    MaterializationLimitKind.WriteBytes
                ],
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
                    MaterializationGuaranteeKind.BaselinePlusCatchUp,
                    MaterializationGuaranteeKind.CompleteMutationDelivery
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
            MaterializationCapabilityKind.TargetContributorLedger =>
                [
                    MaterializationGuaranteeKind.RequestLocalCompleteness,
                    MaterializationGuaranteeKind.IdempotentWrite,
                    MaterializationGuaranteeKind.VersionConditionalWrite,
                    MaterializationGuaranteeKind.FencedMutation,
                    MaterializationGuaranteeKind.AtomicWithMaterializationMutation
                ],
            _ => []
        };
}
