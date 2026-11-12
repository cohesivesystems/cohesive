using System.Globalization;
using System.Text.Json;

namespace Cohesive.Semantics;

/// <summary>Reconstructs complete JSON from bounded inspection pages without assuming document-specific pointers.</summary>
public static class WorkspaceInspectionReader
{
    /// <summary>Reads a complete selected value through a snapshot-bound inspection callback.</summary>
    /// <param name="pointer">RFC 6901 pointer to read.</param>
    /// <param name="inspect">Fetches a pointer and child offset. Must pin one document and exact workspace snapshot.</param>
    /// <param name="maximumBytes">Maximum encoded result size in bytes; defaults to 16 MiB.</param>
    /// <param name="maximumRequests">Maximum inspection calls; defaults to 4096.</param>
    /// <param name="cancellationToken">Cancels traversal and inspection calls.</param>
    /// <returns>Owned complete JSON, preserving nulls, empty containers, property names and array order.</returns>
    /// <exception cref="ArgumentNullException">Pointer or callback is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive.</exception>
    /// <exception cref="InvalidOperationException">Pages are inconsistent or incomplete, or a size, request or depth bound is exceeded.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public static async Task<JsonElement> ReadAsync(string pointer,
        Func<string, int, CancellationToken, Task<WorkspaceInspection>> inspect,
        int maximumBytes = 16 * 1024 * 1024, int maximumRequests = 4096,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(inspect);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRequests);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        var requests = 0;
        string? subject = null;
        await WriteAsync(pointer, 0);
        writer.Flush();
        using var document = JsonDocument.Parse(stream.GetBuffer().AsMemory(0, checked((int)stream.Length)));
        return document.RootElement.Clone();

        async Task<WorkspaceInspection> Fetch(string path, int offset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++requests > maximumRequests) throw new InvalidOperationException("Inspection request bound exceeded.");
            var page = await inspect(path, offset, cancellationToken);
            subject ??= page.Subject.Id;
            Require(page.Subject.Id == subject && page.Pointer == path, "Inspection identity or pointer changed.");
            return page;
        }

        void CheckSize()
        {
            writer.Flush();
            Require(stream.Length <= maximumBytes, "Complete inspection byte bound exceeded.");
        }

        async Task WriteAsync(string path, int depth)
        {
            Require(depth < 64, "Complete inspection depth bound exceeded.");
            var page = await Fetch(path, 0);
            if (!page.IsTruncated)
            {
                if (page.ValueKind == JsonValueKind.Null) writer.WriteNullValue();
                else
                {
                    Require(page.Value.HasValue && page.Value.Value.ValueKind == page.ValueKind, "Inspection value kind differs from its content.");
                    page.Value!.Value.WriteTo(writer);
                }
                CheckSize();
                return;
            }
            var kind = page.ValueKind;
            Require(kind is JsonValueKind.Array or JsonValueKind.Object, "Truncated scalar cannot be reconstructed.");
            if (kind == JsonValueKind.Array) writer.WriteStartArray(); else writer.WriteStartObject();
            var total = page.Children.Total;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            var offset = 0;
            while (true)
            {
                Require(page.ValueKind == kind && page.IsTruncated && page.Children.Total == total, "Inspection page shape changed.");
                foreach (var child in page.Children.Items)
                {
                    Require(child.StartsWith(path + "/", StringComparison.Ordinal), "Child pointer is outside its parent.");
                    var segment = child[(path.Length + 1)..];
                    Require(!segment.Contains('/') && seen.Add(segment), "Duplicate or non-immediate child pointer.");
                    if (kind == JsonValueKind.Array)
                        Require(segment == count.ToString(CultureInfo.InvariantCulture), "Array children are missing or unordered.");
                    else writer.WritePropertyName(segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
                    await WriteAsync(child, depth + 1);
                    count++;
                }
                if (page.Children.NextOffset is not { } next) break;
                Require(next > offset && next == count && next < total, "Inspection pagination did not advance consistently.");
                offset = next;
                page = await Fetch(path, offset);
            }
            Require(count == total, "Inspection child pages are incomplete.");
            if (kind == JsonValueKind.Array) writer.WriteEndArray(); else writer.WriteEndObject();
            CheckSize();
        }
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
