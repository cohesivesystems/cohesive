using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;
using DevelopmentMcp.Example;
using ModelContextProtocol.Client;

namespace Cohesive.Semantics.Tests;

public sealed class SemanticWorkspaceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "cohesive-workspace-tests-" + Guid.NewGuid().ToString("N"));
    const string Bundle = "workspace.json";

    public SemanticWorkspaceTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "source.cs"), "authoring input");
    }

    [Fact]
    public void ExportIsDeterministicAndInspectionRetainsAuthoringSource()
    {
        var first = Export();
        Assert.Equal(first, Export());
        var workspace = Open();
        var discovery = workspace.Describe();
        var subject = Assert.Single(discovery.Result.Items);
        Assert.Equal("orders/cancel", subject.Id);
        Assert.Equal(OrderModel.Source, subject.Source);
        Assert.Empty(discovery.ChangedInputs);
        Assert.NotNull(discovery.SemanticFingerprint);
        var inspection = workspace.Inspect(subject.Id, discovery.SnapshotId, "/definition");
        Assert.NotNull(inspection.Result.Value);
        Assert.NotEmpty(inspection.Result.SourceReferences.Items);
        var check = Assert.Single(workspace.Check(discovery.SnapshotId).Result.Items);
        Assert.True(check.DomainValidationAvailable);
        Assert.True(check.IsValid, string.Join("\n", check.Diagnostics.Items));
    }

    [Fact]
    public void SourceEditAndDeletionAreReportedEvenWithoutANewExport()
    {
        Export();
        var workspace = Open();
        var baseline = workspace.Describe();
        File.AppendAllText(Path.Combine(root, "source.cs"), " // changed");
        var stale = workspace.Check(baseline.SnapshotId);
        Assert.Equal(baseline.SnapshotId, stale.SnapshotId);
        Assert.Equal("source.cs", Assert.Single(stale.ChangedInputs));
        File.Delete(Path.Combine(root, "source.cs"));
        Assert.Equal("source.cs", Assert.Single(workspace.Describe().ChangedInputs));
    }

    [Fact]
    public void FailedRefreshNeverFallsBackToPreviousValidGraph()
    {
        Export();
        var workspace = Open();
        workspace.Describe();
        File.WriteAllText(Path.Combine(root, Bundle), "{");
        Assert.ThrowsAny<JsonException>(() => workspace.Describe());
    }

    [Fact]
    public void CompareIgnoresSourceOnlyChangesAndRejectsStaleContext()
    {
        Export();
        var workspace = Open();
        var baseline = workspace.Describe();
        File.AppendAllText(Path.Combine(root, "source.cs"), " // comment");
        Export();
        Assert.Throws<InvalidOperationException>(() => workspace.Resolve("cancel", baseline.SnapshotId));
        var current = workspace.Describe();
        Assert.NotEqual(baseline.InputRevision, current.InputRevision);
        Assert.Equal(baseline.SemanticFingerprint, current.SemanticFingerprint);
        Assert.Empty(workspace.Compare(baseline.SnapshotId, current.SnapshotId).Result.Changes.Items);
    }

    [Fact]
    public void CompareReportsAddedRemovedAndChangedDefinitions()
    {
        Export([Document("a", "one"), Document("removed", "one")]);
        var workspace = Open();
        var baseline = workspace.Describe();
        Export([Document("a", "two"), Document("added", "one")]);
        var current = workspace.Describe();
        var page = workspace.Compare(baseline.SnapshotId, current.SnapshotId, limit: 2).Result.Changes;
        Assert.Equal(3, page.Total);
        Assert.Equal(2, page.NextOffset);
        Assert.Equal(["a", "added"], page.Items.Select(x => x.Id));
        var removed = Assert.Single(workspace.Compare(baseline.SnapshotId, current.SnapshotId, offset: 2).Result.Changes.Items);
        Assert.Equal("removed", removed.Id);
        Assert.Null(removed.After);
    }

    [Fact]
    public void AmbiguousNamesRemainCandidatesAndUnknownKindsDoNotClaimDomainValidation()
    {
        Export([Document("a", "one"), Document("b", "two")]);
        var workspace = Open();
        var current = workspace.Describe();
        Assert.Equal(2, workspace.Resolve("same name", current.SnapshotId).Result.Total);
        Assert.All(workspace.Check(current.SnapshotId).Result.Items, c => Assert.False(c.DomainValidationAvailable));
    }

    [Fact]
    public void InvalidFingerprintRemainsDiagnosableButCannotBeCompared()
    {
        Export();
        var workspace = Open();
        var baseline = workspace.Describe();
        var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(root, Bundle)))!;
        bundle["documents"]![0]!["metadata"]!["fingerprint"]!["value"] = new string('0', 64);
        File.WriteAllText(Path.Combine(root, Bundle), bundle.ToJsonString());
        var invalid = workspace.Describe();
        Assert.Null(invalid.SemanticFingerprint);
        Assert.Contains(workspace.Check(invalid.SnapshotId).Result.Items[0].Diagnostics.Items,
            d => d.Code == ExecutionDefinitionDiagnosticCodes.FingerprintMismatch);
        Assert.Throws<InvalidOperationException>(() => workspace.Compare(baseline.SnapshotId, invalid.SnapshotId));
    }

    [Fact]
    public void ExportRejectsConcurrentSourceChangeAndDuplicateIdentity()
    {
        Assert.Throws<IOException>(() => SemanticWorkspaceExport.Create(root, ["source.cs"], "test", _ =>
        {
            File.AppendAllText(Path.Combine(root, "source.cs"), " changed");
            return [OrderModel.Create()];
        }));
        Assert.Throws<InvalidOperationException>(() => Export([OrderModel.Create(), OrderModel.Create()]));
    }

    [Fact]
    public void InputBoundaryRejectsTraversalAndSymbolicLinks()
    {
        Assert.Throws<ArgumentException>(() => SemanticWorkspaceExport.Create(root, ["../outside.cs"], "test", _ => []));
        File.CreateSymbolicLink(Path.Combine(root, "link.cs"), Path.Combine(root, "source.cs"));
        Assert.Throws<ArgumentException>(() => SemanticWorkspaceExport.Create(root, ["link.cs"], "test", _ => []));
    }

    [Fact]
    public void InspectionBoundsLargeObjectsAndDecodesPointers()
    {
        var payload = new Dictionary<string, string> { ["a/b~c"] = "value" };
        for (var i = 0; i < 100; i++) payload["key" + i] = new string('x', 500);
        Export([ExecutionDefinitionDocument.Create(new("test"), new("large"), new("one"), new { Payload = payload }, Provenance())]);
        var workspace = Open();
        var current = workspace.Describe();
        var large = workspace.Inspect("large", current.SnapshotId, "/definition/payload", limit: 5).Result;
        Assert.Null(large.Value);
        Assert.Equal(101, large.Children.Total);
        Assert.Equal(5, large.Children.NextOffset);
        Assert.Equal("value", workspace.Inspect("large", current.SnapshotId, "/definition/payload/a~1b~0c").Result.Value!.Value.GetString());
        Assert.Throws<ArgumentException>(() => workspace.Inspect("large", current.SnapshotId, "/definition/a~2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Describe(limit: 101));
    }

    [Fact]
    public void RetentionIsBoundedAndEvictedBaselinesAreExplicit()
    {
        Export();
        var workspace = Open();
        var first = workspace.Describe();
        for (var i = 0; i < 4; i++)
        {
            Export([Document("a", i.ToString())]);
            workspace.Describe();
        }
        Assert.Throws<KeyNotFoundException>(() => workspace.Compare(first.SnapshotId, workspace.Describe().SnapshotId));
    }

    [Fact]
    public void DuplicateBundlePropertiesAndUnsupportedVersionsAreRejected()
    {
        var json = Export();
        File.WriteAllText(Path.Combine(root, Bundle), json.Insert(1, "\"schemaVersion\":\"cohesive-workspace/v1\","));
        Assert.Throws<JsonException>(() => Open().Describe());
        File.WriteAllText(Path.Combine(root, Bundle), json.Replace("cohesive-workspace/v1", "cohesive-workspace/v999"));
        Assert.Throws<JsonException>(() => Open().Describe());
    }

    [Fact]
    public void DiagnosticsArePagedWithoutHidingOverallFailure()
    {
        Export();
        var workspace = new SemanticWorkspace(root, Bundle,
            new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>>
            {
                [TransitionDefinitionDocuments.Kind] = _ => DocumentValidationResult.FromDiagnostics(
                    [.. Enumerable.Range(0, 25).Select(i => new DocumentValidationDiagnostic(
                        "test." + i, Cohesive.Model.DiagnosticSeverity.Error, "Failure " + i))])
            });
        var snapshot = workspace.Describe().SnapshotId;
        var first = Assert.Single(workspace.Check(snapshot).Result.Items);
        Assert.False(first.IsValid);
        Assert.Equal(20, first.Diagnostics.Items.Length);
        Assert.Equal(25, first.Diagnostics.Total);
        Assert.Equal(20, first.Diagnostics.NextOffset);
        var last = Assert.Single(workspace.Check(snapshot, diagnosticOffset: 20).Result.Items);
        Assert.Equal(5, last.Diagnostics.Items.Length);
        Assert.Null(last.Diagnostics.NextOffset);
    }

    [Fact]
    public void DomainValidatorCannotEraseSharedIntegrityFailures()
    {
        Export();
        var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(root, Bundle)))!;
        bundle["documents"]![0]!["metadata"]!["fingerprint"]!["value"] = new string('0', 64);
        File.WriteAllText(Path.Combine(root, Bundle), bundle.ToJsonString());
        var workspace = WithValidator(_ => DocumentValidationResult.Valid);
        var check = Assert.Single(workspace.Check(workspace.Describe().SnapshotId).Result.Items);
        Assert.False(check.IsValid);
        Assert.Contains(check.Diagnostics.Items, d => d.Code == ExecutionDefinitionDiagnosticCodes.FingerprintMismatch);

        var normal = Open();
        var normalCheck = Assert.Single(normal.Check(normal.Describe().SnapshotId).Result.Items);
        // Domain validation may attach additional source evidence to the same finding. Retain that evidence.
        Assert.Contains(normalCheck.Diagnostics.Items, d => d.Code == ExecutionDefinitionDiagnosticCodes.FingerprintMismatch && d.Evidence is not null);
        Assert.Equal(normalCheck.Diagnostics.Items.Length, normalCheck.Diagnostics.Items.Distinct().Count());
    }

    [Fact]
    public void CheckOnlyInvokesValidatorsForRequestedPage()
    {
        Export([Document("a", "one"), Document("b", "two")]);
        var calls = new List<string>();
        var workspace = new SemanticWorkspace(root, Bundle,
            new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>>
            {
                [new("test")] = d =>
                {
                    calls.Add(d.Metadata.DefinitionId.Value);
                    return DocumentValidationResult.Valid;
                }
            });
        var snapshot = workspace.Describe().SnapshotId;
        var page = workspace.Check(snapshot, offset: 1, limit: 1).Result;
        Assert.Equal("b", Assert.Single(calls));
        Assert.Equal(2, page.Total);
        calls.Clear();
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Check(snapshot, diagnosticOffset: -1));
        Assert.Empty(calls);
    }

    [Fact]
    public void SourceChangesDuringValidationAreReported()
    {
        Export();
        var workspace = WithValidator(_ =>
        {
            File.AppendAllText(Path.Combine(root, "source.cs"), " changed during validation");
            return DocumentValidationResult.Valid;
        });
        var check = workspace.Check(workspace.Describe().SnapshotId);
        Assert.Equal("source.cs", Assert.Single(check.ChangedInputs));
    }

    [Fact]
    public void BundleReplacementDuringValidationRejectsTheResult()
    {
        Export();
        var workspace = WithValidator(_ =>
        {
            Export([Document("replacement", "one")]);
            return DocumentValidationResult.Valid;
        });
        var error = Assert.Throws<InvalidOperationException>(() => workspace.Check(workspace.Describe().SnapshotId));
        Assert.Contains("changed during inspection", error.Message);
    }

    [Fact]
    public void OversizedScalarIsAnExplicitUnsupportedRead()
    {
        Export([Document("large", new string('x', 17000))]);
        var workspace = Open();
        var error = Assert.Throws<InvalidOperationException>(() => workspace.Inspect("large", workspace.Describe().SnapshotId, "/definition/value"));
        Assert.Contains("local exported artifact", error.Message);
    }

    [Fact]
    public void NullValidatorsAndNullLoweringResultsAreRejectedAtTheirBoundary()
    {
        Assert.Throws<ArgumentException>(() => WithValidator(null!));
        Assert.Throws<InvalidOperationException>(() => SemanticWorkspaceExport.Create(root, ["source.cs"], "test", _ => null!));
        Assert.Throws<InvalidOperationException>(() => Export([null!]));
    }

    [Fact]
    public async Task OfficialSdkClientCanDiscoverInspectCheckAndLearnOverStdio()
    {
        Export();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var basePath = AppContext.BaseDirectory;
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = "dotnet",
            Arguments = ["exec", "--runtimeconfig", Path.Combine(basePath, "Cohesive.Semantics.Tests.runtimeconfig.json"),
                "--depsfile", Path.Combine(basePath, "Cohesive.Semantics.Tests.deps.json"),
                Path.Combine(basePath, "Cohesive.Adapters.Mcp.Development.dll"), root, Bundle]
        }), cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(["check", "compare", "inspect", "learn", "resolve", "workspace"], tools.Select(t => t.Name).Order());
        var discovery = await client.CallToolAsync("workspace", cancellationToken: timeout.Token);
        Assert.False(discovery.IsError ?? false);
        var snapshot = discovery.StructuredContent!.Value.GetProperty("snapshotId").GetString()!;
        foreach (var tool in new[] { "resolve", "inspect", "check", "learn", "compare" })
        {
            var arguments = new Dictionary<string, object?> { ["expectedSnapshot"] = snapshot };
            if (tool == "resolve") arguments["query"] = "cancel";
            if (tool == "inspect") arguments["id"] = "orders/cancel";
            if (tool == "compare") arguments["baselineSnapshot"] = snapshot;
            var result = await client.CallToolAsync(tool, arguments, cancellationToken: timeout.Token);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result.Content));
            Assert.NotNull(result.StructuredContent);
        }
        File.AppendAllText(Path.Combine(root, "source.cs"), " // edited through the agent's normal editor");
        var stale = await client.CallToolAsync("workspace", cancellationToken: timeout.Token);
        Assert.Equal("source.cs", stale.StructuredContent!.Value.GetProperty("changedInputs")[0].GetString());
        Export([Document("added", "one")]);
        var rejected = await client.CallToolAsync("check", new Dictionary<string, object?> { ["expectedSnapshot"] = snapshot }, cancellationToken: timeout.Token);
        Assert.True(rejected.IsError);
        Assert.Contains("snapshot changed", JsonSerializer.Serialize(rejected.Content), StringComparison.OrdinalIgnoreCase);
        var refreshed = await client.CallToolAsync("workspace", cancellationToken: timeout.Token);
        var comparison = await client.CallToolAsync("compare", new Dictionary<string, object?>
        {
            ["baselineSnapshot"] = snapshot,
            ["expectedSnapshot"] = refreshed.StructuredContent!.Value.GetProperty("snapshotId").GetString()
        }, cancellationToken: timeout.Token);
        Assert.False(comparison.IsError ?? false);
        Assert.Equal(2, comparison.StructuredContent!.Value.GetProperty("result").GetProperty("changes").GetProperty("total").GetInt32());
    }

    string Export(IReadOnlyList<ExecutionDefinitionDocument>? documents = null)
    {
        var json = SemanticWorkspaceExport.Create(root, ["source.cs"], "tests/v1", revision => documents ?? [OrderModel.Create(revision)]);
        File.WriteAllText(Path.Combine(root, Bundle), json);
        return json;
    }

    SemanticWorkspace Open() => new(root, Bundle, new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>>
    {
        [TransitionDefinitionDocuments.Kind] = TransitionDefinitionDocuments.Validate
    });

    SemanticWorkspace WithValidator(Func<ExecutionDefinitionDocument, DocumentValidationResult> validate) =>
        new(root, Bundle, new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>>
        {
            [TransitionDefinitionDocuments.Kind] = validate
        });

    static ExecutionProvenance Provenance() => new(new("tests", "1"), new("source.cs"), DocumentOrigin.User);
    static ExecutionDefinitionDocument Document(string id, string value) =>
        ExecutionDefinitionDocument.Create(new("test"), new(id), new(value), new { Value = value }, Provenance(), displayName: "same name");

    public void Dispose() => Directory.Delete(root, recursive: true);
}
