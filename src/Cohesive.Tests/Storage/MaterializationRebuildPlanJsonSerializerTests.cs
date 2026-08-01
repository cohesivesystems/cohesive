using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Serialization;
using Cohesive.Relations.TestFixtures;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildPlanJsonSerializerTests
{
    const long ReadBytes = 4_096;
    const long WriteItems = 100;
    const long WriteBytes = 1_000_000;

    [Fact]
    public void Plan_RoundTripsThroughStrictCanonicalJsonWithTheSameFingerprint()
    {
        var plan = CreatePlan();

        var json = MaterializationRebuildPlanJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var restored = MaterializationRebuildPlanJsonSerializer.Deserialize(json);

        Assert.Equal(plan.Fingerprint, restored.Fingerprint);
        Assert.Equal(
            MaterializationRebuildPlanFingerprinter.Compute(restored),
            restored.Fingerprint);
        Assert.Equal(
            MaterializationRebuildPlanJsonSerializer.GetCanonicalBytes(plan),
            MaterializationRebuildPlanJsonSerializer.GetCanonicalBytes(restored));
    }

    [Fact]
    public void Plan_RejectsAForgedPersistedFingerprint()
    {
        var plan = CreatePlan();
        var json = MaterializationRebuildPlanJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var tampered = json.Replace(
            plan.Fingerprint.Value,
            new string('0', plan.Fingerprint.Value.Length),
            StringComparison.Ordinal);

        Assert.NotEqual(json, tampered);
        Assert.Throws<JsonException>(() =>
            MaterializationRebuildPlanJsonSerializer.Deserialize(tampered));
    }

    [Fact]
    public void Plan_RejectsUnknownPropertiesAtTheClosedWireBoundary()
    {
        var plan = CreatePlan();
        var json = MaterializationRebuildPlanJsonSerializer.Serialize(
            plan,
            PortableDocumentJsonFormatting.Compact);
        var unknown = json.Insert(startIndex: 1, "\"unknown\":true,");

        Assert.Throws<JsonException>(() =>
            MaterializationRebuildPlanJsonSerializer.Deserialize(unknown));
    }

    [Fact]
    public void Plan_RejectsQuarantineUntilTheReferenceInterpreterCanDurablyRealizeIt()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreatePlan(MaterializationFailureDisposition.QuarantineAndContinue));

        Assert.Contains("stop-on-exhaustion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RejectsWholeSetOutputBecauseBoundedPagesCannotProveCompleteInput()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreatePlan(
                exhaustedDisposition: MaterializationFailureDisposition.Stop,
                outputMode: RelationOutputMode.Set,
                maximumPageItems: 1));

        Assert.Contains("whole-set", exception.Message, StringComparison.Ordinal);
        Assert.Contains("bounded pages", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RejectsManyPerRootOutputWithoutAFiniteHydrationExpansionBoundary()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreatePlan(
                exhaustedDisposition: MaterializationFailureDisposition.Stop,
                outputMode: RelationOutputMode.ManyPerRoot,
                maximumPageItems: 1));

        Assert.Contains("many-per-root", exception.Message, StringComparison.Ordinal);
        Assert.Contains("finitely bound", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RejectsFingerprintCorrectDocumentThatBypassesSemanticFactory()
    {
        var valid = CreatePlan();
        var retained = valid.Materialization.Definition;
        var invalidDefinition = new MaterializationDefinition(
            id: retained.Id,
            relation: retained.Relation,
            sources: retained.Sources,
            targetCapabilities:
            [
                .. retained.TargetCapabilities.Where(static requirement =>
                    requirement.Capability != MaterializationCapabilityKind.TargetGenerationAbandonment)
            ],
            updatePolicy: retained.UpdatePolicy,
            failurePolicy: retained.FailurePolicy,
            freshnessPolicy: retained.FreshnessPolicy,
            controlLoops: retained.ControlLoops,
            provenance: retained.Provenance);
        var forgedDocument = new MaterializationDocument(
            MaterializationDocument.CurrentSchemaVersion,
            invalidDefinition,
            MaterializationDefinitionFingerprinter.Compute(invalidDefinition));

        var exception = Assert.Throws<ArgumentException>(() => new MaterializationRebuildPlan(
            forgedDocument,
            valid.Sources,
            valid.Target,
            valid.TargetCapabilityMatch,
            valid.Shards,
            valid.Limits,
            valid.Provenance));

        Assert.Contains(
            MaterializationDefinitionDiagnosticCodes.ProtocolCapabilityMissing,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("TargetGenerationAbandonment", exception.Message, StringComparison.Ordinal);
    }

    static MaterializationRebuildPlan CreatePlan(
        MaterializationFailureDisposition exhaustedDisposition = MaterializationFailureDisposition.Stop,
        RelationOutputMode outputMode = RelationOutputMode.OnePerRoot,
        int maximumPageItems = 100)
    {
        var materialization = MaterializationDocument.FromDefinition(CreateDefinition(exhaustedDisposition, outputMode));
        var compiled = materialization.Definition.Relation.Compile().Plan
            ?? throw new InvalidOperationException("The test materialization relation did not compile.");
        var sourcePlans = materialization.Definition.Sources.Select(source =>
        {
            RelationQuerySourceInstanceId sourceId = new($"source/{source.Input.Value}");
            var profile = Profile(
                role: MaterializationEndpointRole.Source,
                subject: sourceId.Value,
                source.Capabilities);
            return new MaterializationRebuildSourcePlan(
                source.Input,
                sourceId,
                profile,
                MaterializationCapabilityMatcher.MatchForMode(
                    source.Capabilities,
                    profile,
                    MaterializationSynchronizationMode.Rebuild));
        }).ToImmutableArray();

        MaterializationTargetId targetId = new("target/loads-search");
        var targetProfile = Profile(
            role: MaterializationEndpointRole.Target,
            subject: targetId.Value,
            materialization.Definition.TargetCapabilities);
        var target = new MaterializationTargetDescriptor(
            targetId,
            materialization.Definition.Id,
            targetProfile);
        var targetMatch = MaterializationCapabilityMatcher.MatchForMode(
            materialization.Definition.TargetCapabilities,
            targetProfile,
            MaterializationSynchronizationMode.Rebuild);

        var root = Assert.Single(
            compiled.InputContract.Sources,
            static source => source.Role == RelationQuerySourceInputRole.RelationRoot);
        var rootSource = sourcePlans.Single(source => source.Input == root.Input.Id);
        RelationQueryPhysicalPlanFingerprint physicalPlan = new(
            algorithm: "sha256",
            canonicalization: "tests/materialization-rebuild-physical-plan/v1",
            value: new string('a', 64));
        var fieldBindings = root.Fields.Select(field => new RelationQuerySourceFieldBinding(
            field.Input.Id,
            field.Input.Field.Path,
            sourceSelector: field.Input.Field.Path.ToString())).ToImmutableArray();
        RelationQuerySourcePlacementBinding placement = new(
            id: new("placement/rebuild-root"),
            input: root.Input.Id,
            node: root.Node,
            binding: root.Binding,
            shape: root.Shape,
            source: rootSource.Source,
            kind: RelationQuerySourcePlacementBindingKind.SourceSet,
            acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            origin: RelationQuerySourcePlacementOrigin.Explicit,
            identity: new(root.Shape, sourceSelector: "$identity"),
            fields: fieldBindings);
        MaterializationSourceScope scope = new(
            physicalPlan,
            placement,
            partition: new("partition/a"),
            orderingScope: new("ordering/root"));
        RelationQuerySourceReadRequest read = new(
            physicalPlan,
            stage: new("stage/rebuild-root"),
            placementBinding: placement.Id,
            source: placement.Source,
            shape: placement.Shape,
            identitySelector: placement.Identity!.SourceSelector,
            fields:
            [
                .. fieldBindings.Select(static field => new RelationQuerySourceReadField(
                    field.Input,
                    field.SemanticPath,
                    field.SourceSelector,
                    RelationQuerySourceReadFieldPurpose.SemanticInput))
            ],
            constraint: new RelationQueryBoundedEnumeration(maximumRows: 100),
            maximumBufferedRows: 100);
        MaterializationRebuildShardPlan shard = new(
            id: new("shard/a"),
            scope,
            read,
            hydrationPhysicalPlan: new(
                algorithm: "sha256",
                canonicalization: "tests/materialization-rebuild-hydration-plan/v1",
                value: new string('e', 64)));

        return new(
            materialization,
            sourcePlans,
            target,
            targetMatch,
            shards: [shard],
            limits: new(
                maximumPageItems: maximumPageItems,
                maximumPageBytes: ReadBytes,
                maximumBulkItems: 100,
                maximumBulkBytes: WriteBytes,
                maximumPagesPerShard: 100,
                maximumStartsPerActivation: 2,
                maximumParallelism: 2),
            provenance: Provenance());
    }

    static MaterializationDefinition CreateDefinition(
        MaterializationFailureDisposition exhaustedDisposition,
        RelationOutputMode outputMode)
    {
        var fixtureDefinition = Assert.IsType<RelationDefinition>(FederatedLoadRelationFixture.RelationDocument.Definition);
        var relationDocument = outputMode == fixtureDefinition.Output.Mode
            ? FederatedLoadRelationFixture.RelationDocument
            : RelationQueryDocument.FromDefinition(
                fixtureDefinition with
                {
                    Output = fixtureDefinition.Output with { Mode = outputMode }
                });
        RelationQueryCompilationRequest request = new(
            relationDocument,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument);
        var compilation = RelationQueryStaticCompiler.Compile(request);
        var plan = compilation.Plan
            ?? throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var output = Assert.Single(
            plan.RequirementGraph.Outputs,
            static candidate => candidate.Field is null);
        var relation = MaterializationRelationReference.From(request, output.Id);
        ImmutableArray<MaterializationSourceRequirement> sources =
        [
            .. plan.InputContract.Sources.Select(source => SourceRequirement(
                source.Input.Id,
                MaterializationCapabilityKind.SourceBoundedEnumeration)),
            .. plan.InputContract.Traversals.Select(traversal => SourceRequirement(
                traversal.Input.Id,
                traversal.Input.Direction == RelationshipTraversalDirection.Forward
                    ? MaterializationCapabilityKind.SourceBatchedPointRead
                    : MaterializationCapabilityKind.SourceParameterizedPredicateQuery))
        ];
        ImmutableArray<MaterializationCapabilityRequirement> targets =
        [
            Requirement(
                id: "target/isolation",
                capability: MaterializationCapabilityKind.TargetGenerationIsolation,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/upsert",
                capability: MaterializationCapabilityKind.TargetBulkUpsert,
                modes: MaterializationSynchronizationMode.All),
            Requirement(
                id: "target/delete",
                capability: MaterializationCapabilityKind.TargetBulkDelete,
                modes: MaterializationSynchronizationMode.All),
            Requirement(
                id: "target/outcomes",
                capability: MaterializationCapabilityKind.TargetPerItemOutcomes,
                modes: MaterializationSynchronizationMode.All),
            Requirement(
                id: "target/seal",
                capability: MaterializationCapabilityKind.TargetSeal,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/validation",
                capability: MaterializationCapabilityKind.TargetValidation,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/promotion",
                capability: MaterializationCapabilityKind.TargetFencedPromotion,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/abandonment",
                capability: MaterializationCapabilityKind.TargetGenerationAbandonment,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/retirement",
                capability: MaterializationCapabilityKind.TargetRetirement,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: "target/cleanup",
                capability: MaterializationCapabilityKind.TargetCleanup,
                modes: MaterializationSynchronizationMode.Rebuild)
        ];

        return new(
            id: new("loads/search-json"),
            relation,
            sources,
            targetCapabilities: targets,
            updatePolicy: new(
                supportedModes: MaterializationSynchronizationMode.All,
                consistency: MaterializationConsistencyKind.BaselinePlusCatchUp,
                idempotency: MaterializationIdempotencyKind.StableOutputIdentityAndVersion),
            failurePolicy: new(
                maximumAttempts: 5,
                exhaustedDisposition: exhaustedDisposition),
            freshnessPolicy: new(
                maximumLagMilliseconds: 30_000,
                maximumUnsettledMilliseconds: 10_000),
            controlLoops: [],
            provenance: Provenance());
    }

    static MaterializationSourceRequirement SourceRequirement(
        RelationQueryInputId input,
        MaterializationCapabilityKind rebuildRead) => new(
        input,
        [
            Requirement(
                id: $"{input.Value}/read",
                capability: rebuildRead,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: $"{input.Value}/continuation",
                capability: MaterializationCapabilityKind.SourceContinuation,
                modes: MaterializationSynchronizationMode.Rebuild),
            Requirement(
                id: $"{input.Value}/changes",
                capability: MaterializationCapabilityKind.SourceChangeDelivery,
                modes: MaterializationSynchronizationMode.All),
            Requirement(
                id: $"{input.Value}/settlement",
                capability: MaterializationCapabilityKind.SourceSettlement,
                modes: MaterializationSynchronizationMode.All)
        ]);

    static MaterializationCapabilityRequirement Requirement(
        string id,
        MaterializationCapabilityKind capability,
        MaterializationSynchronizationMode modes) => new(
        id: new(id),
        capability,
        guarantees: Guarantees(capability),
        operatingLimits: OperatingLimits(capability),
        modes);

    static MaterializationCapabilityProfile Profile(
        MaterializationEndpointRole role,
        string subject,
        ImmutableArray<MaterializationCapabilityRequirement> requirements) => new(
        id: new($"profile/{Uri.EscapeDataString(subject)}/v1"),
        role,
        subject,
        evidence:
        [
            .. requirements.Select(static requirement => new MaterializationCapabilityEvidence(
                id: new($"evidence/{requirement.Id.Value}"),
                capability: requirement.Capability,
                realization: CapabilityRealizationKind.Native,
                guarantees: requirement.Guarantees,
                operatingLimits: requirement.OperatingLimits,
                sourceReferences: ["tests/materialization-rebuild-json/v1"]))
        ]);

    static ImmutableArray<MaterializationOperatingLimit> OperatingLimits(
        MaterializationCapabilityKind capability) => capability switch
        {
            MaterializationCapabilityKind.SourceBatchedPointRead
                or MaterializationCapabilityKind.SourceParameterizedPredicateQuery
                or MaterializationCapabilityKind.SourceBoundedEnumeration =>
                [
                    new(MaterializationLimitKind.ReadItems, 100),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.SourceChangeDelivery =>
                [
                    new(MaterializationLimitKind.ChangeItems, 100),
                    new(MaterializationLimitKind.ReadBytes, ReadBytes)
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [
                    new(MaterializationLimitKind.WriteItems, WriteItems),
                    new(MaterializationLimitKind.WriteBytes, WriteBytes)
                ],
            _ => []
        };

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
            MaterializationCapabilityKind.SourceSettlement =>
                [MaterializationGuaranteeKind.ExplicitSettlement],
            MaterializationCapabilityKind.TargetGenerationIsolation =>
                [
                    MaterializationGuaranteeKind.GenerationIsolation,
                    MaterializationGuaranteeKind.FencedMutation
                ],
            MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete =>
                [
                    MaterializationGuaranteeKind.IdempotentWrite,
                    MaterializationGuaranteeKind.FencedMutation,
                    MaterializationGuaranteeKind.VersionConditionalWrite
                ],
            MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [MaterializationGuaranteeKind.ExactPerItemOutcome],
            MaterializationCapabilityKind.TargetFencedPromotion =>
                [
                    MaterializationGuaranteeKind.AtomicPromotion,
                    MaterializationGuaranteeKind.FencedPromotion
                ],
            MaterializationCapabilityKind.TargetGenerationAbandonment =>
                [MaterializationGuaranteeKind.AtomicDurableGenerationExclusion],
            MaterializationCapabilityKind.TargetSeal
                or MaterializationCapabilityKind.TargetValidation
                or MaterializationCapabilityKind.TargetRetirement
                or MaterializationCapabilityKind.TargetCleanup =>
                [MaterializationGuaranteeKind.FencedMutation],
            _ => []
        };

    static ExecutionProvenance Provenance() => new(
        new ExecutionProducerProvenance("tests/materialization-rebuild-json", "1"),
        new ExecutionSourceProvenance("tests/materialization-rebuild-json-plan"),
        DocumentOrigin.Generated);
}
