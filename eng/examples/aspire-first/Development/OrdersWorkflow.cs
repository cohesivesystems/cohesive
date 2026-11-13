using System.Diagnostics;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Semantics;
using Cohesive.Adapters.Mcp.Development;
using ModelContextProtocol.Client;

// An opt-in integration exercise, not an MCP tool. All edits/builds run in an isolated source copy.
internal static class OrdersWorkflow
{
    internal static async Task RunAsync(string root, string? authoringBase)
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

            // With a base revision, replay the actual edits to the two authoring files in the isolated copy.
            // The host, serializer and tool versions stay fixed for both snapshots.
            string[] authoringFiles = ["eng/examples/aspire-first/Orders.Model/FulfillmentQueries.cs", "eng/examples/aspire-first/Orders.Model/OrderApi.cs"];
            if (authoringBase is not null)
            {
                var commit = (await RunProcess(root, "git", ["rev-parse", "--verify", "--end-of-options", authoringBase + "^{commit}"], token)).Trim();
                foreach (var relative in authoringFiles)
                    await File.WriteAllTextAsync(Path.Combine(scratch, relative), await RunProcess(root, "git", ["show", commit + ":" + relative], token), token);
            }
            // Match the retained generated reader in OrdersWireContext. This policy is local to the
            // exercise copy; the application exporter keeps its normal ASP.NET web defaults.
            var exporterPath = Path.Combine(scratch, OrdersWorkspace.Project, "Program.cs");
            var exporter = await File.ReadAllTextAsync(exporterPath, token);
            exporter = ReplaceOnce(exporter, "new JsonSerializerOptions(JsonSerializerDefaults.Web),",
                "new JsonSerializerOptions(JsonSerializerDefaults.Web) { RespectRequiredConstructorParameters = true },");
            exporter = ReplaceOnce(exporter, "aspnet-web-defaults/problem-details-v1", "exercise/strict-constructors/problem-details-v1");
            await File.WriteAllTextAsync(exporterPath, exporter, token);
            await Export();
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
            {
                Command = "dotnet",
                Arguments = [typeof(OrdersWorkspace).Assembly.Location, "serve", scratch]
            }), cancellationToken: token);
            var tools = await client.ListToolsAsync(cancellationToken: token);
            Require(tools.Select(t => t.Name).Order().SequenceEqual(new[] { "check", "compare", "compatibility", "impact", "inspect", "learn", "references", "resolve", "workspace" }), "Tool discovery differed.");
            var guide = await Call("learn");
            Require(guide.GetProperty("content").GetString()!.Contains("Author a query and expose it as a read endpoint", StringComparison.Ordinal), "Query authoring recipe is missing.");
            JsonElement? authoringComparison = null;
            JsonElement? existingClients = null;
            JsonElement? proposedClients = null;
            const string skuQuery = "fulfillment/reservations-by-sku";
            const string skuEndpoint = "api:Order.ReservationsBySku";
            if (authoringBase is not null)
            {
                var baseline = await Workspace();
                foreach (var id in new[] { skuQuery, skuEndpoint })
                {
                    var resolvedBase = await Call("resolve", new() { ["query"] = id, ["expectedSnapshot"] = baseline.SnapshotId });
                    Require(resolvedBase.GetProperty("result").GetProperty("total").GetInt32() == 0, $"Authoring base already contains {id}.");
                }
                // Write the edits with fresh modification times so incremental compilation sees the restored source.
                foreach (var relative in authoringFiles)
                    await File.WriteAllBytesAsync(Path.Combine(scratch, relative), await File.ReadAllBytesAsync(Path.Combine(root, relative), token), token);
                await Export();
                var proposed = await Workspace();
                authoringComparison = await Call("compare", new() { ["baselineSnapshot"] = baseline.SnapshotId, ["expectedSnapshot"] = proposed.SnapshotId, ["limit"] = 100 });
                foreach (var id in new[] { skuQuery, skuEndpoint })
                    Require(authoringComparison.Value.GetProperty("result").GetProperty("changes").GetProperty("items").EnumerateArray()
                        .Any(item => item.GetProperty("id").GetString() == id && (!item.TryGetProperty("before", out var old) || old.ValueKind == JsonValueKind.Null)),
                        $"Authored addition missing: {id}.");
                async Task<JsonElement> CompareClients(string direction) => await Call("compatibility", new()
                {
                    ["id"] = skuEndpoint, ["baselineSnapshot"] = baseline.SnapshotId, ["expectedSnapshot"] = proposed.SnapshotId, ["direction"] = direction
                });
                existingClients = await CompareClients("ExistingClientToProposedServer");
                proposedClients = await CompareClients("ProposedClientToExistingServer");
                Require(existingClients.Value.GetProperty("result").GetProperty("verdict").GetString() == "Compatible", "Existing clients should tolerate an added endpoint.");
                var reverse = proposedClients.Value.GetProperty("result");
                Require(reverse.GetProperty("verdict").GetString() == "Breaking" && reverse.GetProperty("diagnostics").EnumerateArray()
                    .Any(d => d.GetProperty("code").GetString() == "api.compatibility.endpointRemoved"), "New clients require the missing endpoint.");
            }
            var before = await Workspace();
            Require(before.ChangedInputs.IsEmpty && before.Result.Total > 5, "Expected fresh execution, relation and endpoint documents.");
            var skuReferences = await Call("references", new() { ["id"] = skuQuery, ["expectedSnapshot"] = before.SnapshotId });
            Require(skuReferences.GetProperty("result").GetProperty("references").GetProperty("items").EnumerateArray().Any(row =>
                row.GetProperty("source").GetProperty("definitionId").GetString() == skuEndpoint && row.GetProperty("isContentMatch").GetBoolean()),
                "Reservations-by-SKU endpoint does not reference the authored query fingerprint.");
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
            var transitionImpact = await Call("impact", new() { ["id"] = "fulfillment/reserve", ["expectedSnapshot"] = before.SnapshotId });
            var potential = transitionImpact.GetProperty("result").GetProperty("items").EnumerateArray().ToArray();
            Require(potential.Any(item => item.GetProperty("consumer").GetProperty("definitionId").GetString() == "fulfillment")
                && potential.Any(item => item.GetProperty("consumer").GetProperty("definitionId").GetString()!.StartsWith("api:", StringComparison.Ordinal)
                    && item.GetProperty("path").GetArrayLength() == 2),
                "Expected service and API consumers through the process; direct process bindings yield the shortest endpoint paths.");

            var path = Path.Combine(scratch, OrdersWorkspace.DomainSource);
            var source = await File.ReadAllTextAsync(path, token);
            // A demonstration policy change: retain at least one unit rather than exhausting available stock.
            var edited = ReplaceOnce(source, "item.Available >= input.Quantity", "item.Available > input.Quantity");
            edited = ReplaceOnce(edited, "id: new(\"fulfillment/reserve\"), revision: new(\"2\")", "id: new(\"fulfillment/reserve\"), revision: new(\"3\")");
            edited = ReplaceOnce(edited, "[Process(id: \"fulfillment/submit\", revision: \"8\")]", "[Process(id: \"fulfillment/submit\", revision: \"9\")]");
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
            var predicted = transitionImpact.GetProperty("result").GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("consumer").GetProperty("definitionId").GetString()!).Append("fulfillment/reserve").Order().ToArray();
            Require(changedIds.SequenceEqual(predicted), "Actual transition propagation differed from reference evidence: " + string.Join(", ", changedIds));

            const string queryId = "fulfillment/order-details";
            var queryBefore = await Call("inspect", new() { ["id"] = queryId, ["expectedSnapshot"] = after.SnapshotId, ["pointer"] = "/definition" });
            var queryConsumers = await Call("references", new() { ["id"] = queryId, ["expectedSnapshot"] = after.SnapshotId });
            var queryConsumer = queryConsumers.GetProperty("result").GetProperty("references").GetProperty("items").EnumerateArray().Single();
            Require(queryConsumer.GetProperty("isContentMatch").GetBoolean() && !queryConsumer.GetProperty("isExactMatch").GetBoolean(),
                "Native query content evidence or unknown revision was misrepresented.");
            var endpointId = queryConsumer.GetProperty("source").GetProperty("definitionId").GetString()!;
            var endpointBefore = await Call("inspect", new() { ["id"] = endpointId, ["expectedSnapshot"] = after.SnapshotId });
            const string querySource = "eng/examples/aspire-first/Orders.Model/FulfillmentQueries.cs";
            var queryPath = Path.Combine(scratch, querySource);
            var queryText = await File.ReadAllTextAsync(queryPath, token);
            await File.WriteAllTextAsync(queryPath, ReplaceOnce(queryText,
                "Where(order => order.Partition == FulfillmentStoragePolicy.LocalPartition)",
                "Where(order => order.Partition == FulfillmentStoragePolicy.LocalPartition && order.Status == OrderStatus.Submitted)"), token);
            var queryStale = await Workspace();
            Require(queryStale.ChangedInputs.Contains(querySource), "Query source edit was not reported.");
            await Export();
            var queryAfter = await Workspace();
            await Check(queryAfter.SnapshotId);
            var queryComparison = await Call("compare", new() { ["baselineSnapshot"] = after.SnapshotId, ["expectedSnapshot"] = queryAfter.SnapshotId });
            var queryChanges = queryComparison.GetProperty("result").GetProperty("changes").GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()!).Order().ToArray();
            Require(queryChanges.SequenceEqual(new[] { endpointId, queryId }.Order()), "Expected only the native query and bound API projection to change.");
            var queryImpact = await Call("impact", new() { ["id"] = queryId, ["expectedSnapshot"] = queryAfter.SnapshotId });
            Require(queryImpact.GetProperty("result").GetProperty("items").GetArrayLength() == 1, "Expected the actual query endpoint consumer.");

            var fields = await Call("resolve", new() { ["query"] = "AvailableStock", ["expectedSnapshot"] = queryAfter.SnapshotId, ["limit"] = 100 });
            JsonElement fieldImpact = default;
            string? fieldId = null;
            foreach (var candidate in fields.GetProperty("result").GetProperty("items").EnumerateArray())
            {
                var candidateId = candidate.GetProperty("id").GetString()!;
                var evidencePath = await Call("impact", new() { ["id"] = candidateId, ["expectedSnapshot"] = queryAfter.SnapshotId, ["maxDepth"] = 8, ["limit"] = 100 });
                var ids = evidencePath.GetProperty("result").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("consumer").GetProperty("definitionId").GetString()).ToArray();
                if (ids.Contains(queryId) && ids.Contains(endpointId)) { fieldId = candidateId; fieldImpact = evidencePath; break; }
            }
            Require(fieldId is not null, "No field → shape/type → query → API path was found.");
            var responseSource = await File.ReadAllTextAsync(queryPath, token);
            responseSource = ReplaceOnce(responseSource, "int AvailableStock, bool Released = false);", "int AvailableStock, bool Released = false)\n{\n    /// <summary>Optional inventory partition.</summary>\n    public string? InventoryPartition { get; init; }\n}");
            responseSource = ReplaceOnce(responseSource, ".Field(result => result.AvailableStock, inventory, item => item.Available)",
                ".Field(result => result.InventoryPartition, inventory, item => item.Partition)\n                    .Field(result => result.AvailableStock, inventory, item => item.Available)");
            await File.WriteAllTextAsync(queryPath, responseSource, token);
            await Export();
            var additive = await Workspace();
            await Check(additive.SnapshotId);
            var additiveCompatibility = await Call("compatibility", new() { ["id"] = endpointId, ["baselineSnapshot"] = queryAfter.SnapshotId, ["expectedSnapshot"] = additive.SnapshotId });
            Require(additiveCompatibility.GetProperty("result").GetProperty("verdict").GetString() == "Compatible",
                "Adding an optional response field should preserve this tolerant reader contract: " + additiveCompatibility.GetRawText());
            var additivePayload = await RunProcess(scratch, "dotnet", [Path.Combine(OrdersWorkspace.Project, "bin/Debug/net10.0/Orders.Development.dll"), "wire-sample", scratch], token);
            Require(JsonSerializer.Deserialize<JsonElement>(additivePayload).GetProperty("reservations")[0].TryGetProperty("inventoryPartition", out _)
                && JsonSerializer.SerializeToElement(JsonSerializer.Deserialize(additivePayload, OrdersWireContext.Default.OrderDetails), OrdersWireContext.Default.OrderDetails!).GetProperty("reservations")[0].GetProperty("availableStock").GetInt32() == 42,
                "Old generated reader did not preserve the known value with an additional response field.");
            responseSource = ReplaceOnce(responseSource, "int AvailableStock,", "int RemainingStock,");
            responseSource = ReplaceOnce(responseSource, "result => result.AvailableStock", "result => result.RemainingStock");
            responseSource = ReplaceOnce(responseSource, "<param name=\"AvailableStock\">", "<param name=\"RemainingStock\">");
            await File.WriteAllTextAsync(queryPath, responseSource, token);
            Require((await Workspace()).ChangedInputs.Contains(querySource), "Response-field source change was not detected.");
            await Export();
            var responseAfter = await Workspace();
            await Check(responseAfter.SnapshotId);
            var responseComparison = await Call("compare", new() { ["baselineSnapshot"] = additive.SnapshotId, ["expectedSnapshot"] = responseAfter.SnapshotId, ["limit"] = 100 });
            Require(responseComparison.GetProperty("result").GetProperty("changes").GetProperty("items").EnumerateArray()
                .Any(i => i.GetProperty("id").GetString() == endpointId), "Response-field change did not change the portable endpoint contract.");
            var compatibility = await Call("compatibility", new() { ["id"] = endpointId, ["baselineSnapshot"] = additive.SnapshotId, ["expectedSnapshot"] = responseAfter.SnapshotId });
            Require(compatibility.GetProperty("result").GetProperty("verdict").GetString() == "Breaking", "Removing an existing required response field must break old clients.");
            Require(compatibility.GetProperty("result").GetProperty("diagnostics").EnumerateArray().Any(d =>
                d.GetProperty("code").GetString() == "api.compatibility.requiredField" && d.GetProperty("location").GetString()!.EndsWith("/availableStock", StringComparison.Ordinal)),
                "Compatibility did not explain the affected response-field path.");

            var payload = await RunProcess(scratch, "dotnet", [Path.Combine(OrdersWorkspace.Project, "bin/Debug/net10.0/Orders.Development.dll"), "wire-sample", scratch], token);
            var emitted = JsonSerializer.Deserialize<JsonElement>(payload);
            Require(emitted.GetProperty("reservations")[0].GetProperty("remainingStock").GetInt32() == 42, "Rebuilt generated writer did not emit the revised field.");
            try
            {
                _ = JsonSerializer.Deserialize(payload, OrdersWireContext.Default.OrderDetails);
                throw new InvalidOperationException("Old generated reader accepted the missing required response field.");
            }
            catch (JsonException error) when (error.Message.Contains("availableStock", StringComparison.OrdinalIgnoreCase)) { }

            // Rebuild a comment-only source edit to verify metadata/input changes do not invent semantic changes.
            await File.AppendAllTextAsync(path, "\n// Agent exercise: source-only review note.\n", token);
            await Export();
            var comment = await Workspace();
            var commentComparison = await Call("compare", new() { ["baselineSnapshot"] = responseAfter.SnapshotId, ["expectedSnapshot"] = comment.SnapshotId });
            Require(comment.ChangedInputs.IsEmpty && comment.InputRevision != responseAfter.InputRevision && comment.SemanticFingerprint == responseAfter.SemanticFingerprint,
                "Comment-only edit changed semantics or failed to update source identity.");
            Require(commentComparison.GetProperty("result").GetProperty("changes").GetProperty("total").GetInt32() == 0, "Comment-only comparison reported semantic changes.");
            var evidence = new
            {
                AuthoringBase = authoringBase, AuthoringComparison = authoringComparison, ExistingClients = existingClients, ProposedClients = proposedClients, SkuReferences = skuReferences,
                ToolNames = tools.Select(t => t.Name).Order(), Before = before.SnapshotId, After = after.SnapshotId,
                ChangedInputsBeforeExport = stale.ChangedInputs, Inspection = inspection, SourceMap = sourceMap, Guard = guard, Checks = checks,
                AdditiveCompatibility = additiveCompatibility, OldGeneratedReaderAcceptedAddition = true, RevisedPayload = emitted, OldGeneratedReaderRejected = true, FieldIdentity = fieldId, FieldImpact = fieldImpact, ResponseComparison = responseComparison, Compatibility = compatibility,
                QueryInspection = queryBefore, EndpointInspection = endpointBefore, QueryComparison = queryComparison, QueryImpact = queryImpact, TransitionImpact = transitionImpact,
                Comparison = comparison, ConsumersBeforeEdit = consumers, ConsumerInspection = consumerInspection, CommentOnlyChanges = 0,
                Scope = "Offline source/lowering/MCP exercise; bounded dependencies and directional HTTP/JSON policy checked against generated Orders readers/writers; no deployed runtime or storage qualification. Source-map line numbers describe the isolated copy."
            };
            var report = Path.Combine(root, "tmp", "orders-development-exercise.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine($"PASS: mixed domain documents; stale source and snapshot rejection; transition/service/API propagation; query/API change; bounded impact; additive and breaking response-field compatibility; comment-only equivalence. Evidence: {report}");

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
                JsonElement result;
                var offset = 0;
                do
                {
                    result = await Call("check", new() { ["expectedSnapshot"] = snapshot, ["offset"] = offset, ["limit"] = 100 });
                    foreach (var check in result.GetProperty("result").GetProperty("items").EnumerateArray())
                    {
                        Require(check.GetProperty("isValid").GetBoolean(), check.GetRawText());
                        if (!check.GetProperty("domainValidationAvailable").GetBoolean())
                        {
                            // Recovery assessments have checked integrity, not an executable domain validator.
                            // Keep requiring domain validation for every other exported document kind.
                            var schema = await Call("inspect", new() { ["id"] = check.GetProperty("id").GetString(),
                                ["expectedSnapshot"] = snapshot, ["pointer"] = "/schemaVersion" });
                            Require(schema.GetProperty("result").GetProperty("value").GetString() == ProcessRecoveryWorkspaceProjection.Schema,
                                check.GetRawText());
                        }
                    }
                    offset = result.GetProperty("result").TryGetProperty("nextOffset", out var next) && next.ValueKind == JsonValueKind.Number
                        ? next.GetInt32() : -1;
                } while (offset >= 0);
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

    internal static async Task<string> RunProcess(string root, string command, string[] arguments, CancellationToken token)
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
