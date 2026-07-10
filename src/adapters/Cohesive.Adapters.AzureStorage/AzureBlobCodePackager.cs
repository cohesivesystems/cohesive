using System.IO.Compression;
using System.Security.Cryptography;
using Cohesive.AI.Training;

namespace Cohesive.Adapters.AzureStorage;

/// <summary>
/// Packages source archives into normalized zip artifacts stored in Azure Blob Storage.
/// </summary>
public sealed class AzureBlobCodePackager(
    IBlobClientByNameResolver blobClientResolver
    ) : ICodePackager
{
    static readonly DateTimeOffset ZipMinimumTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Packages a code archive into a training artifact.</summary>
    public async ValueTask<TrainingCodeArtifact> PackageAsync(CodeRevision revision, CodeArchive archive, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ct.ThrowIfCancellationRequested();
        
        var tempPath = Path.Combine(Path.GetTempPath(), $"cohesive-code-{Guid.NewGuid():N}.zip");
        try
        {
            await using var tempStream = new FileStream(
                path: tempPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 81920,
                options: FileOptions.Asynchronous
                );

            await NormalizeArchiveAsync(revision, source: archive.Content, destination: tempStream, ct).ConfigureAwait(false);
            tempStream.Position = 0;

            var hash = await SHA256.HashDataAsync(tempStream, ct).ConfigureAwait(false);
            var version = $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
            tempStream.Position = 0;
            
            var blobName = BuildArtifactPath(revision, version: version);
            var blobClient = await blobClientResolver.GetBlobClient(blobName: blobName, ct: ct);
            await blobClient.UploadAsync(tempStream, options: new() { HttpHeaders = new() { ContentType = "application/zip" } }, ct).ConfigureAwait(false);

            return new(BlobUri: blobClient.Uri.ToString(), Version: version);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {
            }
        }
    }

    static async Task NormalizeArchiveAsync(CodeRevision revision, Stream source, Stream destination, CancellationToken ct)
    {
        using var input = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        using var output = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var files = input.Entries
            .Where(static entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => new ArchiveEntry(entry, NormalizeZipPath(entry.FullName)))
            .ToArray();

        if (files.Length == 0)
            throw new InvalidOperationException($"Repository '{revision.Repository}' at '{revision.CommitHash}' did not contain any files.");

        var commonRoot = TryGetCommonRoot(files.Select(static file => file.NormalizedPath));
        var requestedSubPath = NormalizeSubPath(revision.SubPath);

        var packagedFiles = files
            .Select(file => new
            {
                file.Entry,
                PackagedPath = GetPackagedPath(file.NormalizedPath, commonRoot, requestedSubPath)
            })
            .Where(static file => file.PackagedPath is not null)
            .OrderBy(static file => file.PackagedPath, StringComparer.Ordinal)
            .ToArray();

        if (packagedFiles.Length == 0)
            throw new InvalidOperationException($"Repository '{revision.Repository}' at '{revision.CommitHash}' did not contain any files under '{revision.SubPath}'.");

        foreach (var file in packagedFiles)
        {
            var outputEntry = output.CreateEntry(file.PackagedPath!, CompressionLevel.SmallestSize);
            outputEntry.LastWriteTime = ZipMinimumTimestamp;

            await using var inputStream = file.Entry.Open();
            await using var outputStream = outputEntry.Open();
            await inputStream.CopyToAsync(outputStream, ct).ConfigureAwait(false);
        }
    }

    static string BuildArtifactPath(CodeRevision revision, string version)
    {
        var repositorySegment = revision.Repository.ToLettersOrDigitsWithSeparator(separator: '-');
        if (string.IsNullOrWhiteSpace(repositorySegment))
            repositorySegment = "repository";

        var versionSegment = version.Replace(":", "-", StringComparison.Ordinal);
        return $"{repositorySegment}/{revision.CommitHash}/{versionSegment}.zip";
    }

    static string NormalizeZipPath(string path) => path.Replace('\\', '/').Trim('/');

    static string? NormalizeSubPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return NormalizeZipPath(path);
    }

    static string? TryGetCommonRoot(IEnumerable<string> paths)
    {
        string? root = null;
        foreach (var path in paths)
        {
            var separatorIndex = path.IndexOf('/', StringComparison.Ordinal);
            if (separatorIndex <= 0)
                return null;

            var segment = path[..separatorIndex];
            if (root is null)
            {
                root = segment;
                continue;
            }

            if (!string.Equals(root, segment, StringComparison.Ordinal))
                return null;
        }

        return root;
    }

    static string? GetPackagedPath(string normalizedPath, string? commonRoot, string? subPath)
    {
        var relativePath = normalizedPath;
        if (!string.IsNullOrWhiteSpace(commonRoot)
            && relativePath.StartsWith($"{commonRoot}/", StringComparison.Ordinal))
        {
            relativePath = relativePath[(commonRoot.Length + 1)..];
        }

        if (string.IsNullOrWhiteSpace(subPath))
            return relativePath;

        if (string.Equals(relativePath, subPath, StringComparison.Ordinal))
            return Path.GetFileName(relativePath);

        var prefix = $"{subPath}/";
        return relativePath.StartsWith(prefix, StringComparison.Ordinal)
            ? relativePath[prefix.Length..]
            : null;
    }

    readonly record struct ArchiveEntry(
        ZipArchiveEntry Entry,
        string NormalizedPath
        );
}
