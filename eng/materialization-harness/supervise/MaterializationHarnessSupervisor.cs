using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;

namespace Cohesive.MaterializationHarness.Supervise;

static partial class MaterializationHarnessSupervisor
{
    static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        var options = await SupervisorOptions.ParseAsync(args).ConfigureAwait(false);
        PrepareArtifactDirectory(options);
        var artifacts = new BoundedArtifactWriter(
            directory: options.ArtifactDirectory,
            maximumBytes: SupervisorOptions.MaximumArtifactBytes);
        using var timeout = new CancellationTokenSource(options.Timeout);
        SupervisedHost? host = null;
        try
        {
            await RunCliAsync(
                options,
                artifacts,
                artifactPrefix: "sdk-start",
                arguments: ["--start", options.Provider],
                cancellationToken: timeout.Token).ConfigureAwait(false);

            host = StartHost(options, armFault: true);
            var marker = await WaitForMarkerAsync(options, host, timeout.Token).ConfigureAwait(false);
            await StopHostAsync(host, artifacts, artifactPrefix: "interrupted-host").ConfigureAwait(false);
            host = null;

            using var http = CreateHttpClient();
            await CaptureElasticAsync(http, options, artifacts, "before-recovery").ConfigureAwait(false);
            var before = await CaptureEvidenceAsync(
                options,
                artifacts,
                artifactPrefix: "before-recovery",
                selectedGeneration: marker.Generation,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            if (before.ActiveGeneration == marker.Generation
                && options.Boundary != MaterializationExecutionBoundaryPoint.AfterGenerationPromotion)
            {
                throw new InvalidOperationException("The interrupted unpromoted generation became visible.");
            }

            if (options.Mode == RecoveryMode.RestartAttempt)
            {
                await RunRestartAttemptAsync(
                    options,
                    artifacts,
                    cancellationToken: timeout.Token).ConfigureAwait(false);
            }

            host = StartHost(options, armFault: false);
            await WaitForCompletedAsync(options, host, timeout.Token).ConfigureAwait(false);
            await CaptureHttpEvidenceAsync(http, options, artifacts).ConfigureAwait(false);
            await StopHostAsync(host, artifacts, artifactPrefix: "recovered-host").ConfigureAwait(false);
            host = null;

            var current = await CaptureEvidenceAsync(
                options,
                artifacts,
                artifactPrefix: "after-recovery-current",
                selectedGeneration: null,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var interrupted = await CaptureEvidenceAsync(
                options,
                artifacts,
                artifactPrefix: "after-recovery-interrupted",
                selectedGeneration: marker.Generation,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            await CaptureElasticAsync(http, options, artifacts, "after-recovery").ConfigureAwait(false);
            ValidateRecovery(options, marker, current, interrupted);

            var summary = new
            {
                options.RunIdentity,
                mode = options.Mode.ToString(),
                options.Provider,
                boundary = options.Boundary.ToString(),
                interruptedGeneration = marker.Generation,
                recoveredGeneration = current.CurrentGeneration,
                current.ActiveGeneration,
                interruptedState = interrupted.SelectedGenerationState,
                completed = true
            };
            await artifacts.WriteManifestAsync(summary).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(summary, WebJson));
            return 0;
        }
        catch (Exception exception)
        {
            if (host is not null)
                await StopHostAsync(host, artifacts, artifactPrefix: "failed-host").ConfigureAwait(false);
            await artifacts.WriteTextAsync("supervisor-failure.txt", exception.ToString()).ConfigureAwait(false);
            await artifacts.WriteManifestAsync(new
            {
                options.RunIdentity,
                mode = options.Mode.ToString(),
                options.Provider,
                boundary = options.Boundary.ToString(),
                completed = false,
                failure = exception.GetType().FullName
            }).ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    static void PrepareArtifactDirectory(SupervisorOptions options)
    {
        if (Directory.Exists(options.ArtifactDirectory)
            && Directory.EnumerateFileSystemEntries(options.ArtifactDirectory).Any())
        {
            throw new InvalidOperationException(
                $"Artifact directory '{options.ArtifactDirectory}' must be absent or empty.");
        }
        Directory.CreateDirectory(options.ArtifactDirectory);
        if (!File.Exists(options.HostAssemblyPath))
            throw new FileNotFoundException("Build the Release materialization host before supervising it.", options.HostAssemblyPath);
    }

    static SupervisedHost StartHost(SupervisorOptions options, bool armFault)
        => StartHost(
            options,
            armFault
                ? new HostFaultPlan(
                    Point: options.Boundary,
                    MarkerPath: options.MarkerPath,
                    ScopeIdentity: null,
                    OperationIdentity: null)
                : null);

    static SupervisedHost StartHost(SupervisorOptions options, HostFaultPlan? fault)
    {
        var start = HostProcessStartInfo(options);
        if (fault is not null)
        {
            start.Environment["COHESIVE_MATERIALIZATION_FAULT_BOUNDARY"] = fault.Point.ToString();
            start.Environment["COHESIVE_MATERIALIZATION_FAULT_MARKER_PATH"] = fault.MarkerPath;
            start.Environment["COHESIVE_MATERIALIZATION_FAULT_OCCURRENCE"] = "0";
            start.Environment["COHESIVE_MATERIALIZATION_FAULT_PROVIDER"] = options.Provider;
            start.Environment["COHESIVE_MATERIALIZATION_FAULT_RUN_ID"] = options.RunIdentity;
            if (fault.ScopeIdentity is not null)
                start.Environment["COHESIVE_MATERIALIZATION_FAULT_SCOPE"] = fault.ScopeIdentity;
            if (fault.OperationIdentity is not null)
                start.Environment["COHESIVE_MATERIALIZATION_FAULT_OPERATION"] = fault.OperationIdentity;
        }
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var stdout = new BoundedLineCapture(SupervisorOptions.MaximumArtifactBytes);
        var stderr = new BoundedLineCapture(SupervisorOptions.MaximumArtifactBytes);
        process.OutputDataReceived += (_, eventArgs) => stdout.Add(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => stderr.Add(eventArgs.Data);
        if (!process.Start())
            throw new InvalidOperationException("The materialization host process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new(process, stdout, stderr);
    }

    static async Task<ReachedBoundary> WaitForMarkerAsync(
        SupervisorOptions options,
        SupervisedHost host,
        CancellationToken cancellationToken) => await WaitForMarkerAsync(
            options,
            host,
            markerPath: options.MarkerPath,
            point: options.Boundary,
            cancellationToken).ConfigureAwait(false);

    static async Task<ReachedBoundary> WaitForMarkerAsync(
        SupervisorOptions options,
        SupervisedHost host,
        string markerPath,
        MaterializationExecutionBoundaryPoint point,
        CancellationToken cancellationToken)
    {
        while (!File.Exists(markerPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (host.Process.HasExited)
                throw new InvalidOperationException($"The armed host exited with code {host.Process.ExitCode} before reaching its marker.");
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        var marker = JsonSerializer.Deserialize<ReachedBoundary>(
            await File.ReadAllTextAsync(markerPath, cancellationToken).ConfigureAwait(false),
            WebJson) ?? throw new InvalidOperationException("The reached-boundary marker was empty.");
        if (marker.RunIdentity != options.RunIdentity
            || marker.Provider != options.Provider
            || marker.Point != point
            || marker.Occurrence != 0
            || marker.HostProcessId != host.Process.Id)
        {
            throw new InvalidOperationException("The reached-boundary marker does not match the armed host fault plan.");
        }
        return marker;
    }

    static async Task WaitForCompletedAsync(
        SupervisorOptions options,
        SupervisedHost host,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient();
        var evidenceUri = ProviderFailureEvidenceUri(options);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (host.Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The replacement host exited with code {host.Process.ExitCode} before recovery completed.");
            }
            try
            {
                using var response = await client.GetAsync(evidenceUri, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var evidence = JsonSerializer.Deserialize<FailureEvidence>(
                        await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                        WebJson);
                    if (evidence?.TerminalOutcome == ExecutionTerminalOutcomeKind.Completed)
                        return;
                    if (evidence?.TerminalOutcome is ExecutionTerminalOutcomeKind.Failed
                        or ExecutionTerminalOutcomeKind.Cancelled
                        or ExecutionTerminalOutcomeKind.Terminated)
                    {
                        throw new InvalidOperationException(
                            $"The recovered Process terminated as '{evidence.TerminalOutcome}'.");
                    }
                }
            }
            catch (HttpRequestException)
            {
                // The replacement host may still be binding its HTTP endpoint.
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    static async Task CaptureHttpEvidenceAsync(
        HttpClient client,
        SupervisorOptions options,
        BoundedArtifactWriter artifacts)
    {
        await artifacts.CaptureHttpAsync(client, "http-inspect.json", ProcessUri(options, suffix: null))
            .ConfigureAwait(false);
        await artifacts.CaptureHttpAsync(client, "http-explain.json", ProcessUri(options, "explain"))
            .ConfigureAwait(false);
        await artifacts.CaptureHttpAsync(client, "http-traces.json", ProcessUri(options, "traces"))
            .ConfigureAwait(false);
    }

    static async Task CaptureElasticAsync(
        HttpClient client,
        SupervisorOptions options,
        BoundedArtifactWriter artifacts,
        string prefix)
    {
        await artifacts.CaptureHttpAsync(
                client,
                $"{prefix}-elastic-alias.json",
                new Uri(options.ElasticUrl, $"_alias/freight-order-search-{options.Provider}"))
            .ConfigureAwait(false);
        await artifacts.CaptureHttpAsync(
                client,
                $"{prefix}-elastic-indices.json",
                new Uri(options.ElasticUrl, "_cat/indices/cohesive-freight-*?format=json&h=index,docs.count,store.size,status"))
            .ConfigureAwait(false);
    }

    static async Task<FailureEvidence> CaptureEvidenceAsync(
        SupervisorOptions options,
        BoundedArtifactWriter artifacts,
        string artifactPrefix,
        string? selectedGeneration,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["--failure-evidence", options.Provider];
        if (selectedGeneration is not null)
            arguments.Add(selectedGeneration);
        var result = await RunCliAsync(
            options,
            artifacts,
            artifactPrefix,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<FailureEvidence>(result.StandardOutput)
            ?? throw new InvalidOperationException("The host returned no failure evidence.");
    }

    static void ValidateRecovery(
        SupervisorOptions options,
        ReachedBoundary marker,
        FailureEvidence current,
        FailureEvidence interrupted)
    {
        if (current.TerminalOutcome != ExecutionTerminalOutcomeKind.Completed)
            throw new InvalidOperationException("The recovered Process did not retain completed terminal evidence.");
        if (current.CurrentGeneration is null || current.ActiveGeneration != current.CurrentGeneration)
            throw new InvalidOperationException("The recovered Process generation is not the active target generation.");
        if (current.SelectedGenerationState != MaterializationGenerationState.Active)
            throw new InvalidOperationException("The recovered Process generation is not active in its target authority.");
        if (current.SelectedVisibleItemCount != options.ExpectedVisibleItemCount
            || current.SelectedTombstoneCount != 0)
        {
            throw new InvalidOperationException(
                $"The recovered generation retained {current.SelectedVisibleItemCount?.ToString() ?? "unknown"} visible "
                + $"and {current.SelectedTombstoneCount?.ToString() ?? "unknown"} tombstoned documents; expected "
                + $"{options.ExpectedVisibleItemCount} visible and zero tombstoned documents.");
        }
        if (options.Mode == RecoveryMode.Resume)
        {
            if (current.CurrentGeneration != marker.Generation)
                throw new InvalidOperationException("Same-attempt recovery changed the candidate generation.");
            return;
        }
        if (current.CurrentGeneration == marker.Generation)
            throw new InvalidOperationException("RestartAttempt reused the interrupted generation.");
        if (interrupted.SelectedGenerationState != MaterializationGenerationState.Retired)
            throw new InvalidOperationException("RestartAttempt did not retire the interrupted candidate.");
        if (current.ActiveGeneration == marker.Generation)
            throw new InvalidOperationException("The abandoned interrupted generation remained visible.");
    }

    static async Task<ProcessResult> RunCliAsync(
        SupervisorOptions options,
        BoundedArtifactWriter artifacts,
        string artifactPrefix,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteCliAsync(
            options: options,
            artifacts: artifacts,
            artifactPrefix: artifactPrefix,
            arguments: arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Host SDK command exited with code {result.ExitCode}: {result.StandardError}{result.StandardOutput}");
        }
        return result;
    }

    static async Task RunRestartAttemptAsync(
        SupervisorOptions options,
        BoundedArtifactWriter artifacts,
        CancellationToken cancellationToken)
    {
        var transientAttempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteCliAsync(
                options: options,
                artifacts: artifacts,
                artifactPrefix: "sdk-restart-attempt",
                arguments: ["--restart-attempt", options.Provider],
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                await artifacts.WriteTextAsync(
                    "sdk-restart-attempt-retries.json",
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        transientAttempts,
                        reason = "durable worker ownership handoff"
                    }, WebJson)).ConfigureAwait(false);
                return;
            }
            if (!IsTransientRuntimeDisposition(result.StandardOutput))
            {
                throw new InvalidOperationException(
                    $"RestartAttempt SDK command exited with code {result.ExitCode}: "
                    + result.StandardError
                    + result.StandardOutput);
            }
            transientAttempts++;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    static bool IsTransientRuntimeDisposition(string output) =>
        output.Contains(RuntimeDiagnostic(ProcessDurableRuntimeDisposition.LeaseHeld), StringComparison.Ordinal)
        || output.Contains(RuntimeDiagnostic(ProcessDurableRuntimeDisposition.RevisionConflict), StringComparison.Ordinal)
        || output.Contains(RuntimeDiagnostic(ProcessDurableRuntimeDisposition.StaleFence), StringComparison.Ordinal)
        || output.Contains(RuntimeDiagnostic(ProcessDurableRuntimeDisposition.LeaseExpired), StringComparison.Ordinal);

    static string RuntimeDiagnostic(ProcessDurableRuntimeDisposition disposition) =>
        $"materialization-harness.process-disposition.{disposition}";

    static async Task<ProcessResult> ExecuteCliAsync(
        SupervisorOptions options,
        BoundedArtifactWriter artifacts,
        string artifactPrefix,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? assemblyPath = null)
    {
        var start = AssemblyProcessStartInfo(options, assemblyPath ?? options.HostAssemblyPath);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start };
        var stdout = new BoundedLineCapture(SupervisorOptions.MaximumArtifactBytes);
        var stderr = new BoundedLineCapture(SupervisorOptions.MaximumArtifactBytes);
        process.OutputDataReceived += (_, eventArgs) => stdout.Add(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => stderr.Add(eventArgs.Data);
        if (!process.Start())
            throw new InvalidOperationException("The materialization host SDK command did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        process.WaitForExit();
        var capturedOut = stdout.Snapshot();
        var capturedError = stderr.Snapshot();
        await artifacts.WriteTextAsync(
            $"{artifactPrefix}-stdout.json",
            capturedOut.Text,
            capturedOut.ObservedBytes,
            capturedOut.Truncated).ConfigureAwait(false);
        await artifacts.WriteTextAsync(
            $"{artifactPrefix}-stderr.log",
            capturedError.Text,
            capturedError.ObservedBytes,
            capturedError.Truncated).ConfigureAwait(false);
        return new(process.ExitCode, capturedOut.Text.Trim(), capturedError.Text);
    }

    static async Task StopHostAsync(
        SupervisedHost host,
        BoundedArtifactWriter artifacts,
        string artifactPrefix)
    {
        if (!host.Process.HasExited)
        {
            try
            {
                host.Process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (host.Process.HasExited)
            {
                // The host completed between the liveness check and the explicit stop.
            }
        }
        await host.Process.WaitForExitAsync().ConfigureAwait(false);
        host.Process.WaitForExit();
        var stdout = host.StandardOutput.Snapshot();
        var stderr = host.StandardError.Snapshot();
        await artifacts.WriteTextAsync(
            $"{artifactPrefix}-stdout.log",
            stdout.Text,
            stdout.ObservedBytes,
            stdout.Truncated).ConfigureAwait(false);
        await artifacts.WriteTextAsync(
            $"{artifactPrefix}-stderr.log",
            stderr.Text,
            stderr.ObservedBytes,
            stderr.Truncated).ConfigureAwait(false);
        host.Process.Dispose();
    }

    static ProcessStartInfo HostProcessStartInfo(SupervisorOptions options)
        => AssemblyProcessStartInfo(options, options.HostAssemblyPath);

    static ProcessStartInfo AssemblyProcessStartInfo(SupervisorOptions options, string assemblyPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = options.RepositoryRoot
        };
        start.ArgumentList.Add(assemblyPath);
        start.Environment["COHESIVE_MATERIALIZATION_PROCESS_INSTANCE_ID"] = options.ProcessInstancePrefix;
        foreach (var name in new[]
        {
            "COHESIVE_MATERIALIZATION_FAULT_BOUNDARY",
            "COHESIVE_MATERIALIZATION_FAULT_MARKER_PATH",
            "COHESIVE_MATERIALIZATION_FAULT_OCCURRENCE",
            "COHESIVE_MATERIALIZATION_FAULT_OPERATION",
            "COHESIVE_MATERIALIZATION_FAULT_PROVIDER",
            "COHESIVE_MATERIALIZATION_FAULT_RUN_ID",
            "COHESIVE_MATERIALIZATION_FAULT_SCOPE"
        })
        {
            start.Environment.Remove(name);
        }
        return start;
    }

    static HttpClient CreateHttpClient() => new() { Timeout = TimeSpan.FromSeconds(10) };

    static Uri ProcessUri(SupervisorOptions options, string? suffix)
    {
        var path = $"execution-control/processes/{Uri.EscapeDataString(options.ProcessInstanceId)}";
        if (suffix is not null)
            path += $"/{suffix}";
        return new(options.HostUrl, path);
    }

    static Uri ProviderFailureEvidenceUri(SupervisorOptions options) => new(
        options.HostUrl,
        $"materialization-harness/providers/{Uri.EscapeDataString(options.Provider)}/failure-evidence");

    sealed record ReachedBoundary(
        int SchemaVersion,
        string RunIdentity,
        string Provider,
        string ProcessInstanceId,
        string ProcessAttemptId,
        DateTimeOffset AttemptStartedAtUtc,
        string Generation,
        MaterializationExecutionBoundaryPoint Point,
        string ScopeIdentity,
        string OperationIdentity,
        int Occurrence,
        DateTimeOffset ObservedAtUtc,
        int HostProcessId);

    sealed record FailureEvidence(
        string Provider,
        string ProcessInstanceId,
        string CurrentAttemptId,
        string ControlRevision,
        ProcessControlMode ControlMode,
        ExecutionTerminalOutcomeKind TerminalOutcome,
        string? CurrentGeneration,
        string? SelectedGeneration,
        string TargetRevision,
        string? ActiveGeneration,
        MaterializationGenerationState? SelectedGenerationState,
        long? SelectedVisibleItemCount,
        long? SelectedTombstoneCount,
        JsonElement DurableOperations);

    sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

    sealed record HostFaultPlan(
        MaterializationExecutionBoundaryPoint Point,
        string MarkerPath,
        string? ScopeIdentity,
        string? OperationIdentity);

    sealed record SupervisedHost(
        Process Process,
        BoundedLineCapture StandardOutput,
        BoundedLineCapture StandardError);
}
