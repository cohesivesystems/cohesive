using System.Text.Json;
using System.Xml.Linq;
using Cohesive.Processes.Compilation;
using Cohesive.Semantics;
using Cohesive.Model.Serialization;
using ModelContextProtocol.Client;

/// <summary>Replays source authoring against an explicit earlier commit through one MCP session.</summary>
internal static class CancellationWorkflow
{
    internal static async Task RunAsync(string root, string baselineRevision, CancellationExerciseScope scope = CancellationExerciseScope.Semantic)
    {
        if (scope == CancellationExerciseScope.Recovery
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")))
            throw new InvalidOperationException("Recovery qualification requires COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING pointing to a disposable PostgreSQL database; skipped tests are not evidence.");
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
            if (scope == CancellationExerciseScope.Recovery)
                Require(learned.GetProperty("content").GetString()!.Contains("Qualify the authored recovery policy", StringComparison.Ordinal),
                    "Recovery qualification recipe missing from learn.");
            var before = await Workspace();
            var missing = await Call("resolve", new() { ["query"] = "fulfillment/cancel", ["expectedSnapshot"] = before.SnapshotId });
            var introducesCancellation = missing.GetProperty("result").GetProperty("total").GetInt32() == 0;
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
                if (introducesCancellation)
                {
                    Require(oldClient.GetProperty("result").GetProperty("verdict").GetString() == "Compatible", "Old clients must tolerate the additional endpoint.");
                    Require(newClient.GetProperty("result").GetProperty("verdict").GetString() == "Breaking", "New clients require the new endpoint.");
                }
                contracts.Add(JsonSerializer.SerializeToElement(new { Id = id, ExistingClient = oldClient, ProposedClient = newClient }));
            }
            Require(contracts.Count == 2, "Expected cancellation admission and polling endpoints.");
            var existingOrder = await Compatibility("api:Order.Get", "ExistingClientToProposedServer");
            var reversedOrder = await Compatibility("api:Order.Get", "ProposedClientToExistingServer");
            if (introducesCancellation)
            {
                Require(existingOrder.GetProperty("result").GetProperty("verdict").GetString() == "Unknown",
                    "The changed order lifecycle enum requires a wire-catalog interpretation.");
                Require(reversedOrder.GetProperty("result").GetProperty("verdict").GetString() == "Unknown",
                    "The reverse lifecycle analysis must retain its coverage gap.");
            }
            var existingContracts = new List<JsonElement>();
            int? apiOffset = 0;
            do
            {
                var page = (await Call("resolve", new() { ["query"] = "api:", ["expectedSnapshot"] = after.SnapshotId,
                    ["offset"] = apiOffset.Value, ["limit"] = 100 })).GetProperty("result");
                foreach (var subject in page.GetProperty("items").EnumerateArray())
                {
                    var id = subject.GetProperty("id").GetString()!;
                    if (!id.StartsWith("api:", StringComparison.Ordinal) || id.StartsWith("api:service/cancellation/", StringComparison.Ordinal)) continue;
                    existingContracts.Add(JsonSerializer.SerializeToElement(new
                    {
                        Id = id,
                        ExistingClient = await Compatibility(id, "ExistingClientToProposedServer"),
                        ProposedClient = await Compatibility(id, "ProposedClientToExistingServer")
                    }));
                }
                apiOffset = page.TryGetProperty("nextOffset", out var nextApi) && nextApi.ValueKind == JsonValueKind.Number ? nextApi.GetInt32() : null;
            } while (apiOffset is not null);
            JsonElement? recovery = null;
            JsonElement? conformance = null;
            if (scope == CancellationExerciseScope.Recovery)
            {
                recovery = await InspectRecovery();
                Console.WriteLine("Running PostgreSQL cancellation and persisted-policy conformance in the isolated source copy...");
                conformance = await VerifyRecovery();
                var verified = await Workspace();
                Require(verified.SnapshotId == after.SnapshotId && verified.ChangedInputs.IsEmpty,
                    "Source changed during conformance; rebuild and repeat qualification.");
            }
            var path = Path.Combine(root, "tmp", scope == CancellationExerciseScope.Recovery
                ? "orders-recovery-exercise.json" : "orders-cancellation-exercise.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                BaselineCommit = commit, Scope = scope.ToString(), Recovery = recovery, Conformance = conformance, Learn = learned, Before = before.SnapshotId, After = after.SnapshotId,
                Checks = checks, Comparison = comparison, ProcessReferences = processReferences, ReleaseImpact = releaseImpact,
                CancellationEndpoints = contracts, ExistingContracts = existingContracts, ExistingOrder = existingOrder, ReversedOrder = reversedOrder
            }, new JsonSerializerOptions { WriteIndented = true }), token);
            Console.WriteLine("PASS: cancellation source replay, validation, dependencies and directional API evidence. " + path);

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
            async Task<JsonElement> Compatibility(string id, string direction)
            {
                var result = await Call("compatibility", new()
                {
                    ["id"] = id, ["baselineSnapshot"] = before.SnapshotId, ["expectedSnapshot"] = after.SnapshotId, ["direction"] = direction
                });
                if (scope == CancellationExerciseScope.Recovery)
                    Require(result.GetProperty("result").GetProperty("verdict").GetString() is "Compatible" or "Unknown",
                        $"Recovery qualification must not break API {id} in direction {direction}: {result}");
                return result;
            }

            async Task<JsonElement> InspectRecovery()
            {
                var subjects = await Call("resolve", new() { ["query"] = "recovery:", ["expectedSnapshot"] = after.SnapshotId, ["limit"] = 100 });
                Require(subjects.GetProperty("result").GetProperty("total").GetInt32() == 3, "Expected exactly three recovery assessments.");
                var assessments = new List<JsonElement>();
                foreach (var subject in subjects.GetProperty("result").GetProperty("items").EnumerateArray())
                {
                    var id = subject.GetProperty("id").GetString()!;
                    async Task<JsonElement> InspectValue(string pointer)
                    {
                        var response = await Call("inspect", new() { ["id"] = id, ["expectedSnapshot"] = after.SnapshotId,
                            ["pointer"] = pointer, ["limit"] = 100 });
                        var result = response.GetProperty("result");
                        if (result.TryGetProperty("value", out var value) && value.ValueKind != JsonValueKind.Null) return value;
                        // These two known array contracts may exceed the transport byte bound. Follow every child page.
                        Require(pointer is "/payload/report/decisions" or "/payload/report/diagnostics",
                            "Inspection exceeded its bound; narrow the recipe pointer: " + pointer);
                        var items = new List<JsonElement>();
                        while (true)
                        {
                            var children = result.GetProperty("children");
                            foreach (var child in children.GetProperty("items").EnumerateArray())
                                items.Add(await InspectValue(child.GetString()!));
                            if (!children.TryGetProperty("nextOffset", out var next) || next.ValueKind != JsonValueKind.Number) break;
                            response = await Call("inspect", new() { ["id"] = id, ["expectedSnapshot"] = after.SnapshotId,
                                ["pointer"] = pointer, ["offset"] = next.GetInt32(), ["limit"] = 100 });
                            result = response.GetProperty("result");
                        }
                        return JsonSerializer.SerializeToElement(items);
                    }
                    var compilation = await InspectValue("/payload/compilation");
                    var options = compilation.Deserialize<ProcessCompilationOptions>(StrictDocumentJson.CreateOptions())!;
                    Require(options == AspireFirst.Orders.CancellationProcess.FencedRecoveryCompilation,
                        "Assessment dropped or changed the source-selected recovery policy.");
                    var profile = (await InspectValue("/payload/profile")).GetString()!;
                    var status = (await InspectValue("/payload/report/status")).GetString();
                    var native = profile == Cohesive.Adapters.Postgres.PostgresProcessRecoveryProfile.Fenced.Id.Value;
                    Require(status == (native ? "Realizable" : "NotRealizable"), "Unexpected recovery status for " + profile);
                    var limitations = await InspectValue("/payload/limitations");
                    Require(!string.IsNullOrWhiteSpace(limitations.GetString()), "Recovery evidence must retain its limitations.");
                    var decisions = await InspectValue("/payload/report/decisions");
                    var diagnostics = await InspectValue("/payload/report/diagnostics");
                    if (!native)
                        Require(diagnostics.EnumerateArray().Any(d => d.GetProperty("severity").GetString() == "Error"
                            && !string.IsNullOrWhiteSpace(d.GetProperty("resolution").GetString())),
                            "An unavailable target must explain how to resolve its recovery gap.");
                    assessments.Add(JsonSerializer.SerializeToElement(new { Id = id, Profile = profile, Status = status,
                        Compilation = compilation, Decisions = decisions, Diagnostics = diagnostics, Limitations = limitations }));
                }
                return JsonSerializer.SerializeToElement(assessments);
            }

            async Task<JsonElement> VerifyRecovery()
            {
                const string integrationProject = "src/Cohesive.Adapters.Aspire.Tests/Cohesive.Adapters.Aspire.Tests.csproj";
                const string unitProject = "src/Cohesive.Tests/Cohesive.Tests.csproj";
                var runs = new List<JsonElement>();
                foreach (var (project, filter, name) in new[]
                {
                    (integrationProject, "FullyQualifiedName~CancellationIntegrationTests", "cancellation"),
                    (unitProject, "FullyQualifiedName~RetainedCompilationPolicySurvivesCommitAndRejectsReplacementBeforeLeaseOrEffects|FullyQualifiedName~CommitCannotRewriteRetainedCompilationPolicy|FullyQualifiedName~RecoveryRejectsChangedCompilationPolicy", "policy")
                })
                {
                    var results = Path.Combine(scratch, "tmp", "recovery-conformance", name);
                    await Run(scratch, "dotnet", ["test", project, "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false",
                        "--filter", filter, "--logger", "trx;LogFileName=results.trx", "--results-directory", results]);
                    var document = XDocument.Load(Path.Combine(results, "results.trx"));
                    XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
                    var tests = document.Descendants(ns + "UnitTestResult").ToArray();
                    Require(tests.Length > 0 && tests.All(test => (string?)test.Attribute("outcome") == "Passed"),
                        "Conformance must execute tests with zero failures or skips: " + name);
                    runs.Add(JsonSerializer.SerializeToElement(new { Project = project, Filter = filter,
                        Tests = tests.Select(test => new { Name = (string?)test.Attribute("testName"), Outcome = (string?)test.Attribute("outcome") }).ToArray() }));
                }
                return JsonSerializer.SerializeToElement(new { Snapshot = after.SnapshotId, Runs = runs,
                    Limitation = "Physical evidence qualifies the tested PostgreSQL composition only. API Unknown verdicts remain unresolved wire-coverage gaps." });
            }
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

internal enum CancellationExerciseScope { Semantic, Recovery }
