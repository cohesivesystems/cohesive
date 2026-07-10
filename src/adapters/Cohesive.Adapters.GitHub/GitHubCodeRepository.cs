using System.Text.Json;
using Cohesive.AI.Training;

namespace Cohesive.Adapters.GitHub;

/// <summary>
/// GitHub-backed implementation of <see cref="ICodeRepository"/>.
/// </summary>
public sealed class GitHubCodeRepository(
    HttpClient httpClient,
    IGitHubAuthProvider authProvider
    ) : ICodeRepository
{
    /// <summary>Resolves revision asynchronously.</summary>
    public async ValueTask<CodeRevision> ResolveRevisionAsync(CodeReference reference, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var repository = ParseRepository(reference.Repository);
        var token = await authProvider.GetAccessTokenAsync(repository.Owner, ct).ConfigureAwait(false);
        using var request = CreateRequest(HttpMethod.Get, $"repos/{repository.Owner}/{repository.Name}/commits/{Uri.EscapeDataString(reference.Revision)}", token);
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync(response, $"Unable to resolve GitHub revision '{reference.Revision}' in '{repository.CanonicalName}'.", ct).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var commitHash = document.RootElement.TryGetProperty("sha", out var shaElement)
            ? shaElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(commitHash))
            throw new InvalidOperationException($"GitHub revision lookup for '{repository.CanonicalName}' did not return a commit SHA.");

        return new(repository.CanonicalName, commitHash, reference.SubPath);
    }

    /// <summary>Opens a repository revision as a code archive.</summary>
    public async ValueTask<CodeArchive> OpenArchiveAsync(CodeRevision revision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var repository = ParseRepository(revision.Repository);
        var token = await authProvider.GetAccessTokenAsync(repository.Owner, ct).ConfigureAwait(false);
        using var request = CreateRequest(HttpMethod.Get, $"repos/{repository.Owner}/{repository.Name}/zipball/{revision.CommitHash}", token);
        var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using var _ = response;
            throw await CreateFailureAsync(response, $"Unable to download a GitHub archive for '{repository.CanonicalName}' at '{revision.CommitHash}'.", ct).ConfigureAwait(false);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return new(
            content: new HttpResponseStream(stream, response),
            fileName: $"{repository.Name}-{revision.CommitHash}.zip"
            );
    }

    static HttpRequestMessage CreateRequest(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", token);
        return request;
    }

    static RepositoryDescriptor ParseRepository(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        var path = repository;
        if (path.StartsWith("github://", StringComparison.OrdinalIgnoreCase))
        {
            path = path["github://".Length..];
        }
        else if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            if (!uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Repository '{repository}' is not a supported GitHub repository identifier.");

            path = uri.AbsolutePath;
        }

        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
        {
            throw new InvalidOperationException(
                $"Repository '{repository}' must be expressed as 'owner/repository', 'github://owner/repository', or a GitHub HTTPS URL.");
        }

        return new(segments[0], segments[1]);
    }

    static async ValueTask<InvalidOperationException> CreateFailureAsync(HttpResponseMessage response, string message, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new($"{message} GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    readonly record struct RepositoryDescriptor(string Owner, string Name)
    {
        public string CanonicalName => $"{Owner}/{Name}";
    }

    sealed class HttpResponseStream(Stream inner, HttpResponseMessage response) : Stream, IAsyncDisposable
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (inner is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                inner.Dispose();

            response.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
