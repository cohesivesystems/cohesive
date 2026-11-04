using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using Cohesive.Relations.Serialization;
using Cohesive.Semantics;
using Cohesive.Transitions.IR;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cohesive.Adapters.Mcp.Development;

// Transport projection only. Domain validators and canonical documents retain semantic authority.
internal sealed class DevelopmentTools(string root, string bundlePath)
{
    readonly SemanticWorkspace workspace = new(root, bundlePath,
        new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>>
        {
            [TransitionDefinitionDocuments.Kind] = TransitionDefinitionDocuments.Validate,
            [ProcessDefinitionDocuments.Kind] = ProcessDefinitionDocuments.Validate,
            [HostedQueryDefinitionDocuments.Kind] = HostedQueryDefinitionDocuments.Validate
        });

    [McpServerTool(Name = "workspace", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Discover the exported semantic scope, exact snapshot, declared-source freshness, and definition identities. Start here; page with nextOffset.")]
    public WorkspaceResult<WorkspacePage<WorkspaceSubject>> Workspace(int offset = 0, int limit = 20)
        => Invoke(() => workspace.Describe(offset, limit));

    [McpServerTool(Name = "resolve", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find definition identities by identity, display name or owning source reference. Returns all matching candidates; never silently chooses an ambiguous name.")]
    public WorkspaceResult<WorkspacePage<WorkspaceSubject>> Resolve(string query, string expectedSnapshot, int offset = 0, int limit = 20)
        => Invoke(() => workspace.Resolve(query, expectedSnapshot, offset, limit));

    [McpServerTool(Name = "inspect", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read canonical execution semantics and source mappings by exact definition identity. pointer is an RFC 6901 path in the complete document; empty selects the root. Large values return child pointers. Edit owning source, never this derived JSON.")]
    public WorkspaceResult<WorkspaceInspection> Inspect(string id, string expectedSnapshot, string pointer = "", int offset = 0, int limit = 20)
        => Invoke(() => workspace.Inspect(id, expectedSnapshot, pointer, offset, limit));

    [McpServerTool(Name = "check", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Validate exported documents with existing transition, process and hosted-query validators. Does not build source, prove runtime invariants, link external contracts, or validate target realization. Check changedInputs and domainValidationAvailable.")]
    public WorkspaceResult<WorkspacePage<JsonElement>> Check(string expectedSnapshot, int offset = 0, int limit = 20, int diagnosticOffset = 0)
    {
        var checkedWorkspace = Invoke(() => workspace.Check(expectedSnapshot, offset, limit, diagnosticOffset));
        // .NET 10 schema inference cannot serialize default ImmutableArray constructor parameters
        // in DocumentDiagnosticEvidence. Preserve the existing diagnostic wire contract as JSON here;
        // do not introduce a second diagnostic model merely to accommodate transport schema inference.
        var page = checkedWorkspace.Result;
        return new(checkedWorkspace.SnapshotId, checkedWorkspace.InputRevision, checkedWorkspace.SemanticFingerprint,
            checkedWorkspace.ChangedInputs, checkedWorkspace.Limitations,
            new([.. page.Items.Select(item => JsonSerializer.SerializeToElement(item, DiagnosticOptions))], page.Total, page.NextOffset));
    }

    static readonly JsonSerializerOptions DiagnosticOptions = StrictDocumentJson.CreateOptions();

    [McpServerTool(Name = "compare", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Compare current exported semantic fingerprints with a retained baseline snapshot. Reports additions, removals and document content changes; metadata-only changes are omitted. Four snapshots are retained. This is not compatibility analysis.")]
    public WorkspaceResult<WorkspaceComparison> Compare(string baselineSnapshot, string expectedSnapshot, int offset = 0, int limit = 20)
        => Invoke(() => workspace.Compare(baselineSnapshot, expectedSnapshot, offset, limit));

    [McpServerTool(Name = "learn", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read the compiled order-transition authoring example and the source/export/check workflow for this server build. This initial primer covers transitions only; it is not natural-language search or a project-version compatibility guarantee.")]
    public WorkspaceResult<AuthoringPrimer> Learn(string expectedSnapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSnapshot);
        var current = Invoke(() => workspace.Describe(limit: 1, expectedSnapshot: expectedSnapshot));
        using var reader = new StreamReader(typeof(DevelopmentTools).Assembly.GetManifestResourceStream("OrderModel.cs")!);
        var primer = new AuthoringPrimer(
            typeof(TransitionDefinitionDocuments).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            "Edit typed authoring source. Lower through the existing authoring API and export in a separate process. Call workspace again, then check and compare. A successful document check is not a runtime safety proof.",
            reader.ReadToEnd());
        return new(current.SnapshotId, current.InputRevision, current.SemanticFingerprint,
            current.ChangedInputs, current.Limitations, primer);
    }

    static T Invoke<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException
            or KeyNotFoundException or IOException or UnauthorizedAccessException or JsonException)
        {
            // Expected local development failures must retain their repair instructions over MCP.
            throw new McpException(error.Message);
        }
    }
}

internal sealed record AuthoringPrimer(string FrameworkBuild, string Workflow, string CompiledExample);
