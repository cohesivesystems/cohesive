namespace Cohesive.AI.Vectors;

/// <summary>
/// Describes a vector similarity query.
/// </summary>
/// <param name="PartitionKey">Partition key scope for the query.</param>
/// <param name="Embedding">Query embedding vector.</param>
/// <param name="TopK">Maximum number of matches to return.</param>
/// <param name="MinScore">Optional score floor for accepted matches.</param>
public sealed record VectorQuery(
    string PartitionKey,
    ReadOnlyMemory<float> Embedding,
    int TopK,
    float? MinScore = null
    );