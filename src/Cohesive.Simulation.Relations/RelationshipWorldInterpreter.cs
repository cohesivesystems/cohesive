using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Relations;

/// <summary>Replay evidence for one exact relationship-world population member.</summary>
public sealed record RelationshipWorldReplayEvidence
{
    /// <summary>Creates relationship-world replay evidence.</summary>
    /// <param name="rootSeed">Caller-supplied deterministic root seed.</param>
    /// <param name="sequenceIndex">Zero-based population sequence index.</param>
    /// <param name="scope">Exact world-population entropy scope.</param>
    /// <param name="populationId">Stable population identity.</param>
    /// <param name="populationFingerprint">Fingerprint of semantics affecting this population's generation.</param>
    /// <param name="interpreter">Relationship-world interpreter identity and version.</param>
    /// <param name="entropyAlgorithm">Addressable entropy algorithm identity and version.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is negative.</exception>
    /// <exception cref="ArgumentException">A scope or string coordinate is empty.</exception>
    [JsonConstructor]
    public RelationshipWorldReplayEvidence(
        long rootSeed,
        long sequenceIndex,
        GenerationScope scope,
        string populationId,
        string populationFingerprint,
        string interpreter,
        string entropyAlgorithm)
    {
        if (sequenceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sequenceIndex), sequenceIndex, "Sequence index cannot be negative.");
        GenerationScope.Validate(scope, nameof(scope));
        RootSeed = rootSeed;
        SequenceIndex = sequenceIndex;
        Scope = scope;
        PopulationId = Guard.RequireNotNullOrWhiteSpace(populationId);
        PopulationFingerprint = Guard.RequireNotNullOrWhiteSpace(populationFingerprint);
        Interpreter = Guard.RequireNotNullOrWhiteSpace(interpreter);
        EntropyAlgorithm = Guard.RequireNotNullOrWhiteSpace(entropyAlgorithm);
    }

    /// <summary>Gets the deterministic root seed.</summary>
    public long RootSeed { get; }

    /// <summary>Gets the zero-based population sequence index.</summary>
    public long SequenceIndex { get; }

    /// <summary>Gets the exact isolated population scope.</summary>
    public GenerationScope Scope { get; }

    /// <summary>Gets the stable population identity.</summary>
    public string PopulationId { get; }

    /// <summary>Gets the fingerprint of semantics affecting this population's generation.</summary>
    public string PopulationFingerprint { get; }

    /// <summary>Gets the versioned interpreter identity.</summary>
    public string Interpreter { get; }

    /// <summary>Gets the versioned entropy algorithm identity.</summary>
    public string EntropyAlgorithm { get; }

    /// <summary>Encodes this evidence as an opaque canonical URL-safe token.</summary>
    /// <returns>A token accepted by <see cref="ParseToken"/>.</returns>
    /// <exception cref="InvalidOperationException">The evidence has no canonical token representation.</exception>
    /// <exception cref="JsonException">The evidence violates its strict token payload contract.</exception>
    /// <exception cref="NotSupportedException">The evidence contains an unsupported serialization type.</exception>
    public string ToToken() => RelationshipWorldReplayTokenCodec.Encode(this);

    /// <summary>Decodes and validates a relationship-world replay token.</summary>
    /// <param name="token">Opaque token returned by <see cref="ToToken"/>.</param>
    /// <returns>The exact encoded replay evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="token"/> is malformed or noncanonical.</exception>
    public static RelationshipWorldReplayEvidence ParseToken(string token) =>
        RelationshipWorldReplayTokenCodec.Decode(token);
}

/// <summary>One generated relationship-world member with canonical entity identity.</summary>
public sealed record GeneratedRelationshipWorldItem
{
    /// <summary>Creates a generated relationship-world item.</summary>
    /// <param name="entityId">Canonical population entity identity.</param>
    /// <param name="observation">Complete authoritative core observation.</param>
    /// <param name="replay">Exact relationship-world replay evidence.</param>
    /// <exception cref="ArgumentException"><paramref name="entityId"/> is default.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="observation"/> or <paramref name="replay"/> is <see langword="null"/>.
    /// </exception>
    public GeneratedRelationshipWorldItem(
        EntityId entityId,
        Observation observation,
        RelationshipWorldReplayEvidence replay)
    {
        if (string.IsNullOrWhiteSpace(entityId.Value))
            throw new ArgumentException("A generated relationship-world item requires an entity identity.", nameof(entityId));
        EntityId = entityId;
        Observation = Guard.RequireNotNull(observation);
        Replay = Guard.RequireNotNull(replay);
    }

    /// <summary>Gets the canonical entity identity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the complete authoritative core observation.</summary>
    public Observation Observation { get; }

    /// <summary>Gets exact relationship-world replay evidence.</summary>
    public RelationshipWorldReplayEvidence Replay { get; }
}

/// <summary>One generated typed value with canonical identity and relationship-world replay evidence.</summary>
/// <typeparam name="T">CLR value type.</typeparam>
public sealed record GeneratedRelationshipWorldItem<T>
{
    /// <summary>Creates a generated typed relationship-world result.</summary>
    /// <param name="entityId">Canonical population entity identity.</param>
    /// <param name="value">Materialized CLR interpretation.</param>
    /// <param name="observation">Complete authoritative core observation.</param>
    /// <param name="replay">Exact relationship-world replay evidence.</param>
    /// <exception cref="ArgumentException"><paramref name="entityId"/> is default.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="observation"/> or <paramref name="replay"/> is <see langword="null"/>.
    /// </exception>
    public GeneratedRelationshipWorldItem(
        EntityId entityId,
        T value,
        Observation observation,
        RelationshipWorldReplayEvidence replay)
    {
        if (string.IsNullOrWhiteSpace(entityId.Value))
            throw new ArgumentException("A generated relationship-world item requires an entity identity.", nameof(entityId));
        EntityId = entityId;
        Value = value;
        Observation = Guard.RequireNotNull(observation);
        Replay = Guard.RequireNotNull(replay);
    }

    /// <summary>Gets the canonical population entity identity.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the materialized CLR value.</summary>
    public T Value { get; }

    /// <summary>Gets the complete authoritative core observation.</summary>
    public Observation Observation { get; }

    /// <summary>Gets exact relationship-world replay evidence.</summary>
    public RelationshipWorldReplayEvidence Replay { get; }
}

/// <summary>Deterministic reference interpreter for compiled relationship-world plans.</summary>
public static class RelationshipWorldInterpreter
{
    /// <summary>Stable interpreter identity and version.</summary>
    public const string Identity = "cohesive-simulation-relations-reference/v1";

    /// <summary>Stable population replay canonicalization profile.</summary>
    public const string ReplayCanonicalization = "cohesive-simulation-relations-population-replay/v1";

    internal static GeneratedRelationshipWorldItem Generate(
        CompiledRelationshipWorldPopulation population,
        long seed,
        long sequenceIndex)
    {
        var local = population.Population;
        var value = ReferenceGenerationInterpreter.GenerateRecordValue(
            local.GenerationPlan,
            seed,
            local.Scope,
            sequenceIndex);
        foreach (var relationship in population.Relationships)
        {
            value = value.WithField(
                relationship.Relationship.SourceReference,
                SelectReferenceValue(population, relationship, seed, sequenceIndex));
        }

        var observation = Observation.Create(local.GenerationPlan.OutputShape, value);
        if (!local.Definition.EntityIdentity.TryResolve(
                local.Scope,
                observation.Value,
                sequenceIndex,
                out var entityId,
                out var code,
                out var detail))
        {
            throw WorldGenerationException.IdentityFailure(
                local.Definition.Id,
                sequenceIndex,
                code!,
                detail!);
        }

        return new(
            entityId,
            observation,
            new(
                seed,
                sequenceIndex,
                local.Scope,
                local.Definition.Id,
                population.ReplayFingerprint,
                Identity,
                ReferenceGenerationInterpreter.EntropyAlgorithm));
    }

    internal static void ValidateReplay(
        CompiledRelationshipWorldPopulation population,
        RelationshipWorldReplayEvidence replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        List<string> mismatches = [];
        AddMismatch(mismatches, "population", population.Population.Definition.Id, replay.PopulationId);
        AddMismatch(mismatches, "scope", population.Population.Scope.Value, replay.Scope.Value);
        AddMismatch(mismatches, "population fingerprint", population.ReplayFingerprint, replay.PopulationFingerprint);
        AddMismatch(mismatches, "interpreter", Identity, replay.Interpreter);
        AddMismatch(
            mismatches,
            "entropy algorithm",
            ReferenceGenerationInterpreter.EntropyAlgorithm,
            replay.EntropyAlgorithm);
        if (mismatches.Count > 0)
        {
            throw new ArgumentException(
                $"Replay evidence is incompatible with the selected relationship-world population: {string.Join("; ", mismatches)}.",
                nameof(replay));
        }
    }

    internal static void ValidateTargetIdentities(
        CompiledRelationshipWorldPopulation source,
        long seed)
    {
        HashSet<string> validatedPopulations = new(StringComparer.Ordinal);
        foreach (var relationship in source.Relationships)
        {
            var target = relationship.TargetPopulation;
            if (relationship.Definition.Selection.PresenceProbability <= 0d
                || target.Definition.EntityIdentity.Source != WorldEntityIdentitySource.UniqueObservationField
                || !validatedPopulations.Add(target.Definition.Id))
            {
                continue;
            }

            HashSet<EntityId> identities = [];
            for (var index = 0; index < target.Definition.Count; index++)
            {
                var entityId = ResolveTargetIdentity(target, seed, index);
                if (identities.Add(entityId))
                    continue;

                throw WorldGenerationException.IdentityFailure(
                    target.Definition.Id,
                    index,
                    "simulation.world.entityIdentityDuplicate",
                    $"Population '{target.Definition.Id}' resolves entity identity '{entityId.Value}' more than once.");
            }
        }
    }

    static ObservationValue SelectReferenceValue(
        CompiledRelationshipWorldPopulation source,
        CompiledWorldPopulationRelationship relationship,
        long seed,
        long sequenceIndex)
    {
        var probability = relationship.Definition.Selection.PresenceProbability;
        if (probability <= 0d || probability < 1d && UnitInterval(Sample(
                source,
                relationship,
                seed,
                sequenceIndex,
                "presence",
                attempt: 0)) >= probability)
        {
            if (relationship.SourceField.Presence != FieldPresence.Optional)
            {
                throw new InvalidOperationException(
                    $"Compiled relationship '{relationship.Relationship.Id.Value}' selected absence for required "
                    + $"field '{relationship.SourceField.Name.Value}'.");
            }

            return ObservationValue.Undefined;
        }

        var targetIndex = relationship.Relationship.SourceReferenceUniqueness
            == SourceReferenceUniqueness.GloballyUnique
            ? SelectUniqueTargetIndex(source, relationship, seed, sequenceIndex)
            : SelectTargetIndex(source, relationship, seed, sequenceIndex);
        return ObservationValue.FromString(ResolveTargetIdentity(relationship.TargetPopulation, seed, targetIndex).Value);
    }

    static long SelectTargetIndex(
        CompiledRelationshipWorldPopulation source,
        CompiledWorldPopulationRelationship relationship,
        long seed,
        long sequenceIndex)
    {
        var range = (ulong)relationship.TargetPopulation.Definition.Count;
        var threshold = unchecked(0UL - range) % range;
        for (var attempt = 0; ; attempt++)
        {
            var sample = Sample(source, relationship, seed, sequenceIndex, "target", attempt);
            if (sample >= threshold)
                return (long)(sample % range);
        }
    }

    static long SelectUniqueTargetIndex(
        CompiledRelationshipWorldPopulation source,
        CompiledWorldPopulationRelationship relationship,
        long seed,
        long sequenceIndex)
    {
        var range = (ulong)relationship.TargetPopulation.Definition.Count;
        var offset = Sample(source, relationship, seed, 0, "unique-offset", 0) % range;
        var step = Sample(source, relationship, seed, 0, "unique-step", 0) % range;
        if (step == 0)
            step = 1;
        while (GreatestCommonDivisor(step, range) != 1)
            step = step == range - 1 ? 1 : step + 1;
        return (long)(((UInt128)step * (ulong)sequenceIndex + offset) % range);
    }

    static EntityId ResolveTargetIdentity(
        CompiledWorldPopulation target,
        long seed,
        long sequenceIndex)
    {
        if (target.Definition.EntityIdentity.Source == WorldEntityIdentitySource.PopulationSequence)
            return WorldEntitySequenceIdentityConvention.Create(target.Scope, sequenceIndex);

        var generated = ReferenceGenerationInterpreter.GenerateRecordValue(
            target.GenerationPlan,
            seed,
            target.Scope,
            sequenceIndex);
        if (target.Definition.EntityIdentity.TryResolve(
                target.Scope,
                generated,
                sequenceIndex,
                out var entityId,
                out var code,
                out var detail))
        {
            return entityId;
        }

        throw WorldGenerationException.IdentityFailure(
            target.Definition.Id,
            sequenceIndex,
            code!,
            detail!);
    }

    static ulong Sample(
        CompiledRelationshipWorldPopulation source,
        CompiledWorldPopulationRelationship relationship,
        long seed,
        long sequenceIndex,
        string coordinate,
        int attempt) =>
        ReferenceGenerationInterpreter.SampleWorldReferenceEntropy(
            seed,
            source.Population.Scope,
            sequenceIndex,
            source.Population.GenerationPlan.OutputShape.ShapeId.Value,
            relationship.Relationship.Id.Value,
            coordinate,
            attempt);

    static double UnitInterval(ulong value) => (value >> 11) * (1d / (1UL << 53));

    static ulong GreatestCommonDivisor(ulong left, ulong right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }
        return left;
    }

    static void AddMismatch(
        ICollection<string> mismatches,
        string coordinate,
        string expected,
        string observed)
    {
        if (!string.Equals(expected, observed, StringComparison.Ordinal))
            mismatches.Add($"{coordinate} expected '{expected}' but observed '{observed}'");
    }
}

static class RelationshipWorldReplayTokenCodec
{
    const string Prefix = "csimwr1.";

    public static string Encode(RelationshipWorldReplayEvidence evidence) =>
        CanonicalReplayTokenCodec.Encode(evidence, Prefix);

    public static RelationshipWorldReplayEvidence Decode(string token) =>
        CanonicalReplayTokenCodec.Decode<RelationshipWorldReplayEvidence>(
            token,
            Prefix,
            tokenName: "relationship-world replay token",
            evidenceContractName: "relationship-world replay evidence");
}
