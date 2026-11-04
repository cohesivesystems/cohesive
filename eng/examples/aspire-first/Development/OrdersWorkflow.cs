using System.Diagnostics;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Semantics;
using ModelContextProtocol.Client;

// An opt-in integration exercise, not an MCP tool. All edits/builds run in an isolated source copy.
internal static class OrdersWorkflow
{
    internal static async Task RunAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        var scratch = Path.Combine(Path.GetTempPath(), "cohesive-orders-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var tracked = await RunProcess(root, "git", ["ls-files", "-z"], token);
            foreach (var relative in tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                Copy(relative);
            // Include the new host when exercising an uncommitted working tree.
            foreach (var relative in new[] { "Orders.Development.csproj", "Program.cs", "OrdersWorkflow.cs", "Guide.md" })
                Copy(OrdersWorkspace.Project + "/" + relative);
            void Copy(string relative)
            {
                var source = Path.Combine(root, relative);
                if (!File.Exists(source)) throw new IOException($"Tracked source is missing: {relative}");
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"The exercise does not copy symbolic links: {relative}");
                var destination = Path.Combine(scratch, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }

            await Export();
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command = "dotnet",
                Arguments = [typeof(OrdersWorkspace).Assembly.Location, "serve", scratch]
            }), cancellationToken: token);
            var tools = await client.ListToolsAsync(cancellationToken: token);
            Require(tools.Select(t => t.Name).Order().SequenceEqual(new[] { "check", "compare", "inspect", "learn", "references", "resolve", "workspace" }), "Tool discovery differed.");
            var guide = await Call("learn");
            Require(guide.GetProperty("content").GetString()!.Contains(OrdersWorkspace.Project, StringComparison.Ordinal), "Project guidance is missing.");
            var before = await Workspace();
            Require(before.ChangedInputs.IsEmpty && before.Result.Total == 5, "Expected five fresh domain-owned execution documents.");
            var resolved = await Call("resolve", new() { ["query"] = "fulfillment/reserve", ["expectedSnapshot"] = before.SnapshotId });
            Require(resolved.GetProperty("result").GetProperty("total").GetInt32() == 1, "Reservation identity is ambiguous.");
            var inspection = await Call("inspect", new()
            {
                ["id"] = "fulfillment/reserve", ["expectedSnapshot"] = before.SnapshotId, ["pointer"] = "/definition"
            });
            Require(inspection.GetProperty("result").GetProperty("sourceReferences").GetProperty("items").GetArrayLength() > 0,
                "Root attribution is absent.");
            var sourceMap = await Call("inspect", new()
            {
                ["id"] = "fulfillment/reserve", ["expectedSnapshot"] = before.SnapshotId, ["pointer"] = "/metadata/sourceMap"
            });
            Require(sourceMap.GetProperty("result").GetProperty("value").GetProperty("entries").EnumerateArray()
                .Any(entry => entry.GetProperty("description").GetString()!.Contains("FulfillmentDomain.cs:", StringComparison.Ordinal)),
                "Compiler source-map descriptions did not identify the owning file and line.");
            var guard = await Call("inspect", new()
            {
                ["id"] = "fulfillment/reserve", ["expectedSnapshot"] = before.SnapshotId, ["pointer"] = "/definition/preconditions/1"
            });
            Require(guard.GetProperty("result").GetProperty("sourceReferences").GetProperty("items")[0].GetString()!
                .StartsWith("fulfillment/reserve#.ctor:L", StringComparison.Ordinal), "Guard-specific attribution is absent.");
            var consumers = await Call("references", new() { ["id"] = "fulfillment/reserve", ["expectedSnapshot"] = before.SnapshotId });
            var consumer = consumers.GetProperty("result").GetProperty("references").GetProperty("items").EnumerateArray().Single();
            Require(consumer.GetProperty("isExactMatch").GetBoolean(), "Consumer did not capture the current exact transition.");
            var consumerId = consumer.GetProperty("source").GetProperty("definitionId").GetString()!;
            Require(consumerId == "fulfillment/submit", "The process consumer was not discovered.");
            var consumerInspection = await Call("inspect", new() { ["id"] = consumerId, ["expectedSnapshot"] = before.SnapshotId });
            foreach (var view in new[] { inspection, consumerInspection })
            {
                var mappings = view.GetProperty("result").GetProperty("sourceMappings").GetProperty("items");
                Require(mappings.EnumerateArray().Any(mapping => mapping.TryGetProperty("location", out var location)
                    && location.GetProperty("path").GetString() == OrdersWorkspace.DomainSource
                    && location.GetProperty("startLine").GetInt32() > 0), "Owning source could not be located through structured mappings.");
            }
            await Check(before.SnapshotId);

            var path = Path.Combine(scratch, OrdersWorkspace.DomainSource);
            var source = await File.ReadAllTextAsync(path, token);
            // A demonstration policy change: retain at least one unit rather than exhausting available stock.
            var edited = ReplaceOnce(source, "item.Available >= input.Quantity", "item.Available > input.Quantity");
            edited = ReplaceOnce(edited, "id: new(\"fulfillment/reserve\"), revision: new(\"2\")", "id: new(\"fulfillment/reserve\"), revision: new(\"3\")");
            edited = ReplaceOnce(edited, "[Process(id: \"fulfillment/submit\", revision: \"7\")]", "[Process(id: \"fulfillment/submit\", revision: \"8\")]");
            await File.WriteAllTextAsync(path, edited, token);
            var stale = await Workspace();
            Require(stale.SnapshotId == before.SnapshotId && stale.ChangedInputs.Contains(OrdersWorkspace.DomainSource), "Source edit was not reported before export.");
            await Export();
            var after = await Workspace();
            Require(after.ChangedInputs.IsEmpty && after.SnapshotId != before.SnapshotId, "Refreshed export is not current.");
            var rejected = await client.CallToolAsync("check", new Dictionary<string, object?> { ["expectedSnapshot"] = before.SnapshotId }, cancellationToken: token);
            Require(rejected.IsError == true, "An old snapshot was accepted after export.");
            var checks = await Check(after.SnapshotId);
            var comparison = await Call("compare", new() { ["baselineSnapshot"] = before.SnapshotId, ["expectedSnapshot"] = after.SnapshotId });
            var changedIds = comparison.GetProperty("result").GetProperty("changes").GetProperty("items")
                .EnumerateArray().Select(item => item.GetProperty("id").GetString()!).Order().ToArray();
            Require(changedIds.SequenceEqual(new[] { "fulfillment/reserve", "fulfillment/submit" }),
                "Expected the edited transition and its referencing process to change: " + string.Join(", ", changedIds));

            // Rebuild a comment-only source edit to verify metadata/input changes do not invent semantic changes.
            await File.AppendAllTextAsync(path, "\n// Agent exercise: source-only review note.\n", token);
            await Export();
            var comment = await Workspace();
            var commentComparison = await Call("compare", new() { ["baselineSnapshot"] = after.SnapshotId, ["expectedSnapshot"] = comment.SnapshotId });
            Require(comment.ChangedInputs.IsEmpty && comment.InputRevision != after.InputRevision && comment.SemanticFingerprint == after.SemanticFingerprint,
                "Comment-only edit changed semantics or failed to update source identity.");
            Require(commentComparison.GetProperty("result").GetProperty("changes").GetProperty("total").GetInt32() == 0, "Comment-only comparison reported semantic changes.");
            var evidence = new
            {
                ToolNames = tools.Select(t => t.Name).Order(), Before = before.SnapshotId, After = after.SnapshotId,
                ChangedInputsBeforeExport = stale.ChangedInputs, Inspection = inspection, SourceMap = sourceMap, Guard = guard, Checks = checks,
                Comparison = comparison, ConsumersBeforeEdit = consumers, ConsumerInspection = consumerInspection, CommentOnlyChanges = 0,
                Scope = "Offline source/lowering/MCP exercise; no runtime, storage, API/client or impact-analysis proof. Source-map line numbers describe the isolated copy."
            };
            var report = Path.Combine(root, "tmp", "orders-development-exercise.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine($"PASS: five definitions; stale source and snapshot rejection; transition/process change; comment-only equivalence. Evidence: {report}");

            async Task Export()
            {
                Console.WriteLine("Building and exporting the isolated Orders model...");
                await RunProcess(scratch, "dotnet", ["build", OrdersWorkspace.Project, "--nologo", "-v:q", "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false"], token);
                await RunProcess(scratch, "dotnet", [Path.Combine(OrdersWorkspace.Project, "bin/Debug/net10.0/Orders.Development.dll"), "export", scratch], token);
            }
            async Task<JsonElement> Call(string name, Dictionary<string, object?>? arguments = null)
            {
                var result = await client.CallToolAsync(name, arguments, cancellationToken: token);
                Require(result.IsError != true && result.StructuredContent is not null, $"{name}: {JsonSerializer.Serialize(result.Content)}");
                return result.StructuredContent!.Value;
            }
            async Task<WorkspaceResult<WorkspacePage<WorkspaceSubject>>> Workspace() =>
                (await Call("workspace")).Deserialize<WorkspaceResult<WorkspacePage<WorkspaceSubject>>>(StrictDocumentJson.CreateOptions())!;
            async Task<JsonElement> Check(string snapshot)
            {
                var result = await Call("check", new() { ["expectedSnapshot"] = snapshot });
                foreach (var check in result.GetProperty("result").GetProperty("items").EnumerateArray())
                    Require(check.GetProperty("domainValidationAvailable").GetBoolean() && check.GetProperty("isValid").GetBoolean(), check.GetRawText());
                return result;
            }
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    static string ReplaceOnce(string source, string before, string after)
    {
        Require(source.IndexOf(before, StringComparison.Ordinal) >= 0 && source.IndexOf(before, StringComparison.Ordinal) == source.LastIndexOf(before, StringComparison.Ordinal),
            "Exercise source recipe no longer matches exactly once; update it against the owning model.");
        return source.Replace(before, after, StringComparison.Ordinal);
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static async Task<string> RunProcess(string root, string command, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo(command) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{command} exited {process.ExitCode}: {stdout}\n{stderr}");
        return stdout;
    }
}
