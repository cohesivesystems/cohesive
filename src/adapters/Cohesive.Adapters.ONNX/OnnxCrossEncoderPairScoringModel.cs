using System.Text;
using System.Text.Json;
using Cohesive.AI.Inference;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace Cohesive.Adapters.ONNX;

/// <summary>
/// ONNX-backed cross-encoder pair scorer.
/// </summary>
public sealed class OnnxCrossEncoderPairScoringModel : IPairScoringModel, IDisposable
{
    const string DefaultInputIdsName = "input_ids";
    const string DefaultAttentionMaskName = "attention_mask";
    const string DefaultTokenTypeIdsName = "token_type_ids";
    const string DefaultOutputName = "logits";

    readonly InferenceSession session;
    readonly BertTokenizer tokenizer;
    readonly int maxTokenCount;
    readonly int defaultBatchSize;
    readonly string inputIdsName;
    readonly string attentionMaskName;
    readonly string? tokenTypeIdsName;
    readonly string outputName;
    readonly long clsTokenId;
    readonly long sepTokenId;

    /// <summary>
    /// Creates an ONNX cross-encoder pair-scoring model.
    /// </summary>
    public OnnxCrossEncoderPairScoringModel(
        string modelName,
        string version,
        string modelPath,
        string vocabFilePath,
        int maxTokenCount = 256,
        int defaultBatchSize = 16,
        SessionOptions? sessionOptions = null,
        bool lowerCaseBeforeTokenization = true,
        bool applyBasicTokenization = true,
        bool splitOnSpecialTokens = true,
        string inputIdsName = DefaultInputIdsName,
        string attentionMaskName = DefaultAttentionMaskName,
        string tokenTypeIdsName = DefaultTokenTypeIdsName,
        string outputName = DefaultOutputName
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputIdsName);
        ArgumentException.ThrowIfNullOrWhiteSpace(attentionMaskName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenTypeIdsName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX model was not found at '{modelPath}'.", modelPath);
        if (!File.Exists(vocabFilePath))
            throw new FileNotFoundException($"Vocabulary file was not found at '{vocabFilePath}'.", vocabFilePath);
        if (maxTokenCount < 3)
            throw new ArgumentOutOfRangeException(nameof(maxTokenCount), "Maximum token count must be at least 3 for pair encoding.");
        if (defaultBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(defaultBatchSize), "Default batch size must be positive.");

        ModelName = modelName;
        Version = version;
        this.maxTokenCount = maxTokenCount;
        this.defaultBatchSize = defaultBatchSize;
        tokenizer = BertTokenizer.Create(
            vocabFilePath,
            new BertOptions
            {
                LowerCaseBeforeTokenization = lowerCaseBeforeTokenization,
                ApplyBasicTokenization = applyBasicTokenization,
                SplitOnSpecialTokens = splitOnSpecialTokens
            });

        session = sessionOptions is null
            ? new InferenceSession(modelPath)
            : new InferenceSession(modelPath, sessionOptions);

        this.inputIdsName = session.ResolveInputName(inputIdsName, "input_ids");
        this.attentionMaskName = session.ResolveInputName(attentionMaskName, "attention_mask");

        var tokenTypeCandidate = session.ResolveInputName(tokenTypeIdsName, "token_type");
        this.tokenTypeIdsName = session.InputNames.Contains(tokenTypeIdsName, StringComparer.Ordinal)
            || tokenTypeCandidate.Contains("token_type", StringComparison.OrdinalIgnoreCase)
            ? tokenTypeCandidate
            : null;

        this.outputName = session.ResolveOutputName(outputName);
        (clsTokenId, sepTokenId) = ResolveSpecialTokenIds();
    }

    /// <summary>
    /// Creates a cross-encoder model directly from a sentence-transformers export folder.
    /// </summary>
    public static OnnxCrossEncoderPairScoringModel CreateFromSentenceTransformerExport(
        string exportDirectory,
        string modelName,
        string version = "onnx",
        int? maxTokenCount = null,
        bool? lowerCaseBeforeTokenization = null,
        int defaultBatchSize = 16,
        SessionOptions? sessionOptions = null,
        string outputName = DefaultOutputName
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);

        var modelPath = Path.Combine(exportDirectory, "onnx", "model.onnx");
        var vocabPath = Path.Combine(exportDirectory, "vocab.txt");
        var tokenizerConfigPath = Path.Combine(exportDirectory, "tokenizer_config.json");
        var tokenizerConfig = ReadTokenizerConfig(tokenizerConfigPath);

        var effectiveMaxTokens = maxTokenCount ?? tokenizerConfig.MaxTokenCount;
        var effectiveLowerCase = lowerCaseBeforeTokenization ?? tokenizerConfig.LowerCaseBeforeTokenization;

        return new(
            modelName: modelName,
            version: version,
            modelPath: modelPath,
            vocabFilePath: vocabPath,
            maxTokenCount: effectiveMaxTokens,
            defaultBatchSize: defaultBatchSize,
            sessionOptions: sessionOptions,
            lowerCaseBeforeTokenization: effectiveLowerCase,
            outputName: outputName
            );
    }

    /// <inheritdoc />
    public string ModelName { get; }

    /// <inheritdoc />
    public string Version { get; }

    /// <exception cref="ArgumentOutOfRangeException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    /// <inheritdoc />
    public ValueTask<PairScoreBatchResult> ScoreAsync(PairScoreBatchRequest request, CancellationToken ct = default)
    {
        var pairs = request.Pairs;
        if (pairs is null)
            throw new ArgumentNullException(nameof(request), "Pair payloads are required.");
        if (pairs.Count == 0)
            return ValueTask.FromResult(new PairScoreBatchResult([]));

        var effectiveBatchSize = request.BatchSize.GetValueOrDefault(defaultBatchSize);
        if (effectiveBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Batch size must be positive when specified.");

        var scores = new float[pairs.Count];
        for (var start = 0; start < pairs.Count; start += effectiveBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batchCount = Math.Min(effectiveBatchSize, pairs.Count - start);
            ProcessBatch(pairs, start: start, batchCount: batchCount, destinationScores: scores);
        }

        return ValueTask.FromResult(new PairScoreBatchResult(scores));
    }

    /// <inheritdoc />
    public void Dispose() => session.Dispose();

    void ProcessBatch(IReadOnlyList<(ReadOnlyMemory<byte> A, ReadOnlyMemory<byte> B)> pairs, int start, int batchCount, Span<float> destinationScores)
    {
        var tokenizations = new PairTokenization[batchCount];
        var sequenceLength = 0;

        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var pair = pairs[start + batchIndex];
            var tokenization = EncodePair(pair.A.Span, pair.B.Span);
            tokenizations[batchIndex] = tokenization;
            sequenceLength = Math.Max(sequenceLength, tokenization.InputIds.Count);
        }

        if (sequenceLength <= 0)
            sequenceLength = 1;

        var tensorLength = batchCount * sequenceLength;
        var inputIdsBuffer = new long[tensorLength];
        var attentionMaskBuffer = new long[tensorLength];
        var tokenTypeIdsBuffer = tokenTypeIdsName is null ? null : new long[tensorLength];

        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var tokenization = tokenizations[batchIndex];
            var tokenCount = Math.Min(sequenceLength, tokenization.InputIds.Count);

            for (var tokenIndex = 0; tokenIndex < tokenCount; tokenIndex++)
            {
                var index = (batchIndex * sequenceLength) + tokenIndex;
                inputIdsBuffer[index] = tokenization.InputIds[tokenIndex];
                attentionMaskBuffer[index] = tokenization.AttentionMask[tokenIndex] > 0 ? 1L : 0L;
                if (tokenTypeIdsBuffer is not null)
                    tokenTypeIdsBuffer[index] = tokenization.TokenTypeIds[tokenIndex];
            }
        }

        var shape = new long[] { batchCount, sequenceLength };
        using var inputIdsValue = OrtValue.CreateTensorValueFromMemory(inputIdsBuffer, shape);
        using var attentionMaskValue = OrtValue.CreateTensorValueFromMemory(attentionMaskBuffer, shape);
        using var tokenTypeIdsValue = tokenTypeIdsBuffer is null ? null : OrtValue.CreateTensorValueFromMemory(tokenTypeIdsBuffer, shape);

        var inputNames = new List<string>(3) { inputIdsName, attentionMaskName };
        var inputValues = new List<OrtValue>(3) { inputIdsValue, attentionMaskValue };
        if (tokenTypeIdsName is not null && tokenTypeIdsValue is not null)
        {
            inputNames.Add(tokenTypeIdsName);
            inputValues.Add(tokenTypeIdsValue);
        }

        using var runOptions = new RunOptions();
        using var modelOutputs = session.Run(runOptions, inputNames, inputValues, [outputName]);

        var output = modelOutputs.FirstOrDefault();
        if (output is null)
            throw new InvalidOperationException("ONNX session returned no outputs.");

        var batchScores = OnnxScoreOutputParsing.ParseBatchScores(
            output: output,
            expectedBatchCount: batchCount,
            outputName: outputName,
            noClassDimensionMessage: "Cross-encoder output has no class dimension."
            );
        
        for (var i = 0; i < batchCount; i++)
            destinationScores[start + i] = batchScores[i];
    }

    PairTokenization EncodePair(ReadOnlySpan<byte> aUtf8, ReadOnlySpan<byte> bUtf8)
    {
        var textA = Encoding.UTF8.GetString(aUtf8);
        var textB = Encoding.UTF8.GetString(bUtf8);

        var idsA = tokenizer.EncodeToIds(textA, addSpecialTokens: false, considerPreTokenization: true, considerNormalization: true);
        var idsB = tokenizer.EncodeToIds(textB, addSpecialTokens: false, considerPreTokenization: true, considerNormalization: true);

        var tokenBudget = maxTokenCount - 3; // [CLS] A [SEP] B [SEP]
        var lenA = idsA.Count;
        var lenB = idsB.Count;
        
        while (lenA + lenB > tokenBudget)
        {
            if (lenA >= lenB && lenA > 0)
                lenA--;
            else if (lenB > 0)
                lenB--;
            else
                break;
        }

        var totalLength = lenA + lenB + 3;
        var inputIds = new long[totalLength];
        var attentionMask = new long[totalLength];
        var tokenTypeIds = new long[totalLength];
        var cursor = 0;

        inputIds[cursor] = clsTokenId;
        attentionMask[cursor] = 1L;
        tokenTypeIds[cursor] = 0L;
        cursor++;

        for (var i = 0; i < lenA; i++, cursor++)
        {
            inputIds[cursor] = idsA[i];
            attentionMask[cursor] = 1L;
            tokenTypeIds[cursor] = 0L;
        }

        inputIds[cursor] = sepTokenId;
        attentionMask[cursor] = 1L;
        tokenTypeIds[cursor] = 0L;
        cursor++;

        for (var i = 0; i < lenB; i++, cursor++)
        {
            inputIds[cursor] = idsB[i];
            attentionMask[cursor] = 1L;
            tokenTypeIds[cursor] = 1L;
        }

        inputIds[cursor] = sepTokenId;
        attentionMask[cursor] = 1L;
        tokenTypeIds[cursor] = 1L;

        return new PairTokenization(InputIds: inputIds, AttentionMask: attentionMask, TokenTypeIds: tokenTypeIds);
    }

    (long ClsTokenId, long SepTokenId) ResolveSpecialTokenIds()
    {
        var emptyEncoding = tokenizer.EncodeToIds(string.Empty, addSpecialTokens: true, considerPreTokenization: true, considerNormalization: true);
        if (emptyEncoding.Count >= 2)
            return (emptyEncoding[0], emptyEncoding[^1]);

        throw new InvalidOperationException("Tokenizer did not expose expected [CLS] and [SEP] special tokens.");
    }

    static (int MaxTokenCount, bool LowerCaseBeforeTokenization) ReadTokenizerConfig(string tokenizerConfigPath)
    {
        const int fallbackMaxTokenCount = 256;
        const bool fallbackLowerCase = true;

        if (string.IsNullOrWhiteSpace(tokenizerConfigPath) || !File.Exists(tokenizerConfigPath))
            return (fallbackMaxTokenCount, fallbackLowerCase);

        try
        {
            using var stream = File.OpenRead(tokenizerConfigPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            var maxTokenCount = fallbackMaxTokenCount;
            if (root.TryGetProperty("model_max_length", out var maxLengthNode)
                && maxLengthNode.ValueKind == JsonValueKind.Number
                && maxLengthNode.TryGetInt32(out var parsedMaxTokenCount)
                && parsedMaxTokenCount > 0)
            {
                maxTokenCount = parsedMaxTokenCount;
            }

            var lowerCase = fallbackLowerCase;
            if (root.TryGetProperty("do_lower_case", out var lowerCaseNode)
                && (lowerCaseNode.ValueKind is JsonValueKind.False or JsonValueKind.True))
            {
                lowerCase = lowerCaseNode.GetBoolean();
            }

            return (maxTokenCount, lowerCase);
        }
        catch (JsonException)
        {
            return (fallbackMaxTokenCount, fallbackLowerCase);
        }
        catch (IOException)
        {
            return (fallbackMaxTokenCount, fallbackLowerCase);
        }
    }

    readonly record struct PairTokenization(
        IReadOnlyList<long> InputIds,
        IReadOnlyList<long> AttentionMask,
        IReadOnlyList<long> TokenTypeIds
        );
}
