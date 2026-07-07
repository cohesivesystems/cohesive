using Microsoft.ML.OnnxRuntime;

namespace Cohesive.Adapters.ONNX;

/// <summary>
/// Shared tensor output parsing for ONNX score models.
/// </summary>
static class OnnxScoreOutputParsing
{
    /// <summary>
    /// Parses a model output tensor into one calibrated score per batch row.
    /// </summary>
    internal static float[] ParseBatchScores(OrtValue output, int expectedBatchCount, string outputName, string noClassDimensionMessage)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(noClassDimensionMessage);

        var typeAndShape = output.GetTensorTypeAndShape();
        var shape = typeAndShape.Shape;
        var rank = typeAndShape.DimensionsCount;

        ReadOnlySpan<float> buffer;
        try
        {
            buffer = output.GetTensorDataAsSpan<float>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException($"Output '{outputName}' must be a float tensor.", ex);
        }

        if (rank == 1)
        {
            if (shape[0] != expectedBatchCount)
                throw new InvalidOperationException($"Output batch count {shape[0]} does not match expected batch count {expectedBatchCount}.");

            var scores = new float[expectedBatchCount];
            for (var i = 0; i < expectedBatchCount; i++)
                scores[i] = OnnxScoreCalibration.CalibrateSingleLabelScore(buffer[i]);
            return scores;
        }

        if (rank == 2)
            return ParseMatrixScores(shape, buffer, expectedBatchCount, noClassDimensionMessage);

        if (rank == 3 && shape[1] == 1)
            return ParseTensor3Scores(shape, buffer, expectedBatchCount, noClassDimensionMessage);

        throw new InvalidOperationException($"Output '{outputName}' must be rank-1, rank-2, or rank-3 with shape [batch,1,classes], but rank was {rank}.");
    }

    static float[] ParseMatrixScores(ReadOnlySpan<long> shape, ReadOnlySpan<float> buffer, int expectedBatchCount, string noClassDimensionMessage)
    {
        var batchCount = checked((int)shape[0]);
        var classCount = checked((int)shape[1]);
        if (batchCount != expectedBatchCount)
            throw new InvalidOperationException($"Output batch count {batchCount} does not match expected batch count {expectedBatchCount}.");
        if (classCount <= 0)
            throw new InvalidOperationException(noClassDimensionMessage);

        var scores = new float[batchCount];
        for (var row = 0; row < batchCount; row++)
        {
            var offset = row * classCount;
            scores[row] = classCount == 1
                ? OnnxScoreCalibration.CalibrateSingleLabelScore(buffer[offset])
                : OnnxScoreCalibration.CalibrateMultiClassPositiveClassScore(buffer.Slice(offset, classCount));
        }

        return scores;
    }

    static float[] ParseTensor3Scores(ReadOnlySpan<long> shape, ReadOnlySpan<float> buffer, int expectedBatchCount, string noClassDimensionMessage)
    {
        var batchCount = checked((int)shape[0]);
        var classCount = checked((int)shape[2]);
        if (batchCount != expectedBatchCount)
            throw new InvalidOperationException($"Output batch count {batchCount} does not match expected batch count {expectedBatchCount}.");
        if (classCount <= 0)
            throw new InvalidOperationException(noClassDimensionMessage);

        var scores = new float[batchCount];
        for (var row = 0; row < batchCount; row++)
        {
            var offset = row * classCount;
            scores[row] = classCount == 1
                ? OnnxScoreCalibration.CalibrateSingleLabelScore(buffer[offset])
                : OnnxScoreCalibration.CalibrateMultiClassPositiveClassScore(buffer.Slice(offset, classCount));
        }

        return scores;
    }
}
