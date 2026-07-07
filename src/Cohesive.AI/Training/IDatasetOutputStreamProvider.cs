using System.Runtime.CompilerServices;

namespace Cohesive.AI.Training;

/// <summary>
/// Opens writable dataset output streams for a given storage root.
/// </summary>
public interface IDatasetOutputStreamProvider
{
    /// <summary>
    /// Opens a writable output stream.
    /// </summary>
    /// <param name="fileName"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    ValueTask<DatasetOutputWriteTarget> OpenWriteAsync(string fileName, CancellationToken ct = default);
}

/// <summary>
/// Writable dataset output target and its resolved artifact location.
/// </summary>
public sealed class DatasetOutputWriteTarget(Stream stream, Uri location) : IAsyncDisposable
{
    /// <summary>
    /// The output dataset stream.
    /// </summary>
    public Stream Stream => stream;

    /// <summary>
    /// The location of the asset.
    /// </summary>
    public Uri Location => location;

    /// <summary>
    /// Flushes the output stream.
    /// </summary>
    /// <param name="ct">The token to monitor for cancellation requests. The default value is None.</param>
    /// <returns>A task that represents the asynchronous flush operation.</returns>
    public ConfiguredTaskAwaitable Flush(CancellationToken ct = default) =>
        stream.FlushAsync(ct).ConfigureAwait(false);
    
    /// <summary>
    /// Disposes the stream.
    /// </summary>
    /// <returns></returns>
    public ValueTask DisposeAsync() => 
        stream.DisposeAsync();
}