namespace Cohesive.AI.Vectors;

/// <summary>
/// Represents a vector search result item.
/// </summary>
/// <param name="Id">Matched document identifier.</param>
/// <param name="Score">Similarity score for the match.</param>
/// <param name="Metadata">Opaque UTF-8 metadata payload.</param>
public sealed record VectorMatch(
    string Id,
    float Score,
    ReadOnlyMemory<byte> Metadata
    );