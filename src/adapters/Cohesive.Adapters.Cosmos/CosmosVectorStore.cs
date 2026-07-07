using System.Text.Json.Serialization;
using Cohesive.AI.Vectors;
using Cohesive.AI.Numerics;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Cosmos DB-backed implementation of <see cref="IVectorStore"/>.
/// </summary>
public sealed class CosmosVectorStore : IVectorStore
{
    readonly Container container;

    /// <summary>
    /// Creates a vector store using a concrete Cosmos container.
    /// </summary>
    /// <param name="container">Target container for vector documents.</param>
    public CosmosVectorStore(Container container)
    {
        this.container = Guard.RequireNotNull(container);
    }

    /// <inheritdoc />
    public async ValueTask UpsertAsync(VectorDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.PartitionKey);

        var item = new CosmosVectorDocument(
            Id: document.Id,
            PartitionKey: document.PartitionKey,
            Embedding: document.Embedding.ToArray(),
            Metadata: document.Metadata.ToArray()
            );

        await container.UpsertItemAsync(
            item,
            new PartitionKey(document.PartitionKey),
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<VectorMatch>> QueryAsync(VectorQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.PartitionKey);
        if (query.TopK <= 0)
            throw new ArgumentOutOfRangeException(nameof(query.TopK), "TopK must be greater than zero.");

        if (query.Embedding.IsEmpty)
            return [];

        List<VectorMatch> matches = [];
        var queryEmbedding = query.Embedding.ToArray();

        var queryDefinition = new QueryDefinition(
            """
            SELECT c.id, c.partitionKey, c.embedding, c.metadata
            FROM c
            WHERE c.partitionKey = @partitionKey
            """).WithParameter("@partitionKey", query.PartitionKey);

        var iterator = container.GetItemQueryIterator<CosmosVectorDocument>(
            queryDefinition,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(query.PartitionKey) }
            );

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
            {
                if (item.Embedding is null || item.Metadata is null)
                    continue;

                if (!TryCosineSimilarity(queryEmbedding, item.Embedding, out var score))
                    continue;

                if (query.MinScore is { } minScore && score < minScore)
                    continue;

                matches.Add(new VectorMatch(item.Id, score, item.Metadata));
            }
        }

        matches.Sort(static (left, right) => right.Score.CompareTo(left.Score));
        if (matches.Count > query.TopK)
            matches.RemoveRange(query.TopK, matches.Count - query.TopK);

        return matches;
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var existing = await GetByIdAsync(id, ct);
        if (existing is null)
            return;

        try
        {
            await container.DeleteItemAsync<CosmosVectorDocument>(
                id,
                new PartitionKey(existing.PartitionKey),
                cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Idempotent delete behavior.
        }
    }

    async ValueTask<CosmosVectorDocument?> GetByIdAsync(string id, CancellationToken ct)
    {
        var queryDefinition = new QueryDefinition(
            """
            SELECT TOP 1 c.id, c.partitionKey, c.embedding, c.metadata
            FROM c
            WHERE c.id = @id
            """).WithParameter("@id", id);

        var iterator = container.GetItemQueryIterator<CosmosVectorDocument>(
            queryDefinition,
            requestOptions: new QueryRequestOptions { MaxItemCount = 1 });

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(ct);
            foreach (var item in response)
                return item;
        }

        return null;
    }

    /// <summary>
    /// Attempts to compute cosine similarity for two embeddings.
    /// </summary>
    /// <param name="left">First embedding vector.</param>
    /// <param name="right">Second embedding vector.</param>
    /// <param name="similarity">Computed cosine similarity when vectors are valid; otherwise <c>0</c>.</param>
    /// <returns>
    /// <see langword="true"/> when both vectors are non-empty, have equal dimensionality, and non-zero norms;
    /// otherwise <see langword="false"/>.
    /// </returns>
    static bool TryCosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right, out float similarity)
    {
        if (!VectorMath.TryCosineSimilarity(left, right, out var score))
        {
            similarity = 0f;
            return false;
        }

        similarity = (float)score;
        return true;
    }

    sealed record CosmosVectorDocument(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("partitionKey")] string PartitionKey,
        [property: JsonPropertyName("embedding")] float[] Embedding,
        [property: JsonPropertyName("metadata")] byte[] Metadata
        );
}
