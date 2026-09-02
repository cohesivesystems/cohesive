using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Cohesive.Simulation.Provisioning;

/// <summary>Writes deterministic generated world items as UTF-8 newline-delimited JSON.</summary>
/// <remarks>
/// This reference sink is intended for scripts, test harnesses, and Playwright setup. Every line is one independently
/// parseable generated item carrying exact artifact, run, batch, world, population, and replay provenance. The caller
/// owns the stream, which is flushed but never closed. A successful receipt means the complete encoded batch was
/// written and flushed; an exception may leave a partial batch in the stream. The sink does not deduplicate repeated
/// batch IDs.
/// </remarks>
public sealed class WorldJsonLinesSink : IWorldProvisioningSink
{
    /// <summary>Stable identity of the emitted JSON Lines record format.</summary>
    public const string Format = "cohesive-simulation-world-item/v3";

    readonly Stream output;

    /// <summary>Creates a deterministic JSON Lines sink.</summary>
    /// <param name="targetId">Stable logical identity of this output target.</param>
    /// <param name="output">Writable caller-owned stream receiving UTF-8 JSON Lines.</param>
    /// <exception cref="ArgumentException"><paramref name="targetId"/> is empty or white-space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="output"/> is not writable.</exception>
    public WorldJsonLinesSink(string targetId, Stream output)
    {
        TargetId = Guard.RequireNotNullOrWhiteSpace(targetId);
        this.output = Guard.RequireNotNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("A world JSON Lines output stream must be writable.", nameof(output));
    }

    /// <inheritdoc />
    public string TargetId { get; }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="batch"/> names another logical target.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> requests cancellation.</exception>
    /// <exception cref="IOException">The output stream cannot accept or flush the complete encoded batch.</exception>
    /// <exception cref="ObjectDisposedException">The output stream has been disposed.</exception>
    public async ValueTask<WorldProvisioningBatchReceipt> CommitAsync(
        WorldProvisioningBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!string.Equals(TargetId, batch.TargetId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"JSON Lines sink target '{TargetId}' cannot commit batch target '{batch.TargetId}'.",
                nameof(batch));
        }
        cancellationToken.ThrowIfCancellationRequested();
        ArrayBufferWriter<byte> encodedBatch = new();
        ArrayBufferWriter<byte> encodedObservation = new();

        foreach (var item in batch.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            encodedObservation.Clear();
            item.Observation.WriteCanonicalJson(encodedObservation);
            using (var writer = new Utf8JsonWriter(encodedBatch))
            {
                writer.WriteStartObject();
                writer.WriteString("format", Format);
                writer.WriteString("runId", batch.RunId.Value);
                writer.WriteString("batchId", batch.Id.Value);
                writer.WriteString("targetId", batch.TargetId);
                writer.WriteString("artifactManifestSchema", batch.Artifact.SchemaVersion);
                writer.WriteString("artifactId", batch.ArtifactId.Value);
                writer.WriteString("artifactManifestFingerprintAlgorithm", batch.Artifact.Fingerprint.Algorithm);
                writer.WriteString(
                    "artifactManifestFingerprintCanonicalization",
                    batch.Artifact.Fingerprint.Canonicalization);
                writer.WriteString("artifactManifestFingerprint", batch.Artifact.Fingerprint.Value);
                writer.WriteString("worldId", batch.WorldId);
                writer.WriteString("worldRevision", batch.WorldRevision);
                writer.WriteString("worldFingerprintAlgorithm", batch.WorldFingerprintAlgorithm);
                writer.WriteString("worldFingerprintCanonicalization", batch.WorldFingerprintCanonicalization);
                writer.WriteString("worldFingerprint", batch.WorldFingerprint);
                writer.WriteString("rootSeed", batch.RootSeed.ToString(CultureInfo.InvariantCulture));
                writer.WriteString("populationId", batch.PopulationId);
                writer.WriteNumber("populationCount", batch.PopulationCount);
                writer.WriteString("populationScope", batch.PopulationScope.Value);
                writer.WriteNumber("batchOrdinal", batch.Ordinal);
                writer.WriteNumber("sequenceIndex", item.Replay.SequenceIndex);
                writer.WriteStartArray("exemplars");
                foreach (var exemplar in batch.Exemplars)
                {
                    if (exemplar.SequenceIndex == item.Replay.SequenceIndex)
                        writer.WriteStringValue(exemplar.Id);
                }

                writer.WriteEndArray();
                writer.WriteString("definitionId", item.Replay.DefinitionId);
                writer.WriteString("definitionRevision", item.Replay.DefinitionRevision);
                writer.WriteString("definitionFingerprint", item.Replay.DefinitionFingerprint);
                writer.WriteString("interpreter", item.Replay.Interpreter);
                writer.WriteString("entropyAlgorithm", item.Replay.EntropyAlgorithm);
                writer.WriteString("replayToken", item.Replay.ToToken());
                writer.WritePropertyName("observation");
                writer.WriteRawValue(encodedObservation.WrittenSpan, skipInputValidation: true);
                writer.WriteEndObject();
            }

            var newline = encodedBatch.GetSpan(1);
            newline[0] = (byte)'\n';
            encodedBatch.Advance(1);
        }

        await output.WriteAsync(encodedBatch.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new(batch.Id, WorldProvisioningBatchDisposition.Committed);
    }
}
