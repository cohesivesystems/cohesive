using System.Text.Json.Nodes;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Relations.Tests;

public sealed class RelationshipWorldTests
{
    static readonly GraphId GraphId = new("freight/v1");
    static readonly RelationshipId CarrierRelationshipId = new("load-carrier");

    [Fact]
    public void CompileAndGenerate_ResolvesEveryReferenceToTheNamedTargetPopulation()
    {
        var plan = CreateDefinition(carrierCount: 3, loadCount: 20).Compile();
        var carriers = plan.GetPopulation("carriers").Generate(seed: 42);
        var loads = plan.GetPopulation("loads").Generate(seed: 42);
        var carrierIds = carriers.Select(static item => item.EntityId.Value).ToHashSet(StringComparer.Ordinal);

        Assert.All(loads, load =>
            Assert.Contains(load.Observation.GetField("CarrierId").GetString()!, carrierIds));
        Assert.All(loads, load =>
            Assert.Equal(RelationshipWorldInterpreter.Identity, load.Replay.Interpreter));
        var repeated = plan.GetPopulation("loads").Generate(seed: 42);
        Assert.Equal(loads.Select(static item => item.EntityId), repeated.Select(static item => item.EntityId));
        Assert.Equal(loads.Select(static item => item.Replay), repeated.Select(static item => item.Replay));
        Assert.Equal(
            loads.Select(static item => item.Observation.ToCanonicalJson()),
            repeated.Select(static item => item.Observation.ToCanonicalJson()));
    }

    [Fact]
    public void ExemplarGeneration_CompletesCanonicalRelationships()
    {
        var plan = CreateDefinition(carrierCount: 2, loadCount: 3, includeExemplar: true).Compile();

        var exemplar = plan.GenerateExemplar("load-for-ui", seed: 42);
        var direct = plan.GetPopulation("loads").GenerateItem(seed: 42, sequenceIndex: 1);
        var materializer = ObservationMaterializer
            .For<TestLoad>(plan.GetPopulation("loads").Population.GenerationPlan.OutputShape)
            .Compile();
        var typed = plan.GenerateExemplar("load-for-ui", seed: 42, materializer);

        Assert.Equal(direct, exemplar);
        Assert.True(exemplar.Observation.TryGetField("CarrierId", out _));
        Assert.Equal(exemplar.EntityId, typed.EntityId);
        Assert.Equal(exemplar.Observation.GetField("CarrierId").GetString(), typed.Value.CarrierId);
    }

    [Fact]
    public void RelationshipWorld_AllowsOnlyTheCompositionToCompleteReferenceOwnedFields()
    {
        var definition = CreateDefinition(carrierCount: 2, loadCount: 3);
        var load = definition.World.Populations.Single(static population => population.Id == "loads");

        var standalone = GenerationCompiler.Compile(load.Generation);
        var composed = definition.CompileResult();

        Assert.Null(standalone.Plan);
        Assert.Contains(
            standalone.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == "simulation.generation.shapeFieldBindingMissing");
        Assert.True(composed.IsSuccessful);
        Assert.Throws<NotSupportedException>(() => ReferenceGenerationInterpreter.Generate(
            composed.Plan!.GetPopulation("loads").Population.GenerationPlan,
            seed: 1));
    }

    [Fact]
    public void Replay_IsPopulationScopedCanonicalAndStableUnderUnrelatedWorldContent()
    {
        var baselineDefinition = CreateDefinition(carrierCount: 3, loadCount: 5);
        var baseline = baselineDefinition.Compile();
        var extendedDefinition = baselineDefinition;
        var audit = CreateAuditGeneration();
        extendedDefinition = new(
            new WorldDefinition(
                extendedDefinition.World.Id,
                revision: "r2",
                [.. extendedDefinition.World.Populations, new("audit", 7, audit)],
                extendedDefinition.World.Exemplars),
            extendedDefinition.RelationshipCatalog,
            extendedDefinition.RelationshipBindings);
        var extended = extendedDefinition.Compile();
        var relationship = baselineDefinition.RelationshipCatalog.Catalog.GetRelationship(CarrierRelationshipId);
        var extendedCatalog = RelationshipCatalogDocument.FromCatalog(new([
            relationship,
            new RelationshipDefinition(
                new("unbound-load-carrier"),
                relationship.SourceShape,
                relationship.SourceReference,
                relationship.TargetShape,
                relationship.TargetKey,
                SourceReferenceUniqueness.GloballyUnique)
        ]));
        var catalogExtended = new RelationshipWorldDefinition(
            baselineDefinition.World,
            extendedCatalog,
            baselineDefinition.RelationshipBindings).Compile();
        var generated = baseline.GetPopulation("loads").GenerateItem(seed: 91, sequenceIndex: 2);
        var token = generated.Replay.ToToken();

        Assert.Equal(
            generated,
            baseline.GetPopulation("loads").Replay(RelationshipWorldReplayEvidence.ParseToken(token)));
        Assert.Equal(
            generated.Replay,
            extended.GetPopulation("loads").GenerateItem(seed: 91, sequenceIndex: 2).Replay);
        Assert.Equal(
            generated.Replay,
            catalogExtended.GetPopulation("loads").GenerateItem(seed: 91, sequenceIndex: 2).Replay);
        Assert.NotEqual(baseline.Fingerprint, catalogExtended.Fingerprint);
    }

    [Fact]
    public void UniqueRelationship_UsesDeterministicSelectionWithoutReplacement()
    {
        var definition = CreateDefinition(
            carrierCount: 5,
            loadCount: 5,
            uniqueness: SourceReferenceUniqueness.GloballyUnique);

        var selected = definition.Compile()
            .GetPopulation("loads")
            .Generate(seed: 7)
            .Select(static item => item.Observation.GetField("CarrierId").GetString())
            .ToArray();

        Assert.Equal(selected.Length, selected.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void OptionalRelationship_WithZeroPresenceOmitsTheReference()
    {
        var population = CreateDefinition(
                carrierCount: 0,
                loadCount: 2,
                selection: new(presenceProbability: 0d),
                referencePresence: FieldPresence.Optional)
            .Compile()
            .GetPopulation("loads");

        Assert.All(population.Generate(seed: 11), static item =>
            Assert.False(item.Observation.TryGetField("CarrierId", out _)));
    }

    [Fact]
    public void RequiredNullableRelationship_CannotSelectAbsence()
    {
        var result = CreateDefinition(
                carrierCount: 0,
                loadCount: 2,
                selection: new(presenceProbability: 0d),
                referenceNullability: FieldNullability.Nullable)
            .CompileResult();

        AssertDiagnostic(result, "simulation.relationshipWorld.requiredReferenceMayBeAbsent");
    }

    [Fact]
    public void ZeroPresenceUniqueRelationship_DoesNotRequireUnusedTargetCapacity()
    {
        var result = CreateDefinition(
                carrierCount: 0,
                loadCount: 5,
                uniqueness: SourceReferenceUniqueness.GloballyUnique,
                selection: new(presenceProbability: 0d),
                referencePresence: FieldPresence.Optional)
            .CompileResult();

        Assert.True(result.IsSuccessful, string.Join(" | ", result.Validation.Diagnostics.Select(static d => d.Message)));
    }

    [Fact]
    public void ReferenceSelection_UsesTheTargetPopulationIdentityPolicy()
    {
        var definition = CreateDefinition(
            carrierCount: 1,
            loadCount: 1,
            carrierIdentity: WorldEntityIdentityPolicy.FromUniqueObservationField("Name"));
        var plan = definition.Compile();
        var carrier = Assert.Single(plan.GetPopulation("carriers").Generate(seed: 23));
        var load = Assert.Single(plan.GetPopulation("loads").Generate(seed: 23));

        Assert.Equal("Carrier", carrier.EntityId.Value);
        Assert.Equal(carrier.EntityId.Value, load.Observation.GetField("CarrierId").GetString());
        Assert.NotEqual(
            CreateDefinition(carrierCount: 1, loadCount: 1)
                .Compile()
                .GetPopulation("loads")
                .ReplayFingerprint,
            plan.GetPopulation("loads").ReplayFingerprint);
    }

    [Fact]
    public void CompleteSourceGeneration_RejectsDuplicateTargetPopulationIdentities()
    {
        var population = CreateDefinition(
                carrierCount: 2,
                loadCount: 1,
                carrierIdentity: WorldEntityIdentityPolicy.FromUniqueObservationField("Name"))
            .Compile()
            .GetPopulation("loads");

        var exception = Assert.Throws<WorldGenerationException>(() => population.Generate(seed: 23));

        var diagnostic = Assert.Single(exception.Validation.Diagnostics);
        Assert.Equal("simulation.world.entityIdentityDuplicate", diagnostic.Code);
        Assert.Contains("Population 'carriers'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compiler_FailsClosedForGeneratedFieldCollisionAndInsufficientUniqueCapacity()
    {
        var collision = CreateDefinition(carrierCount: 2, loadCount: 2, generateReferenceField: true)
            .CompileResult();
        var insufficient = CreateDefinition(
                carrierCount: 1,
                loadCount: 2,
                uniqueness: SourceReferenceUniqueness.GloballyUnique)
            .CompileResult();

        AssertDiagnostic(collision, "simulation.generation.externalMemberCollision");
        AssertDiagnostic(insufficient, "simulation.relationshipWorld.uniqueCapacityInsufficient");
    }

    [Fact]
    public void Compiler_FailsClosedForUnresolvedSourceReference()
    {
        var result = CreateDefinition(
                carrierCount: 1,
                loadCount: 1,
                sourceReference: FieldPath.FromField("UnknownCarrierId"))
            .CompileResult();

        AssertDiagnostic(result, "simulation.relationshipWorld.sourceReferenceUnsupported");
    }

    [Fact]
    public void PortableDocument_RoundTripsExactCatalogBindingsAndGeneration()
    {
        var definition = CreateDefinition(carrierCount: 3, loadCount: 4);
        var json = RelationshipWorldDefinitionJsonSerializer.Serialize(definition);

        var restored = RelationshipWorldDefinitionJsonSerializer.Deserialize(json);
        var restoredPlan = restored.Compile();
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Document JSON was not an object.");

        Assert.Equal(RelationshipWorldDefinitionDocument.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(definition.Compile().Fingerprint, restoredPlan.Fingerprint);
        Assert.Equal(json, RelationshipWorldDefinitionJsonSerializer.Serialize(restored));
        Assert.Equal(
            CarrierRelationshipId.Value,
            root["definition"]!["relationshipBindings"]![0]!["relationshipId"]!.GetValue<string>());
        Assert.Equal(
            definition.Compile().GetPopulation("loads").Generate(seed: 9)
                .Select(static item => item.Observation.ToCanonicalJson()),
            restoredPlan.GetPopulation("loads").Generate(seed: 9)
                .Select(static item => item.Observation.ToCanonicalJson()));
    }

    [Fact]
    public void PortableDocument_NormalizesNonSemanticDeclarationOrder()
    {
        var definition = CreateDefinition(carrierCount: 3, loadCount: 4);
        var reordered = new RelationshipWorldDefinition(
            new WorldDefinition(
                definition.World.Id,
                definition.World.Revision,
                [.. definition.World.Populations.Reverse()],
                definition.World.Exemplars),
            definition.RelationshipCatalog,
            [.. definition.RelationshipBindings.Reverse()]);

        Assert.Equal(
            RelationshipWorldDefinitionJsonSerializer.Serialize(definition),
            RelationshipWorldDefinitionJsonSerializer.Serialize(reordered));
    }

    [Fact]
    public void PortableDocument_RejectsUnknownProperties()
    {
        var json = RelationshipWorldDefinitionJsonSerializer.Serialize(
            CreateDefinition(carrierCount: 1, loadCount: 1));
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Document JSON was not an object.");
        root["unexpected"] = true;

        var validation = RelationshipWorldDefinitionJsonSerializer.TryDeserialize(
            root.ToJsonString(),
            out var document);

        Assert.False(validation.IsValid);
        Assert.Null(document);
    }

    [Fact]
    public void PortableDocument_RejectsFingerprintTampering()
    {
        var json = RelationshipWorldDefinitionJsonSerializer.Serialize(
            CreateDefinition(carrierCount: 1, loadCount: 1));
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Document JSON was not an object.");
        root["fingerprint"]!["value"] = new string('0', 64);

        var validation = RelationshipWorldDefinitionJsonSerializer.TryDeserialize(
            root.ToJsonString(),
            out var document);

        Assert.False(validation.IsValid);
        Assert.Null(document);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == "simulation.relationshipWorld.document.contentInvalid");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Compiler_RejectsInvalidPresenceProbability(double probability)
    {
        var result = CreateDefinition(
                carrierCount: 2,
                loadCount: 2,
                selection: new(probability))
            .CompileResult();

        AssertDiagnostic(result, "simulation.relationshipWorld.presenceProbabilityInvalid");
    }

    static RelationshipWorldDefinition CreateDefinition(
        int carrierCount,
        int loadCount,
        SourceReferenceUniqueness uniqueness = SourceReferenceUniqueness.NotGuaranteed,
        bool generateReferenceField = false,
        WorldRelationshipSelectionPolicy? selection = null,
        FieldPresence referencePresence = FieldPresence.Required,
        FieldNullability referenceNullability = FieldNullability.NonNullable,
        WorldEntityIdentityPolicy? carrierIdentity = null,
        FieldPath? sourceReference = null,
        bool includeExemplar = false)
    {
        var shapes = CreateGraph(referencePresence, referenceNullability);
        var carrierShape = shapes.GetShape<TestCarrier>();
        var loadShape = shapes.GetShape<TestLoad>();
        var relationship = sourceReference is null
            ? Relationship.From<TestLoad>(shapes)
                .Reference(load => load.CarrierId)
                .To(carrierShape, CarrierRelationshipId, uniqueness)
            : new(
                CarrierRelationshipId,
                loadShape,
                sourceReference.Value,
                carrierShape,
                ObservationIdentityRelationshipTargetKey.Instance,
                uniqueness);
        var catalog = RelationshipCatalogDocument.FromCatalog(new([relationship]));
        var carriers = Simulation.Define<TestCarrier>(shapes, carrier => carrier
            .Member(value => value.Name, Gen.Constant("Carrier")));
        var loads = Simulation.Define<TestLoad>(shapes, load =>
        {
            load.Member(value => value.Number, Gen.Int32(minimum: 1, maximum: 10_000));
            if (generateReferenceField)
            {
                load.Member(
                    value => value.CarrierId,
                    Gen.EntityReference<TestCarrier>("duplicate-authority"));
            }
        });
        return SimulationRelations.DefineWorld(
            "world/freight",
            "r1",
            catalog,
            world =>
            {
                world.Population(
                    "carriers",
                    carrierCount,
                    carrierIdentity ?? WorldEntityIdentityPolicy.PopulationSequence,
                    carriers)
                    .Population("loads", loadCount, loads)
                    .Relationship("loads", CarrierRelationshipId, "carriers", selection);
                if (includeExemplar)
                {
                    world.Exemplar("load-for-ui", "loads", sequenceIndex: 1);
                }
            });
    }

    static ClrShapeGraphBuildResult CreateGraph(
        FieldPresence referencePresence = FieldPresence.Required,
        FieldNullability referenceNullability = FieldNullability.NonNullable) =>
        new ClrShapeGraphBuilder()
            .AddShape<TestCarrier>(ShapeRoles.Entity)
            .AddShape<TestLoad>(ShapeRoles.Entity)
            .AddEntityReference<TestLoad, TestCarrier>(
                load => load.CarrierId,
                presence: referencePresence,
                nullability: referenceNullability)
            .BuildResult(GraphId);

    static GenerationDefinition CreateAuditGeneration() =>
        Simulation.Define<TestAudit>(audit => audit
                .Member(value => value.Code, Gen.Constant("ok")))
            .Definition;

    static void AssertDiagnostic(RelationshipWorldCompilationResult result, string code) =>
        Assert.Contains(result.Validation.Diagnostics, diagnostic => diagnostic.Code == code);

    [ShapeDefinition("Carrier", ShapeRoles.Entity)]
    sealed record TestCarrier(string Name);

    [ShapeDefinition("Load", ShapeRoles.Entity)]
    sealed record TestLoad(int Number, string CarrierId);

    sealed record TestAudit(string Code);
}
