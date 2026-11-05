using Cohesive.Api.Services;
using System.Reflection;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using Cohesive.Relations.Serialization;
using Cohesive.Semantics;
using Cohesive.Transitions.IR;
using ModelContextProtocol.Server;

namespace Cohesive.Adapters.Mcp.Development;

/// <summary>Host-owned, versioned development guidance, independent of any application snapshot.</summary>
/// <param name="Version">Exact guide revision supplied by its producer.</param>
/// <param name="Content">Human-readable authoring and tool guidance.</param>
public sealed record DevelopmentGuide(string Version, string Content);

/// <summary>Composable stdio hosting over an explicitly configured semantic workspace.</summary>
public static class DevelopmentMcpServer
{
    static readonly string Version = typeof(DevelopmentMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    static readonly DevelopmentGuide DefaultGuide = LoadGuide();

    /// <summary>Creates the standalone server's conventional validator configuration.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="bundlePath">Repository-relative export path.</param>
    /// <returns>A workspace with domain-owned execution/relation validators and endpoint inspection readers.</returns>
    /// <exception cref="ArgumentException">The workspace paths are invalid.</exception>
    /// <remarks>Custom hosts may construct SemanticWorkspace directly with their own domain-owned validators.</remarks>
    public static SemanticWorkspace CreateDefaultWorkspace(string root, string bundlePath) => new(root, bundlePath,
        new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, DocumentValidationResult>>
        {
            [ServiceDefinitionDocuments.Kind] = document => ServiceDefinitionDocuments.ValidateAndProject(document, out _),
            [TransitionDefinitionDocuments.Kind] = TransitionDefinitionDocuments.Validate,
            [ProcessDefinitionDocuments.Kind] = ProcessDefinitionDocuments.Validate,
            [HostedQueryDefinitionDocuments.Kind] = HostedQueryDefinitionDocuments.Validate
        }, new Dictionary<ExecutionDefinitionKind, Func<ExecutionDefinitionDocument, IReadOnlyList<ExecutionDefinitionReference>>>
        {
            [ServiceDefinitionDocuments.Kind] = document => document.GetDefinition<ServiceDefinition>().GetDefinitionDependencies(),
            [ProcessDefinitionDocuments.Kind] = ReadProcessReferences,
            [HostedQueryDefinitionDocuments.Kind] = document => document.GetDefinition<Cohesive.Relations.IR.HostedQueryDefinition>().Dependencies.Select(d => d.Definition).ToArray(),
            [TransitionDefinitionDocuments.Kind] = _ => []
        }, DomainDocumentReaders.Readers);

    /// <summary>Runs the same tools over a caller-configured workspace without loading plug-ins or application assemblies.</summary>
    /// <param name="workspace">Workspace owning validator policy and repository scope.</param>
    /// <param name="guide">Optional host-specific guidance; null uses the adapter's versioned guide.</param>
    /// <param name="cancellationToken">Stops the stdio session.</param>
    /// <returns>Completes when the client disconnects or cancellation ends the session.</returns>
    /// <exception cref="ArgumentNullException">The workspace is null.</exception>
    /// <exception cref="ArgumentException">The supplied guide version or content is empty.</exception>
    /// <exception cref="OperationCanceledException">The session is cancelled.</exception>
    /// <exception cref="IOException">Stdio transport fails.</exception>
    public static async Task RunAsync(SemanticWorkspace workspace, DevelopmentGuide? guide = null, CancellationToken cancellationToken = default)
    {
        var options = CreateOptions(workspace, guide);
        await using var server = McpServer.Create(new StdioServerTransport(options), options);
        await server.RunAsync(cancellationToken);
    }

    internal static McpServerOptions CreateOptions(SemanticWorkspace workspace, DevelopmentGuide? guide = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        guide ??= DefaultGuide;
        ArgumentException.ThrowIfNullOrWhiteSpace(guide.Version);
        ArgumentException.ThrowIfNullOrWhiteSpace(guide.Content);
        var tools = new DevelopmentTools(workspace, guide);
        return new()
        {
            ServerInfo = new() { Name = "cohesive-development", Version = Version },
            ServerInstructions = "Use learn for host guidance without a snapshot. Call workspace for exported semantics and retain snapshotId. Use inspect sourceMappings to locate declarations and references to find direct consumers and impact for bounded transitive evidence paths. Edit owning source and re-export. ChangedInputs means the model is stale. Read limitations; this server never edits IR or executes application code.",
            ToolCollection =
            [
                McpServerTool.Create(tools.Workspace), McpServerTool.Create(tools.Resolve),
                McpServerTool.Create(tools.Inspect), McpServerTool.Create(tools.Check),
                McpServerTool.Create(tools.Compare), McpServerTool.Create(tools.Learn), McpServerTool.Create(tools.References), McpServerTool.Create(tools.Impact)
            ]
        };
    }

    static IReadOnlyList<ExecutionDefinitionReference> ReadProcessReferences(ExecutionDefinitionDocument document)
    {
        if (!ProcessDefinitionDocuments.Validate(document).IsValid)
            throw new InvalidOperationException("Process reference inspection requires valid domain semantics. Use check for diagnostics.");
        return document.GetDefinition<ProcessDefinition>().GetDefinitionDependencies();
    }

    static DevelopmentGuide LoadGuide()
    {
        using var reader = new StreamReader(typeof(DevelopmentMcpServer).Assembly.GetManifestResourceStream("AuthoringGuide.md")!);
        return new(Version, reader.ReadToEnd());
    }
}
