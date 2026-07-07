namespace Cohesive.AI.Vectors;

/// <summary>
/// Represents a vectorized document stored in a vector index.
/// </summary>
/// <param name="Id">Unique document identifier.</param>
/// <param name="PartitionKey">Partition key used for physical or logical segmentation.</param>
/// <param name="Embedding">Document embedding vector.</param>
/// <param name="Metadata">Opaque UTF-8 metadata payload.</param>
public sealed record VectorDocument(
    string Id,
    string PartitionKey,
    ReadOnlyMemory<float> Embedding,
    ReadOnlyMemory<byte> Metadata
    );