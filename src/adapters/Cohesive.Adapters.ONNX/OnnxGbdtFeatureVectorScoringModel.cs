using Cohesive.AI.Inference;
using Microsoft.ML.OnnxRuntime;

namespace Cohesive.Adapters.ONNX;

/// <summary>
/// ONNX-backed feature-vector scoring model for GBDT-style tabular inference.
/// </summary>
public sealed class OnnxGbdtFeatureVectorScoringModel : IFeatureVectorScoringModel, IDisposable
{
    const string DefaultInputName = "features";
    const string DefaultOutputName = "probabilities";

    readonly InferenceSession session;
    readonly int defaultBatchSize;
    readonly string inputName;
    readonly string outputName;

    /// <summary>
    /// Creates an ONNX feature-vector scoring model.
    /// </summary>
    public OnnxGbdtFeatureVectorScoringModel(
        string modelName,
        string version,
        string modelPath,
        int defaultBatchSize = 256,
        SessionOptions? sessionOptions = null,
        string inputName = DefaultInputName,
        string outputName = DefaultOutputName
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model was not found at '{modelPath}'.", modelPath);
        if (defaultBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(defaultBatchSize), "Default batch size must be positive.");

        ModelName = modelName;
        Version = version;
        this.defaultBatchSize = defaultBatchSize;
        session = sessionOptions is null
            ? new InferenceSession(modelPath)
            : new InferenceSession(modelPath, sessionOptions);

        this.inputName = session.ResolveInputName(inputName, "feature", "input");
        this.outputName = session.ResolveOutputName(outputName, "prob", "score");
    }

    /// <summary>
    /// Creates a model directly from an export directory.
    /// </summary>
    public static OnnxGbdtFeatureVectorScoringModel CreateFromExportDirectory(
        string exportDirectory,
        string modelName,
        string version = "onnx",
        int defaultBatchSize = 256,
        SessionOptions? sessionOptions = null,
        string inputName = DefaultInputName,
        string outputName = DefaultOutputName
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);

        var modelPath = Path.Combine(exportDirectory, "onnx", "model.onnx");
        if (!File.Exists(modelPath))
            modelPath = Path.Combine(exportDirectory, "model.onnx");

        return new(
            modelName: modelName,
            version: version,
            modelPath: modelPath,
            defaultBatchSize: defaultBatchSize,
            sessionOptions: sessionOptions,
            inputName: inputName,
            outputName: outputName
            );
    }

    /// <inheritdoc />
    public string ModelName { get; }

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public ValueTask<FeatureVectorBatchResult> ScoreAsync(FeatureVectorBatchRequest request, CancellationToken ct = default)
    {
        var featureVectors = request.FeatureVectors;
        if (featureVectors is null)
            throw new ArgumentNullException(nameof(request), "Feature vectors are required.");
        if (featureVectors.Count == 0)
            return ValueTask.FromResult(new FeatureVectorBatchResult([]));

        var effectiveBatchSize = request.BatchSize.GetValueOrDefault(defaultBatchSize);
        if (effectiveBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Batch size must be positive when specified.");

        var featureCount = OnnxFeatureVectorValidation.ValidateAlignedFiniteVectors(featureVectors, nameof(featureVectors));
        var scores = new float[featureVectors.Count];

        for (var start = 0; start < featureVectors.Count; start += effectiveBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batchCount = Math.Min(effectiveBatchSize, featureVectors.Count - start);
            ProcessBatch(featureVectors, start: start, batchCount: batchCount, featureCount: featureCount, destinationScores: scores);
        }

        return ValueTask.FromResult(new FeatureVectorBatchResult(scores));
    }

    /// <inheritdoc />
    public void Dispose() => session.Dispose();

    void ProcessBatch(IReadOnlyList<ReadOnlyMemory<float>> featureVectors, int start, int batchCount, int featureCount, Span<float> destinationScores)
    {
        var inputBuffer = new float[batchCount * featureCount];
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var source = featureVectors[start + batchIndex].Span;
            var offset = batchIndex * featureCount;
            source.CopyTo(inputBuffer.AsSpan(offset, featureCount));
        }

        var shape = new long[] { batchCount, featureCount };
        using var inputValue = OrtValue.CreateTensorValueFromMemory(inputBuffer, shape);
        IReadOnlyCollection<string> inputNames = [inputName];
        IReadOnlyCollection<OrtValue> inputValues = [inputValue];

        using var runOptions = new RunOptions();
        using var modelOutputs = session.Run(runOptions, inputNames, inputValues, [outputName]);
        var output = modelOutputs.ElementAtOrDefault(0);
        if (output is null)
            throw new InvalidOperationException("ONNX session returned no outputs.");

        var batchScores = OnnxScoreOutputParsing.ParseBatchScores(
            output: output,
            expectedBatchCount: batchCount,
            outputName: outputName,
            noClassDimensionMessage: "Feature-vector output has no class dimension.");
        for (var i = 0; i < batchCount; i++)
            destinationScores[start + i] = batchScores[i];
    }

}
