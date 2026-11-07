using System.Text.Json;
using Cohesive.Semantics;
using Cohesive.Model.Serialization;
using ModelContextProtocol.Client;

/// <summary>Replays source authoring against an explicit earlier commit through one MCP session.</summary>
internal static class CancellationWorkflow
{
    internal static async Task RunAsync(string root, string baselineRevision)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var token = deadline.Token;
        var scratch = Path.Combine(Path.GetTempPath(), "cohesive-cancellation-" + Guid.NewGuid().ToString("N"));
        var archive = scratch + ".tar";
        Directory.CreateDirectory(scratch);
        try
        {
            var commit = (await Run(root, "git", ["rev-parse", "--verify", "--end-of-options", baselineRevision + "^{commit}"])).Trim();
            await Run(root, "git", ["archive", "--format=tar", "--output=" + archive, commit]);
            await Run(root, "tar", ["-xf", archive, "-C", scratch]);
            await Export();
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command = "dotnet", Arguments = [typeof(OrdersWorkspace).Assembly.Location, "serve", scratch]
            }), cancellationToken: token);
            var learned = await Call("learn");
            Require(learned.GetProperty("content").GetString()!.Contains("Author cancellation with recovery", StringComparison.Ordinal),
                "Cancellation recipe missing from learn.");
            var before = await Workspace();
            var missing = await Call("resolve", new() { ["query"] = "fulfillment/cancel", ["expectedSnapshot"] = before.SnapshotId });
            Require(missing.GetProperty("result").GetProperty("total").GetInt32() == 0, "The baseline already contains cancellation.");
            var sourceFiles = await Run(root, "git", ["ls-files", "-z"]);
            foreach (var relative in sourceFiles.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var source = Path.Combine(root, relative);
                if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The replay requires ordinary tracked files: " + relative);
                var destination = Path.Combine(scratch, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Actual writes ensure incremental builds observe source replacements.
                await File.WriteAllBytesAsync(destination, await File.ReadAllBytesAsync(source, token), token);
            }
            await Export();
            var after = await Workspace();
            Require(after.ChangedInputs.IsEmpty, "The authored export is stale.");
            var checks = new List<JsonElement>();
            int? offset = 0;
            do
            {
                var page = await Call("check", new() { ["expectedSnapshot"] = after.SnapshotId, ["offset"] = offset.Value, ["limit"] = 100 });
                foreach (var item in page.GetProperty("result").GetProperty("items").EnumerateArray())
                    Require(item.GetProperty("isValid").GetBoolean(), item.GetRawText());
                checks.Add(page);
                offset = page.GetProperty("result").TryGetProperty("nextOffset", out var next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null;
            } while (offset is not null);
            var comparison = await Call("compare", new() { ["baselineSnapshot"] = before.SnapshotId, ["expectedSnapshot"] = after.SnapshotId, ["limit"] = 100 });
            var processReferences = await Call("references", new() { ["id"] = "fulfillment/cancel", ["expectedSnapshot"] = after.SnapshotId });
            var releaseImpact = await Call("impact", new() { ["id"] = "fulfillment/release", ["expectedSnapshot"] = after.SnapshotId, ["maxDepth"] = 8, ["limit"] = 100 });
            var impact = releaseImpact.GetProperty("result");
            Require(!impact.GetProperty("depthLimitReached").GetBoolean() && !impact.GetProperty("resultLimitReached").GetBoolean(),
                "Release impact was truncated.");
            Require(impact.GetProperty("items").EnumerateArray().Any(item =>
                item.GetProperty("consumer").GetProperty("definitionId").GetString() == "api:service/cancellation/operation/cancel"
                && item.GetProperty("path").EnumerateArray().All(edge => edge.GetProperty("isExactMatch").GetBoolean())),
                "Release must reach cancellation admission through exact dependency references.");
            var endpoints = await Call("resolve", new() { ["query"] = "cancellation", ["expectedSnapshot"] = after.SnapshotId, ["limit"] = 100 });
            var contracts = new List<JsonElement>();
            foreach (var subject in endpoints.GetProperty("result").GetProperty("items").EnumerateArray())
            {
                var id = subject.GetProperty("id").GetString()!;
                if (!id.StartsWith("api:", StringComparison.Ordinal)) continue;
                var oldClient = await Compatibility(id, "ExistingClientToProposedServer");
                var newClient = await Compatibility(id, "ProposedClientToExistingServer");
                Require(oldClient.GetProperty("result").GetProperty("verdict").GetString() == "Compatible", "Old clients must tolerate the additional endpoint.");
                Require(newClient.GetProperty("result").GetProperty("verdict").GetString() == "Breaking", "New clients require the new endpoint.");
                contracts.Add(JsonSerializer.SerializeToElement(new { Id = id, ExistingClient = oldClient, ProposedClient = newClient }));
            }
            Require(contracts.Count == 2, "Expected cancellation admission and polling endpoints.");
            var existingOrder = await Compatibility("api:Order.Get", "ExistingClientToProposedServer");
            var reversedOrder = await Compatibility("api:Order.Get", "ProposedClientToExistingServer");
            Require(existingOrder.GetProperty("result").GetProperty("verdict").GetString() == "Unknown",
                "The changed order lifecycle enum requires a wire-catalog interpretation.");
            Require(reversedOrder.GetProperty("result").GetProperty("verdict").GetString() == "Unknown",
                "The reverse lifecycle analysis must retain its coverage gap.");
            var existingContracts = new List<JsonElement>();
            foreach (var id in new[] { "api:Order.Create", "api:Order.Get", "api:Order.Submit", "api:Order.Details",
                "api:Order.ReservationsBySku", "api:Order.availability", "api:service/fulfillment/operation/fulfill",
                "api:service/fulfillment/operation/result" })
                existingContracts.Add(JsonSerializer.SerializeToElement(new
                {
                    Id = id,
                    ExistingClient = await Compatibility(id, "ExistingClientToProposedServer"),
                    ProposedClient = await Compatibility(id, "ProposedClientToExistingServer")
                }));
            var path = Path.Combine(root, "tmp", "orders-cancellation-exercise.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                BaselineCommit = commit, Learn = learned, Before = before.SnapshotId, After = after.SnapshotId,
                Checks = checks, Comparison = comparison, ProcessReferences = processReferences, ReleaseImpact = releaseImpact,
                NewEndpoints = contracts, ExistingContracts = existingContracts, ExistingOrder = existingOrder, ReversedOrder = reversedOrder
            }, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine("PASS: cancellation authoring, validation, dependencies and directional API evidence. " + path);

            Task<string> Run(string directory, string command, string[] arguments) => OrdersWorkflow.RunProcess(directory, command, arguments, token);
            async Task Export()
            {
                Console.WriteLine("Building and exporting isolated cancellation source...");
                await Run(scratch, "dotnet", ["build", OrdersWorkspace.Project, "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false", "-v:q"]);
                await Run(scratch, "dotnet", [Path.Combine(scratch, OrdersWorkspace.Project, "bin/Debug/net10.0/Orders.Development.dll"), "export", scratch]);
            }
            async Task<JsonElement> Call(string tool, Dictionary<string, object?>? arguments = null)
            {
                var result = await client.CallToolAsync(tool, arguments, cancellationToken: token);
                Require(result.IsError != true && result.StructuredContent is not null, tool + ": " + JsonSerializer.Serialize(result.Content));
                return result.StructuredContent!.Value;
            }
            async Task<WorkspaceResult<WorkspacePage<WorkspaceSubject>>> Workspace() =>
                (await Call("workspace")).Deserialize<WorkspaceResult<WorkspacePage<WorkspaceSubject>>>(StrictDocumentJson.CreateOptions())!;
            Task<JsonElement> Compatibility(string id, string direction) => Call("compatibility", new()
            {
                ["id"] = id, ["baselineSnapshot"] = before.SnapshotId, ["expectedSnapshot"] = after.SnapshotId, ["direction"] = direction
            });
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
            File.Delete(archive);
        }
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
