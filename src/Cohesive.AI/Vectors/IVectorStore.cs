namespace Cohesive.AI.Vectors;

/// <summary>
/// Provides vector index persistence and query operations.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    /// Inserts or updates a vector document.
    /// </summary>
    /// <param name="document">Document to upsert.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    ValueTask UpsertAsync(VectorDocument document, CancellationToken ct = default);

    /// <summary>
    /// Executes a similarity query.
    /// </summary>
    /// <param name="query">Query parameters and embedding.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>Ordered list of vector matches.</returns>
    ValueTask<IReadOnlyList<VectorMatch>> QueryAsync(VectorQuery query, CancellationToken ct = default);

    /// <summary>
    /// Deletes a vector document by identifier.
    /// </summary>
    /// <param name="id">Document identifier.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    ValueTask DeleteAsync(string id, CancellationToken ct = default);
}
