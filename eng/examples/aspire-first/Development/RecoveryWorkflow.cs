using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Cohesive.Processes.Compilation;
using Cohesive.Semantics;
using Cohesive.Model.Serialization;

internal static class RecoveryWorkflow
{
    internal static Task RunAsync(string root, string baselineRevision)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")))
            throw new InvalidOperationException("Recovery qualification requires COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING pointing to a disposable PostgreSQL database; skipped tests are not evidence.");
        return CancellationWorkflow.ReplayAsync(root, baselineRevision, QualifyAsync);
    }

    static async Task QualifyAsync(CancellationReplay replay)
    {
        Require(replay.Evidence["Learn"]!["content"]!.GetValue<string>().Contains("Qualify the authored recovery policy", StringComparison.Ordinal),
            "Recovery qualification recipe missing from learn.");
        replay.Evidence["Recovery"] = JsonSerializer.SerializeToNode(await InspectRecovery(replay));
        Console.WriteLine("Running PostgreSQL cancellation and persisted-policy conformance in the isolated source copy...");
        replay.Evidence["Conformance"] = JsonSerializer.SerializeToNode(await VerifyRecovery(replay));
        await replay.SaveAsync("orders-recovery-exercise.json");
    }

    static async Task<JsonElement> InspectRecovery(CancellationReplay replay)
    {
        var assessments = new List<JsonElement>();
        var qualifiedNative = false;
        int? offset = 0;
        do
        {
            var subjects = (await replay.CallAsync("resolve", new() { ["query"] = "recovery:",
                ["expectedSnapshot"] = replay.Snapshot, ["offset"] = offset.Value, ["limit"] = 100 })).GetProperty("result");
            foreach (var subject in subjects.GetProperty("items").EnumerateArray())
            {
                if (subject.GetProperty("kind").GetString() != "process-recovery-assessment") continue;
                var id = subject.GetProperty("id").GetString()!;
                var payload = await WorkspaceInspectionReader.ReadAsync("/payload", async (pointer, childOffset, token) =>
                {
                    var response = await replay.CallAsync("inspect", new() { ["id"] = id, ["expectedSnapshot"] = replay.Snapshot,
                        ["pointer"] = pointer, ["offset"] = childOffset, ["limit"] = 100 });
                    return response.GetProperty("result").Deserialize<WorkspaceInspection>(StrictDocumentJson.CreateOptions())!;
                }, cancellationToken: replay.Token);
                if (payload.GetProperty("definition").GetProperty("definitionId").GetString() != "fulfillment/cancel") continue;
                var options = payload.GetProperty("compilation").Deserialize<ProcessCompilationOptions>(StrictDocumentJson.CreateOptions())!;
                Require(options == AspireFirst.Orders.CancellationProcess.FencedRecoveryCompilation, "Assessment changed the source-selected recovery policy.");
                var status = payload.GetProperty("report").GetProperty("status").GetString();
                var diagnostics = payload.GetProperty("report").GetProperty("diagnostics");
                Require(status is "Realizable" or "NotRealizable", "Invalid recovery profile assessment.");
                if (status == "NotRealizable")
                    Require(diagnostics.EnumerateArray().Any(d => d.GetProperty("severity").GetString() == "Error"
                        && !string.IsNullOrWhiteSpace(d.GetProperty("resolution").GetString())), "Unavailable target must explain its gap.");
                var profile = payload.GetProperty("profile").GetString();
                if (profile == Cohesive.Adapters.Postgres.PostgresProcessRecoveryProfile.Fenced.Id.Value)
                {
                    Require(status == "Realizable", "The PostgreSQL composition selected for conformance is unavailable.");
                    qualifiedNative = true;
                }
                Require(!string.IsNullOrWhiteSpace(payload.GetProperty("limitations").GetString()), "Assessment limitations are missing.");
                assessments.Add(JsonSerializer.SerializeToElement(new { Id = id, Payload = payload }));
            }
            offset = subjects.TryGetProperty("nextOffset", out var next) && next.ValueKind == JsonValueKind.Number ? next.GetInt32() : null;
        } while (offset is not null);
        Require(qualifiedNative, "The selected PostgreSQL recovery assessment is missing.");
        return JsonSerializer.SerializeToElement(assessments);
    }

    static async Task<JsonElement> VerifyRecovery(CancellationReplay replay)
    {
        const string integrationProject = "src/Cohesive.Adapters.Aspire.Tests/Cohesive.Adapters.Aspire.Tests.csproj";
        const string unitProject = "src/Cohesive.Tests/Cohesive.Tests.csproj";
        using var manifest = typeof(RecoveryWorkflow).Assembly.GetManifestResourceStream("Orders.Development.RecoveryConformance.tests.json")!;
        var expected = JsonSerializer.Deserialize<Dictionary<string, string[]>>(manifest)!;
        var runs = new List<JsonElement>();
        foreach (var (project, filter, name) in new[]
        {
            (integrationProject, "FullyQualifiedName~CancellationIntegrationTests", "cancellation"),
            (unitProject, "FullyQualifiedName~RetainedCompilationPolicySurvivesCommitAndRejectsReplacementBeforeLeaseOrEffects|FullyQualifiedName~CommitCannotRewriteRetainedCompilationPolicy|FullyQualifiedName~RecoveryRejectsChangedCompilationPolicy", "policy")
        })
        {
            var results = Path.Combine(replay.Scratch, "tmp", "recovery-conformance", name);
            await OrdersWorkflow.RunProcess(replay.Scratch, "dotnet", ["test", project, "-m:1", "-nodeReuse:false", "-p:UseSharedCompilation=false",
                "--filter", filter, "--logger", "trx;LogFileName=results.trx", "--results-directory", results], replay.Token);
            var document = XDocument.Load(Path.Combine(results, "results.trx"));
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var tests = document.Descendants(ns + "UnitTestResult").ToArray();
            ExerciseAssertions.RequireTests(document, expected[name], name);
            runs.Add(JsonSerializer.SerializeToElement(new { Project = project, Filter = filter,
                Tests = tests.Select(test => new { Name = (string?)test.Attribute("testName"), Outcome = (string?)test.Attribute("outcome") }).ToArray() }));
        }
        return JsonSerializer.SerializeToElement(new { Snapshot = replay.Snapshot, Runs = runs,
            Limitation = "Physical evidence qualifies the tested PostgreSQL composition only. API Unknown verdicts remain unresolved wire-coverage gaps." });
    }
    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
