using System.Text.Json;
using Cohesive.AI.Inference;
using Cohesive.AI.Numerics;
using Cohesive.AI.Text;
using Microsoft.ML.OnnxRuntime;

namespace Cohesive.Adapters.ONNX;

/// <summary>
/// ONNX-backed sentence bi-encoder that emits normalized mean-pooled embeddings.
/// </summary>
public sealed class OnnxBiEncoderEmbeddingModel : IEmbeddingModel, IDisposable
{
    const string DefaultInputIdsName = "input_ids";
    const string DefaultAttentionMaskName = "attention_mask";
    const string DefaultTokenTypeIdsName = "token_type_ids";
    const string DefaultLastHiddenStateOutputName = "last_hidden_state";

    readonly InferenceSession session;
    readonly ITokenizer tokenizer;
    readonly int maxTokenCount;
    readonly int defaultBatchSize;
    readonly string lastHiddenStateOutputName;
    readonly string[] inputNames;
    readonly string[] outputNames;

    /// <summary>
    /// Creates an ONNX bi-encoder embedding model.
    /// </summary>
    public OnnxBiEncoderEmbeddingModel(
        string modelName,
        string version,
        string modelPath,
        ITokenizer tokenizer,
        int maxTokenCount = 256,
        int defaultBatchSize = 32,
        SessionOptions? sessionOptions = null,
        string inputIdsName = DefaultInputIdsName,
        string attentionMaskName = DefaultAttentionMaskName,
        string tokenTypeIdsName = DefaultTokenTypeIdsName,
        string lastHiddenStateOutputName = DefaultLastHiddenStateOutputName
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIdsName);
        ArgumentException.ThrowIfNullOrWhiteSpace(attentionMaskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenTypeIdsName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastHiddenStateOutputName);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model was not found at '{modelPath}'.", modelPath);
        if (maxTokenCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTokenCount), "Maximum token count must be positive.");
        if (defaultBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(defaultBatchSize), "Default batch size must be positive.");

        ModelName = modelName;
        Version = version;
        ArgumentNullException.ThrowIfNull(tokenizer);
        this.tokenizer = tokenizer;
        this.maxTokenCount = maxTokenCount;
        this.defaultBatchSize = defaultBatchSize;
        session = sessionOptions is null
            ? new InferenceSession(modelPath)
            : new InferenceSession(modelPath, sessionOptions);
        this.lastHiddenStateOutputName = session.ResolveOutputName(lastHiddenStateOutputName, "last_hidden_state", "hidden_state");
        inputNames = [inputIdsName, attentionMaskName, tokenTypeIdsName];
        outputNames = [this.lastHiddenStateOutputName];
    }

    /// <summary>
    /// Creates a model directly from a sentence-transformers export folder.
    /// </summary>
    public static OnnxBiEncoderEmbeddingModel CreateFromSentenceTransformerExport(
        string exportDirectory,
        string modelName,
        string version = "onnx",
        int? maxTokenCount = null,
        bool? lowerCaseBeforeTokenization = null,
        int defaultBatchSize = 32,
        SessionOptions? sessionOptions = null
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var modelPath = Path.Combine(exportDirectory, "onnx", "model.onnx");
        var vocabPath = Path.Combine(exportDirectory, "vocab.txt");
        var tokenizerConfigPath = Path.Combine(exportDirectory, "tokenizer_config.json");
        var tokenizerConfig = ReadTokenizerConfig(tokenizerConfigPath);

        var effectiveMaxTokens = maxTokenCount ?? tokenizerConfig.MaxTokenCount;
        var effectiveLowerCase = lowerCaseBeforeTokenization ?? tokenizerConfig.LowerCaseBeforeTokenization;
        var tokenizer = OnnxBertTokenizer.CreateFromVocab(
            vocabFilePath: vocabPath,
            maxTokenCount: effectiveMaxTokens,
            lowerCaseBeforeTokenization: effectiveLowerCase,
            applyBasicTokenization: true,
            splitOnSpecialTokens: true
            );

        return new(
            modelName: modelName,
            version: version,
            modelPath: modelPath,
            tokenizer: tokenizer,
            maxTokenCount: effectiveMaxTokens,
            defaultBatchSize: defaultBatchSize,
            sessionOptions: sessionOptions
            );
    }

    /// <inheritdoc />
    public string ModelName { get; }

    /// <inheritdoc />
    public string Version { get; }

    /// <inheritdoc />
    public ValueTask<EmbeddingBatchResult> EmbedAsync(EmbeddingBatchRequest request, CancellationToken ct = default)
    {
        var inputs = request.Inputs;
        if (inputs is null)
            throw new ArgumentNullException(nameof(request), "Input payloads are required.");
        if (inputs.Count == 0)
            return ValueTask.FromResult(new EmbeddingBatchResult(Array.Empty<ReadOnlyMemory<float>>()));

        var effectiveBatchSize = request.BatchSize.GetValueOrDefault(defaultBatchSize);
        if (effectiveBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Batch size must be positive when specified.");

        var embeddings = new ReadOnlyMemory<float>[inputs.Count];

        for (var start = 0; start < inputs.Count; start += effectiveBatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batchCount = Math.Min(effectiveBatchSize, inputs.Count - start);
            ProcessBatch(inputs, start: start, batchCount: batchCount, embeddings);
        }

        return ValueTask.FromResult(new EmbeddingBatchResult(embeddings));
    }

    /// <inheritdoc />
    public void Dispose() => session.Dispose();

    void ProcessBatch(IReadOnlyList<ReadOnlyMemory<byte>> inputs, int start, int batchCount, ReadOnlyMemory<float>[] destinationEmbeddings)
    {
        var tokenizations = new TokenizationResult[batchCount];
        var sequenceLength = 0;

        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var tokenization = tokenizer.Encode(inputs[start + batchIndex].Span);
            tokenizations[batchIndex] = tokenization;
            sequenceLength = Math.Max(sequenceLength, Math.Min(maxTokenCount, tokenization.InputIds.Length));
        }

        if (sequenceLength <= 0)
            sequenceLength = 1;

        var tensorLength = batchCount * sequenceLength;
        var inputIdsBuffer = new long[tensorLength];
        var attentionMaskBuffer = new long[tensorLength];
        var tokenTypeIdsBuffer = new long[tensorLength];

        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var tokenization = tokenizations[batchIndex];
            var inputIds = tokenization.InputIds.Span;
            var attentionMask = tokenization.AttentionMask.Span;
            var tokenCount = Math.Min(sequenceLength, Math.Min(inputIds.Length, attentionMask.Length));
            for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
            {
                var index = (batchIndex * sequenceLength) + tokenIndex;
                inputIdsBuffer[index] = inputIds[tokenIndex];
                attentionMaskBuffer[index] = attentionMask[tokenIndex] > 0 ? 1L : 0L;
                tokenTypeIdsBuffer[index] = 0L;
            }
        }

        var shape = new long[] { batchCount, sequenceLength };
        using var inputIdsValue = OrtValue.CreateTensorValueFromMemory(inputIdsBuffer, shape: shape);
        using var attentionMaskValue = OrtValue.CreateTensorValueFromMemory(attentionMaskBuffer, shape: shape);
        using var tokenTypeIdsValue = OrtValue.CreateTensorValueFromMemory(tokenTypeIdsBuffer, shape: shape);
        IReadOnlyCollection<OrtValue> inputValues =
        [
            inputIdsValue,
            attentionMaskValue,
            tokenTypeIdsValue
        ];

        using var runOptions = new RunOptions();
        using IDisposableReadOnlyCollection<OrtValue> modelOutputs = session.Run(runOptions, inputNames: inputNames, inputValues, outputNames: outputNames);

        var hiddenState = modelOutputs.FirstOrDefault();
        if (hiddenState is null)
            throw new InvalidOperationException("ONNX session returned no outputs.");

        var hiddenStateTypeAndShape = hiddenState.GetTensorTypeAndShape();
        var dimensions = hiddenStateTypeAndShape.Shape;
        var rank = hiddenStateTypeAndShape.DimensionsCount;
        if (rank != 3)
            throw new InvalidOperationException($"Output '{lastHiddenStateOutputName}' must be rank-3 [batch, tokens, hidden], but rank was {rank}.");

        var outputBatchCount = checked((int)dimensions[0]);
        var outputSequenceLength = checked((int)dimensions[1]);
        var hiddenSize = checked((int)dimensions[2]);
        if (outputBatchCount != batchCount)
            throw new InvalidOperationException($"Output batch count {outputBatchCount} does not match input batch count {batchCount}.");

        var hiddenStateBuffer = hiddenState.GetTensorDataAsSpan<float>();

        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var pooled = new float[hiddenSize];
            var pooledTokenCount = 0;
            var tokenLimit = Math.Min(outputSequenceLength, sequenceLength);

            for (var tokenIndex = 0; tokenIndex < tokenLimit; tokenIndex++)
            {
                var tokenOffset = (batchIndex * sequenceLength) + tokenIndex;
                if (attentionMaskBuffer[tokenOffset] == 0L)
                    continue;

                pooledTokenCount++;
                var hiddenStateOffset = ((batchIndex * outputSequenceLength) + tokenIndex) * hiddenSize;
                for (var hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
                    pooled[hiddenIndex] += hiddenStateBuffer[hiddenStateOffset + hiddenIndex];
            }

            if (pooledTokenCount > 0)
            {
                var inverseCount = 1f / pooledTokenCount;
                for (var hiddenIndex = 0; hiddenIndex < pooled.Length; hiddenIndex++)
                    pooled[hiddenIndex] *= inverseCount;
            }

            NormalizeInPlace(pooled);
            destinationEmbeddings[start + batchIndex] = pooled;
        }
    }

    static void NormalizeInPlace(float[] values)
    {
        var norm = VectorMath.NormL2(values);
        if (norm <= double.Epsilon)
            return;

        var inverseNorm = 1f / (float)norm;
        for (var i = 0; i < values.Length; i++)
            values[i] *= inverseNorm;
    }

    static (int MaxTokenCount, bool LowerCaseBeforeTokenization) ReadTokenizerConfig(string tokenizerConfigPath)
    {
        const int FallbackMaxTokenCount = 256;
        const bool FallbackLowerCase = true;

        if (string.IsNullOrWhiteSpace(tokenizerConfigPath) || !File.Exists(tokenizerConfigPath))
            return (FallbackMaxTokenCount, FallbackLowerCase);

        try
        {
            using var stream = File.OpenRead(tokenizerConfigPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            var maxTokenCount = FallbackMaxTokenCount;
            if (root.TryGetProperty("model_max_length", out var maxLengthNode)
                && maxLengthNode.ValueKind == JsonValueKind.Number
                && maxLengthNode.TryGetInt32(out var parsedMaxTokenCount)
                && parsedMaxTokenCount > 0)
            {
                maxTokenCount = parsedMaxTokenCount;
            }

            var lowerCase = FallbackLowerCase;
            if (root.TryGetProperty("do_lower_case", out var lowerCaseNode)
                && (lowerCaseNode.ValueKind is JsonValueKind.False or JsonValueKind.True))
            {
                lowerCase = lowerCaseNode.GetBoolean();
            }

            return (maxTokenCount, lowerCase);
        }
        catch (JsonException)
        {
            return (FallbackMaxTokenCount, FallbackLowerCase);
        }
        catch (IOException)
        {
            return (FallbackMaxTokenCount, FallbackLowerCase);
        }
    }
}
