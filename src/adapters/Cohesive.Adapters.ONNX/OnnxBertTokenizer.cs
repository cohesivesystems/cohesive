using System.Text;
using Cohesive.AI.Text;
using Microsoft.ML.Tokenizers;

namespace Cohesive.Adapters.ONNX;

/// <summary>
/// BERT WordPiece tokenizer adapter for ONNX transformer models.
/// </summary>
public sealed class OnnxBertTokenizer : ITokenizer
{
    readonly BertTokenizer tokenizer;
    readonly int maxTokenCount;

    /// <summary>
    /// Creates an ONNX tokenizer adapter over a Bert tokenizer.
    /// </summary>
    public OnnxBertTokenizer(BertTokenizer tokenizer, int maxTokenCount = 256)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        if (maxTokenCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTokenCount), "Maximum token count must be positive.");

        this.tokenizer = tokenizer;
        this.maxTokenCount = maxTokenCount;
    }

    /// <summary>
    /// Creates an ONNX BERT tokenizer from a WordPiece vocabulary file.
    /// </summary>
    public static OnnxBertTokenizer CreateFromVocab(
        string vocabFilePath,
        int maxTokenCount = 256,
        bool lowerCaseBeforeTokenization = true,
        bool applyBasicTokenization = true,
        bool splitOnSpecialTokens = true
        )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabFilePath);
        if (!File.Exists(vocabFilePath))
            throw new FileNotFoundException($"Vocabulary file was not found at '{vocabFilePath}'.", vocabFilePath);

        var tokenizer = BertTokenizer.Create(
            vocabFilePath,
            new BertOptions
            {
                LowerCaseBeforeTokenization = lowerCaseBeforeTokenization,
                ApplyBasicTokenization = applyBasicTokenization,
                SplitOnSpecialTokens = splitOnSpecialTokens
            });

        return new OnnxBertTokenizer(tokenizer, maxTokenCount);
    }

    /// <inheritdoc />
    public TokenizationResult Encode(ReadOnlySpan<byte> utf8Input)
    {
        var text = Encoding.UTF8.GetString(utf8Input);
        var rawIds = tokenizer.EncodeToIds(
            text,
            addSpecialTokens: true,
            considerPreTokenization: true,
            considerNormalization: true
            );

        var tokenCount = Math.Min(rawIds.Count, maxTokenCount);
        var inputIds = new long[tokenCount];
        var attentionMask = new long[tokenCount];

        for (var i = 0; i < tokenCount; i++)
        {
            inputIds[i] = rawIds[i];
            attentionMask[i] = 1L;
        }

        return new TokenizationResult(inputIds, attentionMask);
    }
}
