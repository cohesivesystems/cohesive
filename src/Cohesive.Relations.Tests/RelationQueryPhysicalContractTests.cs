using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryPhysicalContractTests
{
    [Fact]
    public void Relationship_reference_key_extraction_is_bounded_before_deduplication()
    {
        var reference = ObservationValue.FromArray(
        [
            ObservationValue.FromString("customer-2"),
            ObservationValue.FromString("customer-1"),
            ObservationValue.FromString("customer-1")
        ]);

        var exceeded = RelationQueryReferenceKeyExtractor.Extract(
            reference,
            maximumKeys: 2,
            CancellationToken.None,
            out var rejected);
        var accepted = RelationQueryReferenceKeyExtractor.Extract(
            reference,
            maximumKeys: 3,
            CancellationToken.None,
            out var keys);

        Assert.Equal(RelationQueryReferenceKeyExtractionState.BoundaryExceeded, exceeded);
        Assert.Empty(rejected);
        Assert.Equal(RelationQueryReferenceKeyExtractionState.Success, accepted);
        Assert.Equal(["customer-1", "customer-2"], keys.ToArray());
    }

    [Fact]
    public void Source_read_inconclusive_state_cannot_carry_unrepresentable_rows()
    {
        var field = new RelationQuerySourceReadField(
            new("field/load/id"),
            FieldPath.FromField("Id"),
            "id",
            RelationQuerySourceReadFieldPurpose.SemanticInput);
        var row = new RelationQuerySourceReadObservation(
            "load-1",
            new(new("graph"), new("Load")),
            [new(field, RelationQuerySourceReadFieldState.Value, ObservationValue.FromString("load-1"))]);

        Assert.Throws<ArgumentException>(() => new RelationQuerySourceReadResult(
            RelationQuerySourceReadState.Inconclusive,
            [row]));
        var partial = new RelationQuerySourceReadResult(RelationQuerySourceReadState.Partial, [row]);
        Assert.Single(partial.Observations);
    }

    [Fact]
    public void Source_read_contracts_retain_canonical_immutable_arrays_and_normalize_otherwise()
    {
        var shape = new QualifiedShapeId(new("graph"), new("Load"));
        var firstField = new RelationQuerySourceReadField(
            new("field/load/a"),
            FieldPath.FromField("A"),
            "a",
            RelationQuerySourceReadFieldPurpose.SemanticInput);
        var secondField = new RelationQuerySourceReadField(
            new("field/load/b"),
            FieldPath.FromField("B"),
            "b",
            RelationQuerySourceReadFieldPurpose.SemanticInput);
        var firstResult = new RelationQuerySourceReadFieldResult(
            firstField,
            RelationQuerySourceReadFieldState.Value,
            ObservationValue.FromString("a"));
        var secondResult = new RelationQuerySourceReadFieldResult(
            secondField,
            RelationQuerySourceReadFieldState.Value,
            ObservationValue.FromString("b"));
        var canonicalFields = ImmutableArray.Create(firstResult, secondResult);
        var reversedFields = ImmutableArray.Create(secondResult, firstResult);

        var first = new RelationQuerySourceReadObservation("load-a", shape, canonicalFields);
        var second = new RelationQuerySourceReadObservation("load-b", shape, reversedFields);

        Assert.True(canonicalFields.Equals(first.Fields));
        Assert.False(reversedFields.Equals(second.Fields));
        Assert.Collection(
            second.Fields,
            field => Assert.Same(firstResult, field),
            field => Assert.Same(secondResult, field));

        var canonicalObservations = ImmutableArray.Create(first, second);
        var reversedObservations = ImmutableArray.Create(second, first);
        var canonical = new RelationQuerySourceReadResult(
            RelationQuerySourceReadState.Complete,
            canonicalObservations);
        var normalized = new RelationQuerySourceReadResult(
            RelationQuerySourceReadState.Complete,
            reversedObservations);

        Assert.True(canonicalObservations.Equals(canonical.Observations));
        Assert.False(reversedObservations.Equals(normalized.Observations));
        Assert.Collection(
            normalized.Observations,
            observation => Assert.Same(first, observation),
            observation => Assert.Same(second, observation));
        Assert.Throws<ArgumentException>(() => new RelationQuerySourceReadResult(
            RelationQuerySourceReadState.Complete,
            [first, second, first]));

        var formattedField = new RelationQuerySourceReadField(
            new("field/load/collision"),
            FieldPath.FromField("[]"),
            "formatted",
            RelationQuerySourceReadFieldPurpose.SemanticInput);
        var elementField = new RelationQuerySourceReadField(
            new("field/load/collision"),
            new([FieldPathSegment.Element()]),
            "element",
            RelationQuerySourceReadFieldPurpose.SemanticInput);
        var formattedResult = new RelationQuerySourceReadFieldResult(
            formattedField,
            RelationQuerySourceReadFieldState.Missing);
        var elementResult = new RelationQuerySourceReadFieldResult(
            elementField,
            RelationQuerySourceReadFieldState.Missing);

        var canonicalCollisionFields = ImmutableArray.Create(formattedResult, elementResult);
        var reversedCollisionFields = ImmutableArray.Create(elementResult, formattedResult);
        var canonicalCollision = new RelationQuerySourceReadObservation(
            "load-canonical",
            shape,
            canonicalCollisionFields);
        var normalizedCollision = new RelationQuerySourceReadObservation(
            "load-normalized",
            shape,
            reversedCollisionFields);

        Assert.True(canonicalCollisionFields.Equals(canonicalCollision.Fields));
        Assert.False(reversedCollisionFields.Equals(normalizedCollision.Fields));
        Assert.Collection(
            normalizedCollision.Fields,
            field => Assert.Same(formattedResult, field),
            field => Assert.Same(elementResult, field));

        var request = new RelationQuerySourceReadRequest(
            new("sha256", "test/v1", "physical-plan"),
            new("stage"),
            new("placement"),
            new("source"),
            shape,
            "identity",
            [elementField, formattedField],
            new RelationQueryBoundedEnumeration(maximumRows: 1),
            maximumBufferedRows: 1);
        Assert.Collection(
            request.Fields,
            field => Assert.Same(formattedField, field),
            field => Assert.Same(elementField, field));

        Assert.Throws<ArgumentException>(() => new RelationQuerySourceReadObservation(
            "load-c",
            shape,
            [formattedResult, elementResult, formattedResult]));
    }

    [Fact]
    public void Placement_fingerprint_is_declaration_order_independent()
    {
        var first = CreatePlacement(reverse: false);
        var second = CreatePlacement(reverse: true);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(
            first.SourceInstances.Select(static source => source.Id),
            second.SourceInstances.Select(static source => source.Id));
        Assert.Equal(
            first.Bindings.Select(static binding => binding.Id),
            second.Bindings.Select(static binding => binding.Id));
    }

    [Fact]
    public void Placement_rejects_stale_fingerprint()
    {
        var placement = CreatePlacement(reverse: false);

        var exception = Assert.Throws<ArgumentException>(() => new RelationQuerySourcePlacement(
            placement.SchemaVersion,
            placement.Plan,
            placement.ConventionSetVersion,
            placement.SourceInstances,
            placement.Bindings,
            new("sha256", RelationQuerySourcePlacementFingerprinter.Canonicalization, new string('0', 64))));

        Assert.Contains("fingerprint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Physical_plan_round_trips_and_preserves_fingerprint()
    {
        var plan = CreatePhysicalPlan(reverseStages: true);
        var options = RelationQueryJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(plan, options);
        var roundTrip = JsonSerializer.Deserialize<CompiledRelationQueryPhysicalPlan>(json, options);

        Assert.NotNull(roundTrip);
        Assert.Equal(plan.Fingerprint, roundTrip.Fingerprint);
        Assert.Equal(
            new[] { new RelationQueryPhysicalStageId("read"), new("evidence"), new("terminal") },
            roundTrip.EvaluationOrder.ToArray());
    }

    [Fact]
    public void Physical_plan_identity_changes_with_policy_or_source_capability_evidence()
    {
        var baseline = CreatePhysicalPlan(reverseStages: false);
        var policyChanged = CreatePhysicalPlan(
            reverseStages: false,
            policy: Policy(maximumReferenceKeysPerObservation: 99));
        var evidenceChanged = CreatePhysicalPlan(
            reverseStages: false,
            placement: CreatePlacement(reverse: false, capabilityEvidenceIdentity: "evidence/enumeration/v2"));

        Assert.NotEqual(baseline.Fingerprint, policyChanged.Fingerprint);
        Assert.NotEqual(baseline.Placement.Fingerprint, evidenceChanged.Placement.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, evidenceChanged.Fingerprint);
        Assert.Equal(baseline.Plan.DefinitionFingerprint, policyChanged.Plan.DefinitionFingerprint);
        Assert.Equal(baseline.Plan.DefinitionFingerprint, evidenceChanged.Plan.DefinitionFingerprint);
    }

    [Fact]
    public void Physical_plan_rejects_a_cycle()
    {
        var placement = CreatePlacement(reverse: false);
        var provenance = Provenance();
        var stages = ImmutableArray.Create(
            new RelationQueryPhysicalStage(
                new("a"),
                RelationQueryPhysicalStageKind.RelationshipKeyExtraction,
                [new("b")],
                null,
                [new("source/load")],
                [],
                null,
                provenance),
            new RelationQueryPhysicalStage(
                new("b"),
                RelationQueryPhysicalStageKind.KeyDeduplication,
                [new("a")],
                null,
                [new("source/load")],
                [],
                null,
                provenance));

        var exception = Assert.Throws<ArgumentException>(() => new CompiledRelationQueryPhysicalPlan(
            CompiledRelationQueryPhysicalPlan.CurrentSchemaVersion,
            placement.Plan,
            RealizationFingerprint(),
            placement,
            Policy(),
            stages,
            new("missing")));

        Assert.Contains("acyclic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    static CompiledRelationQueryPhysicalPlan CreatePhysicalPlan(
        bool reverseStages,
        RelationQueryPhysicalPlanningPolicy? policy = null,
        RelationQuerySourcePlacement? placement = null)
    {
        placement ??= CreatePlacement(reverse: false);
        var provenance = Provenance();
        ImmutableArray<RelationQueryPhysicalStage> stages =
        [
            new(
                new("read"),
                RelationQueryPhysicalStageKind.SourceRead,
                [],
                new("place/load"),
                [new("source/load")],
                [new("field/load/id")],
                null,
                provenance),
            new(
                new("evidence"),
                RelationQueryPhysicalStageKind.RuntimeEvidenceAssembly,
                [new("read")],
                null,
                [new("source/load")],
                [],
                null,
                provenance),
            new(
                new("terminal"),
                RelationQueryPhysicalStageKind.ReferenceInterpreterTerminal,
                [new("evidence")],
                null,
                [new("source/load")],
                [],
                null,
                provenance)
        ];
        if (reverseStages)
            stages = [.. stages.Reverse()];
        return new(
            CompiledRelationQueryPhysicalPlan.CurrentSchemaVersion,
            placement.Plan,
            RealizationFingerprint(),
            placement,
            policy ?? Policy(),
            stages,
            new("terminal"));
    }

    static RelationQuerySourcePlacement CreatePlacement(
        bool reverse,
        string capabilityEvidenceIdentity = "evidence/enumeration/v1")
    {
        var plan = PlanReference();
        var profile = new RelationQueryTargetCapabilityProfile(
            new("source-target"),
            new("source-target/v1"),
            ["relation-query/v1"],
            ["compiler/v1"],
            [
                new RelationQueryTargetCapabilityEvidence(
                    new(capabilityEvidenceIdentity),
                    new PrimitiveRelationQueryCapability(
                        RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration))
            ]);
        ImmutableArray<RelationQuerySourceInstance> instances =
        [
            new(new("load-source"), new("domain-a"), profile, new(100, 1_000, 10, 2)),
            new(new("unused-source"), new("domain-b"), profile, new(100, 1_000, 10, 2))
        ];
        ImmutableArray<RelationQuerySourcePlacementBinding> bindings =
        [
            new(
                new("place/load"),
                new("source/load"),
                new("load"),
                new("load"),
                new(new("graph"), new("Load")),
                new("load-source"),
                RelationQuerySourcePlacementBindingKind.SourceSet,
                RelationQuerySourceAcquisitionKind.BoundedEnumeration,
                RelationQuerySourcePlacementOrigin.Explicit,
                new(new(new("graph"), new("Load")), "id"),
                [new(new("field/load/id"), FieldPath.FromField("Id"), "id")])
        ];
        if (reverse)
        {
            instances = [.. instances.Reverse()];
            bindings = [.. bindings.Reverse()];
        }
        return new(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            plan,
            "placement-conventions/v1",
            instances,
            bindings);
    }

    static RelationQueryCompiledPlanReference PlanReference() => new(
        "compiler/v1",
        "relation-query/v1",
        new("sha256", "definition/v1", "definition"),
        new("sha256", "shapes/v1", "shapes"),
        null,
        new("sha256", "demand/v1", "demand"),
        [new("source/load"), new("field/load/id")]);

    static RelationQueryRealizationFingerprint RealizationFingerprint() =>
        new("sha256", "relation-query-realization/v1", "realization");

    static RelationQueryPhysicalPlanningPolicy Policy(
        long maximumReferenceKeysPerObservation = 100) => new(
        new("physical-policy/v1"),
        "physical-conventions/v1",
        100,
        1_000,
        1_000,
        10,
        maximumReferenceKeysPerObservation,
        2);

    static RelationQueryPhysicalStageProvenance Provenance() => new(
        nodes: [new("load")],
        inputs: [new("source/load"), new("field/load/id")],
        requirements: [new("requirement/source/load")],
        placementBindings: [new("place/load")],
        policyDecisions: [new("bounded-read")]);
}
