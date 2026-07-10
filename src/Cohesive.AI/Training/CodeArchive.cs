namespace Cohesive.AI.Training;

/// <summary>
/// Archive stream representing repository contents at a specific revision.
/// </summary>
public sealed class CodeArchive(
    Stream content,
    string fileName,
    string contentType = "application/zip"
    ) : IDisposable, IAsyncDisposable
{
    /// <summary>Gets the content.</summary>
    public Stream Content { get; } = content ?? throw new ArgumentNullException(nameof(content));

    /// <summary>Gets the file name.</summary>
    public string FileName { get; } = string.IsNullOrWhiteSpace(fileName)
        ? throw new ArgumentException("Archive file name must be provided.", nameof(fileName))
        : fileName;

    /// <summary>Gets the content type.</summary>
    public string ContentType { get; } = string.IsNullOrWhiteSpace(contentType)
        ? throw new ArgumentException("Archive content type must be provided.", nameof(contentType))
        : contentType;

    /// <inheritdoc />
    public void Dispose() => Content.Dispose();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Content is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            return;
        }

        Content.Dispose();
    }
}
