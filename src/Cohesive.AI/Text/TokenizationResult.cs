namespace Cohesive.AI.Text;

/// <summary>
/// Represents tokenized model input tensors.
/// </summary>
/// <param name="InputIds">Encoded token identifiers.</param>
/// <param name="AttentionMask">Attention mask values aligned to <paramref name="InputIds"/>.</param>
public readonly record struct TokenizationResult(
    ReadOnlyMemory<long> InputIds,
    ReadOnlyMemory<long> AttentionMask
    );  
