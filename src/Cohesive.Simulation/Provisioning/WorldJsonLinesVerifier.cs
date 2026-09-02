using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Cohesive.Model.Serialization;
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

/// <summary>Structured outcome from validating a complete world JSON Lines item stream.</summary>
public sealed record WorldJsonLinesValidationResult
{
    internal WorldJsonLinesValidationResult(
        WorldJsonLinesVerificationResult? verification,
        DocumentValidationResult validation)
    {
        Verification = verification;
        Validation = validation;
    }

    /// <summary>Gets completion evidence when the entire stream is valid; otherwise <see langword="null"/>.</summary>
    public WorldJsonLinesVerificationResult? Verification { get; }

    /// <summary>Gets stable structured diagnostics describing stream validity.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Gets whether the entire stream passed validation and produced completion evidence.</summary>
    public bool IsSuccessful => Verification is not null && Validation.IsValid;
}

/// <summary>Failure proving that a world JSON Lines stream does not match its retained artifact manifest.</summary>
public sealed class WorldJsonLinesVerificationException : FormatException
{
    internal WorldJsonLinesVerificationException(
        long lineNumber,
        string? propertyName,
        string code,
        string detail,
        Exception? innerException = null)
        : base(CreateMessage(lineNumber, propertyName, code, detail), innerException)
    {
        LineNumber = lineNumber;
        PropertyName = propertyName;
        Validation = StrictDocumentJson.Error(
            code,
            detail,
            CreateLocation(lineNumber, propertyName));
    }

    /// <summary>Gets the one-based line number containing the failure.</summary>
    /// <remarks>A value one past the final line identifies missing trailing artifact items.</remarks>
    public long LineNumber { get; }

    /// <summary>Gets the failing wire property, or null for a record- or stream-level failure.</summary>
    public string? PropertyName { get; }

    /// <summary>Gets stable structured validation evidence for the failure.</summary>
    public DocumentValidationResult Validation { get; }

    static string CreateMessage(long lineNumber, string? propertyName, string code, string detail) =>
        propertyName is null
            ? $"World JSON Lines verification failed with '{code}' at line {lineNumber}: {detail}"
            : $"World JSON Lines verification failed with '{code}' at line {lineNumber}, property "
            + $"'{propertyName}': {detail}";

    static string CreateLocation(long lineNumber, string? propertyName)
    {
        var lineLocation = $"/lines/{lineNumber - 1}";
        return propertyName is null
            ? lineLocation
            : $"{lineLocation}/{EscapeJsonPointerSegment(propertyName)}";
    }

    static string EscapeJsonPointerSegment(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);
}

/// <summary>Independently verifies a complete v3 world JSON Lines stream against an exact artifact manifest.</summary>
/// <remarks>
/// Verification is bounded to one JSON record and one regenerated observation at a time. Success proves exact item
/// count and order, canonical v3 record bytes, artifact and world provenance, target and batching identity, exemplar
/// aliases, replay evidence, and canonical observation bytes. No generated item is exposed before the complete stream
/// has passed verification.
/// </remarks>
public static class WorldJsonLinesVerifier
{
    const string FormatUnsupportedCode = "simulation.worldArtifact.jsonLines.formatUnsupported";
    const string ArtifactMismatchCode = "simulation.worldArtifact.jsonLines.artifactMismatch";
    const string ProvisioningIdentityMismatchCode =
        "simulation.worldArtifact.jsonLines.provisioningIdentityMismatch";
    const string PopulationMismatchCode = "simulation.worldArtifact.jsonLines.populationMismatch";
    const string GenerationMismatchCode = "simulation.worldArtifact.jsonLines.generationMismatch";
    const string ReplayInvalidCode = "simulation.worldArtifact.jsonLines.replayInvalid";
    const string ObservationMismatchCode = "simulation.worldArtifact.jsonLines.observationMismatch";
    const string ItemMissingCode = "simulation.worldArtifact.jsonLines.itemMissing";
    const string ItemUnexpectedCode = "simulation.worldArtifact.jsonLines.itemUnexpected";

    static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>Validates a complete item stream without throwing for invalid stream content.</summary>
    /// <param name="artifact">Exact fingerprint-verified manifest expected to govern every item.</param>
    /// <param name="input">Readable caller-owned UTF-8 JSON Lines stream.</param>
    /// <param name="cancellationToken">Token requesting cancellation between records.</param>
    /// <returns>
    /// A successful result with completion evidence, or a failed result with one stable diagnostic for the first
    /// invalid record or incomplete stream condition.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="artifact"/> or <paramref name="input"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> is not readable.</exception>
    /// <exception cref="NotSupportedException">
    /// The manifest requires an interpreter or entropy algorithm unsupported by the reference verifier.
    /// </exception>
    /// <exception cref="IOException">The input stream cannot be read.</exception>
    /// <exception cref="ObjectDisposedException">The input stream has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    public static async Task<WorldJsonLinesValidationResult> ValidateAsync(
        WorldArtifactManifest artifact,
        Stream input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var verification = await VerifyCoreAsync(artifact, input, cancellationToken).ConfigureAwait(false);
            return new(verification, DocumentValidationResult.Valid);
        }
        catch (WorldJsonLinesVerificationException exception)
        {
            return new(verification: null, exception.Validation);
        }
    }

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
    /// <exception cref="IOException">The input stream cannot be read.</exception>
    /// <exception cref="ObjectDisposedException">The input stream has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    public static Task<WorldJsonLinesVerificationResult> VerifyAsync(
        WorldArtifactManifest artifact,
        Stream input,
        CancellationToken cancellationToken = default) =>
        VerifyCoreAsync(artifact, input, cancellationToken);

    static async Task<WorldJsonLinesVerificationResult> VerifyCoreAsync(
        WorldArtifactManifest artifact,
        Stream input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("A world JSON Lines verification stream must be readable.", nameof(input));
        }

        WorldProvisioner.RequireReferenceCompatibility(artifact);

        var world = artifact.World.Compile();
        using StreamReader reader = new(
            input,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: -1,
            leaveOpen: true);
        string? targetId = null;
        WorldProvisioningRunId? runId = null;
        int? batchSize = null;
        var populationIndex = 0;
        long expectedSequenceIndex = 0;
        long itemCount = 0;
        long lineNumber = 0;
        ArrayBufferWriter<byte> expectedObservation = new();

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DecoderFallbackException exception)
            {
                throw Failure(
                    lineNumber + 1,
                    propertyName: null,
                    WorldJsonLinesV3Codec.JsonInvalidCode,
                    "The record is not valid UTF-8.",
                    exception);
            }
            if (line is null)
            {
                break;
            }

            lineNumber++;
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                throw Failure(
                    lineNumber,
                    propertyName: null,
                    WorldJsonLinesV3Codec.RecordInvalidCode,
                    "Blank records are not part of the v3 contract.");
            }

            while (populationIndex < world.Populations.Length
                   && expectedSequenceIndex >= world.Populations[populationIndex].Definition.Count)
            {
                populationIndex++;
                expectedSequenceIndex = 0;
            }
            if (populationIndex >= world.Populations.Length)
            {
                throw Failure(
                    lineNumber,
                    propertyName: null,
                    ItemUnexpectedCode,
                    "The stream contains more items than the manifest.");
            }

            WorldJsonLinesV3Record record;
            try
            {
                record = WorldJsonLinesV3Codec.ReadRecord(line);
            }
            catch (WorldJsonLinesV3CodecException exception)
            {
                throw Failure(
                    lineNumber,
                    exception.PropertyName,
                    exception.Code,
                    exception.Message,
                    exception);
            }
            if (!WorldJsonLinesV3Codec.HasCanonicalEncoding(line, record))
            {
                throw Failure(
                    lineNumber,
                    propertyName: null,
                    WorldJsonLinesV3Codec.WireNonCanonicalCode,
                    "The record differs from its unique canonical v3 wire representation.");
            }

            var population = world.Populations[populationIndex];
            var generation = population.GenerationPlan;

            RequireEqual(
                record.Format,
                WorldJsonLinesV3Codec.Format,
                lineNumber,
                WorldJsonLinesV3Codec.FormatProperty,
                FormatUnsupportedCode);
            RequireIdentity(record.TargetId, lineNumber, WorldJsonLinesV3Codec.TargetIdProperty);
            RequirePositive(record.BatchSize, lineNumber, WorldJsonLinesV3Codec.BatchSizeProperty);
            var expectedRunId = WorldProvisioningIdentityConvention.CreateRunId(
                artifact,
                record.TargetId,
                record.BatchSize);
            RequireEqual(
                record.RunId,
                expectedRunId.Value,
                lineNumber,
                WorldJsonLinesV3Codec.RunIdProperty,
                ProvisioningIdentityMismatchCode);
            targetId ??= record.TargetId;
            runId ??= expectedRunId;
            batchSize ??= record.BatchSize;
            RequireEqual(
                record.TargetId,
                targetId,
                lineNumber,
                WorldJsonLinesV3Codec.TargetIdProperty,
                ProvisioningIdentityMismatchCode);
            RequireEqual(
                expectedRunId,
                runId.Value,
                lineNumber,
                WorldJsonLinesV3Codec.RunIdProperty,
                ProvisioningIdentityMismatchCode);
            RequireEqual(
                record.BatchSize,
                batchSize.Value,
                lineNumber,
                WorldJsonLinesV3Codec.BatchSizeProperty,
                ProvisioningIdentityMismatchCode);

            RequireEqual(
                record.ArtifactManifestSchema,
                artifact.SchemaVersion,
                lineNumber,
                WorldJsonLinesV3Codec.ArtifactManifestSchemaProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.ArtifactId,
                artifact.ArtifactId.Value,
                lineNumber,
                WorldJsonLinesV3Codec.ArtifactIdProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.ArtifactManifestFingerprintAlgorithm,
                artifact.Fingerprint.Algorithm,
                lineNumber,
                WorldJsonLinesV3Codec.ArtifactManifestFingerprintAlgorithmProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.ArtifactManifestFingerprintCanonicalization,
                artifact.Fingerprint.Canonicalization,
                lineNumber,
                WorldJsonLinesV3Codec.ArtifactManifestFingerprintCanonicalizationProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.ArtifactManifestFingerprint,
                artifact.Fingerprint.Value,
                lineNumber,
                WorldJsonLinesV3Codec.ArtifactManifestFingerprintProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldId,
                artifact.World.Definition.Id,
                lineNumber,
                WorldJsonLinesV3Codec.WorldIdProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldRevision,
                artifact.World.Definition.Revision,
                lineNumber,
                WorldJsonLinesV3Codec.WorldRevisionProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldFingerprintAlgorithm,
                artifact.World.Fingerprint.Algorithm,
                lineNumber,
                WorldJsonLinesV3Codec.WorldFingerprintAlgorithmProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldFingerprintCanonicalization,
                artifact.World.Fingerprint.Canonicalization,
                lineNumber,
                WorldJsonLinesV3Codec.WorldFingerprintCanonicalizationProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldFingerprint,
                artifact.World.Fingerprint.Value,
                lineNumber,
                WorldJsonLinesV3Codec.WorldFingerprintProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.RootSeed,
                artifact.RootSeed,
                lineNumber,
                WorldJsonLinesV3Codec.RootSeedProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.PopulationId,
                population.Definition.Id,
                lineNumber,
                WorldJsonLinesV3Codec.PopulationIdProperty,
                PopulationMismatchCode);
            RequireEqual(
                record.PopulationCount,
                population.Definition.Count,
                lineNumber,
                WorldJsonLinesV3Codec.PopulationCountProperty,
                PopulationMismatchCode);
            RequireEqual(
                record.PopulationScope,
                population.Scope.Value,
                lineNumber,
                WorldJsonLinesV3Codec.PopulationScopeProperty,
                PopulationMismatchCode);

            RequireEqual(
                record.SequenceIndex,
                expectedSequenceIndex,
                lineNumber,
                WorldJsonLinesV3Codec.SequenceIndexProperty,
                PopulationMismatchCode);
            var expectedBatchOrdinal = (int)(record.SequenceIndex / record.BatchSize);
            var expectedBatchStart = (long)expectedBatchOrdinal * record.BatchSize;
            var expectedBatchItemCount = (int)Math.Min(
                record.BatchSize,
                population.Definition.Count - expectedBatchStart);
            RequireEqual(
                record.BatchOrdinal,
                expectedBatchOrdinal,
                lineNumber,
                WorldJsonLinesV3Codec.BatchOrdinalProperty,
                ProvisioningIdentityMismatchCode);
            RequireEqual(
                record.BatchStartSequenceIndex,
                expectedBatchStart,
                lineNumber,
                WorldJsonLinesV3Codec.BatchStartSequenceIndexProperty,
                ProvisioningIdentityMismatchCode);
            RequireEqual(
                record.BatchItemCount,
                expectedBatchItemCount,
                lineNumber,
                WorldJsonLinesV3Codec.BatchItemCountProperty,
                ProvisioningIdentityMismatchCode);
            var expectedBatchId = WorldProvisioningIdentityConvention.CreateBatchId(
                expectedRunId,
                population.Definition.Id,
                population.Scope,
                expectedBatchOrdinal,
                expectedBatchStart,
                expectedBatchItemCount);
            RequireEqual(
                record.BatchId,
                expectedBatchId.Value,
                lineNumber,
                WorldJsonLinesV3Codec.BatchIdProperty,
                ProvisioningIdentityMismatchCode);

            ValidateExemplars(record.Exemplars, artifact, population.Definition.Id, record.SequenceIndex, lineNumber);
            RequireEqual(
                record.DefinitionId,
                generation.Definition.Id,
                lineNumber,
                WorldJsonLinesV3Codec.DefinitionIdProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.DefinitionRevision,
                generation.Definition.Revision,
                lineNumber,
                WorldJsonLinesV3Codec.DefinitionRevisionProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.DefinitionFingerprint,
                generation.Fingerprint,
                lineNumber,
                WorldJsonLinesV3Codec.DefinitionFingerprintProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.Interpreter,
                artifact.Interpreter,
                lineNumber,
                WorldJsonLinesV3Codec.InterpreterProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.EntropyAlgorithm,
                artifact.EntropyAlgorithm,
                lineNumber,
                WorldJsonLinesV3Codec.EntropyAlgorithmProperty,
                GenerationMismatchCode);

            GeneratedObservation replayed;
            try
            {
                var replay = GenerationReplayEvidence.ParseToken(record.ReplayToken);
                RequireEqual(
                    replay.RootSeed,
                    artifact.RootSeed,
                    lineNumber,
                    WorldJsonLinesV3Codec.ReplayTokenProperty,
                    ReplayInvalidCode);
                RequireEqual(
                    replay.Scope,
                    population.Scope,
                    lineNumber,
                    WorldJsonLinesV3Codec.ReplayTokenProperty,
                    ReplayInvalidCode);
                RequireEqual(
                    replay.SequenceIndex,
                    record.SequenceIndex,
                    lineNumber,
                    WorldJsonLinesV3Codec.ReplayTokenProperty,
                    ReplayInvalidCode);
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
                    WorldJsonLinesV3Codec.ReplayTokenProperty,
                    ReplayInvalidCode,
                    $"Replay evidence is invalid for the manifest: {exception.Message}",
                    exception);
            }

            expectedObservation.Clear();
            replayed.Observation.WriteCanonicalJson(expectedObservation);
            if (!record.ObservationUtf8.Span.SequenceEqual(expectedObservation.WrittenSpan))
            {
                throw Failure(
                    lineNumber,
                    WorldJsonLinesV3Codec.ObservationProperty,
                    ObservationMismatchCode,
                    "The observation does not equal the canonical deterministic replay from the manifest.");
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
                ItemMissingCode,
                $"The stream ended before population '{population.Definition.Id}' sequence index "
                + $"'{expectedSequenceIndex}'.");
        }

        return new(artifact.ArtifactId, targetId, runId, batchSize, itemCount);
    }

    static void ValidateExemplars(
        ImmutableArray<string> actual,
        WorldArtifactManifest artifact,
        string populationId,
        long sequenceIndex,
        long lineNumber)
    {
        var actualIndex = 0;
        foreach (var exemplar in artifact.Exemplars)
        {
            if (!string.Equals(exemplar.PopulationId, populationId, StringComparison.Ordinal)
                || exemplar.SequenceIndex != sequenceIndex)
            {
                continue;
            }
            if (actualIndex >= actual.Length
                || !string.Equals(actual[actualIndex], exemplar.Id, StringComparison.Ordinal))
            {
                throw Failure(
                    lineNumber,
                    WorldJsonLinesV3Codec.ExemplarsProperty,
                    PopulationMismatchCode,
                    $"Expected exemplar '{exemplar.Id}'.");
            }
            actualIndex++;
        }

        if (actualIndex != actual.Length)
        {
            throw Failure(
                lineNumber,
                WorldJsonLinesV3Codec.ExemplarsProperty,
                PopulationMismatchCode,
                "The record contains an undeclared exemplar.");
        }
    }

    static void RequireIdentity(string value, long lineNumber, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Failure(
                lineNumber,
                propertyName,
                ProvisioningIdentityMismatchCode,
                "The identity cannot be empty or white-space.");
        }
    }

    static void RequirePositive(int value, long lineNumber, string propertyName)
    {
        if (value <= 0)
        {
            throw Failure(
                lineNumber,
                propertyName,
                ProvisioningIdentityMismatchCode,
                "The value must be positive.");
        }
    }

    static void RequireEqual<T>(
        T actual,
        T expected,
        long lineNumber,
        string propertyName,
        string code)
        where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
        {
            throw Failure(
                lineNumber,
                propertyName,
                code,
                $"Expected '{expected}' but found '{actual}'.");
        }
    }

    static WorldJsonLinesVerificationException Failure(
        long lineNumber,
        string? propertyName,
        string code,
        string detail,
        Exception? innerException = null) =>
        new(lineNumber, propertyName, code, detail, innerException);
}
