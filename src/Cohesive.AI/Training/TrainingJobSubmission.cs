using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cohesive.AI.Training;

/// <summary>
/// Binds one stable logical submission identity to an immutable snapshot of an exact training request.
/// </summary>
/// <remarks>
/// The submission identity is the authority for idempotent provider submission. Reusing an identity with a
/// request whose <see cref="RequestFingerprint"/> differs is a conflict rather than a second submission.
/// Dataset order is excluded from the fingerprint because dataset names are provider binding identities;
/// all other request values, including provider configuration text, are preserved exactly.
/// </remarks>
public sealed record TrainingJobSubmission
{
    /// <summary>The current canonical fingerprint algorithm identifier.</summary>
    public const string RequestFingerprintAlgorithm = "sha256-v1";

    /// <summary>
    /// Initializes a stable training-job submission.
    /// </summary>
    /// <param name="submissionId">Stable logical identity supplied by the submitting workflow.</param>
    /// <param name="request">Exact provider-neutral training request bound to the identity.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="submissionId"/> is empty or when dataset binding names are empty or duplicated.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="request"/>, its dataset collection, or one of its datasets is <see langword="null"/>.
    /// </exception>
    public TrainingJobSubmission(string submissionId, TrainingRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Datasets);

        var datasets = SnapshotDatasets(request.Datasets);
        SubmissionId = submissionId;
        Request = request with { Datasets = datasets };
        RequestFingerprint = CreateRequestFingerprint(Request, datasets);
    }

    /// <summary>Stable logical identity supplied by the submitting workflow.</summary>
    public string SubmissionId { get; }

    /// <summary>Immutable snapshot of the exact request bound to <see cref="SubmissionId"/>.</summary>
    public TrainingRequest Request { get; }

    /// <summary>
    /// Versioned deterministic fingerprint of <see cref="Request"/> used to reject conflicting identity reuse.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RequestFingerprint { get; }

    static ImmutableArray<TrainingDatasetArtifact> SnapshotDatasets(
        IReadOnlyList<TrainingDatasetArtifact> source)
    {
        var builder = ImmutableArray.CreateBuilder<TrainingDatasetArtifact>(source.Count);
        var names = new HashSet<string>(capacity: source.Count, comparer: StringComparer.Ordinal);

        for (var i = 0; i < source.Count; i++)
        {
            var dataset = source[i]
                ?? throw new ArgumentNullException(nameof(source), $"Dataset at index {i} cannot be null.");
            ArgumentException.ThrowIfNullOrWhiteSpace(dataset.Name);
            if (!names.Add(dataset.Name))
            {
                throw new ArgumentException(
                    $"Training request contains duplicate dataset binding name '{dataset.Name}'.",
                    nameof(source));
            }

            builder.Add(dataset);
        }

        return builder.MoveToImmutable();
    }

    static string CreateRequestFingerprint(
        TrainingRequest request,
        ImmutableArray<TrainingDatasetArtifact> datasets)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("modelName", request.ModelName);
            WriteOptionalString(writer, "baseVersion", request.BaseVersion);

            writer.WriteStartArray("datasets");
            foreach (var dataset in datasets.OrderBy(static dataset => dataset.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", dataset.Name);
                writer.WriteString("location", dataset.Location);
                writer.WriteNumber("kind", (byte)dataset.Kind);
                WriteOptionalString(writer, "format", dataset.Format);
                WriteOptionalString(writer, "schemaHash", dataset.SchemaHash);
                if (dataset.RowCount is { } rowCount)
                    writer.WriteNumber("rowCount", rowCount);
                else
                    writer.WriteNull("rowCount");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (request.Code is { } code)
            {
                writer.WriteStartObject("code");
                writer.WriteString("blobUri", code.BlobUri);
                writer.WriteString("version", code.Version);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("code");
            }

            writer.WriteString("outputModelName", request.OutputModelName);
            WriteOptionalString(writer, "experimentName", request.ExperimentName);
            WriteOptionalString(writer, "computeTarget", request.ComputeTarget);
            writer.WriteString("configJson", request.ConfigJson);
            writer.WriteEndObject();
        }

        var hash = SHA256.HashData(buffer.WrittenSpan);
        return $"{RequestFingerprintAlgorithm}:{Convert.ToHexStringLower(hash)}";
    }

    static void WriteOptionalString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
            writer.WriteNull(propertyName);
        else
            writer.WriteString(propertyName, value);
    }
}
