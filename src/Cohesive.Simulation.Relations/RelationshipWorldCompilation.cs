using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Relations;

/// <summary>Result of attempting relationship-aware world compilation.</summary>
public sealed class RelationshipWorldCompilationResult
{
    internal RelationshipWorldCompilationResult(
        RelationshipWorldDefinition definition,
        CompiledRelationshipWorldPlan? plan,
        DocumentValidationResult validation)
    {
        Definition = definition;
        Plan = plan;
        Validation = validation;
    }

    /// <summary>Gets the exact supplied definition.</summary>
    public RelationshipWorldDefinition Definition { get; }

    /// <summary>Gets the compiled plan when validation succeeded.</summary>
    public CompiledRelationshipWorldPlan? Plan { get; }

    /// <summary>Gets deterministic structured diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Gets whether a complete plan was produced.</summary>
    public bool IsSuccessful => Plan is not null && Validation.IsValid;
}

/// <summary>Failure raised when relationship-world compilation produces errors.</summary>
public sealed class RelationshipWorldCompilationException : InvalidOperationException
{
    /// <summary>Creates a compilation exception from structured diagnostics.</summary>
    /// <param name="validation">Diagnostics explaining why compilation failed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    public RelationshipWorldCompilationException(DocumentValidationResult validation)
        : base(CreateMessage(validation)) => Validation = validation;

    /// <summary>Gets the structured compilation diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    static string CreateMessage(DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        return "Relationship world could not be compiled: " + string.Join(
            " | ",
            validation.Diagnostics
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }
}

/// <summary>Compiled resolution of one population binding against the canonical relationship authority.</summary>
public sealed class CompiledWorldPopulationRelationship
{
    internal CompiledWorldPopulationRelationship(
        WorldPopulationRelationshipBinding definition,
        RelationshipDefinition relationship,
        FieldDefinition sourceField,
        CompiledWorldPopulation targetPopulation)
    {
        Definition = definition;
        Relationship = relationship;
        SourceField = sourceField;
        TargetPopulation = targetPopulation;
    }

    /// <summary>Gets the normalized world-local population binding.</summary>
    public WorldPopulationRelationshipBinding Definition { get; }

    /// <summary>Gets the exact canonical relationship resolved by the binding.</summary>
    public RelationshipDefinition Relationship { get; }

    /// <summary>Gets the source field resolved from the canonical relationship and shape graph.</summary>
    public FieldDefinition SourceField { get; }

    /// <summary>Gets the compiled target population.</summary>
    public CompiledWorldPopulation TargetPopulation { get; }
}

/// <summary>One compiled population in a relationship-aware world plan.</summary>
public sealed class CompiledRelationshipWorldPopulation
{
    internal CompiledRelationshipWorldPopulation(
        CompiledWorldPopulation population,
        ImmutableArray<CompiledWorldPopulationRelationship> relationships,
        string replayFingerprint)
    {
        Population = population;
        Relationships = relationships;
        ReplayFingerprint = replayFingerprint;
    }

    /// <summary>Gets the normalized population and locally compiled generation plan.</summary>
    public CompiledWorldPopulation Population { get; }

    /// <summary>Gets compiled outgoing relationships ordered by canonical relationship identity.</summary>
    public ImmutableArray<CompiledWorldPopulationRelationship> Relationships { get; }

    /// <summary>Gets the fingerprint of semantics affecting generation of this population.</summary>
    public string ReplayFingerprint { get; }

    /// <summary>Generates one exact member at a stable sequence coordinate.</summary>
    /// <param name="seed">World root seed.</param>
    /// <param name="sequenceIndex">Zero-based population sequence index.</param>
    /// <returns>The generated observation, canonical entity identity, and relationship-world replay evidence.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is outside population bounds.</exception>
    /// <remarks>
    /// This operation cannot prove population-wide identity or relationship uniqueness in isolation. Use
    /// <see cref="Generate(long)"/> when the source or a referenced target uses a uniqueness assertion.
    /// </remarks>
    public GeneratedRelationshipWorldItem GenerateItem(long seed, long sequenceIndex)
    {
        if (sequenceIndex < 0 || sequenceIndex >= Population.Definition.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceIndex),
                sequenceIndex,
                $"Sequence index must be from 0 through {Population.Definition.Count - 1}.");
        }

        return RelationshipWorldInterpreter.Generate(this, seed, sequenceIndex);
    }

    /// <summary>Generates and materializes one exact member at a stable sequence coordinate.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="seed">World root seed.</param>
    /// <param name="sequenceIndex">Zero-based population sequence index.</param>
    /// <param name="materializer">Materializer compiled for the population's exact output shape.</param>
    /// <returns>The typed value, authoritative observation, identity, and replay evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="materializer"/> targets another shape.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is outside population bounds.</exception>
    /// <remarks>
    /// This operation cannot prove population-wide identity or relationship uniqueness in isolation. Use
    /// <see cref="Generate{T}(long, ObservationMaterializer{T})"/> when the source or a referenced target uses a
    /// uniqueness assertion.
    /// </remarks>
    public GeneratedRelationshipWorldItem<T> GenerateItem<T>(
        long seed,
        long sequenceIndex,
        ObservationMaterializer<T> materializer)
    {
        ValidateMaterializer(materializer);
        var item = GenerateItem(seed, sequenceIndex);
        return new(
            item.EntityId,
            materializer.Materialize(item.Observation),
            item.Observation,
            item.Replay);
    }

    /// <summary>Generates the complete bounded population eagerly.</summary>
    /// <param name="seed">World root seed.</param>
    /// <returns>Items in ascending population sequence order.</returns>
    /// <exception cref="WorldGenerationException">
    /// A source or referenced target identity is invalid or duplicated, or a globally unique relationship repeats.
    /// </exception>
    public ImmutableArray<GeneratedRelationshipWorldItem> Generate(long seed)
    {
        var generated = ImmutableArray.CreateBuilder<GeneratedRelationshipWorldItem>(Population.Definition.Count);
        foreach (var item in Enumerate(seed))
            generated.Add(item);
        return generated.MoveToImmutable();
    }

    internal IEnumerable<GeneratedRelationshipWorldItem> Enumerate(long seed)
    {
        RelationshipWorldInterpreter.ValidateTargetIdentities(this, seed);
        var unique = Population.Definition.EntityIdentity.Source == WorldEntityIdentitySource.UniqueObservationField
            ? new HashSet<EntityId>()
            : null;
        var uniqueRelationships = Relationships
            .Where(static relationship =>
                relationship.Relationship.SourceReferenceUniqueness == SourceReferenceUniqueness.GloballyUnique)
            .ToDictionary(
                static relationship => relationship.Relationship.Id,
                static _ => new HashSet<EntityId>());
        for (var index = 0; index < Population.Definition.Count; index++)
        {
            var item = GenerateItem(seed, index);
            if (unique is not null && !unique.Add(item.EntityId))
            {
                throw WorldGenerationException.IdentityFailure(
                    Population.Definition.Id,
                    index,
                    "simulation.world.entityIdentityDuplicate",
                    $"Population '{Population.Definition.Id}' resolves entity identity '{item.EntityId.Value}' more than once.");
            }

            foreach (var relationship in Relationships)
            {
                if (!uniqueRelationships.TryGetValue(relationship.Relationship.Id, out var selected)
                    || !item.Observation.TryGetField(relationship.Relationship.SourceReference, out var reference))
                {
                    continue;
                }

                var targetId = new EntityId(reference.GetString()!);
                if (!selected.Add(targetId))
                {
                    throw RelationshipFailure(
                        Population.Definition.Id,
                        index,
                        relationship.Relationship.SourceReference,
                        "simulation.relationshipWorld.referenceIdentityDuplicate",
                        $"Globally unique relationship '{relationship.Relationship.Id.Value}' selected target "
                        + $"identity '{targetId.Value}' more than once.");
                }
            }

            yield return item;
        }
    }

    static WorldGenerationException RelationshipFailure(
        string populationId,
        long sequenceIndex,
        FieldPath sourceReference,
        string code,
        string detail) =>
        new(new([
            new(
                Code: code,
                Severity: DiagnosticSeverity.Error,
                Message: detail,
                Location: $"/populations/{EscapePointerToken(populationId)}/items/{sequenceIndex}/observation/"
                    + string.Join('/', sourceReference.Segments.Select(static segment =>
                        EscapePointerToken(segment.Segment!))),
                Evidence: new(stage: "relationship-world-generation"))
        ]));

    static string EscapePointerToken(string value) => value
        .Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>Generates and materializes the complete bounded population.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="seed">World root seed.</param>
    /// <param name="materializer">Materializer compiled for the population's exact output shape.</param>
    /// <returns>Generated CLR values retaining authoritative observations and replay evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materializer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="materializer"/> targets another shape.</exception>
    /// <exception cref="WorldGenerationException">
    /// A source or referenced target identity is invalid or duplicated, or a globally unique relationship repeats.
    /// </exception>
    public ImmutableArray<GeneratedRelationshipWorldItem<T>> Generate<T>(
        long seed,
        ObservationMaterializer<T> materializer)
    {
        ValidateMaterializer(materializer);

        var observations = Generate(seed);
        var generated = ImmutableArray.CreateBuilder<GeneratedRelationshipWorldItem<T>>(observations.Length);
        foreach (var item in observations)
        {
            generated.Add(new(
                item.EntityId,
                materializer.Materialize(item.Observation),
                item.Observation,
                item.Replay));
        }

        return generated.MoveToImmutable();
    }

    void ValidateMaterializer<T>(ObservationMaterializer<T> materializer)
    {
        ArgumentNullException.ThrowIfNull(materializer);
        if (materializer.ShapeId == Population.GenerationPlan.OutputShape.QualifiedId)
            return;

        throw new ArgumentException(
            $"Materializer shape '{materializer.ShapeId}' does not match population shape "
            + $"'{Population.GenerationPlan.OutputShape.QualifiedId}'.",
            nameof(materializer));
    }

    /// <summary>Replays one exact population member.</summary>
    /// <param name="replay">Evidence emitted by this compiled population.</param>
    /// <returns>The exact regenerated item.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="replay"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="replay"/> names another population plan.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The retained sequence index is outside population bounds.</exception>
    public GeneratedRelationshipWorldItem Replay(RelationshipWorldReplayEvidence replay)
    {
        RelationshipWorldInterpreter.ValidateReplay(this, replay);
        return GenerateItem(replay.RootSeed, replay.SequenceIndex);
    }
}

/// <summary>Immutable provider-neutral executable plan for one exact relationship world.</summary>
public sealed class CompiledRelationshipWorldPlan
{
    readonly ImmutableDictionary<string, CompiledRelationshipWorldPopulation> populationsById;

    internal CompiledRelationshipWorldPlan(
        RelationshipWorldDefinition definition,
        ImmutableArray<CompiledRelationshipWorldPopulation> populations,
        string fingerprint)
    {
        Definition = definition;
        Populations = populations;
        Fingerprint = fingerprint;
        populationsById = populations.ToImmutableDictionary(
            static population => population.Population.Definition.Id,
            StringComparer.Ordinal);
    }

    /// <summary>Gets the normalized canonical relationship-world definition.</summary>
    public RelationshipWorldDefinition Definition { get; }

    /// <summary>Gets compiled populations ordered by stable identity.</summary>
    public ImmutableArray<CompiledRelationshipWorldPopulation> Populations { get; }

    /// <summary>Gets named exact members ordered by stable exemplar identity.</summary>
    public ImmutableArray<WorldExemplarDefinition> Exemplars => Definition.World.Exemplars;

    /// <summary>Gets the lowercase SHA-256 fingerprint of exact relationship-world semantic content.</summary>
    public string Fingerprint { get; }

    /// <summary>Gets the fingerprint algorithm identity.</summary>
    public string FingerprintAlgorithm => RelationshipWorldCompiler.FingerprintAlgorithm;

    /// <summary>Gets the fingerprint canonicalization profile.</summary>
    public string FingerprintCanonicalization => RelationshipWorldCompiler.FingerprintCanonicalization;

    /// <summary>Gets one compiled population by stable identity.</summary>
    /// <param name="id">Stable population identity.</param>
    /// <returns>The compiled relationship-aware population.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">The plan has no population with the supplied identity.</exception>
    public CompiledRelationshipWorldPopulation GetPopulation(string id)
    {
        id = Guard.RequireNotNullOrWhiteSpace(id);
        return populationsById.TryGetValue(id, out var population)
            ? population
            : throw new KeyNotFoundException($"Relationship world '{Definition.World.Id}' has no population '{id}'.");
    }

    /// <summary>Finds one named exemplar by stable world-wide identity.</summary>
    /// <param name="id">Stable exemplar identity.</param>
    /// <param name="exemplar">Receives the exemplar declaration when found.</param>
    /// <returns><see langword="true"/> when the exemplar exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public bool TryGetExemplar(string id, out WorldExemplarDefinition? exemplar)
    {
        id = Guard.RequireNotNullOrWhiteSpace(id);
        foreach (var candidate in Exemplars)
        {
            if (!string.Equals(candidate.Id, id, StringComparison.Ordinal))
                continue;

            exemplar = candidate;
            return true;
        }

        exemplar = null;
        return false;
    }

    /// <summary>Gets one named exemplar by stable world-wide identity.</summary>
    /// <param name="id">Stable exemplar identity.</param>
    /// <returns>The exemplar declaration named by <paramref name="id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">The plan has no exemplar with the supplied identity.</exception>
    public WorldExemplarDefinition GetExemplar(string id) =>
        TryGetExemplar(id, out var exemplar)
            ? exemplar!
            : throw new KeyNotFoundException(
                $"Relationship world '{Definition.World.Id}' has no exemplar '{id}'.");

    /// <summary>Generates the exact relationship-complete item named by a world exemplar.</summary>
    /// <param name="id">Stable exemplar identity.</param>
    /// <param name="seed">World root seed.</param>
    /// <returns>The generated observation, canonical entity identity, and replay evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">The plan has no exemplar with the supplied identity.</exception>
    public GeneratedRelationshipWorldItem GenerateExemplar(string id, long seed)
    {
        var exemplar = GetExemplar(id);
        return GetPopulation(exemplar.PopulationId).GenerateItem(seed, exemplar.SequenceIndex);
    }

    /// <summary>Generates and materializes the exact relationship-complete item named by a world exemplar.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="id">Stable exemplar identity.</param>
    /// <param name="seed">World root seed.</param>
    /// <param name="materializer">Materializer compiled for the exemplar population's exact output shape.</param>
    /// <returns>The typed value, authoritative observation, identity, and replay evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> or <paramref name="materializer"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is empty or <paramref name="materializer"/> targets another shape.
    /// </exception>
    /// <exception cref="KeyNotFoundException">The plan has no exemplar with the supplied identity.</exception>
    public GeneratedRelationshipWorldItem<T> GenerateExemplar<T>(
        string id,
        long seed,
        ObservationMaterializer<T> materializer)
    {
        var exemplar = GetExemplar(id);
        return GetPopulation(exemplar.PopulationId).GenerateItem(seed, exemplar.SequenceIndex, materializer);
    }
}

/// <summary>Compiles relationship-world IR against exact world, shape, and relationship authorities.</summary>
public static class RelationshipWorldCompiler
{
    /// <summary>Fingerprint algorithm for relationship-world plans.</summary>
    public const string FingerprintAlgorithm = "sha256";

    /// <summary>Canonicalization profile for relationship-world plans.</summary>
    public const string FingerprintCanonicalization = "cohesive-simulation-relations-world/v3-c14n/v1";

    /// <summary>Compiles and validates one canonical relationship-world definition.</summary>
    /// <param name="definition">Definition to compile.</param>
    /// <returns>A complete plan or deterministic structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static RelationshipWorldCompilationResult Compile(RelationshipWorldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DocumentValidationDiagnostic> diagnostics = [];
        var documentValidation = RelationshipCatalogDocumentSemanticValidator.Validate(definition.RelationshipCatalog);
        AddDiagnostics(diagnostics, documentValidation, "/relationshipCatalog");

        Dictionary<string, (WorldPopulationDefinition Population, int Index)> populations =
            new(StringComparer.Ordinal);
        Dictionary<GraphId, ShapeGraph> graphs = [];
        for (var index = 0; index < definition.World.Populations.Length; index++)
        {
            var population = definition.World.Populations[index];
            if (population is null || !populations.TryAdd(population.Id, (population, index)))
                continue;

            var graph = population.Generation.ShapeGraph;
            if (!graphs.TryAdd(graph.Id, graph) && !AreEquivalentGraphs(graphs[graph.Id], graph))
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.shapeGraphSnapshotConflict",
                    $"Populations retain conflicting snapshots for shape graph '{graph.Id.Value}'.",
                    $"/populations/{index}/generation/shapeGraph");
            }
        }

        if (documentValidation.IsValid)
        {
            AddDiagnostics(
                diagnostics,
                RelationshipCatalogValidator.Validate(definition.RelationshipCatalog.Catalog, graphs.Values),
                "/relationshipCatalog/catalog");
        }

        var externalMembers = ValidateBindings(definition, populations, diagnostics);
        var baseCompilation = WorldCompiler.Compile(definition.World, externalMembers);
        AddDiagnostics(diagnostics, baseCompilation.Validation, string.Empty);

        var validation = new DocumentValidationResult(
            DocumentValidationDiagnostics.Normalize([.. diagnostics]));
        if (!validation.IsValid || baseCompilation.Plan is null)
            return new(definition, plan: null, validation);

        var basePlan = baseCompilation.Plan;
        var normalizedBindings = definition.RelationshipBindings
            .OrderBy(static binding => binding.SourcePopulationId, StringComparer.Ordinal)
            .ThenBy(static binding => binding.RelationshipId.Value, StringComparer.Ordinal)
            .ThenBy(static binding => binding.TargetPopulationId, StringComparer.Ordinal)
            .ToImmutableArray();
        var normalized = new RelationshipWorldDefinition(
            basePlan.Definition,
            definition.RelationshipCatalog,
            normalizedBindings);
        var fingerprint = ComputeFingerprint(basePlan, normalized);
        var compiledPopulations = CompilePopulations(basePlan, normalized);
        return new(
            definition,
            new CompiledRelationshipWorldPlan(normalized, compiledPopulations, fingerprint),
            validation);
    }

    static Dictionary<string, ImmutableArray<FieldName>> ValidateBindings(
        RelationshipWorldDefinition definition,
        IReadOnlyDictionary<string, (WorldPopulationDefinition Population, int Index)> populations,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        Dictionary<string, HashSet<FieldName>> external = new(StringComparer.Ordinal);
        HashSet<(string Population, RelationshipId Relationship)> relationships = [];
        HashSet<(string Population, string Field)> fields = [];
        for (var index = 0; index < definition.RelationshipBindings.Length; index++)
        {
            var binding = definition.RelationshipBindings[index];
            var location = $"/relationshipBindings/{index}";
            if (binding is null)
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.bindingMissing",
                    "A relationship world cannot contain a null population binding.",
                    location);
                continue;
            }

            if (!relationships.Add((binding.SourcePopulationId, binding.RelationshipId)))
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.relationshipDuplicate",
                    $"Population '{binding.SourcePopulationId}' binds relationship '{binding.RelationshipId.Value}' more than once.",
                    $"{location}/relationshipId");
            }

            if (!populations.TryGetValue(binding.SourcePopulationId, out var source))
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.sourcePopulationUnknown",
                    $"Relationship binding names unknown source population '{binding.SourcePopulationId}'.",
                    $"{location}/sourcePopulationId");
                continue;
            }

            if (!definition.RelationshipCatalog.Catalog.TryGetRelationship(binding.RelationshipId, out var relationship))
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.relationshipUnknown",
                    $"Relationship binding names unknown relationship '{binding.RelationshipId.Value}'.",
                    $"{location}/relationshipId");
                continue;
            }

            var sourceShape = new QualifiedShapeId(
                source.Population.Generation.ShapeGraph.Id,
                source.Population.Generation.Root.ShapeId);
            if (relationship.SourceShape != sourceShape)
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.sourceShapeMismatch",
                    $"Relationship '{relationship.Id.Value}' starts at '{relationship.SourceShape}', not source population shape '{sourceShape}'.",
                    $"{location}/sourcePopulationId");
                continue;
            }

            if (!TryResolveSourceField(source.Population, relationship, out var sourceField))
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.sourceReferenceUnsupported",
                    $"Relationship '{relationship.Id.Value}' must identify one top-level source field in population "
                    + $"'{binding.SourcePopulationId}'.",
                    $"{location}/relationshipId");
                continue;
            }

            if (!fields.Add((binding.SourcePopulationId, sourceField.Name.Value)))
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.sourceFieldDuplicate",
                    $"Population '{binding.SourcePopulationId}' field '{sourceField.Name.Value}' is supplied by more than one relationship binding.",
                    $"{location}/relationshipId");
            }

            if (!external.TryGetValue(binding.SourcePopulationId, out var populationFields))
            {
                populationFields = [];
                external.Add(binding.SourcePopulationId, populationFields);
            }
            populationFields.Add(sourceField.Name);

            ValidateSourceContract(source.Population, sourceField, relationship, binding, location, diagnostics);
            if (!populations.TryGetValue(binding.TargetPopulationId, out var target))
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.targetPopulationUnknown",
                    $"Relationship binding names unknown target population '{binding.TargetPopulationId}'.",
                    $"{location}/targetPopulationId");
                continue;
            }

            var targetShape = new QualifiedShapeId(
                target.Population.Generation.ShapeGraph.Id,
                target.Population.Generation.Root.ShapeId);
            if (relationship.TargetShape != targetShape)
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.targetShapeMismatch",
                    $"Relationship '{relationship.Id.Value}' targets '{relationship.TargetShape}', not target population shape '{targetShape}'.",
                    $"{location}/targetPopulationId");
            }

            if (binding.Selection.PresenceProbability > 0d && target.Population.Count == 0)
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.targetPopulationEmpty",
                    $"Relationship '{relationship.Id.Value}' can select a target but population '{target.Population.Id}' is empty.",
                    $"{location}/targetPopulationId");
            }

            if (relationship.SourceReferenceUniqueness == SourceReferenceUniqueness.GloballyUnique
                && binding.Selection.PresenceProbability > 0d
                && source.Population.Count > target.Population.Count)
            {
                Add(
                    diagnostics,
                    "simulation.relationshipWorld.uniqueCapacityInsufficient",
                    $"Unique relationship '{relationship.Id.Value}' has '{source.Population.Count}' source members but only '{target.Population.Count}' target members.",
                    $"{location}/targetPopulationId");
            }
        }

        foreach (var (populationId, population) in populations)
        {
            if (population.Population.EntityIdentity.ObservationField is not { } identityField
                || identityField.Segments is not [{ Kind: SegmentKind.Field, Segment: { } fieldName }]
                || !external.TryGetValue(populationId, out var populationFields)
                || !populationFields.Contains(new(fieldName)))
            {
                continue;
            }

            Add(
                diagnostics,
                "simulation.relationshipWorld.identityDependsOnRelationship",
                $"Population '{populationId}' cannot derive its entity identity from relationship-bound field '{fieldName}'.",
                $"/populations/{population.Index}/entityIdentity/observationField");
        }

        return external.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.OrderBy(static field => field.Value, StringComparer.Ordinal).ToImmutableArray(),
            StringComparer.Ordinal);
    }

    static void ValidateSourceContract(
        WorldPopulationDefinition source,
        FieldDefinition field,
        RelationshipDefinition relationship,
        WorldPopulationRelationshipBinding binding,
        string location,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (field.Type is not EntityReferenceTypeRef
            || field.Cardinality != FieldCardinality.Single
            || relationship.TargetKey is not ObservationIdentityRelationshipTargetKey)
        {
            Add(
                diagnostics,
                "simulation.relationshipWorld.referenceContractUnsupported",
                $"Relationship '{relationship.Id.Value}' must use a single-valued entity reference targeting observation identity.",
                $"{location}/relationshipId");
        }

        if (field.Mutability == FieldMutability.Computed)
        {
            Add(
                diagnostics,
                "simulation.relationshipWorld.computedReferenceUnsupported",
                $"Computed field '{field.Name.Value}' cannot be assigned by relationship-world generation.",
                $"{location}/relationshipId");
        }

        var probability = binding.Selection.PresenceProbability;
        if (!double.IsFinite(probability) || probability is < 0d or > 1d)
        {
            Add(
                diagnostics,
                "simulation.relationshipWorld.presenceProbabilityInvalid",
                $"Presence probability '{probability}' must be finite and from zero through one.",
                $"{location}/selection/presenceProbability");
        }

        if (field.Presence == FieldPresence.Required && probability != 1d)
        {
            Add(
                diagnostics,
                "simulation.relationshipWorld.requiredReferenceMayBeAbsent",
                $"Required reference field '{field.Name.Value}' must use presence probability one.",
                $"{location}/selection/presenceProbability");
        }
    }

    static bool TryResolveSourceField(
        WorldPopulationDefinition source,
        RelationshipDefinition relationship,
        out FieldDefinition field)
    {
        field = null!;
        if (relationship.SourceReference.Segments is not [{ Kind: SegmentKind.Field, Segment: { } fieldName }]
            || !source.Generation.ShapeGraph.TryGetShape(source.Generation.Root.ShapeId, out var shape)
            || !shape.TryGetField(fieldName, out var resolved)
            || resolved is null)
        {
            return false;
        }

        field = resolved;
        return true;
    }

    static ImmutableArray<CompiledRelationshipWorldPopulation> CompilePopulations(
        CompiledWorldPlan basePlan,
        RelationshipWorldDefinition definition)
    {
        var baseById = basePlan.Populations.ToDictionary(
            static population => population.Definition.Id,
            StringComparer.Ordinal);
        Dictionary<string, ImmutableArray<CompiledWorldPopulationRelationship>> relationships =
            new(StringComparer.Ordinal);
        foreach (var group in definition.RelationshipBindings.GroupBy(
                     static binding => binding.SourcePopulationId,
                     StringComparer.Ordinal))
        {
            relationships.Add(
                group.Key,
                [.. group.Select(binding =>
                {
                    var relationship = definition.RelationshipCatalog.Catalog.GetRelationship(binding.RelationshipId);
                    TryResolveSourceField(baseById[group.Key].Definition, relationship, out var field);
                    return new CompiledWorldPopulationRelationship(
                        binding,
                        relationship,
                        field,
                        baseById[binding.TargetPopulationId]);
                })]);
        }

        var compiled = ImmutableArray.CreateBuilder<CompiledRelationshipWorldPopulation>(basePlan.Populations.Length);
        foreach (var population in basePlan.Populations)
        {
            var outgoing = relationships.TryGetValue(population.Definition.Id, out var configured)
                ? configured
                : ImmutableArray<CompiledWorldPopulationRelationship>.Empty;
            compiled.Add(new(
                population,
                outgoing,
                ComputeReplayFingerprint(population, outgoing)));
        }

        return compiled.MoveToImmutable();
    }

    static string ComputeFingerprint(CompiledWorldPlan basePlan, RelationshipWorldDefinition definition)
    {
        using SimulationFingerprintWriter writer = new();
        writer.Append(FingerprintCanonicalization);
        writer.Append(basePlan.FingerprintAlgorithm);
        writer.Append(basePlan.FingerprintCanonicalization);
        writer.Append(basePlan.Fingerprint);
        AppendCatalog(writer, definition.RelationshipCatalog);
        writer.Append(definition.RelationshipBindings.Length);
        foreach (var binding in definition.RelationshipBindings)
            AppendBinding(writer, binding);
        return writer.Complete();
    }

    static string ComputeReplayFingerprint(
        CompiledWorldPopulation population,
        ImmutableArray<CompiledWorldPopulationRelationship> relationships)
    {
        using SimulationFingerprintWriter writer = new();
        writer.Append(RelationshipWorldInterpreter.ReplayCanonicalization);
        writer.Append(RelationshipWorldInterpreter.Identity);
        writer.Append(ReferenceGenerationInterpreter.EntropyAlgorithm);
        writer.Append(WorldPopulationScopeConvention.Identity);
        writer.Append(WorldEntitySequenceIdentityConvention.Identity);
        writer.Append(population.Scope.Value);
        writer.Append(population.GenerationPlan.FingerprintAlgorithm);
        writer.Append(population.GenerationPlan.FingerprintCanonicalization);
        writer.Append(population.GenerationPlan.Fingerprint);
        writer.Append((int)population.Definition.EntityIdentity.Source);
        if (population.Definition.EntityIdentity.ObservationField is { } identityField)
        {
            writer.Append(identityField.Segments.Length);
            foreach (var segment in identityField.Segments)
            {
                writer.Append((int)segment.Kind);
                writer.Append(segment.Segment ?? string.Empty);
            }
        }
        else
        {
            writer.Append(0);
        }

        writer.Append(relationships.Length);
        foreach (var relationship in relationships)
        {
            AppendRelationship(writer, relationship.Relationship);
            AppendBinding(writer, relationship.Definition);
            var target = relationship.TargetPopulation;
            writer.Append(target.Definition.Count);
            writer.Append(target.Scope.Value);
            writer.Append((int)target.Definition.EntityIdentity.Source);
            if (target.Definition.EntityIdentity.Source == WorldEntityIdentitySource.UniqueObservationField)
            {
                writer.Append(target.GenerationPlan.Fingerprint);
                AppendFieldPath(writer, target.Definition.EntityIdentity.ObservationField!.Value);
            }
        }
        return writer.Complete();
    }

    static void AppendRelationship(SimulationFingerprintWriter writer, RelationshipDefinition relationship) =>
        AppendCatalog(
            writer,
            RelationshipCatalogDocument.FromCatalog(new RelationshipCatalog([relationship])));

    static void AppendFieldPath(SimulationFingerprintWriter writer, FieldPath path)
    {
        writer.Append(path.Segments.Length);
        foreach (var segment in path.Segments)
        {
            writer.Append((int)segment.Kind);
            writer.Append(segment.Segment ?? string.Empty);
        }
    }

    static void AppendCatalog(SimulationFingerprintWriter writer, RelationshipCatalogDocument catalog)
    {
        writer.Append(catalog.SchemaVersion);
        writer.Append(catalog.CatalogFingerprint.Algorithm);
        writer.Append(catalog.CatalogFingerprint.Canonicalization);
        writer.Append(catalog.CatalogFingerprint.Value);
    }

    static void AppendBinding(SimulationFingerprintWriter writer, WorldPopulationRelationshipBinding binding)
    {
        writer.Append(binding.SourcePopulationId);
        writer.Append(binding.RelationshipId.Value);
        writer.Append(binding.TargetPopulationId);
        writer.Append(binding.Selection.PresenceProbability);
    }

    static void AddDiagnostics(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        DocumentValidationResult validation,
        string prefix)
    {
        foreach (var diagnostic in validation.Diagnostics)
        {
            diagnostics.Add(diagnostic with
            {
                Location = string.IsNullOrEmpty(prefix)
                    ? diagnostic.Location
                    : string.IsNullOrEmpty(diagnostic.Location) || diagnostic.Location == "/"
                        ? prefix
                        : prefix + diagnostic.Location,
                Evidence = new(stage: "relationship-world-compilation")
            });
        }
    }

    static void Add(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string code,
        string message,
        string location) =>
        diagnostics.Add(new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location,
            Evidence: new(stage: "relationship-world-compilation")));

    static bool AreEquivalentGraphs(ShapeGraph left, ShapeGraph right)
    {
        if (ReferenceEquals(left, right))
            return true;
        var options = StrictDocumentJson.CreateOptions();
        var leftBytes = StrictDocumentJson.GetCanonicalBytes(ShapeGraphDocument.FromGraph(left), options);
        var rightBytes = StrictDocumentJson.GetCanonicalBytes(ShapeGraphDocument.FromGraph(right), options);
        return leftBytes.AsSpan().SequenceEqual(rightBytes);
    }
}
