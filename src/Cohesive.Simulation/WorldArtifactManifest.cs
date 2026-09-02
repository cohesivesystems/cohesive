using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Artifacts;

/// <summary>Stable content-addressed identity of one exact generated world artifact.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct WorldArtifactId
{
    /// <summary>Creates a world-artifact identity.</summary>
    /// <param name="value">Stable nonempty identity value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public WorldArtifactId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Gets the stable identity value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    internal static void Validate(WorldArtifactId id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A world-artifact identity is required.", parameterName);
    }
}

/// <summary>Versioned fingerprint of exact world-artifact manifest content.</summary>
public sealed record WorldArtifactManifestFingerprint
{
    /// <summary>Cryptographic hash algorithm used by the current manifest profile.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current manifest fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-simulation-world-artifact-manifest/v1-c14n/v1";

    /// <summary>Creates world-artifact manifest fingerprint metadata.</summary>
    /// <param name="algorithm">Hash-algorithm identity.</param>
    /// <param name="canonicalization">Canonical manifest profile identity.</param>
    /// <param name="value">Lowercase hexadecimal fingerprint value.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="world"/>, <paramref name="interpreter"/>, or <paramref name="entropyAlgorithm"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A parameter is empty or white-space.</exception>
    [JsonConstructor]
    public WorldArtifactManifestFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Gets the hash-algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the canonical manifest profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal fingerprint value.</summary>
    public string Value { get; }
}

/// <summary>Compiled population coordinates retained by a world-artifact manifest.</summary>
public sealed record WorldArtifactPopulationManifest
{
    /// <summary>Creates one population entry in a world-artifact manifest.</summary>
    /// <param name="id">Stable population identity within the world.</param>
    /// <param name="count">Exact number of generated members.</param>
    /// <param name="scope">Exact isolated entropy scope assigned to the population.</param>
    /// <param name="generationId">Stable generation-definition identity.</param>
    /// <param name="generationRevision">Exact generation-definition revision.</param>
    /// <param name="generationFingerprint">Fingerprint of exact generation semantics.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/>, <paramref name="generationId"/>, <paramref name="generationRevision"/>, or
    /// <paramref name="generationFingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity or revision is empty, or <paramref name="scope"/> is default.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    [JsonConstructor]
    public WorldArtifactPopulationManifest(
        string id,
        int count,
        GenerationScope scope,
        string generationId,
        string generationRevision,
        GenerationDefinitionFingerprint generationFingerprint)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "A population count cannot be negative.");
        GenerationScope.Validate(scope, nameof(scope));
        Count = count;
        Scope = scope;
        GenerationId = Guard.RequireNotNullOrWhiteSpace(generationId);
        GenerationRevision = Guard.RequireNotNullOrWhiteSpace(generationRevision);
        GenerationFingerprint = Guard.RequireNotNull(generationFingerprint);
    }

    /// <summary>Gets the stable population identity.</summary>
    public string Id { get; }

    /// <summary>Gets the exact number of generated members.</summary>
    public int Count { get; }

    /// <summary>Gets the exact isolated entropy scope assigned to the population.</summary>
    public GenerationScope Scope { get; }

    /// <summary>Gets the stable generation-definition identity.</summary>
    public string GenerationId { get; }

    /// <summary>Gets the exact generation-definition revision.</summary>
    public string GenerationRevision { get; }

    /// <summary>Gets the fingerprint of exact generation semantics.</summary>
    public GenerationDefinitionFingerprint GenerationFingerprint { get; }
}

/// <summary>Portable self-validating manifest for one exact generated world artifact.</summary>
/// <remarks>
/// The manifest embeds the canonical world definition needed for replay but does not materialize generated
/// observations. Concrete artifact-batch framing remains an independent format contract. Population and exemplar
/// projections are derived evidence and are validated against the embedded world rather than becoming another source
/// of semantic truth.
/// </remarks>
public sealed record WorldArtifactManifest
{
    /// <summary>Current portable world-artifact manifest schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-world-artifact-manifest/v1";

    const string ArtifactIdPrefix = "csimartifact1_";

    /// <summary>Creates or restores one portable world-artifact manifest.</summary>
    /// <param name="schemaVersion">Exact manifest schema.</param>
    /// <param name="artifactId">Content-addressed identity derived from the manifest fingerprint.</param>
    /// <param name="world">Exact canonical world definition used for generation.</param>
    /// <param name="rootSeed">Deterministic root seed shared by all populations.</param>
    /// <param name="interpreter">Exact generation-interpreter identity and version.</param>
    /// <param name="entropyAlgorithm">Exact addressable entropy-algorithm identity and version.</param>
    /// <param name="populations">Compiled population coordinates in stable identity order.</param>
    /// <param name="exemplars">Stable exemplar aliases in world-wide identity order.</param>
    /// <param name="fingerprint">Fingerprint of exact manifest semantic content.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/>, <paramref name="world"/>, <paramref name="interpreter"/>,
    /// <paramref name="entropyAlgorithm"/>, <paramref name="fingerprint"/>, or an element of
    /// <paramref name="populations"/> or <paramref name="exemplars"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported; an identity is empty; population or exemplar evidence disagrees with the embedded
    /// world; or the supplied artifact identity or fingerprint does not match canonical content.
    /// </exception>
    [JsonConstructor]
    public WorldArtifactManifest(
        string schemaVersion,
        WorldArtifactId artifactId,
        WorldDefinitionDocument world,
        long rootSeed,
        string interpreter,
        string entropyAlgorithm,
        ImmutableArray<WorldArtifactPopulationManifest> populations,
        ImmutableArray<WorldExemplarDefinition> exemplars,
        WorldArtifactManifestFingerprint fingerprint)
        : this(ValidateAndNormalize(
            schemaVersion,
            artifactId,
            world,
            rootSeed,
            interpreter,
            entropyAlgorithm,
            populations,
            exemplars,
            fingerprint))
    {
    }

    WorldArtifactManifest(ManifestState state)
    {
        SchemaVersion = state.SchemaVersion;
        ArtifactId = state.ArtifactId;
        World = state.World;
        RootSeed = state.RootSeed;
        Interpreter = state.Interpreter;
        EntropyAlgorithm = state.EntropyAlgorithm;
        Populations = state.Populations;
        Exemplars = state.Exemplars;
        Fingerprint = state.Fingerprint;
    }

    /// <summary>Gets the exact portable manifest schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the content-addressed artifact identity.</summary>
    public WorldArtifactId ArtifactId { get; }

    /// <summary>Gets the exact canonical world definition used for generation.</summary>
    public WorldDefinitionDocument World { get; }

    /// <summary>Gets the deterministic root seed shared by all populations.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long RootSeed { get; }

    /// <summary>Gets the exact generation-interpreter identity and version.</summary>
    public string Interpreter { get; }

    /// <summary>Gets the exact addressable entropy-algorithm identity and version.</summary>
    public string EntropyAlgorithm { get; }

    /// <summary>Gets compiled population coordinates in stable identity order.</summary>
    public ImmutableArray<WorldArtifactPopulationManifest> Populations { get; }

    /// <summary>Gets stable exemplar aliases in world-wide identity order.</summary>
    public ImmutableArray<WorldExemplarDefinition> Exemplars { get; }

    /// <summary>Gets the fingerprint of exact manifest semantic content.</summary>
    public WorldArtifactManifestFingerprint Fingerprint { get; }

    /// <summary>Creates a reference-interpreter manifest from one compiled world.</summary>
    /// <param name="world">Exact compiled world to describe.</param>
    /// <param name="rootSeed">Deterministic root seed shared by all populations.</param>
    /// <returns>A current-version target-independent world-artifact manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is <see langword="null"/>.</exception>
    public static WorldArtifactManifest FromWorld(CompiledWorldPlan world, long rootSeed)
    {
        ArgumentNullException.ThrowIfNull(world);
        var document = WorldDefinitionDocument.FromCompiledPlan(world);
        return Create(document, world, rootSeed);
    }

    /// <summary>Creates a reference-interpreter manifest from one portable world-definition document.</summary>
    /// <param name="world">Exact portable world definition to describe.</param>
    /// <param name="rootSeed">Deterministic root seed shared by all populations.</param>
    /// <returns>A current-version target-independent world-artifact manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is <see langword="null"/>.</exception>
    public static WorldArtifactManifest FromWorld(WorldDefinitionDocument world, long rootSeed)
        => FromWorld(
            world,
            rootSeed,
            ReferenceGenerationInterpreter.Identity,
            ReferenceGenerationInterpreter.EntropyAlgorithm);

    /// <summary>Creates a manifest for an exact world and explicitly selected generation interpreter.</summary>
    /// <param name="world">Exact portable world definition to describe.</param>
    /// <param name="rootSeed">Deterministic root seed shared by all populations.</param>
    /// <param name="interpreter">Exact generation-interpreter identity and version.</param>
    /// <param name="entropyAlgorithm">Exact addressable entropy-algorithm identity and version.</param>
    /// <returns>A current-version target-independent world-artifact manifest.</returns>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="interpreter"/> or <paramref name="entropyAlgorithm"/> is empty or white-space.
    /// </exception>
    public static WorldArtifactManifest FromWorld(
        WorldDefinitionDocument world,
        long rootSeed,
        string interpreter,
        string entropyAlgorithm)
    {
        ArgumentNullException.ThrowIfNull(world);
        return Create(
            world,
            world.Compile(),
            rootSeed,
            Guard.RequireNotNullOrWhiteSpace(interpreter),
            Guard.RequireNotNullOrWhiteSpace(entropyAlgorithm));
    }

    /// <summary>Finds one exemplar alias by stable world-wide identity.</summary>
    /// <param name="id">Stable exemplar identity.</param>
    /// <param name="exemplar">Receives the exact population coordinate when found.</param>
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

    /// <summary>Gets one exemplar alias by stable world-wide identity.</summary>
    /// <param name="id">Stable exemplar identity.</param>
    /// <returns>The exact population coordinate named by <paramref name="id"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is empty or white-space.</exception>
    /// <exception cref="KeyNotFoundException">The manifest contains no exemplar with the supplied identity.</exception>
    public WorldExemplarDefinition GetExemplar(string id) =>
        TryGetExemplar(id, out var exemplar)
            ? exemplar!
            : throw new KeyNotFoundException(
                $"World artifact '{ArtifactId.Value}' contains no exemplar with identity '{id}'.");

    static WorldArtifactManifest Create(
        WorldDefinitionDocument document,
        CompiledWorldPlan world,
        long rootSeed,
        string interpreter = ReferenceGenerationInterpreter.Identity,
        string entropyAlgorithm = ReferenceGenerationInterpreter.EntropyAlgorithm)
    {
        var populations = ProjectPopulations(world);
        var exemplars = world.Exemplars;
        var fingerprint = ComputeFingerprint(
            CurrentSchemaVersion,
            document,
            rootSeed,
            interpreter,
            entropyAlgorithm,
            populations,
            exemplars);
        return new(new ManifestState(
            CurrentSchemaVersion,
            CreateArtifactId(fingerprint),
            document,
            rootSeed,
            interpreter,
            entropyAlgorithm,
            populations,
            exemplars,
            fingerprint));
    }

    static ManifestState ValidateAndNormalize(
        string schemaVersion,
        WorldArtifactId artifactId,
        WorldDefinitionDocument world,
        long rootSeed,
        string interpreter,
        string entropyAlgorithm,
        ImmutableArray<WorldArtifactPopulationManifest> populations,
        ImmutableArray<WorldExemplarDefinition> exemplars,
        WorldArtifactManifestFingerprint fingerprint)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"World-artifact manifest schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        WorldArtifactId.Validate(artifactId, nameof(artifactId));
        ArgumentNullException.ThrowIfNull(world);
        interpreter = Guard.RequireNotNullOrWhiteSpace(interpreter);
        entropyAlgorithm = Guard.RequireNotNullOrWhiteSpace(entropyAlgorithm);
        ArgumentNullException.ThrowIfNull(fingerprint);

        var plan = world.Compile();
        var normalizedPopulations = NormalizePopulations(populations);
        var normalizedExemplars = NormalizeExemplars(exemplars);
        if (!normalizedPopulations.SequenceEqual(ProjectPopulations(plan)))
        {
            throw new ArgumentException(
                "World-artifact population evidence does not match the embedded world definition.",
                nameof(populations));
        }
        if (!normalizedExemplars.SequenceEqual(plan.Exemplars))
        {
            throw new ArgumentException(
                "World-artifact exemplar evidence does not match the embedded world definition.",
                nameof(exemplars));
        }

        var expectedFingerprint = ComputeFingerprint(
            schemaVersion,
            world,
            rootSeed,
            interpreter,
            entropyAlgorithm,
            normalizedPopulations,
            normalizedExemplars);
        if (fingerprint != expectedFingerprint)
        {
            throw new ArgumentException(
                "The supplied world-artifact manifest fingerprint does not match canonical semantic content.",
                nameof(fingerprint));
        }

        var expectedArtifactId = CreateArtifactId(expectedFingerprint);
        if (artifactId != expectedArtifactId)
        {
            throw new ArgumentException(
                "The supplied world-artifact identity does not match the manifest fingerprint.",
                nameof(artifactId));
        }

        return new(
            schemaVersion,
            expectedArtifactId,
            world,
            rootSeed,
            interpreter,
            entropyAlgorithm,
            normalizedPopulations,
            normalizedExemplars,
            expectedFingerprint);
    }

    static ImmutableArray<WorldArtifactPopulationManifest> ProjectPopulations(CompiledWorldPlan world)
    {
        var result = ImmutableArray.CreateBuilder<WorldArtifactPopulationManifest>(world.Populations.Length);
        foreach (var population in world.Populations)
        {
            var generation = population.GenerationPlan;
            result.Add(new(
                population.Definition.Id,
                population.Definition.Count,
                population.Scope,
                generation.Definition.Id,
                generation.Definition.Revision,
                new(
                    generation.FingerprintAlgorithm,
                    generation.FingerprintCanonicalization,
                    generation.Fingerprint)));
        }

        return result.MoveToImmutable();
    }

    static ImmutableArray<WorldArtifactPopulationManifest> NormalizePopulations(
        ImmutableArray<WorldArtifactPopulationManifest> populations)
    {
        if (populations.IsDefaultOrEmpty)
            return [];

        HashSet<string> identities = new(StringComparer.Ordinal);
        var isCanonical = true;
        string? previousIdentity = null;
        foreach (var population in populations)
        {
            ArgumentNullException.ThrowIfNull(population);
            if (!identities.Add(population.Id))
                throw new ArgumentException($"Population identity '{population.Id}' is duplicated.", nameof(populations));
            if (previousIdentity is not null
                && StringComparer.Ordinal.Compare(previousIdentity, population.Id) > 0)
            {
                isCanonical = false;
            }
            previousIdentity = population.Id;
        }

        if (isCanonical)
            return populations;

        var normalized = populations.ToBuilder();
        normalized.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return normalized.MoveToImmutable();
    }

    static ImmutableArray<WorldExemplarDefinition> NormalizeExemplars(
        ImmutableArray<WorldExemplarDefinition> exemplars)
    {
        if (exemplars.IsDefaultOrEmpty)
            return [];

        HashSet<string> identities = new(StringComparer.Ordinal);
        var isCanonical = true;
        string? previousIdentity = null;
        foreach (var exemplar in exemplars)
        {
            ArgumentNullException.ThrowIfNull(exemplar);
            if (!identities.Add(exemplar.Id))
                throw new ArgumentException($"Exemplar identity '{exemplar.Id}' is duplicated.", nameof(exemplars));
            if (previousIdentity is not null
                && StringComparer.Ordinal.Compare(previousIdentity, exemplar.Id) > 0)
            {
                isCanonical = false;
            }
            previousIdentity = exemplar.Id;
        }

        if (isCanonical)
            return exemplars;

        var normalized = exemplars.ToBuilder();
        normalized.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        return normalized.MoveToImmutable();
    }

    static WorldArtifactManifestFingerprint ComputeFingerprint(
        string schemaVersion,
        WorldDefinitionDocument world,
        long rootSeed,
        string interpreter,
        string entropyAlgorithm,
        ImmutableArray<WorldArtifactPopulationManifest> populations,
        ImmutableArray<WorldExemplarDefinition> exemplars)
    {
        using SimulationFingerprintWriter writer = new();
        writer.Append(WorldArtifactManifestFingerprint.CurrentCanonicalization);
        writer.Append(schemaVersion);
        writer.Append(world.SchemaVersion);
        writer.Append(world.Definition.Id);
        writer.Append(world.Definition.Revision);
        writer.Append(world.Fingerprint.Algorithm);
        writer.Append(world.Fingerprint.Canonicalization);
        writer.Append(world.Fingerprint.Value);
        writer.Append(rootSeed);
        writer.Append(interpreter);
        writer.Append(entropyAlgorithm);
        writer.Append(populations.Length);
        foreach (var population in populations)
        {
            writer.Append(population.Id);
            writer.Append(population.Count);
            writer.Append(population.Scope.Value);
            writer.Append(population.GenerationId);
            writer.Append(population.GenerationRevision);
            writer.Append(population.GenerationFingerprint.Algorithm);
            writer.Append(population.GenerationFingerprint.Canonicalization);
            writer.Append(population.GenerationFingerprint.Value);
        }

        writer.Append(exemplars.Length);
        foreach (var exemplar in exemplars)
        {
            writer.Append(exemplar.Id);
            writer.Append(exemplar.PopulationId);
            writer.Append(exemplar.SequenceIndex);
        }

        return new(
            WorldArtifactManifestFingerprint.CurrentAlgorithm,
            WorldArtifactManifestFingerprint.CurrentCanonicalization,
            writer.Complete());
    }

    static WorldArtifactId CreateArtifactId(WorldArtifactManifestFingerprint fingerprint) =>
        new($"{ArtifactIdPrefix}{fingerprint.Value}");

    readonly record struct ManifestState(
        string SchemaVersion,
        WorldArtifactId ArtifactId,
        WorldDefinitionDocument World,
        long RootSeed,
        string Interpreter,
        string EntropyAlgorithm,
        ImmutableArray<WorldArtifactPopulationManifest> Populations,
        ImmutableArray<WorldExemplarDefinition> Exemplars,
        WorldArtifactManifestFingerprint Fingerprint);
}

/// <summary>Strict deterministic JSON boundary for portable world-artifact manifests.</summary>
public static class WorldArtifactManifestJsonSerializer
{
    const string ContractName = "world-artifact manifest";

    /// <summary>Creates strict serializer options for the closed manifest wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict case-sensitive portable-document options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one verified portable world-artifact manifest.</summary>
    /// <param name="manifest">Manifest to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable manifest JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Manifest content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Manifest content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Manifest content contains an unsupported runtime type.</exception>
    public static string Serialize(
        WorldArtifactManifest manifest,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(manifest))
            : JsonSerializer.Serialize(manifest, CreateOptions(formatting));
    }

    /// <summary>Gets canonical UTF-8 JSON for one complete world-artifact manifest.</summary>
    /// <param name="manifest">Manifest to serialize.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Manifest content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Manifest content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Manifest content contains an unsupported runtime type.</exception>
    public static byte[] GetCanonicalBytes(WorldArtifactManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return StrictDocumentJson.GetCanonicalBytes(manifest, CreateOptions());
    }

    /// <summary>Deserializes and validates one current-version world-artifact manifest.</summary>
    /// <param name="json">Persisted manifest JSON.</param>
    /// <returns>A normalized fingerprint-verified manifest.</returns>
    /// <exception cref="JsonException">
    /// JSON is empty, malformed, duplicated, noncanonical, unsupported, invalid, or fingerprint-inconsistent.
    /// </exception>
    public static WorldArtifactManifest Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var manifest);
        if (validation.IsValid && manifest is not null)
            return manifest;

        throw new JsonException(BuildFailureMessage(validation));
    }

    /// <summary>Attempts to deserialize and validate one world-artifact manifest.</summary>
    /// <param name="json">Persisted manifest JSON.</param>
    /// <param name="manifest">Receives the validated manifest when successful; otherwise <see langword="null"/>.</param>
    /// <returns>Structured wire, schema, projection, and fingerprint diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out WorldArtifactManifest? manifest)
    {
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                ContractName,
                out manifest,
                out var error))
        {
            return DocumentValidationResult.Valid;
        }

        manifest = null;
        return StrictDocumentJson.Error(
            Code(error.Failure),
            error.Message,
            error.Location);
    }

    static string Code(StrictDocumentJsonReadFailure failure) => failure switch
    {
        StrictDocumentJsonReadFailure.Empty => "simulation.worldArtifact.manifest.jsonEmpty",
        StrictDocumentJsonReadFailure.InvalidJson => "simulation.worldArtifact.manifest.jsonInvalid",
        StrictDocumentJsonReadFailure.RootInvalid => "simulation.worldArtifact.manifest.rootInvalid",
        StrictDocumentJsonReadFailure.DuplicateProperty => "simulation.worldArtifact.manifest.duplicateProperty",
        StrictDocumentJsonReadFailure.DeserializationInvalid => "simulation.worldArtifact.manifest.contentInvalid",
        StrictDocumentJsonReadFailure.DeserializationNull => "simulation.worldArtifact.manifest.contentMissing",
        StrictDocumentJsonReadFailure.WireNonCanonical => "simulation.worldArtifact.manifest.wireNonCanonical",
        _ => "simulation.worldArtifact.manifest.unknown"
    };

    static string BuildFailureMessage(DocumentValidationResult validation) =>
        string.Join(
            " | ",
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
