using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Worlds;

/// <summary>Source of stable identity for members of one generated world population.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorldEntityIdentitySource
{
    /// <summary>Derive identity from the stable world-population scope and sequence index.</summary>
    PopulationSequence = 0,

    /// <summary>Read identity from a field asserted unique across the complete generated population.</summary>
    UniqueObservationField = 1
}

/// <summary>Portable deterministic identity policy for one generated world population.</summary>
/// <remarks>
/// Identity is part of world meaning because generated relationships and external provisioning must address the same
/// instances. Storage adapters consume resolved identities and do not select another identity policy.
/// </remarks>
public sealed record WorldEntityIdentityPolicy
{
    /// <summary>Creates a population identity policy.</summary>
    /// <param name="source">Source from which generated entity identities are resolved.</param>
    /// <param name="observationField">
    /// Field-only path used by <see cref="WorldEntityIdentitySource.UniqueObservationField"/>; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// Cross-property validity is retained for structured world-compilation diagnostics rather than rejected here.
    /// Prefer <see cref="PopulationSequence"/> or <see cref="FromUniqueObservationField(FieldPath)"/> when authoring.
    /// </remarks>
    [JsonConstructor]
    public WorldEntityIdentityPolicy(
        WorldEntityIdentitySource source,
        FieldPath? observationField = null)
    {
        Source = source;
        ObservationField = observationField;
    }

    /// <summary>Gets the conventional stable population-sequence identity policy.</summary>
    public static WorldEntityIdentityPolicy PopulationSequence { get; } = new(
        WorldEntityIdentitySource.PopulationSequence);

    /// <summary>Gets the configured source of generated entity identity.</summary>
    public WorldEntityIdentitySource Source { get; }

    /// <summary>Gets the observation field path used for unique-field identity, when applicable.</summary>
    public FieldPath? ObservationField { get; }

    /// <summary>Creates a policy asserting that one scalar observation field is unique across the population.</summary>
    /// <param name="path">Non-empty field-only path to the population-unique identity value.</param>
    /// <returns>An immutable unique-observation-field identity policy.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is default, contains an empty segment, or contains collection navigation.
    /// </exception>
    public static WorldEntityIdentityPolicy FromUniqueObservationField(FieldPath path)
    {
        ValidateFieldPath(path, nameof(path));
        return new(WorldEntityIdentitySource.UniqueObservationField, path);
    }

    /// <summary>Creates a policy asserting that one top-level scalar field is unique across the population.</summary>
    /// <param name="fieldIdentity">Canonical population-unique top-level field identity.</param>
    /// <returns>An immutable unique-observation-field identity policy.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty or white-space.</exception>
    public static WorldEntityIdentityPolicy FromUniqueObservationField(string fieldIdentity) =>
        FromUniqueObservationField(FieldPath.FromField(fieldIdentity));

    internal bool TryResolve(
        GenerationScope populationScope,
        GeneratedObservation generated,
        out EntityId entityId,
        out string? code,
        out string? detail)
        => TryResolve(
            populationScope,
            generated.Observation.Value,
            generated.Replay.SequenceIndex,
            out entityId,
            out code,
            out detail);

    internal bool TryResolve(
        GenerationScope populationScope,
        ObservationValue value,
        long sequenceIndex,
        out EntityId entityId,
        out string? code,
        out string? detail)
    {
        switch (Source)
        {
            case WorldEntityIdentitySource.PopulationSequence:
                entityId = WorldEntitySequenceIdentityConvention.Create(
                    populationScope,
                    sequenceIndex);
                code = null;
                detail = null;
                return true;

            case WorldEntityIdentitySource.UniqueObservationField when ObservationField is { } path:
                return TryResolveField(path, value, sequenceIndex, out entityId, out code, out detail);

            case WorldEntityIdentitySource.UniqueObservationField:
                entityId = default;
                code = "simulation.world.entityIdentityFieldMissing";
                detail = "A unique-observation-field identity policy has no observation field path.";
                return false;

            default:
                entityId = default;
                code = "simulation.world.entityIdentitySourceInvalid";
                detail = $"World entity identity source '{Source}' is unsupported.";
                return false;
        }
    }

    internal static bool IsValidFieldPath(FieldPath path) =>
        !path.Segments.IsDefaultOrEmpty
        && path.Segments.All(static segment =>
            segment.Kind == SegmentKind.Field
            && !string.IsNullOrWhiteSpace(segment.Segment));

    static bool TryResolveField(
        FieldPath path,
        ObservationValue observation,
        long sequenceIndex,
        out EntityId entityId,
        out string? code,
        out string? detail)
    {
        if (!observation.TryGetField(path, out var value))
        {
            entityId = default;
            code = "simulation.world.entityIdentityValueMissing";
            detail = $"Generated observation at sequence index '{sequenceIndex}' does not contain "
                + $"entity identity path '{path}'.";
            return false;
        }

        var text = value.Kind switch
        {
            ObservationValueKind.String
                or ObservationValueKind.DateTimeOffset
                or ObservationValueKind.DateOnly
                or ObservationValueKind.TimeOnly
                or ObservationValueKind.TimeSpan => value.GetString(),
            ObservationValueKind.Int64 => value.GetInt64().ToString(CultureInfo.InvariantCulture),
            ObservationValueKind.Decimal => value.GetDecimal().ToString(CultureInfo.InvariantCulture),
            ObservationValueKind.Double when double.IsFinite(value.GetDouble()) =>
                value.GetDouble().ToString("R", CultureInfo.InvariantCulture),
            ObservationValueKind.Bool => value.GetBoolean() ? "true" : "false",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(text))
        {
            entityId = default;
            code = "simulation.world.entityIdentityValueInvalid";
            detail = $"Generated observation at sequence index '{sequenceIndex}' has entity identity "
                + $"path '{path}' with unsupported or empty value kind '{value.Kind}'.";
            return false;
        }

        entityId = new(text);
        code = null;
        detail = null;
        return true;
    }

    static void ValidateFieldPath(FieldPath path, string parameterName)
    {
        if (!IsValidFieldPath(path))
        {
            throw new ArgumentException(
                "An entity identity field path must contain one or more non-empty field segments and no collection navigation.",
                parameterName);
        }
    }
}

/// <summary>Stable convention for assigning entity identities to world population sequence positions.</summary>
public static class WorldEntitySequenceIdentityConvention
{
    /// <summary>Stable identity of the current population-sequence entity identity convention.</summary>
    /// <remarks>The value is retained from the original storage bridge so existing generated identities remain stable.</remarks>
    public const string Identity = "cohesive-simulation-storage-entity-sequence/v1";

    /// <summary>Derives a stable entity identity from an exact population scope and sequence index.</summary>
    /// <param name="populationScope">Exact isolated world-population scope.</param>
    /// <param name="sequenceIndex">Non-negative generated sequence index.</param>
    /// <returns>An identity stable across seeds and revisions that retain the same world and population identities.</returns>
    /// <exception cref="ArgumentException"><paramref name="populationScope"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sequenceIndex"/> is negative.</exception>
    public static EntityId Create(GenerationScope populationScope, long sequenceIndex)
    {
        GenerationScope.Validate(populationScope, nameof(populationScope));
        if (sequenceIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequenceIndex),
                sequenceIndex,
                "Sequence index cannot be negative.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Identity);
        Append(hash, populationScope.Value);
        var scopeFingerprint = Convert.ToHexStringLower(hash.GetHashAndReset());
        return new($"csimentity1_{scopeFingerprint}_{sequenceIndex.ToString(CultureInfo.InvariantCulture)}");
    }

    static void Append(IncrementalHash hash, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, byteCount);
        hash.AppendData(length);
        if (byteCount == 0)
            return;

        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, rented);
            hash.AppendData(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>One generated world member with its canonical entity identity and replayable observation.</summary>
public sealed record GeneratedWorldItem
{
    /// <summary>Creates a generated world item.</summary>
    /// <param name="entityId">Canonical identity assigned by the population's world identity policy.</param>
    /// <param name="generated">Generated identity-free observation and exact replay evidence.</param>
    /// <exception cref="ArgumentException"><paramref name="entityId"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="generated"/> is <see langword="null"/>.</exception>
    public GeneratedWorldItem(EntityId entityId, GeneratedObservation generated)
    {
        if (string.IsNullOrWhiteSpace(entityId.Value))
            throw new ArgumentException("A generated world item requires an entity identity.", nameof(entityId));

        EntityId = entityId;
        Generated = Guard.RequireNotNull(generated);
    }

    /// <summary>Gets the canonical identity assigned to this generated world member.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the generated identity-free observation and exact replay evidence.</summary>
    public GeneratedObservation Generated { get; }

    /// <summary>Gets the generated identity-free core observation.</summary>
    [JsonIgnore]
    public Observation Observation => Generated.Observation;

    /// <summary>Gets the exact generation replay evidence.</summary>
    [JsonIgnore]
    public GenerationReplayEvidence Replay => Generated.Replay;
}

/// <summary>Failure to generate a semantically complete world population.</summary>
public sealed class WorldGenerationException : InvalidOperationException
{
    /// <summary>Creates a world-generation failure with structured diagnostics.</summary>
    /// <param name="validation">Structured diagnostics describing why generation failed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    public WorldGenerationException(DocumentValidationResult validation)
        : base(CreateMessage(validation)) => Validation = validation;

    /// <summary>Gets deterministic structured world-generation diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    internal static WorldGenerationException IdentityFailure(
        string populationId,
        long sequenceIndex,
        string code,
        string detail) =>
        new(new([
            new(
                Code: code,
                Severity: DiagnosticSeverity.Error,
                Message: detail,
                Location: $"/populations/{EscapePointerToken(populationId)}/items/{sequenceIndex}/entityId",
                Evidence: new(stage: "world-generation"))
        ]));

    static string EscapePointerToken(string value) => value
        .Replace("~", "~0", StringComparison.Ordinal)
        .Replace("/", "~1", StringComparison.Ordinal);

    static string CreateMessage(DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        return "World generation failed: " + string.Join(
            " | ",
            validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }
}
