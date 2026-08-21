using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cohesive.MaterializationHarness.Supervise;

sealed record ArtifactManifestEntry(
    string Name,
    long ObservedBytes,
    int RetainedBytes,
    bool Truncated,
    string RetainedSha256);

sealed class BoundedArtifactWriter(string directory, int maximumBytes)
{
    readonly List<ArtifactManifestEntry> entries = [];

    internal ImmutableArray<ArtifactManifestEntry> Entries => [.. entries];

    internal async Task WriteTextAsync(
        string name,
        string content,
        long? observedBytes = null,
        bool alreadyTruncated = false)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var retain = Math.Min(bytes.Length, maximumBytes);
        var retained = bytes.AsMemory(0, retain);
        var path = Path.Combine(directory, name);
        await File.WriteAllBytesAsync(path, retained.ToArray()).ConfigureAwait(false);
        entries.RemoveAll(entry => string.Equals(entry.Name, name, StringComparison.Ordinal));
        entries.Add(new(
            Name: name,
            ObservedBytes: observedBytes ?? bytes.LongLength,
            RetainedBytes: retain,
            Truncated: alreadyTruncated || retain != bytes.Length || observedBytes > retain,
            RetainedSha256: Convert.ToHexString(SHA256.HashData(retained.Span)).ToLowerInvariant()));
    }

    internal async Task<string> CaptureHttpAsync(HttpClient client, string name, Uri uri)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var maximumBodyBytes = maximumBytes / 2;
        using var retained = new MemoryStream(capacity: maximumBodyBytes);
        var buffer = new byte[8192];
        long observed = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0)
                break;
            observed += read;
            hash.AppendData(buffer, 0, read);
            var remaining = maximumBodyBytes - checked((int)retained.Length);
            if (remaining > 0)
                retained.Write(buffer, 0, Math.Min(remaining, read));
        }
        var body = Encoding.UTF8.GetString(retained.GetBuffer(), 0, checked((int)retained.Length));
        var envelope = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            request = uri.AbsoluteUri,
            status = (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType,
            observedBytes = observed,
            retainedBytes = retained.Length,
            truncated = observed > retained.Length,
            sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
            body
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await WriteTextAsync(name, envelope).ConfigureAwait(false);
        return body;
    }

    internal async Task WriteManifestAsync(object summary)
    {
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            summary,
            artifacts = Entries
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), json).ConfigureAwait(false);
    }
}

sealed class BoundedLineCapture(int maximumCharacters)
{
    readonly object gate = new();
    readonly StringBuilder retained = new(capacity: Math.Min(maximumCharacters, 4096));
    long observedBytes;

    internal void Add(string? line)
    {
        if (line is null)
            return;
        lock (gate)
        {
            observedBytes += Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
            if (retained.Length >= maximumCharacters)
                return;
            var available = maximumCharacters - retained.Length;
            if (line.Length <= available)
                retained.Append(line);
            else
                retained.Append(line.AsSpan(0, available));
            if (retained.Length < maximumCharacters)
                retained.AppendLine();
        }
    }

    internal (string Text, long ObservedBytes, bool Truncated) Snapshot()
    {
        lock (gate)
        {
            var text = retained.ToString();
            var retainedBytes = Encoding.UTF8.GetByteCount(text);
            return (text, observedBytes, observedBytes > retainedBytes);
        }
    }
}
