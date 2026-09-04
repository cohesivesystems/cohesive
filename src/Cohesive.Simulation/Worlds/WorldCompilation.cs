using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Worlds;

/// <summary>Compiled deterministic generation plan for one world population.</summary>
public sealed class CompiledWorldPopulation
{
    internal CompiledWorldPopulation(
        WorldPopulationDefinition definition,
        CompiledGenerationPlan generationPlan,
        GenerationScope scope)
    {
        Definition = definition;
        GenerationPlan = generationPlan;
        Scope = scope;
    }

    /// <summary>Gets the normalized canonical population definition.</summary>
    public WorldPopulationDefinition Definition { get; }

    /// <summary>Gets the compiled generation semantics for one population member.</summary>
    public CompiledGenerationPlan GenerationPlan { get; }

    /// <summary>Gets the deterministic isolated entropy scope for this population.</summary>
    public GenerationScope Scope { get; }

    /// <summary>Generates the complete bounded population eagerly.</summary>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <returns>Generated world items in ascending population sequence-index order.</returns>
    /// <exception cref="WorldGenerationException">An identity cannot be resolved or repeats within the population.</exception>
    public ImmutableArray<GeneratedWorldItem> Generate(long seed)
    {
        var generated = ImmutableArray.CreateBuilder<GeneratedWorldItem>(Definition.Count);
        foreach (var item in Enumerate(seed))
            generated.Add(item);
        return generated.MoveToImmutable();
    }

    /// <summary>Generates the complete bounded population through a compatible CLR interpretation.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <param name="generator">Typed generator compiled from this population's exact generation definition.</param>
    /// <returns>Generated CLR values in ascending population sequence-index order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="generator"/> names another generation definition, revision, or fingerprint.
    /// </exception>
    /// <exception cref="WorldGenerationException">An identity cannot be resolved or repeats within the population.</exception>
    public ImmutableArray<Generated<T>> Generate<T>(
        long seed,
        CompiledPocoGenerator<T> generator)
    {
        ValidateGenerator(generator);
        var generated = ImmutableArray.CreateBuilder<Generated<T>>(Definition.Count);
        foreach (var item in EnumerateTypedCore(seed, generator))
            generated.Add(item);
        return generated.MoveToImmutable();
    }

    /// <summary>Lazily enumerates the bounded population.</summary>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <returns>A lazy sequence in ascending population sequence-index order.</returns>
    /// <exception cref="WorldGenerationException">An identity cannot be resolved or repeats within the population.</exception>
    public IEnumerable<GeneratedWorldItem> Enumerate(long seed) => EnumerateCore(seed);

    /// <summary>Generates one world item at an exact sequence coordinate.</summary>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <param name="sequenceIndex">Zero-based population sequence index.</param>
    /// <returns>The generated world item and its canonical entity identity.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sequenceIndex"/> is outside this population's declared bounds.
    /// </exception>
    /// <exception cref="WorldGenerationException">The item's identity cannot be resolved.</exception>
    /// <remarks>
    /// This operation cannot prove population-wide uniqueness in isolation. Use <see cref="Generate(long)"/> or
    /// <see cref="Enumerate(long)"/> when the identity policy requires uniqueness across the complete population.
    /// </remarks>
    public GeneratedWorldItem GenerateItem(long seed, long sequenceIndex)
    {
        if (sequenceIndex < 0 || sequenceIndex >= Definition.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceIndex),
                sequenceIndex,
                $"Sequence index must be from 0 through {Definition.Count - 1}.");
        }

        return ResolveIdentity(ReferenceGenerationInterpreter.Generate(
            GenerationPlan,
            seed,
            Scope,
            sequenceIndex));
    }

    /// <summary>Lazily enumerates the bounded population through a compatible CLR interpretation.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <param name="generator">Typed generator compiled from this population's exact generation definition.</param>
    /// <returns>A lazy typed sequence in ascending population sequence-index order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="generator"/> names another generation definition, revision, or fingerprint.
    /// </exception>
    /// <exception cref="WorldGenerationException">An identity cannot be resolved or repeats within the population.</exception>
    public IEnumerable<Generated<T>> Enumerate<T>(
        long seed,
        CompiledPocoGenerator<T> generator)
    {
        ValidateGenerator(generator);
        return EnumerateTypedCore(seed, generator);
    }

    internal void ValidateGenerator<T>(CompiledPocoGenerator<T> generator)
    {
        ArgumentNullException.ThrowIfNull(generator);
        var observed = generator.Plan;
        if (string.Equals(GenerationPlan.Definition.Id, observed.Definition.Id, StringComparison.Ordinal)
            && string.Equals(GenerationPlan.Definition.Revision, observed.Definition.Revision, StringComparison.Ordinal)
            && string.Equals(GenerationPlan.Fingerprint, observed.Fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        throw new ArgumentException(
            $"Typed generator '{observed.Definition.Id}/{observed.Definition.Revision}/{observed.Fingerprint}' "
            + $"does not match population '{Definition.Id}' generation "
            + $"'{GenerationPlan.Definition.Id}/{GenerationPlan.Definition.Revision}/{GenerationPlan.Fingerprint}'.",
            nameof(generator));
    }

    IEnumerable<GeneratedWorldItem> EnumerateCore(long seed)
    {
        var uniqueIdentities = CreateUniqueIdentitySet();
        foreach (var generated in ReferenceGenerationInterpreter.EnumerateSequence(
                     GenerationPlan,
                     seed,
                     Scope,
                     Definition.Count))
        {
            var item = ResolveIdentity(generated);
            ValidateUniqueIdentity(item, uniqueIdentities);

            yield return item;
        }
    }

    IEnumerable<Generated<T>> EnumerateTypedCore<T>(
        long seed,
        CompiledPocoGenerator<T> generator)
    {
        var uniqueIdentities = CreateUniqueIdentitySet();
        foreach (var generated in generator.EnumerateSequence(seed, Scope, Definition.Count))
        {
            var item = ResolveIdentity(new(generated.Observation, generated.Replay));
            ValidateUniqueIdentity(item, uniqueIdentities);
            yield return generated;
        }
    }

    HashSet<EntityId>? CreateUniqueIdentitySet() =>
        Definition.EntityIdentity.Source == WorldEntityIdentitySource.UniqueObservationField
            ? []
            : null;

    void ValidateUniqueIdentity(
        GeneratedWorldItem item,
        HashSet<EntityId>? uniqueIdentities)
    {
        if (uniqueIdentities is null || uniqueIdentities.Add(item.EntityId))
            return;

        throw WorldGenerationException.IdentityFailure(
            Definition.Id,
            item.Replay.SequenceIndex,
            "simulation.world.entityIdentityDuplicate",
            $"Population '{Definition.Id}' resolves entity identity '{item.EntityId.Value}' more than once.");
    }

    internal GeneratedWorldItem ResolveIdentity(GeneratedObservation generated)
    {
        if (Definition.EntityIdentity.TryResolve(
                Scope,
                generated,
                out var entityId,
                out var code,
                out var detail))
        {
            return new(entityId, generated);
        }

        throw WorldGenerationException.IdentityFailure(
            Definition.Id,
            generated.Replay.SequenceIndex,
            code!,
            detail!);
    }
}

/// <summary>Immutable provider-neutral executable index over one exact world definition.</summary>
public sealed class CompiledWorldPlan
{
    internal CompiledWorldPlan(
        WorldDefinition definition,
        ImmutableArray<CompiledWorldPopulation> populations,
        ImmutableArray<WorldExemplarDefinition> exemplars,
        string fingerprint)
    {
        Definition = definition;
        Populations = populations;
        Exemplars = exemplars;
        Fingerprint = fingerprint;
    }

    /// <summary>Gets the normalized canonical world definition.</summary>
    public WorldDefinition Definition { get; }

    /// <summary>Gets compiled populations in stable identity order.</summary>
    public ImmutableArray<CompiledWorldPopulation> Populations { get; }

    /// <summary>Gets named exemplars in stable identity order.</summary>
    public ImmutableArray<WorldExemplarDefinition> Exemplars { get; }

    /// <summary>Gets the lowercase SHA-256 fingerprint of exact world semantic content.</summary>
    public string Fingerprint { get; }

    /// <summary>Gets the fingerprint algorithm identity.</summary>
    public string FingerprintAlgorithm => WorldCanonicalizer.FingerprintAlgorithm;

    /// <summary>Gets the canonicalization profile used by <see cref="Fingerprint"/>.</summary>
    public string FingerprintCanonicalization => WorldCanonicalizer.CanonicalizationProfile;

    /// <summary>Finds one compiled population by stable identity.</summary>
    /// <param name="id">Stable population identity.</param>
    /// <param name="population">Receives the compiled population when found.</param>
    /// <returns><see langword="true"/> when the population exists; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    public bool TryGetPopulation(string id, out CompiledWorldPopulation? population)
    {
        id = Guard.RequireNotNullOrWhiteSpace(id);
        foreach (var candidate in Populations)
        {
            if (!string.Equals(candidate.Definition.Id, id, StringComparison.Ordinal))
                continue;

            population = candidate;
            return true;
        }

        population = null;
        return false;
    }

    /// <summary>Gets one compiled population by stable identity.</summary>
    /// <param name="id">Stable population identity.</param>
    /// <returns>The compiled population named by <paramref name="id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">The world contains no population with the supplied identity.</exception>
    public CompiledWorldPopulation GetPopulation(string id) =>
        TryGetPopulation(id, out var population)
            ? population!
            : throw new KeyNotFoundException(
                $"World '{Definition.Id}' contains no population with identity '{id}'.");

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
    /// <exception cref="KeyNotFoundException">The world contains no exemplar with the supplied identity.</exception>
    public WorldExemplarDefinition GetExemplar(string id) =>
        TryGetExemplar(id, out var exemplar)
            ? exemplar!
            : throw new KeyNotFoundException(
                $"World '{Definition.Id}' contains no exemplar with identity '{id}'.");

    /// <summary>Generates the exact observation named by a world exemplar.</summary>
    /// <param name="id">Stable exemplar identity.</param>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <returns>The generated world item, canonical entity identity, and exact replay evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">The world contains no exemplar with the supplied identity.</exception>
    public GeneratedWorldItem GenerateExemplar(string id, long seed)
    {
        var exemplar = GetExemplar(id);
        var population = GetPopulation(exemplar.PopulationId);
        return population.GenerateItem(seed, exemplar.SequenceIndex);
    }

    /// <summary>Generates and materializes the exact observation named by a world exemplar.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="id">Stable exemplar identity.</param>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <param name="generator">Typed generator compiled from the exemplar population's generation definition.</param>
    /// <returns>The generated CLR value, authoritative observation, and exact replay evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> or <paramref name="generator"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is empty, or <paramref name="generator"/> names another generation definition.
    /// </exception>
    /// <exception cref="KeyNotFoundException">The world contains no exemplar with the supplied identity.</exception>
    /// <exception cref="WorldGenerationException">The exemplar's identity cannot be resolved.</exception>
    public Generated<T> GenerateExemplar<T>(
        string id,
        long seed,
        CompiledPocoGenerator<T> generator)
    {
        var exemplar = GetExemplar(id);
        var population = GetPopulation(exemplar.PopulationId);
        population.ValidateGenerator(generator);
        var generated = generator.Generate(seed, population.Scope, exemplar.SequenceIndex);
        population.ResolveIdentity(new(generated.Observation, generated.Replay));
        return generated;
    }
}

/// <summary>Result of attempting provider-neutral world compilation.</summary>
public sealed class WorldCompilationResult
{
    internal WorldCompilationResult(
        WorldDefinition definition,
        CompiledWorldPlan? plan,
        DocumentValidationResult validation)
    {
        Definition = definition;
        Plan = plan;
        Validation = validation;
    }

    /// <summary>Gets the exact supplied world definition.</summary>
    public WorldDefinition Definition { get; }

    /// <summary>Gets a compiled world only when validation succeeds.</summary>
    public CompiledWorldPlan? Plan { get; }

    /// <summary>Gets deterministically ordered structured diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Gets whether compilation produced a complete world plan.</summary>
    public bool IsSuccessful => Plan is not null && Validation.IsValid;
}

/// <summary>Failure raised when convenience world compilation encounters structured diagnostics.</summary>
public sealed class WorldCompilationException : InvalidOperationException
{
    /// <summary>Creates a world compilation exception.</summary>
    /// <param name="validation">Structured validation evidence explaining the failure.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    public WorldCompilationException(DocumentValidationResult validation)
        : base(CreateMessage(validation)) => Validation = validation;

    /// <summary>Gets structured validation evidence explaining the failure.</summary>
    public DocumentValidationResult Validation { get; }

    static string CreateMessage(DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        var errors = validation.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}");
        return $"World definition could not be compiled: {string.Join(" | ", errors)}";
    }
}

/// <summary>Compiles canonical world IR into isolated deterministic population streams.</summary>
public static class WorldCompiler
{
    /// <summary>Compiles and validates one canonical world definition.</summary>
    /// <param name="definition">Canonical world definition to compile.</param>
    /// <returns>A result containing either a complete world plan or precise structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldCompilationResult Compile(WorldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<DocumentValidationDiagnostic> diagnostics = [];
        if (definition.Populations.IsDefaultOrEmpty)
        {
            Add(
                diagnostics,
                code: "simulation.world.populationsMissing",
                message: "A world must declare at least one initial population.",
                location: "/populations");
        }

        HashSet<string> identities = new(StringComparer.Ordinal);
        Dictionary<string, int> populationCounts = new(StringComparer.Ordinal);
        List<(WorldPopulationDefinition Population, CompiledGenerationPlan Generation)> compiled = [];
        for (var index = 0; index < definition.Populations.Length; index++)
        {
            var population = definition.Populations[index];
            var location = $"/populations/{index}";
            if (population is null)
            {
                Add(
                    diagnostics,
                    code: "simulation.world.populationMissing",
                    message: "A world cannot contain a null population.",
                    location: location);
                continue;
            }

            var usable = true;
            if (!identities.Add(population.Id))
            {
                Add(
                    diagnostics,
                    code: "simulation.world.populationIdentityDuplicate",
                    message: $"Population identity '{population.Id}' is declared more than once.",
                    location: $"{location}/id");
                usable = false;
            }
            else
            {
                populationCounts.Add(population.Id, population.Count);
            }

            if (population.Count < 0)
            {
                Add(
                    diagnostics,
                    code: "simulation.world.populationCountInvalid",
                    message: $"Population '{population.Id}' count '{population.Count}' cannot be negative.",
                    location: $"{location}/count");
                usable = false;
            }

            if (!ValidateIdentity(population.EntityIdentity, location, diagnostics))
                usable = false;

            var generation = GenerationCompiler.Compile(population.Generation);
            foreach (var diagnostic in generation.Validation.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Location = PrefixLocation($"{location}/generation", diagnostic.Location)
                });
            }

            if (generation.Plan is null)
            {
                usable = false;
            }

            if (usable)
                compiled.Add((population, generation.Plan!));
        }

        HashSet<string> exemplarIdentities = new(StringComparer.Ordinal);
        List<WorldExemplarDefinition> compiledExemplars = [];
        for (var index = 0; index < definition.Exemplars.Length; index++)
        {
            var exemplar = definition.Exemplars[index];
            var location = $"/exemplars/{index}";
            if (exemplar is null)
            {
                Add(
                    diagnostics,
                    code: "simulation.world.exemplarMissing",
                    message: "A world cannot contain a null exemplar.",
                    location: location);
                continue;
            }

            var usable = true;
            if (!exemplarIdentities.Add(exemplar.Id))
            {
                Add(
                    diagnostics,
                    code: "simulation.world.exemplarIdentityDuplicate",
                    message: $"Exemplar identity '{exemplar.Id}' is declared more than once.",
                    location: $"{location}/id");
                usable = false;
            }

            if (!populationCounts.TryGetValue(exemplar.PopulationId, out var populationCount))
            {
                Add(
                    diagnostics,
                    code: "simulation.world.exemplarPopulationUnknown",
                    message: $"Exemplar '{exemplar.Id}' references unknown population '{exemplar.PopulationId}'.",
                    location: $"{location}/populationId");
                usable = false;
            }
            else if (populationCount >= 0
                     && (exemplar.SequenceIndex < 0 || exemplar.SequenceIndex >= populationCount))
            {
                var message = populationCount == 0
                    ? $"Exemplar '{exemplar.Id}' cannot reference empty population '{exemplar.PopulationId}'."
                    : $"Exemplar '{exemplar.Id}' sequence index '{exemplar.SequenceIndex}' is outside population "
                      + $"'{exemplar.PopulationId}' bounds 0 through {populationCount - 1}.";
                Add(
                    diagnostics,
                    code: "simulation.world.exemplarSequenceIndexInvalid",
                    message,
                    location: $"{location}/sequenceIndex");
                usable = false;
            }

            if (usable)
                compiledExemplars.Add(exemplar);
        }

        var validation = CreateValidation(diagnostics);
        if (!validation.IsValid)
            return new(definition, plan: null, validation);

        compiled.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Population.Id, right.Population.Id));
        compiledExemplars.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Id, right.Id));
        var populations = ImmutableArray.CreateBuilder<CompiledWorldPopulation>(compiled.Count);
        var normalizedDefinitions = ImmutableArray.CreateBuilder<WorldPopulationDefinition>(compiled.Count);
        foreach (var item in compiled)
        {
            var normalizedGeneration = GenerationDefinitionDocument.Normalize(item.Generation);
            var normalizedPopulation = item.Population.Generation == normalizedGeneration
                ? item.Population
                : new(
                    item.Population.Id,
                    item.Population.Count,
                    item.Population.EntityIdentity,
                    normalizedGeneration);
            var normalizedGenerationPlan = ReferenceEquals(normalizedGeneration, item.Generation.Definition)
                ? item.Generation
                : GenerationCompiler.Compile(normalizedGeneration).Plan
                  ?? throw new InvalidOperationException(
                      $"Normalized generation definition '{normalizedGeneration.Id}' could not be recompiled.");
            normalizedDefinitions.Add(normalizedPopulation);
            populations.Add(new(
                normalizedPopulation,
                normalizedGenerationPlan,
                WorldPopulationScopeConvention.Create(definition.Id, normalizedPopulation.Id)));
        }

        ImmutableArray<WorldExemplarDefinition> exemplars = [.. compiledExemplars];
        var normalizedWorld = new WorldDefinition(
            definition.Id,
            definition.Revision,
            normalizedDefinitions.MoveToImmutable(),
            exemplars);
        var compiledPopulations = populations.MoveToImmutable();
        var fingerprint = WorldCanonicalizer.ComputeDefinitionFingerprint(compiledPopulations, exemplars);
        return new(
            definition,
            new CompiledWorldPlan(normalizedWorld, compiledPopulations, exemplars, fingerprint),
            validation);
    }

    static bool ValidateIdentity(
        WorldEntityIdentityPolicy identity,
        string populationLocation,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        switch (identity.Source)
        {
            case WorldEntityIdentitySource.PopulationSequence:
                if (identity.ObservationField is null)
                    return true;

                Add(
                    diagnostics,
                    code: "simulation.world.entityIdentityFieldUnexpected",
                    message: "A population-sequence identity policy cannot declare an observation field.",
                    location: $"{populationLocation}/entityIdentity/observationField");
                return false;

            case WorldEntityIdentitySource.UniqueObservationField:
                if (identity.ObservationField is not { } path)
                {
                    Add(
                        diagnostics,
                        code: "simulation.world.entityIdentityFieldMissing",
                        message: "A unique-observation-field identity policy requires an observation field path.",
                        location: $"{populationLocation}/entityIdentity/observationField");
                    return false;
                }

                if (WorldEntityIdentityPolicy.IsValidFieldPath(path))
                    return true;

                Add(
                    diagnostics,
                    code: "simulation.world.entityIdentityFieldInvalid",
                    message: "An entity identity field path must contain non-empty field segments and no collection navigation.",
                    location: $"{populationLocation}/entityIdentity/observationField");
                return false;

            default:
                Add(
                    diagnostics,
                    code: "simulation.world.entityIdentitySourceInvalid",
                    message: $"World entity identity source '{identity.Source}' is unsupported.",
                    location: $"{populationLocation}/entityIdentity/source");
                return false;
        }
    }

    static string PrefixLocation(string prefix, string? location) =>
        string.IsNullOrEmpty(location) || location == "/"
            ? prefix
            : location[0] == '/'
                ? prefix + location
                : $"{prefix}/{location}";

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
            Evidence: new(stage: "world-compilation")));

    static DocumentValidationResult CreateValidation(
        IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        new(DocumentValidationDiagnostics.Normalize([.. diagnostics]));
}

static class WorldCanonicalizer
{
    public const string FingerprintAlgorithm = WorldDefinitionFingerprint.CurrentAlgorithm;
    public const string CanonicalizationProfile = WorldDefinitionFingerprint.CurrentCanonicalization;

    public static string ComputeDefinitionFingerprint(
        ImmutableArray<CompiledWorldPopulation> populations,
        ImmutableArray<WorldExemplarDefinition> exemplars)
    {
        using SimulationFingerprintWriter writer = new();
        writer.Append(CanonicalizationProfile);
        writer.Append(WorldPopulationScopeConvention.Identity);
        writer.Append(populations.Length);
        foreach (var population in populations)
        {
            writer.Append(population.Definition.Id);
            writer.Append(population.Definition.Count);
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
            writer.Append(population.GenerationPlan.Definition.Id);
            writer.Append(population.GenerationPlan.Definition.Revision);
            writer.Append(population.GenerationPlan.FingerprintAlgorithm);
            writer.Append(population.GenerationPlan.FingerprintCanonicalization);
            writer.Append(population.GenerationPlan.Fingerprint);
        }

        writer.Append(exemplars.Length);
        foreach (var exemplar in exemplars)
        {
            writer.Append(exemplar.Id);
            writer.Append(exemplar.PopulationId);
            writer.Append(exemplar.SequenceIndex);
        }

        return writer.Complete();
    }
}
