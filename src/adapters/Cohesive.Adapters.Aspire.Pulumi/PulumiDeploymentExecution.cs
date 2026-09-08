using System.Collections.Immutable;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Pulumi.Automation;

namespace Cohesive.Adapters.Aspire.Pulumi;

/// <summary>Environment-variable contract exposed to existing Pulumi programs by the adapter.</summary>
public static class CohesivePulumiEnvironmentVariables
{
    /// <summary>Absolute path to the exact serialized <see cref="AspirePulumiDeploymentHandoff"/>.</summary>
    public const string HandoffPath = "COHESIVE_INFRA_HANDOFF_PATH";

    /// <summary>Exact handoff fingerprint value.</summary>
    public const string HandoffFingerprint = "COHESIVE_INFRA_HANDOFF_FINGERPRINT";

    /// <summary>Cohesive/Aspire environment profile selected for the operation.</summary>
    public const string EnvironmentName = "COHESIVE_INFRA_ENVIRONMENT";
}

/// <summary>Lifecycle operation delegated by an Aspire deployment pipeline to Pulumi.</summary>
public enum AspirePulumiDeploymentOperation
{
    /// <summary>Preview the changes Pulumi would apply without reconciling resources.</summary>
    Preview = 0,

    /// <summary>Reconcile the existing Pulumi program with <c>pulumi up</c>.</summary>
    Apply = 1,

    /// <summary>Destroy resources owned by the Pulumi stack.</summary>
    Destroy = 2
}

/// <summary>Stable diagnostics emitted by the Aspire-to-Pulumi execution boundary.</summary>
public static class AspirePulumiDeploymentDiagnosticCodes
{
    /// <summary>Pulumi did not return a successful outcome for the requested exact deployment operation.</summary>
    public const string ExecutionFailed = "infra.aspire.pulumi.execution.failed";
}

/// <summary>
/// Typed failure for an exact Pulumi operation whose provider-owned details remain in forwarded Pulumi diagnostics.
/// </summary>
public sealed class AspirePulumiDeploymentException : InvalidOperationException
{
    const string ExecutionStage = "aspire-pulumi-execution";
    const string HandoffReferenceScheme = "cohesive-infra-handoff";
    const string OperationReferenceScheme = "aspire-pulumi-operation";

    internal AspirePulumiDeploymentException(
        AspirePulumiDeploymentRequest request,
        Exception innerException)
        : this(request, CreateDiagnostic(request), innerException)
    {
    }

    AspirePulumiDeploymentException(
        AspirePulumiDeploymentRequest request,
        DocumentValidationDiagnostic diagnostic,
        Exception innerException)
        : base($"{diagnostic.Code}: {diagnostic.Message}", innerException)
    {
        Operation = request.Operation;
        LifecycleAuthority = request.Handoff.LifecycleAuthority;
        HandoffFingerprint = request.Handoff.Fingerprint;
        Diagnostic = diagnostic;
    }

    /// <summary>Exact lifecycle operation that failed.</summary>
    public AspirePulumiDeploymentOperation Operation { get; }

    /// <summary>Canonical lifecycle authority selected by the exact deployment realization.</summary>
    public InfrastructureLifecycleAuthorityId LifecycleAuthority { get; }

    /// <summary>Exact handoff fingerprint supplied to Pulumi for the failed operation.</summary>
    public AspirePulumiDeploymentHandoffFingerprint HandoffFingerprint { get; }

    /// <summary>
    /// Stable non-secret diagnostic identifying the requested operation, lifecycle authority, and exact handoff.
    /// </summary>
    public DocumentValidationDiagnostic Diagnostic { get; }

    static DocumentValidationDiagnostic CreateDiagnostic(AspirePulumiDeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operation = request.Operation.ToString().ToLowerInvariant();
        return new(
            AspirePulumiDeploymentDiagnosticCodes.ExecutionFailed,
            DiagnosticSeverity.Error,
            $"Pulumi {operation} failed for lifecycle authority '{request.Handoff.LifecycleAuthority.Value}'. "
            + "Inspect the Pulumi diagnostics forwarded by Aspire for the provider-owned cause.",
            Evidence: new(
                stage: ExecutionStage,
                subject: request.Handoff.LifecycleAuthority.Value,
                sourceReferences:
                [
                    SourceReference.Create(HandoffReferenceScheme, request.Handoff.Fingerprint.Value).Value,
                    SourceReference.Create(OperationReferenceScheme, operation).Value
                ],
                resolutionOptions:
                [
                    "Inspect the Pulumi diagnostics forwarded through the Aspire pipeline step.",
                    "Correct the Pulumi program, provider configuration, credentials, or target state before retrying the same operation."
                ],
                expected: $"Pulumi {operation} returns a successful outcome.",
                observed: "The Pulumi executor failed before returning a successful outcome."));
    }
}

/// <summary>Stream from which a Pulumi Automation message originated.</summary>
public enum AspirePulumiOutputStream
{
    /// <summary>Pulumi standard output.</summary>
    StandardOutput = 0,

    /// <summary>Pulumi standard error.</summary>
    StandardError = 1
}

/// <summary>One redacted-by-Pulumi progress message forwarded to the Aspire pipeline.</summary>
/// <param name="Stream">Originating Pulumi output stream.</param>
/// <param name="Message">Message emitted by Pulumi Automation.</param>
public sealed record AspirePulumiDeploymentProgress(
    AspirePulumiOutputStream Stream,
    string Message);

/// <summary>Runtime request for executing an exact deployment handoff through Pulumi Automation.</summary>
public sealed record AspirePulumiDeploymentRequest
{
    /// <summary>Creates a Pulumi execution request.</summary>
    /// <param name="handoff">Exact persisted Cohesive-to-Pulumi handoff.</param>
    /// <param name="repositoryRoot">Absolute repository root used to resolve the program directory.</param>
    /// <param name="handoffPath">Absolute path at which the serialized handoff was materialized.</param>
    /// <param name="operation">Pulumi lifecycle operation.</param>
    /// <param name="reportProgress">Optional synchronous progress sink.</param>
    /// <exception cref="ArgumentNullException"><paramref name="handoff"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A path is empty or not absolute, or <paramref name="operation"/> is unsupported.</exception>
    public AspirePulumiDeploymentRequest(
        AspirePulumiDeploymentHandoff handoff,
        string repositoryRoot,
        string handoffPath,
        AspirePulumiDeploymentOperation operation,
        Action<AspirePulumiDeploymentProgress>? reportProgress = null)
    {
        Handoff = Guard.RequireNotNull(handoff);
        RepositoryRoot = RequireAbsolutePath(repositoryRoot, nameof(repositoryRoot));
        HandoffPath = RequireAbsolutePath(handoffPath, nameof(handoffPath));
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported Pulumi deployment operation.");
        Operation = operation;
        ReportProgress = reportProgress;
    }

    /// <summary>Exact persisted Cohesive-to-Pulumi handoff.</summary>
    public AspirePulumiDeploymentHandoff Handoff { get; }

    /// <summary>Absolute repository root used to resolve <see cref="AspirePulumiDeploymentHandoff.ProgramDirectory"/>.</summary>
    public string RepositoryRoot { get; }

    /// <summary>Absolute path at which the serialized handoff was materialized.</summary>
    public string HandoffPath { get; }

    /// <summary>Pulumi lifecycle operation.</summary>
    public AspirePulumiDeploymentOperation Operation { get; }

    /// <summary>Optional synchronous progress sink receiving Pulumi's redacted output.</summary>
    public Action<AspirePulumiDeploymentProgress>? ReportProgress { get; }

    static string RequireAbsolutePath(string path, string paramName)
    {
        path = Guard.RequireNotNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("A Pulumi execution path must be absolute.", paramName);
        return Path.GetFullPath(path);
    }
}

/// <summary>Provider-neutral summary of one completed Pulumi Automation operation.</summary>
public sealed record AspirePulumiDeploymentResult
{
    /// <summary>Creates a deployment result.</summary>
    /// <param name="operation">Completed lifecycle operation.</param>
    /// <param name="outcome">Pulumi update outcome.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> or <paramref name="outcome"/> is invalid.</exception>
    public AspirePulumiDeploymentResult(AspirePulumiDeploymentOperation operation, string outcome)
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported Pulumi deployment operation.");
        Operation = operation;
        Outcome = Guard.RequireNotNullOrWhiteSpace(outcome);
    }

    /// <summary>Completed lifecycle operation.</summary>
    public AspirePulumiDeploymentOperation Operation { get; }

    /// <summary>Pulumi update outcome without provider-specific result types.</summary>
    public string Outcome { get; }
}

/// <summary>Runtime boundary through which Aspire delegates physical reconciliation to Pulumi.</summary>
public interface IAspirePulumiDeploymentExecutor
{
    /// <summary>Executes one exact handoff.</summary>
    /// <param name="request">Validated runtime request.</param>
    /// <param name="cancellationToken">Cancellation propagated from the Aspire pipeline.</param>
    /// <returns>The Pulumi operation outcome.</returns>
    /// <exception cref="InvalidOperationException">
    /// The Pulumi program is absent, its project name differs from the handoff, or Pulumi cannot complete the operation.
    /// </exception>
    Task<AspirePulumiDeploymentResult> ExecuteAsync(
        AspirePulumiDeploymentRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes an existing local Pulumi program through Pulumi's official Automation API.
/// </summary>
/// <remarks>
/// This adapter does not implement provider reconciliation or maintain deployment state. It validates the local
/// Pulumi project before selecting a stack, forwards the exact Cohesive handoff through stable environment-variable
/// names, and delegates apply/destroy semantics to Pulumi.
/// </remarks>
public sealed class PulumiAutomationDeploymentExecutor : IAspirePulumiDeploymentExecutor
{
    static readonly ImmutableArray<string> PulumiProjectFiles = ["Pulumi.yaml", "Pulumi.yml", "Pulumi.json"];

    /// <summary>Creates an executor using the Pulumi CLI discovered by Pulumi Automation.</summary>
    public PulumiAutomationDeploymentExecutor()
    {
    }

    /// <inheritdoc />
    public async Task<AspirePulumiDeploymentResult> ExecuteAsync(
        AspirePulumiDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var programDirectory = ResolveProgramDirectory(request.RepositoryRoot, request.Handoff.ProgramDirectory.Value);
        EnsurePulumiProjectExists(programDirectory);
        await ValidateHandoffFileAsync(request, cancellationToken).ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CohesivePulumiEnvironmentVariables.HandoffPath] = request.HandoffPath,
            [CohesivePulumiEnvironmentVariables.HandoffFingerprint] = request.Handoff.Fingerprint.Value,
            [CohesivePulumiEnvironmentVariables.EnvironmentName] = request.Handoff.EnvironmentName
        };

        using var workspace = await LocalWorkspace.CreateAsync(
            new LocalWorkspaceOptions
            {
                WorkDir = programDirectory,
                EnvironmentVariables = environment
            },
            cancellationToken).ConfigureAwait(false);
        var project = await workspace.GetProjectSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (project is null
            || !string.Equals(project.Name, request.Handoff.PulumiProjectName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pulumi program '{request.Handoff.ProgramDirectory.Value}' declares project "
                + $"'{project?.Name ?? "none"}', expected '{request.Handoff.PulumiProjectName}'.");
        }

        var stack = await WorkspaceStack.CreateOrSelectAsync(
            request.Handoff.PulumiStackName,
            workspace,
            cancellationToken).ConfigureAwait(false);
        return request.Operation switch
        {
            AspirePulumiDeploymentOperation.Preview => await PreviewAsync(stack, request, cancellationToken).ConfigureAwait(false),
            AspirePulumiDeploymentOperation.Apply => await ApplyAsync(stack, request, cancellationToken).ConfigureAwait(false),
            AspirePulumiDeploymentOperation.Destroy => await DestroyAsync(stack, request, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Operation, "Unsupported Pulumi deployment operation.")
        };
    }

    static async Task<AspirePulumiDeploymentResult> PreviewAsync(
        WorkspaceStack stack,
        AspirePulumiDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await stack.PreviewAsync(
            new PreviewOptions
            {
                Refresh = true,
                OnStandardOutput = message => Report(request, AspirePulumiOutputStream.StandardOutput, message),
                OnStandardError = message => Report(request, AspirePulumiOutputStream.StandardError, message)
            },
            cancellationToken).ConfigureAwait(false);
        var outcome = result.ChangeSummary.Count == 0
            ? "no changes"
            : string.Join(
                ", ",
                result.ChangeSummary
                    .OrderBy(static change => change.Key.ToString(), StringComparer.Ordinal)
                    .Select(static change => $"{change.Key}={change.Value}"));
        return new(request.Operation, outcome);
    }

    static async Task<AspirePulumiDeploymentResult> ApplyAsync(
        WorkspaceStack stack,
        AspirePulumiDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await stack.UpAsync(
            new UpOptions
            {
                Refresh = true,
                ShowSecrets = false,
                OnStandardOutput = message => Report(request, AspirePulumiOutputStream.StandardOutput, message),
                OnStandardError = message => Report(request, AspirePulumiOutputStream.StandardError, message)
            },
            cancellationToken).ConfigureAwait(false);
        return new(request.Operation, result.Summary.Result.ToString());
    }

    static async Task<AspirePulumiDeploymentResult> DestroyAsync(
        WorkspaceStack stack,
        AspirePulumiDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var result = await stack.DestroyAsync(
            new DestroyOptions
            {
                Refresh = true,
                ShowSecrets = false,
                OnStandardOutput = message => Report(request, AspirePulumiOutputStream.StandardOutput, message),
                OnStandardError = message => Report(request, AspirePulumiOutputStream.StandardError, message)
            },
            cancellationToken).ConfigureAwait(false);
        return new(request.Operation, result.Summary.Result.ToString());
    }

    static string ResolveProgramDirectory(string repositoryRoot, string repositoryRelativeDirectory)
    {
        var resolved = Path.GetFullPath(Path.Combine(repositoryRoot, repositoryRelativeDirectory));
        var rootPrefix = repositoryRoot.EndsWith(Path.DirectorySeparatorChar)
            ? repositoryRoot
            : repositoryRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("The Pulumi program directory resolves outside the repository root.");
        if (!Directory.Exists(resolved))
            throw new InvalidOperationException($"Pulumi program directory '{repositoryRelativeDirectory}' does not exist.");
        return resolved;
    }

    static void EnsurePulumiProjectExists(string programDirectory)
    {
        if (!PulumiProjectFiles.Any(file => File.Exists(Path.Combine(programDirectory, file))))
        {
            throw new InvalidOperationException(
                $"Pulumi program directory '{programDirectory}' contains no Pulumi.yaml, Pulumi.yml, or Pulumi.json project file.");
        }
    }

    static async Task ValidateHandoffFileAsync(
        AspirePulumiDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(request.HandoffPath))
            throw new InvalidOperationException($"Cohesive deployment handoff '{request.HandoffPath}' does not exist.");

        var persisted = await File.ReadAllBytesAsync(request.HandoffPath, cancellationToken).ConfigureAwait(false);
        if (!persisted.AsSpan().SequenceEqual(request.Handoff.ToCanonicalBytes()))
        {
            throw new InvalidOperationException(
                $"Cohesive deployment handoff '{request.HandoffPath}' does not match exact fingerprint "
                + $"'{request.Handoff.Fingerprint.Value}'.");
        }
    }

    static void Report(
        AspirePulumiDeploymentRequest request,
        AspirePulumiOutputStream stream,
        string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            request.ReportProgress?.Invoke(new(stream, message));
    }
}
