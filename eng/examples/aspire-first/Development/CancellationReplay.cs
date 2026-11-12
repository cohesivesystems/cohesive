using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Semantics;
using Cohesive.Model.Serialization;
using ModelContextProtocol.Client;

// Owned by ReplayAsync; the completion callback must finish before its client and scratch directory are disposed.
internal sealed record CancellationReplay(string Root, string Scratch, string Snapshot, JsonObject Evidence,
    McpClient Client, CancellationToken Token)
{
    internal async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await Client.CallToolAsync(tool, arguments, cancellationToken: Token);
        if (result.IsError == true || result.StructuredContent is null)
            throw new InvalidOperationException(tool + ": " + JsonSerializer.Serialize(result.Content));
        return result.StructuredContent.Value;
    }

    internal async Task RequireUnchangedAsync()
    {
        var workspace = (await CallAsync("workspace")).Deserialize<WorkspaceResult<WorkspacePage<WorkspaceSubject>>>(StrictDocumentJson.CreateOptions())!;
        if (workspace.SnapshotId != Snapshot || !workspace.ChangedInputs.IsEmpty)
            throw new InvalidOperationException("Source changed during conformance; rebuild and repeat qualification.");
    }

    internal async Task SaveAsync(string file)
    {
        await RequireUnchangedAsync();
        var path = Path.Combine(Root, "tmp", file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Evidence.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Token);
        Console.WriteLine("PASS: cancellation source replay and qualification evidence. " + path);
    }
}
