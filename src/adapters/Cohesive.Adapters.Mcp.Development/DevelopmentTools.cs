using System.ComponentModel;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Semantics;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cohesive.Adapters.Mcp.Development;

// Transport projection only. Domain validators and canonical documents retain semantic authority.
internal sealed class DevelopmentTools(SemanticWorkspace workspace, DevelopmentGuide guide)
{
    [McpServerTool(Name = "workspace", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Discover the exported semantic scope, exact snapshot, declared-source freshness, and definition identities. Start here; page with nextOffset.")]
    public WorkspaceResult<WorkspacePage<WorkspaceSubject>> Workspace(int offset = 0, int limit = 20)
        => Invoke(() => workspace.Describe(offset, limit));

    [McpServerTool(Name = "resolve", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find definition identities by identity, display name or owning source reference. Returns all matching candidates; never silently chooses an ambiguous name.")]
    public WorkspaceResult<WorkspacePage<WorkspaceSubject>> Resolve(string query, string expectedSnapshot, int offset = 0, int limit = 20)
        => Invoke(() => workspace.Resolve(query, expectedSnapshot, offset, limit));

    [McpServerTool(Name = "inspect", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read original domain semantics or explicitly scoped inspection projections and sourceMappings with optional repository-relative line spans by exact definition identity. pointer is an RFC 6901 path in the complete document; empty selects the root. Large values return child pointers. Child offset/limit and sourceOffset/sourceLimit page independently. Edit owning source, never this derived JSON.")]
    public WorkspaceResult<WorkspaceInspection> Inspect(string id, string expectedSnapshot, string pointer = "", int offset = 0, int limit = 20, int sourceOffset = 0, int sourceLimit = 20)
        => Invoke(() => workspace.Inspect(id, expectedSnapshot, pointer, offset, limit, sourceOffset, sourceLimit));

    [McpServerTool(Name = "check", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Validate exported documents with host-registered domain validators. Does not build source, prove runtime invariants, link external contracts, or validate target realization. Check changedInputs and domainValidationAvailable.")]
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
    [Description("Read versioned authoring and inspection guidance supplied by this host. Static guidance requires no workspace snapshot or exported bundle; it does not claim project-version compatibility.")]
    public DevelopmentGuide Learn() => guide;

    [McpServerTool(Name = "references", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find direct consumers of a definition identity in the expected snapshot. Expected is the authored exact reference; Available null means missing, and IsContentMatch verifies content; IsExactMatch also requires revision equality. Missing revisions are unknown, not stale. Review IncompleteDefinitions; this is not transitive impact or compatibility analysis.")]
    public WorkspaceResult<WorkspaceReferences> References(string id, string expectedSnapshot, int offset = 0, int limit = 20)
        => Invoke(() => workspace.References(id, expectedSnapshot, offset, limit));

    [McpServerTool(Name = "impact", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Find potential transitive consumers with shortest evidence paths. maxDepth is 1–16; limit is 1–100. Cycles are suppressed. Inspect depthLimitReached, resultLimitReached, incompleteDefinitions and each edge's content/revision evidence. This is bounded reachability, not compatibility or runtime impact proof.")]
    public WorkspaceResult<WorkspaceImpact> Impact(string id, string expectedSnapshot, int maxDepth = 4, int limit = 20)
        => Invoke(() => workspace.Impact(id, expectedSnapshot, maxDepth, limit));

    [McpServerTool(Name = "compatibility", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Compare a portable api-contract in two exact snapshots under " + Cohesive.Api.ApiContractCompatibility.Policy + ". Default direction is existing clients to proposed server; reverse direction is explicit. Results are Compatible, Breaking or Unknown with structured findings. Does not prove behavior, security or generated-client compatibility. Endpoint additions and removals are interpreted directionally.")]
    public WorkspaceResult<JsonElement> Compatibility(string id, string baselineSnapshot, string expectedSnapshot,
        Cohesive.Api.ApiCompatibilityDirection direction = Cohesive.Api.ApiCompatibilityDirection.ExistingClientToProposedServer)
        => Invoke(() => workspace.CompareDocuments(id, baselineSnapshot, expectedSnapshot, (before, after) =>
            JsonSerializer.SerializeToElement(Cohesive.Api.ApiContractCompatibility.CompareDocuments(before, after, direction), DiagnosticOptions)));

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
