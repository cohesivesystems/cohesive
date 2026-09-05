using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Worlds;

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

/// <summary>Independently verifies a complete v4 world JSON Lines stream against an exact artifact manifest.</summary>
/// <remarks>
/// Verification is bounded to one JSON record and one regenerated observation at a time. Success proves exact item
/// count and order, canonical v4 record bytes, artifact and world provenance, target and batching identity, canonical
/// entity identity, exemplar aliases, replay evidence, and canonical observation bytes. No generated item is exposed
/// before the complete stream has passed verification.
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
    const string EntityIdentityMismatchCode = "simulation.worldArtifact.jsonLines.entityIdentityMismatch";
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
        ArgumentNullException.ThrowIfNull(artifact);
        WorldProvisioner.RequireReferenceCompatibility(artifact);
        var plan = WorldProvisioner.CreateReferencePlan(artifact.GetCoreWorld().Compile(), artifact);
        return await ValidateAsync(plan, input, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<WorldJsonLinesValidationResult> ValidateAsync(
        WorldArtifactInterpreterPlan plan,
        Stream input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var verification = await VerifyCoreAsync(plan, input, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        WorldProvisioner.RequireReferenceCompatibility(artifact);
        return VerifyCoreAsync(
            WorldProvisioner.CreateReferencePlan(artifact.GetCoreWorld().Compile(), artifact),
            input,
            cancellationToken);
    }

    internal static Task<WorldJsonLinesVerificationResult> VerifyAsync(
        WorldArtifactInterpreterPlan plan,
        Stream input,
        CancellationToken cancellationToken = default) =>
        VerifyCoreAsync(plan, input, cancellationToken);

    static async Task<WorldJsonLinesVerificationResult> VerifyCoreAsync(
        WorldArtifactInterpreterPlan plan,
        Stream input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("A world JSON Lines verification stream must be readable.", nameof(input));
        }

        var artifact = plan.Artifact;
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
        var enumeratedPopulationIndex = -1;
        IEnumerator<WorldProvisioningItem>? expectedItems = null;
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
                    WorldJsonLinesCodec.JsonInvalidCode,
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
                    WorldJsonLinesCodec.RecordInvalidCode,
                    "Blank records are not part of the v4 contract.");
            }

            while (populationIndex < plan.Populations.Length
                   && expectedSequenceIndex >= plan.Populations[populationIndex].Count)
            {
                expectedItems?.Dispose();
                expectedItems = null;
                populationIndex++;
                expectedSequenceIndex = 0;
            }
            if (populationIndex >= plan.Populations.Length)
            {
                throw Failure(
                    lineNumber,
                    propertyName: null,
                    ItemUnexpectedCode,
                    "The stream contains more items than the manifest.");
            }

            WorldJsonLinesRecord record;
            try
            {
                record = WorldJsonLinesCodec.ReadRecord(line);
            }
            catch (WorldJsonLinesCodecException exception)
            {
                throw Failure(
                    lineNumber,
                    exception.PropertyName,
                    exception.Code,
                    exception.Message,
                    exception);
            }
            if (!WorldJsonLinesCodec.HasCanonicalEncoding(line, record))
            {
                throw Failure(
                    lineNumber,
                    propertyName: null,
                    WorldJsonLinesCodec.WireNonCanonicalCode,
                    "The record differs from its unique canonical v4 wire representation.");
            }

            var population = plan.Populations[populationIndex];
            var generation = artifact.Populations[populationIndex];
            if (enumeratedPopulationIndex != populationIndex)
            {
                enumeratedPopulationIndex = populationIndex;
                expectedItems = population.Enumerate(artifact.RootSeed).GetEnumerator();
            }

            RequireEqual(
                record.Format,
                WorldJsonLinesCodec.Format,
                lineNumber,
                WorldJsonLinesCodec.FormatProperty,
                FormatUnsupportedCode);
            RequireIdentity(record.TargetId, lineNumber, WorldJsonLinesCodec.TargetIdProperty);
            RequirePositive(record.BatchSize, lineNumber, WorldJsonLinesCodec.BatchSizeProperty);
            var expectedRunId = WorldProvisioningIdentityConvention.CreateRunId(
                artifact,
                record.TargetId,
                record.BatchSize);
            RequireEqual(
                record.RunId,
                expectedRunId.Value,
                lineNumber,
                WorldJsonLinesCodec.RunIdProperty,
                ProvisioningIdentityMismatchCode);
            targetId ??= record.TargetId;
            runId ??= expectedRunId;
            batchSize ??= record.BatchSize;
            RequireEqual(
                record.TargetId,
                targetId,
                lineNumber,
                WorldJsonLinesCodec.TargetIdProperty,
                ProvisioningIdentityMismatchCode);
            RequireEqual(
                expectedRunId,
                runId.Value,
                lineNumber,
                WorldJsonLinesCodec.RunIdProperty,
                ProvisioningIdentityMismatchCode);
            RequireEqual(
                record.BatchSize,
                batchSize.Value,
                lineNumber,
                WorldJsonLinesCodec.BatchSizeProperty,
                ProvisioningIdentityMismatchCode);

            RequireEqual(
                record.ArtifactManifestSchema,
                artifact.SchemaVersion,
                lineNumber,
                WorldJsonLinesCodec.ArtifactManifestSchemaProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.ArtifactId,
                artifact.ArtifactId.Value,
                lineNumber,
                WorldJsonLinesCodec.ArtifactIdProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.ArtifactManifestFingerprintAlgorithm,
                artifact.Fingerprint.Algorithm,
                lineNumber,
                WorldJsonLinesCodec.ArtifactManifestFingerprintAlgorithmProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.ArtifactManifestFingerprintCanonicalization,
                artifact.Fingerprint.Canonicalization,
                lineNumber,
                WorldJsonLinesCodec.ArtifactManifestFingerprintCanonicalizationProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.ArtifactManifestFingerprint,
                artifact.Fingerprint.Value,
                lineNumber,
                WorldJsonLinesCodec.ArtifactManifestFingerprintProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldId,
                artifact.World.Id,
                lineNumber,
                WorldJsonLinesCodec.WorldIdProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldRevision,
                artifact.World.Revision,
                lineNumber,
                WorldJsonLinesCodec.WorldRevisionProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldFingerprintAlgorithm,
                artifact.World.Fingerprint.Algorithm,
                lineNumber,
                WorldJsonLinesCodec.WorldFingerprintAlgorithmProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldFingerprintCanonicalization,
                artifact.World.Fingerprint.Canonicalization,
                lineNumber,
                WorldJsonLinesCodec.WorldFingerprintCanonicalizationProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.WorldFingerprint,
                artifact.World.Fingerprint.Value,
                lineNumber,
                WorldJsonLinesCodec.WorldFingerprintProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.RootSeed,
                artifact.RootSeed,
                lineNumber,
                WorldJsonLinesCodec.RootSeedProperty,
                ArtifactMismatchCode);
            RequireEqual(
                record.PopulationId,
                population.Id,
                lineNumber,
                WorldJsonLinesCodec.PopulationIdProperty,
                PopulationMismatchCode);
            RequireEqual(
                record.PopulationCount,
                population.Count,
                lineNumber,
                WorldJsonLinesCodec.PopulationCountProperty,
                PopulationMismatchCode);
            RequireEqual(
                record.PopulationScope,
                population.Scope.Value,
                lineNumber,
                WorldJsonLinesCodec.PopulationScopeProperty,
                PopulationMismatchCode);

            RequireEqual(
                record.SequenceIndex,
                expectedSequenceIndex,
                lineNumber,
                WorldJsonLinesCodec.SequenceIndexProperty,
                PopulationMismatchCode);
            var expectedBatchOrdinal = (int)(record.SequenceIndex / record.BatchSize);
            var expectedBatchStart = (long)expectedBatchOrdinal * record.BatchSize;
            var expectedBatchItemCount = (int)Math.Min(
                record.BatchSize,
                population.Count - expectedBatchStart);
            RequireEqual(
                record.BatchOrdinal,
                expectedBatchOrdinal,
                lineNumber,
                WorldJsonLinesCodec.BatchOrdinalProperty,
                ProvisioningIdentityMismatchCode);
            RequireEqual(
                record.BatchStartSequenceIndex,
                expectedBatchStart,
                lineNumber,
                WorldJsonLinesCodec.BatchStartSequenceIndexProperty,
                ProvisioningIdentityMismatchCode);
            RequireEqual(
                record.BatchItemCount,
                expectedBatchItemCount,
                lineNumber,
                WorldJsonLinesCodec.BatchItemCountProperty,
                ProvisioningIdentityMismatchCode);
            var expectedBatchId = WorldProvisioningIdentityConvention.CreateBatchId(
                expectedRunId,
                population.Id,
                population.Scope,
                expectedBatchOrdinal,
                expectedBatchStart,
                expectedBatchItemCount);
            RequireEqual(
                record.BatchId,
                expectedBatchId.Value,
                lineNumber,
                WorldJsonLinesCodec.BatchIdProperty,
                ProvisioningIdentityMismatchCode);

            ValidateExemplars(record.Exemplars, artifact, population.Id, record.SequenceIndex, lineNumber);
            RequireEqual(
                record.DefinitionId,
                generation.GenerationId,
                lineNumber,
                WorldJsonLinesCodec.DefinitionIdProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.DefinitionRevision,
                generation.GenerationRevision,
                lineNumber,
                WorldJsonLinesCodec.DefinitionRevisionProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.DefinitionFingerprint,
                generation.GenerationFingerprint.Value,
                lineNumber,
                WorldJsonLinesCodec.DefinitionFingerprintProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.Interpreter,
                artifact.Interpreter,
                lineNumber,
                WorldJsonLinesCodec.InterpreterProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.EntropyAlgorithm,
                artifact.EntropyAlgorithm,
                lineNumber,
                WorldJsonLinesCodec.EntropyAlgorithmProperty,
                GenerationMismatchCode);

            WorldProvisioningItem expectedItem;
            try
            {
                if (expectedItems is null || !expectedItems.MoveNext())
                {
                    throw Failure(
                        lineNumber,
                        propertyName: null,
                        ItemMissingCode,
                        $"Interpreter population '{population.Id}' ended before sequence index "
                        + $"'{record.SequenceIndex}'.");
                }

                expectedItem = expectedItems.Current;
            }
            catch (WorldJsonLinesVerificationException)
            {
                throw;
            }
            catch (Exception exception) when (exception is WorldGenerationException or FormatException or ArgumentException)
            {
                throw Failure(
                    lineNumber,
                    WorldJsonLinesCodec.ReplayTokenProperty,
                    ReplayInvalidCode,
                    $"The manifest interpreter could not replay the expected item: {exception.Message}",
                    exception);
            }

            RequireEqual(
                record.SequenceIndex,
                expectedItem.SequenceIndex,
                lineNumber,
                WorldJsonLinesCodec.SequenceIndexProperty,
                PopulationMismatchCode);
            RequireEqual(
                record.DefinitionId,
                expectedItem.DefinitionId,
                lineNumber,
                WorldJsonLinesCodec.DefinitionIdProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.DefinitionRevision,
                expectedItem.DefinitionRevision,
                lineNumber,
                WorldJsonLinesCodec.DefinitionRevisionProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.DefinitionFingerprint,
                expectedItem.DefinitionFingerprint,
                lineNumber,
                WorldJsonLinesCodec.DefinitionFingerprintProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.Interpreter,
                expectedItem.Interpreter,
                lineNumber,
                WorldJsonLinesCodec.InterpreterProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.EntropyAlgorithm,
                expectedItem.EntropyAlgorithm,
                lineNumber,
                WorldJsonLinesCodec.EntropyAlgorithmProperty,
                GenerationMismatchCode);
            RequireEqual(
                record.ReplayToken,
                expectedItem.ReplayToken,
                lineNumber,
                WorldJsonLinesCodec.ReplayTokenProperty,
                ReplayInvalidCode);

            expectedObservation.Clear();
            expectedItem.Observation.WriteCanonicalJson(expectedObservation);
            if (!record.ObservationUtf8.Span.SequenceEqual(expectedObservation.WrittenSpan))
            {
                throw Failure(
                    lineNumber,
                    WorldJsonLinesCodec.ObservationProperty,
                    ObservationMismatchCode,
                    "The observation does not equal the canonical deterministic replay from the manifest.");
            }

            RequireEqual(
                record.EntityId,
                expectedItem.EntityId.Value,
                lineNumber,
                WorldJsonLinesCodec.EntityIdProperty,
                EntityIdentityMismatchCode);

            expectedSequenceIndex++;
            itemCount++;
        }

        expectedItems?.Dispose();
        while (populationIndex < plan.Populations.Length
               && expectedSequenceIndex >= plan.Populations[populationIndex].Count)
        {
            populationIndex++;
            expectedSequenceIndex = 0;
        }
        if (populationIndex < plan.Populations.Length)
        {
            var population = plan.Populations[populationIndex];
            throw Failure(
                lineNumber + 1,
                propertyName: null,
                ItemMissingCode,
                $"The stream ended before population '{population.Id}' sequence index "
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
                    WorldJsonLinesCodec.ExemplarsProperty,
                    PopulationMismatchCode,
                    $"Expected exemplar '{exemplar.Id}'.");
            }
            actualIndex++;
        }

        if (actualIndex != actual.Length)
        {
            throw Failure(
                lineNumber,
                WorldJsonLinesCodec.ExemplarsProperty,
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
