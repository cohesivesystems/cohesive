using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Relations;

/// <summary>Creates and interprets retained artifacts governed by canonical relationship-world definitions.</summary>
public static class RelationshipWorldArtifact
{
    /// <summary>Creates a retained artifact manifest from one compiled relationship-aware world.</summary>
    /// <param name="world">Exact compiled relationship world to retain.</param>
    /// <param name="rootSeed">Deterministic root seed shared by every population.</param>
    /// <returns>
    /// A core artifact envelope embedding the exact relationship-world document as interpreter authority.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The compiled definition cannot be retained as a valid document.</exception>
    /// <exception cref="JsonException">The relationship-world document has no strict JSON representation.</exception>
    public static WorldArtifactManifest FromWorld(CompiledRelationshipWorldPlan world, long rootSeed)
    {
        ArgumentNullException.ThrowIfNull(world);
        return FromWorld(RelationshipWorldDefinitionDocument.FromDefinition(world.Definition), rootSeed);
    }

    /// <summary>Creates a retained artifact manifest from one exact portable relationship-world document.</summary>
    /// <param name="world">Exact canonical relationship-world authority.</param>
    /// <param name="rootSeed">Deterministic root seed shared by every population.</param>
    /// <returns>
    /// A core artifact envelope embedding <paramref name="world"/> as the exact interpreter authority.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The relationship-world document has no strict JSON representation.</exception>
    public static WorldArtifactManifest FromWorld(
        RelationshipWorldDefinitionDocument world,
        long rootSeed)
    {
        ArgumentNullException.ThrowIfNull(world);
        var plan = world.Compile();
        using var interpreterDocument = JsonDocument.Parse(
            RelationshipWorldDefinitionJsonSerializer.GetCanonicalBytes(world));
        var retainedWorld = new WorldArtifactDefinition(
            world.SchemaVersion,
            plan.Definition.World.Id,
            plan.Definition.World.Revision,
            new(
                world.Fingerprint.Algorithm,
                world.Fingerprint.Canonicalization,
                world.Fingerprint.Value),
            interpreterDocument.RootElement);
        var populations = ImmutableArray.CreateBuilder<CompiledWorldPopulation>(plan.Populations.Length);
        foreach (var population in plan.Populations)
            populations.Add(population.Population);
        return WorldArtifactManifest.FromInterpreterWorld(
            retainedWorld,
            populations.MoveToImmutable(),
            plan.Exemplars,
            rootSeed,
            RelationshipWorldInterpreter.Identity,
            ReferenceGenerationInterpreter.EntropyAlgorithm);
    }

    /// <summary>Gets and validates the exact relationship-world authority retained by an artifact manifest.</summary>
    /// <param name="artifact">Artifact expected to select the relationship-world reference interpreter.</param>
    /// <returns>The fingerprint-verified canonical relationship-world document embedded by the manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="artifact"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="artifact"/> selects another interpreter or entropy algorithm.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The embedded relationship world does not reproduce the manifest's retained world and population projections.
    /// </exception>
    /// <exception cref="JsonException">
    /// The retained definition is not a strict, fingerprint-valid relationship-world document.
    /// </exception>
    public static RelationshipWorldDefinitionDocument GetWorld(WorldArtifactManifest artifact)
        => GetWorldState(artifact).Document;

    internal static WorldArtifactInterpreterPlan CreateInterpreterPlan(WorldArtifactManifest artifact)
    {
        var state = GetWorldState(artifact);
        var world = state.Plan;
        var populations = ImmutableArray.CreateBuilder<WorldArtifactInterpreterPopulation>(world.Populations.Length);
        foreach (var population in world.Populations)
        {
            populations.Add(new(
                population.Population.Definition.Id,
                population.Population.Definition.Count,
                population.Population.Scope,
                rootSeed => EnumeratePopulation(population, rootSeed)));
        }

        return new(artifact, populations.MoveToImmutable());
    }

    static (RelationshipWorldDefinitionDocument Document, CompiledRelationshipWorldPlan Plan) GetWorldState(
        WorldArtifactManifest artifact)
    {
        RequireCompatibility(artifact);
        var world = RelationshipWorldDefinitionJsonSerializer.Deserialize(artifact.World.Document.GetRawText());
        if (!string.Equals(artifact.World.SchemaVersion, world.SchemaVersion, StringComparison.Ordinal)
            || !string.Equals(artifact.World.Id, world.Definition.World.Id, StringComparison.Ordinal)
            || !string.Equals(artifact.World.Revision, world.Definition.World.Revision, StringComparison.Ordinal)
            || !string.Equals(artifact.World.Fingerprint.Algorithm, world.Fingerprint.Algorithm, StringComparison.Ordinal)
            || !string.Equals(
                artifact.World.Fingerprint.Canonicalization,
                world.Fingerprint.Canonicalization,
                StringComparison.Ordinal)
            || !string.Equals(artifact.World.Fingerprint.Value, world.Fingerprint.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The relationship-world document does not reproduce the artifact's retained world projections.",
                nameof(artifact));
        }

        var plan = world.Compile();
        var compiledPopulations = ImmutableArray.CreateBuilder<CompiledWorldPopulation>(plan.Populations.Length);
        foreach (var population in plan.Populations)
            compiledPopulations.Add(population.Population);
        WorldArtifactManifest.ValidateWorldProjections(
            artifact.Populations,
            artifact.Exemplars,
            compiledPopulations.MoveToImmutable(),
            plan.Exemplars,
            nameof(artifact));

        return (world, plan);
    }

    static void RequireCompatibility(WorldArtifactManifest artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!string.Equals(
                artifact.Interpreter,
                RelationshipWorldInterpreter.Identity,
                StringComparison.Ordinal)
            || !string.Equals(
                artifact.EntropyAlgorithm,
                ReferenceGenerationInterpreter.EntropyAlgorithm,
                StringComparison.Ordinal))
        {
            throw new NotSupportedException(
                $"Relationship-world provisioning cannot realize artifact interpreter '{artifact.Interpreter}' with "
                + $"entropy algorithm '{artifact.EntropyAlgorithm}'.");
        }
    }

    static IEnumerable<WorldProvisioningItem> EnumeratePopulation(
        CompiledRelationshipWorldPopulation population,
        long rootSeed)
    {
        var generation = population.Population.GenerationPlan;
        foreach (var item in population.Enumerate(rootSeed))
        {
            yield return new(
                item.EntityId,
                item.Observation,
                item.Replay.SequenceIndex,
                generation.Definition.Id,
                generation.Definition.Revision,
                generation.Fingerprint,
                item.Replay.Interpreter,
                item.Replay.EntropyAlgorithm,
                item.Replay.ToToken());
        }
    }
}

/// <summary>Reference executor for bounded deterministic relationship-world provisioning.</summary>
public static class RelationshipWorldProvisioner
{
    /// <summary>Provisions every population in one compiled relationship-aware world.</summary>
    /// <param name="world">Exact compiled relationship world to generate.</param>
    /// <param name="rootSeed">Deterministic root seed shared by every population.</param>
    /// <param name="sink">Provider-neutral destination receiving sequential deterministic batches.</param>
    /// <param name="options">Optional bounded batching policy.</param>
    /// <param name="cancellationToken">Token requesting cancellation before generation or acknowledgement.</param>
    /// <returns>Completion evidence after every batch is acknowledged as committed.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="world"/> or <paramref name="sink"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The sink target identity is empty.</exception>
    /// <exception cref="WorldGenerationException">A generated population violates an identity invariant.</exception>
    /// <exception cref="WorldProvisioningRejectedException">The sink explicitly rejects a batch.</exception>
    /// <exception cref="InvalidOperationException">The sink returns an invalid acknowledgement.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    public static Task<WorldProvisioningResult> ProvisionAsync(
        CompiledRelationshipWorldPlan world,
        long rootSeed,
        IWorldProvisioningSink sink,
        WorldProvisioningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(world);
        var artifact = RelationshipWorldArtifact.FromWorld(world, rootSeed);
        return WorldProvisioner.ProvisionAsync(
            RelationshipWorldArtifact.CreateInterpreterPlan(artifact),
            sink,
            options,
            cancellationToken);
    }

    /// <summary>Provisions every population governed by one retained relationship-world artifact.</summary>
    /// <param name="artifact">Exact retained relationship-world artifact authority.</param>
    /// <param name="sink">Provider-neutral destination receiving sequential deterministic batches.</param>
    /// <param name="options">Optional bounded batching policy.</param>
    /// <param name="cancellationToken">Token requesting cancellation before generation or acknowledgement.</param>
    /// <returns>Completion evidence after every batch is acknowledged as committed.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="artifact"/> or <paramref name="sink"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">The artifact selects an unsupported interpreter or entropy algorithm.</exception>
    /// <exception cref="ArgumentException">The artifact contains inconsistent world authorities.</exception>
    /// <exception cref="JsonException">The retained relationship-world authority is absent or invalid.</exception>
    /// <exception cref="WorldGenerationException">A generated population violates an identity invariant.</exception>
    /// <exception cref="WorldProvisioningRejectedException">The sink explicitly rejects a batch.</exception>
    /// <exception cref="InvalidOperationException">The sink returns an invalid acknowledgement.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    public static Task<WorldProvisioningResult> ProvisionAsync(
        WorldArtifactManifest artifact,
        IWorldProvisioningSink sink,
        WorldProvisioningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return WorldProvisioner.ProvisionAsync(
            RelationshipWorldArtifact.CreateInterpreterPlan(artifact),
            sink,
            options,
            cancellationToken);
    }
}

/// <summary>Verifies v4 world JSON Lines streams governed by retained relationship-world artifacts.</summary>
public static class RelationshipWorldJsonLinesVerifier
{
    /// <summary>Validates a complete relationship-world item stream without throwing for invalid stream content.</summary>
    /// <param name="artifact">Exact independently retained relationship-world artifact authority.</param>
    /// <param name="input">Readable caller-owned UTF-8 JSON Lines stream.</param>
    /// <param name="cancellationToken">Token requesting cancellation between records.</param>
    /// <returns>A successful verification or one stable diagnostic for invalid stream content.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="artifact"/> or <paramref name="input"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">The artifact selects an unsupported interpreter or entropy algorithm.</exception>
    /// <exception cref="ArgumentException">The input is unreadable or artifact authorities disagree.</exception>
    /// <exception cref="JsonException">The retained relationship-world authority is absent or invalid.</exception>
    /// <exception cref="IOException">The input stream cannot be read.</exception>
    /// <exception cref="ObjectDisposedException">The input stream has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    public static Task<WorldJsonLinesValidationResult> ValidateAsync(
        WorldArtifactManifest artifact,
        Stream input,
        CancellationToken cancellationToken = default) =>
        WorldJsonLinesVerifier.ValidateAsync(
            RelationshipWorldArtifact.CreateInterpreterPlan(artifact),
            input,
            cancellationToken);

    /// <summary>Verifies a complete stream against one independently retained relationship-world artifact.</summary>
    /// <param name="artifact">Exact independently retained relationship-world artifact authority.</param>
    /// <param name="input">Readable caller-owned UTF-8 JSON Lines stream.</param>
    /// <param name="cancellationToken">Token requesting cancellation between records.</param>
    /// <returns>Verified artifact, target, run, batching, and item-count evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="artifact"/> or <paramref name="input"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">The artifact selects an unsupported interpreter or entropy algorithm.</exception>
    /// <exception cref="ArgumentException">The input is unreadable or artifact authorities disagree.</exception>
    /// <exception cref="JsonException">The retained relationship-world authority is absent or invalid.</exception>
    /// <exception cref="WorldJsonLinesVerificationException">The stream does not match the retained artifact.</exception>
    /// <exception cref="IOException">The input stream cannot be read.</exception>
    /// <exception cref="ObjectDisposedException">The input stream has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    public static Task<WorldJsonLinesVerificationResult> VerifyAsync(
        WorldArtifactManifest artifact,
        Stream input,
        CancellationToken cancellationToken = default) =>
        WorldJsonLinesVerifier.VerifyAsync(
            RelationshipWorldArtifact.CreateInterpreterPlan(artifact),
            input,
            cancellationToken);
}
