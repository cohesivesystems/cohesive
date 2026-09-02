using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.Provisioning;

/// <summary>Completion evidence for a fully verified world JSON Lines item stream.</summary>
public sealed record WorldJsonLinesVerificationResult
{
    internal WorldJsonLinesVerificationResult(
        WorldArtifactId artifactId,
        string? targetId,
        WorldProvisioningRunId? runId,
        int? batchSize,
        long itemCount)
    {
        ArtifactId = artifactId;
        TargetId = targetId;
        RunId = runId;
        BatchSize = batchSize;
        ItemCount = itemCount;
    }

    /// <summary>Gets the independently supplied artifact identity verified against every item.</summary>
    public WorldArtifactId ArtifactId { get; }

    /// <summary>Gets the logical target retained by the item stream, or null when the world has no items.</summary>
    public string? TargetId { get; }

    /// <summary>Gets the verified provisioning run identity, or null when the world has no items.</summary>
    public WorldProvisioningRunId? RunId { get; }

    /// <summary>Gets the verified provisioning batch size, or null when the world has no items.</summary>
    public int? BatchSize { get; }

    /// <summary>Gets the exact number of verified items.</summary>
    public long ItemCount { get; }
}

/// <summary>Failure proving that a world JSON Lines stream does not match its retained artifact manifest.</summary>
public sealed class WorldJsonLinesVerificationException : FormatException
{
    internal WorldJsonLinesVerificationException(long lineNumber, string? propertyName, string detail)
        : base(CreateMessage(lineNumber, propertyName, detail))
    {
        LineNumber = lineNumber;
        PropertyName = propertyName;
    }

    /// <summary>Gets the one-based line number containing the failure.</summary>
    /// <remarks>A value one past the final line identifies missing trailing artifact items.</remarks>
    public long LineNumber { get; }

    /// <summary>Gets the failing wire property, or null for a record- or stream-level failure.</summary>
    public string? PropertyName { get; }

    static string CreateMessage(long lineNumber, string? propertyName, string detail) =>
        propertyName is null
            ? $"World JSON Lines verification failed at line {lineNumber}: {detail}"
            : $"World JSON Lines verification failed at line {lineNumber}, property '{propertyName}': {detail}";
}

/// <summary>Independently verifies a complete v3 world JSON Lines stream against an exact artifact manifest.</summary>
/// <remarks>
/// Verification is bounded to one JSON record and one regenerated observation at a time. Success proves exact item
/// count and order, artifact and world provenance, target and batching identity, exemplar aliases, replay evidence,
/// and canonical observation bytes. No generated item is exposed before the complete stream has passed verification.
/// </remarks>
public static class WorldJsonLinesVerifier
{
    static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    static readonly string[] PropertyOrder =
    [
        "format",
        "runId",
        "batchId",
        "targetId",
        "artifactManifestSchema",
        "artifactId",
        "artifactManifestFingerprintAlgorithm",
        "artifactManifestFingerprintCanonicalization",
        "artifactManifestFingerprint",
        "worldId",
        "worldRevision",
        "worldFingerprintAlgorithm",
        "worldFingerprintCanonicalization",
        "worldFingerprint",
        "rootSeed",
        "populationId",
        "populationCount",
        "populationScope",
        "batchSize",
        "batchOrdinal",
        "batchStartSequenceIndex",
        "batchItemCount",
        "sequenceIndex",
        "exemplars",
        "definitionId",
        "definitionRevision",
        "definitionFingerprint",
        "interpreter",
        "entropyAlgorithm",
        "replayToken",
        "observation"
    ];

    /// <summary>Verifies a complete item stream against an independently retained artifact manifest.</summary>
    /// <param name="artifact">Exact fingerprint-verified manifest expected to govern every item.</param>
    /// <param name="input">Readable caller-owned UTF-8 JSON Lines stream.</param>
    /// <param name="cancellationToken">Token requesting cancellation between records.</param>
    /// <returns>Verified artifact, target, run, batching, and item-count evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="artifact"/> or <paramref name="input"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not readable.</exception>
    /// <exception cref="NotSupportedException">
    /// The manifest requires an interpreter or entropy algorithm unsupported by the reference verifier.
    /// </exception>
    /// <exception cref="WorldJsonLinesVerificationException">
    /// A record is malformed, noncanonical, incomplete, duplicated, out of order, or inconsistent with the manifest,
    /// its deterministic replay, or its provisioning identity.
    /// </exception>
    /// <exception cref="DecoderFallbackException">The stream contains invalid UTF-8.</exception>
    /// <exception cref="IOException">The input stream cannot be read.</exception>
    /// <exception cref="ObjectDisposedException">The input stream has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    public static async Task<WorldJsonLinesVerificationResult> VerifyAsync(
        WorldArtifactManifest artifact,
        Stream input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
            throw new ArgumentException("A world JSON Lines verification stream must be readable.", nameof(input));
        WorldProvisioner.RequireReferenceCompatibility(artifact);

        var world = artifact.World.Compile();
        using StreamReader reader = new(
            input,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: -1,
            leaveOpen: true);
        string? targetId = null;
        WorldProvisioningRunId? runId = null;
        int? batchSize = null;
        var populationIndex = 0;
        long expectedSequenceIndex = 0;
        long itemCount = 0;
        long lineNumber = 0;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0)
                throw Failure(lineNumber, propertyName: null, "Blank records are not part of the v3 contract.");

            while (populationIndex < world.Populations.Length
                   && expectedSequenceIndex >= world.Populations[populationIndex].Definition.Count)
            {
                populationIndex++;
                expectedSequenceIndex = 0;
            }
            if (populationIndex >= world.Populations.Length)
                throw Failure(lineNumber, propertyName: null, "The stream contains more items than the manifest.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw Failure(lineNumber, propertyName: null, $"The record is not valid JSON: {exception.Message}");
            }

            using (document)
            {
                var root = document.RootElement;
                ValidatePropertyOrder(root, lineNumber);
                var population = world.Populations[populationIndex];
                var generation = population.GenerationPlan;

                RequireEqual(ReadString(root, "format", lineNumber), WorldJsonLinesSink.Format, lineNumber, "format");
                var recordTargetId = ReadRequiredIdentity(root, "targetId", lineNumber);
                var recordBatchSize = ReadPositiveInt32(root, "batchSize", lineNumber);
                var expectedRunId = WorldProvisioningIdentityConvention.CreateRunId(
                    artifact,
                    recordTargetId,
                    recordBatchSize);
                RequireEqual(ReadString(root, "runId", lineNumber), expectedRunId.Value, lineNumber, "runId");
                targetId ??= recordTargetId;
                runId ??= expectedRunId;
                batchSize ??= recordBatchSize;
                RequireEqual(recordTargetId, targetId, lineNumber, "targetId");
                RequireEqual(expectedRunId, runId.Value, lineNumber, "runId");
                RequireEqual(recordBatchSize, batchSize.Value, lineNumber, "batchSize");

                RequireEqual(
                    ReadString(root, "artifactManifestSchema", lineNumber),
                    artifact.SchemaVersion,
                    lineNumber,
                    "artifactManifestSchema");
                RequireEqual(
                    ReadString(root, "artifactId", lineNumber),
                    artifact.ArtifactId.Value,
                    lineNumber,
                    "artifactId");
                RequireEqual(
                    ReadString(root, "artifactManifestFingerprintAlgorithm", lineNumber),
                    artifact.Fingerprint.Algorithm,
                    lineNumber,
                    "artifactManifestFingerprintAlgorithm");
                RequireEqual(
                    ReadString(root, "artifactManifestFingerprintCanonicalization", lineNumber),
                    artifact.Fingerprint.Canonicalization,
                    lineNumber,
                    "artifactManifestFingerprintCanonicalization");
                RequireEqual(
                    ReadString(root, "artifactManifestFingerprint", lineNumber),
                    artifact.Fingerprint.Value,
                    lineNumber,
                    "artifactManifestFingerprint");
                RequireEqual(
                    ReadString(root, "worldId", lineNumber),
                    artifact.World.Definition.Id,
                    lineNumber,
                    "worldId");
                RequireEqual(
                    ReadString(root, "worldRevision", lineNumber),
                    artifact.World.Definition.Revision,
                    lineNumber,
                    "worldRevision");
                RequireEqual(
                    ReadString(root, "worldFingerprintAlgorithm", lineNumber),
                    artifact.World.Fingerprint.Algorithm,
                    lineNumber,
                    "worldFingerprintAlgorithm");
                RequireEqual(
                    ReadString(root, "worldFingerprintCanonicalization", lineNumber),
                    artifact.World.Fingerprint.Canonicalization,
                    lineNumber,
                    "worldFingerprintCanonicalization");
                RequireEqual(
                    ReadString(root, "worldFingerprint", lineNumber),
                    artifact.World.Fingerprint.Value,
                    lineNumber,
                    "worldFingerprint");
                RequireEqual(ReadInt64String(root, "rootSeed", lineNumber), artifact.RootSeed, lineNumber, "rootSeed");
                RequireEqual(
                    ReadString(root, "populationId", lineNumber),
                    population.Definition.Id,
                    lineNumber,
                    "populationId");
                RequireEqual(
                    ReadInt32(root, "populationCount", lineNumber),
                    population.Definition.Count,
                    lineNumber,
                    "populationCount");
                RequireEqual(
                    ReadString(root, "populationScope", lineNumber),
                    population.Scope.Value,
                    lineNumber,
                    "populationScope");

                var sequenceIndex = ReadInt64(root, "sequenceIndex", lineNumber);
                RequireEqual(sequenceIndex, expectedSequenceIndex, lineNumber, "sequenceIndex");
                var expectedBatchOrdinal = (int)(sequenceIndex / recordBatchSize);
                var expectedBatchStart = (long)expectedBatchOrdinal * recordBatchSize;
                var expectedBatchItemCount = (int)Math.Min(
                    recordBatchSize,
                    population.Definition.Count - expectedBatchStart);
                RequireEqual(
                    ReadInt32(root, "batchOrdinal", lineNumber),
                    expectedBatchOrdinal,
                    lineNumber,
                    "batchOrdinal");
                RequireEqual(
                    ReadInt64(root, "batchStartSequenceIndex", lineNumber),
                    expectedBatchStart,
                    lineNumber,
                    "batchStartSequenceIndex");
                RequireEqual(
                    ReadInt32(root, "batchItemCount", lineNumber),
                    expectedBatchItemCount,
                    lineNumber,
                    "batchItemCount");
                var expectedBatchId = WorldProvisioningIdentityConvention.CreateBatchId(
                    expectedRunId,
                    population.Definition.Id,
                    population.Scope,
                    expectedBatchOrdinal,
                    expectedBatchStart,
                    expectedBatchItemCount);
                RequireEqual(ReadString(root, "batchId", lineNumber), expectedBatchId.Value, lineNumber, "batchId");

                ValidateExemplars(root.GetProperty("exemplars"), artifact, population.Definition.Id, sequenceIndex, lineNumber);
                RequireEqual(
                    ReadString(root, "definitionId", lineNumber),
                    generation.Definition.Id,
                    lineNumber,
                    "definitionId");
                RequireEqual(
                    ReadString(root, "definitionRevision", lineNumber),
                    generation.Definition.Revision,
                    lineNumber,
                    "definitionRevision");
                RequireEqual(
                    ReadString(root, "definitionFingerprint", lineNumber),
                    generation.Fingerprint,
                    lineNumber,
                    "definitionFingerprint");
                RequireEqual(
                    ReadString(root, "interpreter", lineNumber),
                    artifact.Interpreter,
                    lineNumber,
                    "interpreter");
                RequireEqual(
                    ReadString(root, "entropyAlgorithm", lineNumber),
                    artifact.EntropyAlgorithm,
                    lineNumber,
                    "entropyAlgorithm");

                var replayToken = ReadString(root, "replayToken", lineNumber);
                GeneratedObservation replayed;
                try
                {
                    var replay = GenerationReplayEvidence.ParseToken(replayToken);
                    RequireEqual(replay.RootSeed, artifact.RootSeed, lineNumber, "replayToken");
                    RequireEqual(replay.Scope, population.Scope, lineNumber, "replayToken");
                    RequireEqual(replay.SequenceIndex, sequenceIndex, lineNumber, "replayToken");
                    replayed = ReferenceGenerationInterpreter.Replay(generation, replay);
                }
                catch (WorldJsonLinesVerificationException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is FormatException or ArgumentException)
                {
                    throw Failure(
                        lineNumber,
                        "replayToken",
                        $"Replay evidence is invalid for the manifest: {exception.Message}");
                }

                RequireEqual(
                    root.GetProperty("observation").GetRawText(),
                    replayed.Observation.ToCanonicalJson(),
                    lineNumber,
                    "observation");
            }

            expectedSequenceIndex++;
            itemCount++;
        }

        while (populationIndex < world.Populations.Length
               && expectedSequenceIndex >= world.Populations[populationIndex].Definition.Count)
        {
            populationIndex++;
            expectedSequenceIndex = 0;
        }
        if (populationIndex < world.Populations.Length)
        {
            var population = world.Populations[populationIndex];
            throw Failure(
                lineNumber + 1,
                propertyName: null,
                $"The stream ended before population '{population.Definition.Id}' sequence index "
                + $"'{expectedSequenceIndex}'.");
        }

        return new(artifact.ArtifactId, targetId, runId, batchSize, itemCount);
    }

    static void ValidatePropertyOrder(JsonElement root, long lineNumber)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Failure(lineNumber, propertyName: null, "Each record must be a JSON object.");

        var index = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (index >= PropertyOrder.Length)
                throw Failure(lineNumber, property.Name, "The property is not part of the v3 contract.");
            if (!string.Equals(property.Name, PropertyOrder[index], StringComparison.Ordinal))
            {
                throw Failure(
                    lineNumber,
                    property.Name,
                    $"Expected canonical property '{PropertyOrder[index]}' at ordinal {index}.");
            }
            index++;
        }

        if (index != PropertyOrder.Length)
            throw Failure(lineNumber, PropertyOrder[index], "The required property is missing.");
    }

    static void ValidateExemplars(
        JsonElement value,
        WorldArtifactManifest artifact,
        string populationId,
        long sequenceIndex,
        long lineNumber)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw Failure(lineNumber, "exemplars", "The value must be an array.");

        var actual = value.EnumerateArray();
        foreach (var exemplar in artifact.Exemplars)
        {
            if (!string.Equals(exemplar.PopulationId, populationId, StringComparison.Ordinal)
                || exemplar.SequenceIndex != sequenceIndex)
            {
                continue;
            }
            if (!actual.MoveNext()
                || actual.Current.ValueKind != JsonValueKind.String
                || !string.Equals(actual.Current.GetString(), exemplar.Id, StringComparison.Ordinal))
            {
                throw Failure(lineNumber, "exemplars", $"Expected exemplar '{exemplar.Id}'.");
            }
        }

        if (actual.MoveNext())
            throw Failure(lineNumber, "exemplars", "The record contains an undeclared exemplar.");
    }

    static string ReadRequiredIdentity(JsonElement root, string propertyName, long lineNumber)
    {
        var value = ReadString(root, propertyName, lineNumber);
        if (string.IsNullOrWhiteSpace(value))
            throw Failure(lineNumber, propertyName, "The identity cannot be empty or white-space.");
        return value;
    }

    static string ReadString(JsonElement root, string propertyName, long lineNumber)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
            throw Failure(lineNumber, propertyName, "The value must be a non-null string.");
        return text;
    }

    static int ReadPositiveInt32(JsonElement root, string propertyName, long lineNumber)
    {
        var value = ReadInt32(root, propertyName, lineNumber);
        if (value <= 0)
            throw Failure(lineNumber, propertyName, "The value must be positive.");
        return value;
    }

    static int ReadInt32(JsonElement root, string propertyName, long lineNumber)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw Failure(lineNumber, propertyName, "The value must be a 32-bit integer.");
        return result;
    }

    static long ReadInt64(JsonElement root, string propertyName, long lineNumber)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            throw Failure(lineNumber, propertyName, "The value must be a 64-bit integer.");
        return result;
    }

    static long ReadInt64String(JsonElement root, string propertyName, long lineNumber)
    {
        var text = ReadString(root, propertyName, lineNumber);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
            || !string.Equals(text, result.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw Failure(lineNumber, propertyName, "The value must be a canonical signed 64-bit decimal string.");
        }
        return result;
    }

    static void RequireEqual<T>(T actual, T expected, long lineNumber, string propertyName)
        where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
        {
            throw Failure(
                lineNumber,
                propertyName,
                $"Expected '{expected}' but found '{actual}'.");
        }
    }

    static WorldJsonLinesVerificationException Failure(
        long lineNumber,
        string? propertyName,
        string detail) =>
        new(lineNumber, propertyName, detail);
}
