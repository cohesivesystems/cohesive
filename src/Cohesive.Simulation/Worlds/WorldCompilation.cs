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
    /// <returns>Generated observations in ascending population sequence-index order.</returns>
    public ImmutableArray<GeneratedObservation> Generate(long seed) =>
        ReferenceGenerationInterpreter.GenerateSequence(
            GenerationPlan,
            seed,
            Scope,
            Definition.Count);

    /// <summary>Generates the complete bounded population through a compatible CLR interpretation.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <param name="generator">Typed generator compiled from this population's exact generation definition.</param>
    /// <returns>Generated CLR values in ascending population sequence-index order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="generator"/> names another generation definition, revision, or fingerprint.
    /// </exception>
    public ImmutableArray<Generated<T>> Generate<T>(
        long seed,
        CompiledPocoGenerator<T> generator)
    {
        ValidateGenerator(generator);
        return generator.GenerateSequence(seed, Scope, Definition.Count);
    }

    /// <summary>Lazily enumerates the bounded population.</summary>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <returns>A lazy sequence in ascending population sequence-index order.</returns>
    public IEnumerable<GeneratedObservation> Enumerate(long seed) =>
        ReferenceGenerationInterpreter.EnumerateSequence(
            GenerationPlan,
            seed,
            Scope,
            Definition.Count);

    /// <summary>Lazily enumerates the bounded population through a compatible CLR interpretation.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="seed">World root seed shared by all populations.</param>
    /// <param name="generator">Typed generator compiled from this population's exact generation definition.</param>
    /// <returns>A lazy typed sequence in ascending population sequence-index order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="generator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="generator"/> names another generation definition, revision, or fingerprint.
    /// </exception>
    public IEnumerable<Generated<T>> Enumerate<T>(
        long seed,
        CompiledPocoGenerator<T> generator)
    {
        ValidateGenerator(generator);
        return generator.EnumerateSequence(seed, Scope, Definition.Count);
    }

    void ValidateGenerator<T>(CompiledPocoGenerator<T> generator)
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
}

/// <summary>Immutable provider-neutral executable index over one exact world definition.</summary>
public sealed class CompiledWorldPlan
{
    internal CompiledWorldPlan(
        WorldDefinition definition,
        ImmutableArray<CompiledWorldPopulation> populations,
        string fingerprint)
    {
        Definition = definition;
        Populations = populations;
        Fingerprint = fingerprint;
    }

    /// <summary>Gets the normalized canonical world definition.</summary>
    public WorldDefinition Definition { get; }

    /// <summary>Gets compiled populations in stable identity order.</summary>
    public ImmutableArray<CompiledWorldPopulation> Populations { get; }

    /// <summary>Gets the lowercase SHA-256 fingerprint of exact world population content.</summary>
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

            if (population.Count < 0)
            {
                Add(
                    diagnostics,
                    code: "simulation.world.populationCountInvalid",
                    message: $"Population '{population.Id}' count '{population.Count}' cannot be negative.",
                    location: $"{location}/count");
                usable = false;
            }

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

        var validation = CreateValidation(diagnostics);
        if (!validation.IsValid)
            return new(definition, plan: null, validation);

        compiled.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Population.Id, right.Population.Id));
        var populations = ImmutableArray.CreateBuilder<CompiledWorldPopulation>(compiled.Count);
        var normalizedDefinitions = ImmutableArray.CreateBuilder<WorldPopulationDefinition>(compiled.Count);
        foreach (var item in compiled)
        {
            var normalizedGeneration = GenerationDefinitionDocument.Normalize(item.Generation);
            var normalizedPopulation = item.Population.Generation == normalizedGeneration
                ? item.Population
                : new(item.Population.Id, item.Population.Count, normalizedGeneration);
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

        var normalizedWorld = new WorldDefinition(
            definition.Id,
            definition.Revision,
            normalizedDefinitions.MoveToImmutable());
        var compiledPopulations = populations.MoveToImmutable();
        var fingerprint = WorldCanonicalizer.ComputeDefinitionFingerprint(compiledPopulations);
        return new(
            definition,
            new CompiledWorldPlan(normalizedWorld, compiledPopulations, fingerprint),
            validation);
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
        ImmutableArray<CompiledWorldPopulation> populations)
    {
        using SimulationFingerprintWriter writer = new();
        writer.Append(CanonicalizationProfile);
        writer.Append(WorldPopulationScopeConvention.Identity);
        writer.Append(populations.Length);
        foreach (var population in populations)
        {
            writer.Append(population.Definition.Id);
            writer.Append(population.Definition.Count);
            writer.Append(population.GenerationPlan.Definition.Id);
            writer.Append(population.GenerationPlan.Definition.Revision);
            writer.Append(population.GenerationPlan.FingerprintAlgorithm);
            writer.Append(population.GenerationPlan.FingerprintCanonicalization);
            writer.Append(population.GenerationPlan.Fingerprint);
        }

        return writer.Complete();
    }
}
