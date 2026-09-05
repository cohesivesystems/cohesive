using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cohesive.Simulation.Provisioning;

internal readonly record struct WorldJsonLinesRecord(
    string Format,
    string RunId,
    string BatchId,
    string TargetId,
    string ArtifactManifestSchema,
    string ArtifactId,
    string ArtifactManifestFingerprintAlgorithm,
    string ArtifactManifestFingerprintCanonicalization,
    string ArtifactManifestFingerprint,
    string WorldId,
    string WorldRevision,
    string WorldFingerprintAlgorithm,
    string WorldFingerprintCanonicalization,
    string WorldFingerprint,
    long RootSeed,
    string PopulationId,
    int PopulationCount,
    string PopulationScope,
    int BatchSize,
    int BatchOrdinal,
    long BatchStartSequenceIndex,
    int BatchItemCount,
    long SequenceIndex,
    string EntityId,
    ImmutableArray<string> Exemplars,
    string DefinitionId,
    string DefinitionRevision,
    string DefinitionFingerprint,
    string Interpreter,
    string EntropyAlgorithm,
    string ReplayToken,
    ReadOnlyMemory<byte> ObservationUtf8);

internal sealed class WorldJsonLinesCodecException(
    string code,
    string? propertyName,
    string detail,
    Exception? innerException = null)
    : FormatException(detail, innerException)
{
    public string Code { get; } = code;

    public string? PropertyName { get; } = propertyName;
}

internal static class WorldJsonLinesCodec
{
    public const string Format = "cohesive-simulation-world-item/v4";

    public const string FormatProperty = "format";
    public const string RunIdProperty = "runId";
    public const string BatchIdProperty = "batchId";
    public const string TargetIdProperty = "targetId";
    public const string ArtifactManifestSchemaProperty = "artifactManifestSchema";
    public const string ArtifactIdProperty = "artifactId";
    public const string ArtifactManifestFingerprintAlgorithmProperty = "artifactManifestFingerprintAlgorithm";
    public const string ArtifactManifestFingerprintCanonicalizationProperty =
        "artifactManifestFingerprintCanonicalization";
    public const string ArtifactManifestFingerprintProperty = "artifactManifestFingerprint";
    public const string WorldIdProperty = "worldId";
    public const string WorldRevisionProperty = "worldRevision";
    public const string WorldFingerprintAlgorithmProperty = "worldFingerprintAlgorithm";
    public const string WorldFingerprintCanonicalizationProperty = "worldFingerprintCanonicalization";
    public const string WorldFingerprintProperty = "worldFingerprint";
    public const string RootSeedProperty = "rootSeed";
    public const string PopulationIdProperty = "populationId";
    public const string PopulationCountProperty = "populationCount";
    public const string PopulationScopeProperty = "populationScope";
    public const string BatchSizeProperty = "batchSize";
    public const string BatchOrdinalProperty = "batchOrdinal";
    public const string BatchStartSequenceIndexProperty = "batchStartSequenceIndex";
    public const string BatchItemCountProperty = "batchItemCount";
    public const string SequenceIndexProperty = "sequenceIndex";
    public const string EntityIdProperty = "entityId";
    public const string ExemplarsProperty = "exemplars";
    public const string DefinitionIdProperty = "definitionId";
    public const string DefinitionRevisionProperty = "definitionRevision";
    public const string DefinitionFingerprintProperty = "definitionFingerprint";
    public const string InterpreterProperty = "interpreter";
    public const string EntropyAlgorithmProperty = "entropyAlgorithm";
    public const string ReplayTokenProperty = "replayToken";
    public const string ObservationProperty = "observation";

    public const string JsonInvalidCode = "simulation.worldArtifact.jsonLines.jsonInvalid";
    public const string RecordInvalidCode = "simulation.worldArtifact.jsonLines.recordInvalid";
    public const string PropertyUnknownCode = "simulation.worldArtifact.jsonLines.propertyUnknown";
    public const string PropertyDuplicateCode = "simulation.worldArtifact.jsonLines.propertyDuplicate";
    public const string PropertyOrderInvalidCode = "simulation.worldArtifact.jsonLines.propertyOrderInvalid";
    public const string PropertyMissingCode = "simulation.worldArtifact.jsonLines.propertyMissing";
    public const string WireNonCanonicalCode = "simulation.worldArtifact.jsonLines.wireNonCanonical";

    static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    static readonly string[] PropertyOrder =
    [
        FormatProperty,
        RunIdProperty,
        BatchIdProperty,
        TargetIdProperty,
        ArtifactManifestSchemaProperty,
        ArtifactIdProperty,
        ArtifactManifestFingerprintAlgorithmProperty,
        ArtifactManifestFingerprintCanonicalizationProperty,
        ArtifactManifestFingerprintProperty,
        WorldIdProperty,
        WorldRevisionProperty,
        WorldFingerprintAlgorithmProperty,
        WorldFingerprintCanonicalizationProperty,
        WorldFingerprintProperty,
        RootSeedProperty,
        PopulationIdProperty,
        PopulationCountProperty,
        PopulationScopeProperty,
        BatchSizeProperty,
        BatchOrdinalProperty,
        BatchStartSequenceIndexProperty,
        BatchItemCountProperty,
        SequenceIndexProperty,
        EntityIdProperty,
        ExemplarsProperty,
        DefinitionIdProperty,
        DefinitionRevisionProperty,
        DefinitionFingerprintProperty,
        InterpreterProperty,
        EntropyAlgorithmProperty,
        ReplayTokenProperty,
        ObservationProperty
    ];

    public static void WriteRecord(
        IBufferWriter<byte> output,
        WorldProvisioningBatch batch,
        WorldProvisioningItem item,
        ReadOnlyMemory<byte> observationUtf8)
    {
        ImmutableArray<string> exemplarIds;
        if (batch.Exemplars.IsDefaultOrEmpty)
        {
            exemplarIds = [];
        }
        else
        {
            var exemplars = ImmutableArray.CreateBuilder<string>();
            foreach (var exemplar in batch.Exemplars)
            {
                if (exemplar.SequenceIndex == item.SequenceIndex)
                {
                    exemplars.Add(exemplar.Id);
                }
            }
            exemplarIds = exemplars.ToImmutable();
        }

        WriteRecord(
            output,
            new(
                Format: Format,
                RunId: batch.RunId.Value,
                BatchId: batch.Id.Value,
                TargetId: batch.TargetId,
                ArtifactManifestSchema: batch.Artifact.SchemaVersion,
                ArtifactId: batch.ArtifactId.Value,
                ArtifactManifestFingerprintAlgorithm: batch.Artifact.Fingerprint.Algorithm,
                ArtifactManifestFingerprintCanonicalization: batch.Artifact.Fingerprint.Canonicalization,
                ArtifactManifestFingerprint: batch.Artifact.Fingerprint.Value,
                WorldId: batch.WorldId,
                WorldRevision: batch.WorldRevision,
                WorldFingerprintAlgorithm: batch.WorldFingerprintAlgorithm,
                WorldFingerprintCanonicalization: batch.WorldFingerprintCanonicalization,
                WorldFingerprint: batch.WorldFingerprint,
                RootSeed: batch.RootSeed,
                PopulationId: batch.PopulationId,
                PopulationCount: batch.PopulationCount,
                PopulationScope: batch.PopulationScope.Value,
                BatchSize: batch.BatchSize,
                BatchOrdinal: batch.Ordinal,
                BatchStartSequenceIndex: batch.StartSequenceIndex,
                BatchItemCount: batch.Items.Length,
                SequenceIndex: item.SequenceIndex,
                EntityId: item.EntityId.Value,
                Exemplars: exemplarIds,
                DefinitionId: item.DefinitionId,
                DefinitionRevision: item.DefinitionRevision,
                DefinitionFingerprint: item.DefinitionFingerprint,
                Interpreter: item.Interpreter,
                EntropyAlgorithm: item.EntropyAlgorithm,
                ReplayToken: item.ReplayToken,
                ObservationUtf8: observationUtf8));
    }

    public static void WriteRecord(IBufferWriter<byte> output, in WorldJsonLinesRecord record)
    {
        using Utf8JsonWriter writer = new(output);
        writer.WriteStartObject();
        writer.WriteString(FormatProperty, record.Format);
        writer.WriteString(RunIdProperty, record.RunId);
        writer.WriteString(BatchIdProperty, record.BatchId);
        writer.WriteString(TargetIdProperty, record.TargetId);
        writer.WriteString(ArtifactManifestSchemaProperty, record.ArtifactManifestSchema);
        writer.WriteString(ArtifactIdProperty, record.ArtifactId);
        writer.WriteString(
            ArtifactManifestFingerprintAlgorithmProperty,
            record.ArtifactManifestFingerprintAlgorithm);
        writer.WriteString(
            ArtifactManifestFingerprintCanonicalizationProperty,
            record.ArtifactManifestFingerprintCanonicalization);
        writer.WriteString(ArtifactManifestFingerprintProperty, record.ArtifactManifestFingerprint);
        writer.WriteString(WorldIdProperty, record.WorldId);
        writer.WriteString(WorldRevisionProperty, record.WorldRevision);
        writer.WriteString(WorldFingerprintAlgorithmProperty, record.WorldFingerprintAlgorithm);
        writer.WriteString(WorldFingerprintCanonicalizationProperty, record.WorldFingerprintCanonicalization);
        writer.WriteString(WorldFingerprintProperty, record.WorldFingerprint);
        writer.WriteString(RootSeedProperty, record.RootSeed.ToString(CultureInfo.InvariantCulture));
        writer.WriteString(PopulationIdProperty, record.PopulationId);
        writer.WriteNumber(PopulationCountProperty, record.PopulationCount);
        writer.WriteString(PopulationScopeProperty, record.PopulationScope);
        writer.WriteNumber(BatchSizeProperty, record.BatchSize);
        writer.WriteNumber(BatchOrdinalProperty, record.BatchOrdinal);
        writer.WriteNumber(BatchStartSequenceIndexProperty, record.BatchStartSequenceIndex);
        writer.WriteNumber(BatchItemCountProperty, record.BatchItemCount);
        writer.WriteNumber(SequenceIndexProperty, record.SequenceIndex);
        writer.WriteString(EntityIdProperty, record.EntityId);
        writer.WriteStartArray(ExemplarsProperty);
        foreach (var exemplar in record.Exemplars)
        {
            writer.WriteStringValue(exemplar);
        }

        writer.WriteEndArray();
        writer.WriteString(DefinitionIdProperty, record.DefinitionId);
        writer.WriteString(DefinitionRevisionProperty, record.DefinitionRevision);
        writer.WriteString(DefinitionFingerprintProperty, record.DefinitionFingerprint);
        writer.WriteString(InterpreterProperty, record.Interpreter);
        writer.WriteString(EntropyAlgorithmProperty, record.EntropyAlgorithm);
        writer.WriteString(ReplayTokenProperty, record.ReplayToken);
        writer.WritePropertyName(ObservationProperty);
        writer.WriteRawValue(record.ObservationUtf8.Span, skipInputValidation: true);
        writer.WriteEndObject();
    }

    public static WorldJsonLinesRecord ReadRecord(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new WorldJsonLinesCodecException(
                JsonInvalidCode,
                propertyName: null,
                $"The record is not valid JSON: {exception.Message}",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            ValidatePropertyOrder(root);
            return new(
                Format: ReadString(root, FormatProperty),
                RunId: ReadString(root, RunIdProperty),
                BatchId: ReadString(root, BatchIdProperty),
                TargetId: ReadString(root, TargetIdProperty),
                ArtifactManifestSchema: ReadString(root, ArtifactManifestSchemaProperty),
                ArtifactId: ReadString(root, ArtifactIdProperty),
                ArtifactManifestFingerprintAlgorithm: ReadString(
                    root,
                    ArtifactManifestFingerprintAlgorithmProperty),
                ArtifactManifestFingerprintCanonicalization: ReadString(
                    root,
                    ArtifactManifestFingerprintCanonicalizationProperty),
                ArtifactManifestFingerprint: ReadString(root, ArtifactManifestFingerprintProperty),
                WorldId: ReadString(root, WorldIdProperty),
                WorldRevision: ReadString(root, WorldRevisionProperty),
                WorldFingerprintAlgorithm: ReadString(root, WorldFingerprintAlgorithmProperty),
                WorldFingerprintCanonicalization: ReadString(root, WorldFingerprintCanonicalizationProperty),
                WorldFingerprint: ReadString(root, WorldFingerprintProperty),
                RootSeed: ReadInt64String(root, RootSeedProperty),
                PopulationId: ReadString(root, PopulationIdProperty),
                PopulationCount: ReadInt32(root, PopulationCountProperty),
                PopulationScope: ReadString(root, PopulationScopeProperty),
                BatchSize: ReadInt32(root, BatchSizeProperty),
                BatchOrdinal: ReadInt32(root, BatchOrdinalProperty),
                BatchStartSequenceIndex: ReadInt64(root, BatchStartSequenceIndexProperty),
                BatchItemCount: ReadInt32(root, BatchItemCountProperty),
                SequenceIndex: ReadInt64(root, SequenceIndexProperty),
                EntityId: ReadString(root, EntityIdProperty),
                Exemplars: ReadStringArray(root, ExemplarsProperty),
                DefinitionId: ReadString(root, DefinitionIdProperty),
                DefinitionRevision: ReadString(root, DefinitionRevisionProperty),
                DefinitionFingerprint: ReadString(root, DefinitionFingerprintProperty),
                Interpreter: ReadString(root, InterpreterProperty),
                EntropyAlgorithm: ReadString(root, EntropyAlgorithmProperty),
                ReplayToken: ReadString(root, ReplayTokenProperty),
                ObservationUtf8: StrictUtf8.GetBytes(root.GetProperty(ObservationProperty).GetRawText()));
        }
    }

    public static bool HasCanonicalEncoding(string json, in WorldJsonLinesRecord record)
    {
        ArrayBufferWriter<byte> canonical = new();
        WriteRecord(canonical, record);
        var supplied = StrictUtf8.GetBytes(json);
        return supplied.AsSpan().SequenceEqual(canonical.WrittenSpan);
    }

    static void ValidatePropertyOrder(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new WorldJsonLinesCodecException(
                RecordInvalidCode,
                propertyName: null,
                "Each record must be a JSON object.");
        }

        Span<bool> seen = stackalloc bool[PropertyOrder.Length];
        foreach (var property in root.EnumerateObject())
        {
            var propertyIndex = FindPropertyIndex(property.Name);
            if (propertyIndex < 0)
            {
                throw new WorldJsonLinesCodecException(
                    PropertyUnknownCode,
                    property.Name,
                    "The property is not part of the v4 contract.");
            }
            if (seen[propertyIndex])
            {
                throw new WorldJsonLinesCodecException(
                    PropertyDuplicateCode,
                    property.Name,
                    "The property occurs more than once.");
            }
            seen[propertyIndex] = true;
        }

        for (var index = 0; index < seen.Length; index++)
        {
            if (!seen[index])
            {
                throw new WorldJsonLinesCodecException(
                    PropertyMissingCode,
                    PropertyOrder[index],
                    "The required property is missing.");
            }
        }

        var expectedIndex = 0;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, PropertyOrder[expectedIndex], StringComparison.Ordinal))
            {
                throw new WorldJsonLinesCodecException(
                    PropertyOrderInvalidCode,
                    property.Name,
                    $"Expected canonical property '{PropertyOrder[expectedIndex]}' at ordinal {expectedIndex}.");
            }
            expectedIndex++;
        }
    }

    static int FindPropertyIndex(string propertyName)
    {
        for (var index = 0; index < PropertyOrder.Length; index++)
        {
            if (string.Equals(propertyName, PropertyOrder[index], StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    static string ReadString(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            throw InvalidValue(propertyName, "The value must be a non-null string.");
        }

        return text;
    }

    static ImmutableArray<string> ReadStringArray(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidValue(propertyName, "The value must be an array of strings.");
        }

        var items = ImmutableArray.CreateBuilder<string>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text)
            {
                throw InvalidValue(propertyName, "Every array item must be a non-null string.");
            }

            items.Add(text);
        }
        return items.MoveToImmutable();
    }

    static int ReadInt32(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw InvalidValue(propertyName, "The value must be a 32-bit integer.");
        }

        return result;
    }

    static long ReadInt64(JsonElement root, string propertyName)
    {
        var value = root.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw InvalidValue(propertyName, "The value must be a 64-bit integer.");
        }

        return result;
    }

    static long ReadInt64String(JsonElement root, string propertyName)
    {
        var text = ReadString(root, propertyName);
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result))
        {
            throw InvalidValue(propertyName, "The value must be a signed 64-bit decimal string.");
        }

        return result;
    }

    static WorldJsonLinesCodecException InvalidValue(string propertyName, string detail) =>
        new(RecordInvalidCode, propertyName, detail);
}
