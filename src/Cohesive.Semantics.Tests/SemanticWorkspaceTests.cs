using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;
using DevelopmentMcp.Example;
using ModelContextProtocol.Client;
using Cohesive.Adapters.Mcp.Development;

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
    public void UnmappedCompilerLocationsDoNotAllocatePerConstruct()
    {
        _ = ExecutionSourceLocation.FromCompiler("/build/source.cs", 2);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            _ = ExecutionSourceLocation.FromCompiler("/build/source.cs", 2);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ReferencesRetainMissingAndStaleTargetsAndExplicitCoverage()
    {
        var target = Document("target", "before");
        var consumer = Document("consumer", "uses-target");
        var workspace = new SemanticWorkspace(root, Bundle, referenceReaders:
            new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, IReadOnlyList<ExecutionDefinitionReference>>>
            { [consumer.Kind] = document => document.Metadata.DefinitionId == consumer.Metadata.DefinitionId ? [target.Reference] : [] });
        Export([consumer, target]);
        var current = workspace.Describe();
        var exact = workspace.References("target", current.SnapshotId);
        Assert.True(Assert.Single(exact.Result.References.Items).IsExactMatch);
        Assert.Empty(exact.Result.IncompleteDefinitions);
        Export([consumer, ExecutionDefinitionDocument.Create(target.Kind, target.Metadata.DefinitionId, new("new-revision"),
            new { Value = "before" }, Provenance())]);
        var stale = Assert.Single(workspace.References("target", workspace.Describe().SnapshotId).Result.References.Items);
        Assert.False(stale.IsExactMatch);
        Assert.NotNull(stale.Available);
        Assert.Equal(WorkspaceDocumentReference.FromExecution(target.Reference), stale.Expected);
        Assert.Equal(target.Reference.Fingerprint, stale.Available!.Fingerprint);
        Export([consumer, ExecutionDefinitionDocument.Create(target.Kind, target.Metadata.DefinitionId, target.Metadata.RevisionId,
            new { Value = "after" }, Provenance())]);
        var changedContent = Assert.Single(workspace.References("target", workspace.Describe().SnapshotId).Result.References.Items);
        Assert.Equal(target.Reference.RevisionId, changedContent.Available!.RevisionId);
        Assert.False(changedContent.IsExactMatch);
        Export([consumer]);
        var missing = Assert.Single(workspace.References("target", workspace.Describe().SnapshotId).Result.References.Items);
        Assert.Null(missing.Available);
        Assert.False(missing.IsExactMatch);
        var unknown = new SemanticWorkspace(root, Bundle);
        Assert.Equal("consumer", Assert.Single(unknown.References("target", unknown.Describe().SnapshotId).Result.IncompleteDefinitions));
        Assert.Throws<InvalidOperationException>(() => workspace.References("target", current.SnapshotId));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../source.cs")]
    [InlineData("a/../source.cs")]
    [InlineData("C:/source.cs")]
    [InlineData("a//source.cs")]
    public void SourceLocationsRequirePortableNormalizedPaths(string path) =>
        Assert.Throws<ArgumentException>(() => new ExecutionSourceLocation(path, 1, 1));

    [Fact]
    public void StructuredLocationsRoundTripAndDoNotAlterSemanticFingerprints()
    {
        Assert.Null(ExecutionSourceLocation.FromCompiler("/private/build/source.cs", 5));
        var location = ExecutionSourceLocation.FromCompiler("/_/source.cs", 5)!;
        Assert.Equal("source.cs", location.Path);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionSourceLocation("source.cs", 5, 4));
        var original = Document("mapped", "body");
        var mapped = ExecutionDefinitionDocument.Create(original.Kind, original.Metadata.DefinitionId, original.Metadata.RevisionId,
            new { Value = "body" }, Provenance(), sourceMap: new([
                new("source#body", new(["value"]), location: location),
                new("source#body", new(["value"]), location: new("source.cs", 2, 3))]));
        Assert.Equal(original.Metadata.Fingerprint, mapped.Metadata.Fingerprint);
        var wire = ExecutionDefinitionJsonSerializer.Serialize(mapped);
        var validation = ExecutionDefinitionJsonSerializer.TryDeserialize(wire, out var restored);
        Assert.True(validation.IsValid);
        Assert.Equal(mapped.Metadata.SourceMap, restored!.Metadata.SourceMap);
        Assert.Equal(2, restored.Metadata.SourceMap.Entries[0].Location!.StartLine);
        Export([mapped]);
        var workspace = Open();
        var view = workspace.Inspect("mapped", workspace.Describe().SnapshotId);
        Assert.Equal(2, view.Result.SourceMappings.Total);
        Assert.All(view.Result.SourceMappings.Items, entry => Assert.Equal("source.cs", entry.Location!.Path));
    }

    [Fact]
    public void ChildAndSourcePagesAreIndependent()
    {
        Export();
        var workspace = Open();
        var snapshot = workspace.Describe().SnapshotId;
        var page = workspace.Inspect("orders/cancel", snapshot, "/definition", offset: 1, limit: 1);
        Assert.Single(page.Result.Children.Items);
        Assert.NotEmpty(page.Result.SourceReferences.Items);
        var sourcePage = workspace.Inspect("orders/cancel", snapshot, "/definition", sourceOffset: 100);
        Assert.Empty(sourcePage.Result.SourceReferences.Items);
        Assert.NotEmpty(sourcePage.Result.Children.Items);
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Inspect("orders/cancel", snapshot, sourceOffset: -1));
    }

    [Fact]
    public void SameSizeEditWithRestoredTimestampStillInvalidatesInputs()
    {
        Export();
        var workspace = Open();
        workspace.Describe();
        var path = Path.Combine(root, "source.cs");
        var timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, new string('x', File.ReadAllText(path).Length));
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Equal("source.cs", Assert.Single(workspace.Describe().ChangedInputs));
    }

    [Fact]
    public void InputHashAllocationDoesNotScaleWithFileLength()
    {
        File.WriteAllText(Path.Combine(root, "source.cs"), new string('x', 8 * 1024 * 1024));
        Export();
        var workspace = Open();
        workspace.Describe();
        workspace.Describe();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = workspace.Describe();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(result.ChangedInputs);
        // Two freshness passes must not allocate a buffer proportional to the 8 MiB source file.
        Assert.True(allocated < 256 * 1024, $"Allocated {allocated} bytes.");
    }

    [Fact]
    public void InputRevisionRetainsExistingCanonicalDigestContract()
    {
        Export();
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["source.cs"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "source.cs"))))
        };
        var canonical = StrictDocumentJson.GetCanonicalBytes(new
        {
            Schema = "cohesive-workspace/v1", ProducerVersion = "tests/v1", Inputs = inputs
        }, StrictDocumentJson.CreateOptions());
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(canonical));
        Assert.Equal(expected, Open().Describe().InputRevision);
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
        Assert.Throws<InvalidOperationException>(() => workspace.References("orders/cancel", invalid.SnapshotId));
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
    public void EquivalentSeparatelyAllocatedDiagnosticEvidenceIsDeduplicated()
    {
        Export();
        DocumentValidationDiagnostic Diagnostic() => new("test.evidence", Cohesive.Model.DiagnosticSeverity.Error, "same finding",
            Evidence: new(sourceReferences: ["source.cs"], relatedLocations: ["/definition"], resolutionOptions: ["edit source"]));
        var first = Diagnostic();
        var second = Diagnostic();
        Assert.NotSame(first.Evidence, second.Evidence);
        var workspace = WithValidator(_ => DocumentValidationResult.FromDiagnostics([first, second]));
        Assert.Single(workspace.Check(workspace.Describe().SnapshotId).Result.Items[0].Diagnostics.Items);
    }

    [Fact]
    public void ExportDiscoveryAndAtomicPublicationOwnTheCommonFileWorkflow()
    {
        Directory.CreateDirectory(Path.Combine(root, "model", "bin"));
        Directory.CreateDirectory(Path.Combine(root, "model", "obj"));
        File.WriteAllText(Path.Combine(root, "model", "Model.cs"), "source");
        File.WriteAllText(Path.Combine(root, "model", "bin", "Generated.cs"), "ignored");
        File.WriteAllText(Path.Combine(root, "model", "obj", "assets.json"), "ignored");
        File.WriteAllText(Path.Combine(root, "model", "README.md"), "ignored");
        var inputs = SemanticWorkspaceExport.DiscoverInputs(root, ["model"], ["source.cs"]);
        Assert.Equal(new[] { "model/Model.cs", "source.cs" }, inputs.ToArray());
        SemanticWorkspaceExport.Write(root, Bundle, inputs, "test", revision => [OrderModel.Create(revision)]);
        var previous = File.ReadAllText(Path.Combine(root, Bundle));
        Assert.Throws<InvalidOperationException>(() => SemanticWorkspaceExport.Write(root, Bundle, inputs, "test", _ => throw new InvalidOperationException("failed lowering")));
        Assert.Equal(previous, File.ReadAllText(Path.Combine(root, Bundle)));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        Assert.Throws<ArgumentException>(() => SemanticWorkspaceExport.Write(root, "source.cs", inputs, "test", _ => []));
        Assert.Equal("source", File.ReadAllText(Path.Combine(root, "model", "Model.cs")));
        Assert.Empty(Open().Describe().ChangedInputs);
    }

    [Fact]
    public void HostCanSupplyCustomValidatorsAndSnapshotIndependentGuidance()
    {
        var guide = new DevelopmentGuide("custom/v1", "Custom authoring guidance");
        var workspace = WithValidator(_ => DocumentValidationResult.FromDiagnostics(
            [new("custom.failure", Cohesive.Model.DiagnosticSeverity.Error, "Custom semantics")]));
        var tools = new DevelopmentTools(workspace, guide);
        // No export exists yet; the static guide is still available.
        Assert.Same(guide, tools.Learn());
        Assert.Equal(8, DevelopmentMcpServer.CreateOptions(workspace, guide).ToolCollection!.Count);
        Export();
        var result = tools.Check(workspace.Describe().SnapshotId).Result.Items[0];
        Assert.False(result.GetProperty("isValid").GetBoolean());
        Assert.Contains("custom.failure", result.GetRawText());
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
        Assert.Equal(["check", "compare", "impact", "inspect", "learn", "references", "resolve", "workspace"], tools.Select(t => t.Name).Order());
        var discovery = await client.CallToolAsync("workspace", cancellationToken: timeout.Token);
        Assert.False(discovery.IsError ?? false);
        var snapshot = discovery.StructuredContent!.Value.GetProperty("snapshotId").GetString()!;
        foreach (var tool in new[] { "resolve", "inspect", "check", "learn", "compare", "references", "impact" })
        {
            var arguments = new Dictionary<string, object?>();
            if (tool != "learn") arguments["expectedSnapshot"] = snapshot;
            if (tool == "resolve") arguments["query"] = "cancel";
            if (tool is "inspect" or "references" or "impact") arguments["id"] = "orders/cancel";
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

    [Fact]
    public void ImpactIsCycleSafeDeterministicAndBoundedAcrossDiamonds()
    {
        var docs = new[] { "a", "b", "c", "d", "e" }.Select(id => Document(id, "1")).ToDictionary(d => d.Metadata.DefinitionId.Value);
        var dependencies = new Dictionary<string, string[]> { ["a"] = ["d"], ["b"] = ["a"], ["c"] = ["a"], ["d"] = ["b", "c"], ["e"] = ["d"] };
        Export(docs.Values.Reverse().ToArray());
        var workspace = new SemanticWorkspace(root, Bundle, referenceReaders:
            new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, IReadOnlyList<ExecutionDefinitionReference>>>
            { [new("test")] = d => dependencies[d.Metadata.DefinitionId.Value].Select(id => docs[id].Reference).ToArray() });
        var snapshot = workspace.Describe().SnapshotId;
        var all = workspace.Impact("a", snapshot, maxDepth: 16).Result;
        Assert.Equal(["b", "c", "d", "e"], all.Items.Select(i => i.Consumer.DefinitionId.Value));
        Assert.Equal(["b", "d"], all.Items[2].Path.Select(e => e.Source.DefinitionId.Value));
        Assert.All(all.Items.SelectMany(i => i.Path), edge => Assert.True(edge.IsExactMatch));
        Assert.False(all.DepthLimitReached);
        Assert.False(all.ResultLimitReached);
        Assert.True(workspace.Impact("a", snapshot, maxDepth: 1).Result.DepthLimitReached);
        Assert.False(workspace.Impact("a", snapshot, maxDepth: 3).Result.DepthLimitReached);
        var limited = workspace.Impact("a", snapshot, limit: 2).Result;
        Assert.Equal(2, limited.Items.Length);
        Assert.True(limited.ResultLimitReached);
        Assert.False(limited.DepthLimitReached);
        Assert.Equal(JsonSerializer.Serialize(all), JsonSerializer.Serialize(workspace.Impact("a", snapshot, maxDepth: 16).Result));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Impact("a", snapshot, maxDepth: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Impact("a", snapshot, maxDepth: 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Impact("a", snapshot, limit: 101));
        Export([docs["a"]]);
        Assert.Throws<InvalidOperationException>(() => workspace.Impact("a", snapshot));
    }

    [Fact]
    public void ImpactRetainsMissingEvidenceAndUnknownCoverage()
    {
        var missing = Document("missing", "1");
        var consumer = Document("consumer", "1");
        var unknown = ExecutionDefinitionDocument.Create(new("unknown"), new("unknown"), new("1"), new { }, Provenance());
        Export([consumer, unknown]);
        var workspace = new SemanticWorkspace(root, Bundle, referenceReaders:
            new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, IReadOnlyList<ExecutionDefinitionReference>>>
            { [consumer.Kind] = _ => [missing.Reference] });
        var result = workspace.Impact("missing", workspace.Describe().SnapshotId).Result;
        Assert.Null(Assert.Single(Assert.Single(result.Items).Path).Available);
        Assert.Equal("unknown", Assert.Single(result.IncompleteDefinitions));
        Assert.False(result.DepthLimitReached);
    }

    [Fact]
    public void NativeRelationAndEndpointRoundTripPreserveContentWithoutInventingRevision()
    {
        var query = Cohesive.Relations.Serialization.RelationQueryDocument.FromDefinition(new Cohesive.Relations.IR.QueryDefinition(
            new("query"), new("Query"), new([new Cohesive.Relations.IR.SourceQueryNode(new("source"), new("row"), new(new("graph"), new("shape")))]),
            [new Cohesive.Relations.IR.RowsQueryResultDefinition(new("rows"), new("source"))]));
        var fingerprint = query.DefinitionFingerprint;
        var reference = new ExecutionDefinitionReference(new("query"), new("authored-1"), new(fingerprint.Algorithm, fingerprint.Canonicalization, fingerprint.Value));
        var endpoint = Cohesive.Application.ApplicationEndpointDocument.Create(new(new("query-endpoint"), Cohesive.Api.ApiOperationKind.Query,
            new("GET", "/query"), null, Cohesive.Api.ApiResultKind.Success, reference, null, [], null));
        var queryJson = JsonSerializer.Deserialize<JsonElement>(Cohesive.Relations.Serialization.RelationQueryJsonSerializer.Serialize(query));
        File.WriteAllText(Path.Combine(root, Bundle), SemanticWorkspaceExport.CreateDocuments(root, ["source.cs"], "tests/v1", _ => [queryJson, endpoint.ToJson()]));
        var workspace = DevelopmentMcpServer.CreateDefaultWorkspace(root, Bundle);
        var snapshot = workspace.Describe();
        Assert.Equal(2, snapshot.Result.Total);
        var edge = Assert.Single(workspace.References("query", snapshot.SnapshotId).Result.References.Items);
        Assert.True(edge.IsContentMatch);
        Assert.False(edge.IsExactMatch);
        Assert.Null(edge.Available!.RevisionId);
        Assert.Equal("api:query-endpoint", Assert.Single(workspace.Impact("query", snapshot.SnapshotId).Result.Items).Consumer.DefinitionId.Value);
        var checks = workspace.Check(snapshot.SnapshotId).Result.Items;
        Assert.All(checks, check => Assert.True(check.IsValid));
        Assert.False(checks.Single(c => c.Id == "api:query-endpoint").DomainValidationAvailable);
        var tampered = JsonNode.Parse(endpoint.ToJson().GetRawText())!;
        tampered["endpoint"]!["http"]!["route"] = "/changed";
        Assert.Throws<JsonException>(() => Cohesive.Application.ApplicationEndpointDocument.Read(JsonSerializer.SerializeToElement(tampered)));
        var corruptQuery = JsonNode.Parse(queryJson.GetRawText())!;
        corruptQuery["definitionFingerprint"]!["value"] = new string('0', 64);
        File.WriteAllText(Path.Combine(root, Bundle), SemanticWorkspaceExport.CreateDocuments(root, ["source.cs"], "tests/v1", _ => [JsonSerializer.SerializeToElement(corruptQuery)]));
        var corrupt = workspace.Describe();
        Assert.Null(corrupt.SemanticFingerprint);
        Assert.False(Assert.Single(workspace.Check(corrupt.SnapshotId).Result.Items).IsValid);
        Assert.Throws<InvalidOperationException>(() => workspace.Impact("query", corrupt.SnapshotId));
    }

    [Fact]
    public void ReferenceGraphRejectsAnOversizedReaderAndUnregisteredSchemas()
    {
        var target = Document("target", "1");
        Export([target]);
        var workspace = new SemanticWorkspace(root, Bundle, referenceReaders:
            new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, IReadOnlyList<ExecutionDefinitionReference>>>
            { [target.Kind] = _ => Enumerable.Repeat(target.Reference, 4097).ToArray() });
        var snapshot = workspace.Describe().SnapshotId;
        Assert.Throws<InvalidOperationException>(() => workspace.Impact("target", snapshot));
        File.WriteAllText(Path.Combine(root, Bundle), SemanticWorkspaceExport.CreateDocuments(root, ["source.cs"], "tests/v1",
            _ => [JsonSerializer.SerializeToElement(new { schemaVersion = "unknown/v1" })]));
        Assert.Throws<JsonException>(() => workspace.Describe());
    }

    [Fact]
    public void IdentityOnlyReferencesNeverClaimContentEquality()
    {
        var identity = new WorkspaceDocumentReference(new("api:result"), null, null);
        var edge = new WorkspaceReference(identity, identity, identity);
        Assert.False(edge.IsContentMatch);
        Assert.False(edge.IsExactMatch);
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
