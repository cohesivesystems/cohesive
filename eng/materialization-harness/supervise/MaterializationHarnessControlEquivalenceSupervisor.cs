using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Control;
using Cohesive.Processes.Runtime;
using Cohesive.Storage.Materialization;
using ProcessStartResult = Cohesive.Execution.ProcessStartResult;

namespace Cohesive.MaterializationHarness.Supervise;

static class MaterializationHarnessControlEquivalenceSupervisor
{
    static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        var options = ControlEquivalenceOptions.Parse(args);
        PrepareArtifactDirectory(options);
        var artifacts = new BoundedArtifactWriter(
            directory: options.ArtifactDirectory,
            maximumBytes: SupervisorOptions.MaximumArtifactBytes);
        using var timeout = new CancellationTokenSource(options.Timeout);
        CapturedHost? httpHost = null;
        try
        {
            var sdk = await RunSdkScenarioAsync(options, artifacts, timeout.Token).ConfigureAwait(false);
            var sdkSemantics = sdk.ValidateAndProjectSemantics();

            httpHost = StartHttpHost(options);
            using var client = new HttpClient
            {
                BaseAddress = options.HostUrl,
                Timeout = TimeSpan.FromSeconds(30)
            };
            await WaitForHostAsync(client, options, httpHost, timeout.Token).ConfigureAwait(false);
            var http = await RunHttpScenarioAsync(
                    client: client,
                    options: options,
                    artifacts: artifacts,
                    host: httpHost,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            var httpSemantics = http.ValidateAndProjectSemantics();

            await artifacts.WriteTextAsync(
                "sdk-result.json",
                JsonSerializer.Serialize(sdk, WebJson)).ConfigureAwait(false);
            await artifacts.WriteTextAsync(
                "http-result.json",
                JsonSerializer.Serialize(http, WebJson)).ConfigureAwait(false);
            await artifacts.WriteTextAsync(
                "sdk-semantics.json",
                JsonSerializer.Serialize(sdkSemantics, WebJson)).ConfigureAwait(false);
            await artifacts.WriteTextAsync(
                "http-semantics.json",
                JsonSerializer.Serialize(httpSemantics, WebJson)).ConfigureAwait(false);
            if (!sdkSemantics.IsEquivalentTo(httpSemantics))
                throw new InvalidOperationException("SDK and HTTP produced divergent normalized control semantics.");

            await StopHostAsync(httpHost, artifacts, "http-host").ConfigureAwait(false);
            httpHost = null;
            var summary = new
            {
                schemaVersion = 1,
                options.RunIdentity,
                options.Provider,
                equivalent = true,
                operationCount = sdk.Operations.Length,
                sdkProcessInstance = $"{options.SdkProcessInstancePrefix}/{options.Provider}",
                httpProcessInstance = options.HttpProcessInstanceId
            };
            await artifacts.WriteManifestAsync(summary).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(summary, WebJson));
            return 0;
        }
        catch (Exception exception)
        {
            if (httpHost is not null)
                await StopHostAsync(httpHost, artifacts, "failed-http-host").ConfigureAwait(false);
            await artifacts.WriteTextAsync("control-equivalence-failure.txt", exception.ToString()).ConfigureAwait(false);
            await artifacts.WriteManifestAsync(new
            {
                schemaVersion = 1,
                options.RunIdentity,
                options.Provider,
                equivalent = false,
                failure = exception.GetType().FullName
            }).ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    static async Task<MaterializationHarnessControlScenarioResult> RunSdkScenarioAsync(
        ControlEquivalenceOptions options,
        BoundedArtifactWriter artifacts,
        CancellationToken cancellationToken)
    {
        var start = CreateHostStartInfo(options, options.SdkProcessInstancePrefix);
        start.ArgumentList.Add("--control-scenario-sdk");
        start.ArgumentList.Add(options.Provider);
        var process = new Process { StartInfo = start };
        var stdout = new BoundedLineCapture(SupervisorOptions.MaximumArtifactBytes);
        var stderr = new BoundedLineCapture(SupervisorOptions.MaximumArtifactBytes);
        process.OutputDataReceived += (_, eventArgs) => stdout.Add(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            stderr.Add(eventArgs.Data);
            if (eventArgs.Data?.StartsWith("control-phase=", StringComparison.Ordinal) == true)
                Console.Error.WriteLine(eventArgs.Data);
        };
        if (!process.Start())
            throw new InvalidOperationException("The SDK control scenario process did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        process.WaitForExit();
        var capturedOut = stdout.Snapshot();
        var capturedError = stderr.Snapshot();
        await artifacts.WriteTextAsync(
            "sdk-scenario-stdout.json",
            capturedOut.Text,
            capturedOut.ObservedBytes,
            capturedOut.Truncated).ConfigureAwait(false);
        await artifacts.WriteTextAsync(
            "sdk-scenario-stderr.log",
            capturedError.Text,
            capturedError.ObservedBytes,
            capturedError.Truncated).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"The SDK control scenario exited with code {process.ExitCode}: {capturedError.Text}{capturedOut.Text}");
        }
        return JsonSerializer.Deserialize<MaterializationHarnessControlScenarioResult>(capturedOut.Text, WebJson)
            ?? throw new InvalidOperationException("The SDK control scenario emitted no result.");
    }

    static async Task<MaterializationHarnessControlScenarioResult> RunHttpScenarioAsync(
        HttpClient client,
        ControlEquivalenceOptions options,
        BoundedArtifactWriter artifacts,
        CapturedHost host,
        CancellationToken cancellationToken)
    {
        var operations = ImmutableArray.CreateBuilder<MaterializationHarnessControlOperationObservation>(9);

        var start = await ProjectAndPostAsync<ProcessStartResult>(
                client: client,
                options: options,
                artifacts: artifacts,
                operation: ProcessStartWireNames.Start,
                maximumBatchItems: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        operations.Add(MaterializationHarnessControlObservation.Create(
            operation: ProcessStartWireNames.Start,
            resultKind: start.ResultKind,
            body: start.Body));

        var initial = await WaitForEvidenceAsync(
                client: client,
                options: options,
                host: host,
                predicate: static evidence => evidence.CurrentGeneration is not null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var initialGeneration = RequireGeneration(initial);

        var pause = await ProjectAndPostControlAsync(
                client: client,
                options: options,
                artifacts: artifacts,
                operation: ExecutionControlWireNames.Pause,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        operations.Add(MaterializationHarnessControlObservation.Create(
            operation: ExecutionControlWireNames.Pause,
            resultKind: pause.ResultKind,
            body: pause.Body));
        var paused = await WaitForEvidenceAsync(
                client: client,
                options: options,
                host: host,
                predicate: static evidence => evidence.ControlMode == ProcessControlMode.Paused,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var inspect = await GetAsync<ExecutionControlResult>(
                client: client,
                artifacts: artifacts,
                artifactName: "http-inspect-response.json",
                route: MaterializationHarnessExecutionRoutes.InspectFor(options.HttpProcessInstanceId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        operations.Add(MaterializationHarnessControlObservation.Create(
            operation: ExecutionControlWireNames.Inspect,
            resultKind: inspect.ResultKind,
            body: inspect.Body));
        var explain = await GetAsync<ExecutionExplainArtifact>(
                client: client,
                artifacts: artifacts,
                artifactName: "http-explain-response.json",
                route: MaterializationHarnessExecutionRoutes.ExplainFor(options.HttpProcessInstanceId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        operations.Add(MaterializationHarnessControlObservation.Create(
            operation: ExecutionExplainWireNames.Explain,
            resultKind: explain.ResultKind,
            body: explain.Body));
        var traces = await GetAsync<ExecutionApiProblem>(
                client: client,
                artifacts: artifacts,
                artifactName: "http-traces-response.json",
                route: MaterializationHarnessExecutionRoutes.TracesFor(options.HttpProcessInstanceId),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        operations.Add(MaterializationHarnessControlObservation.Create(
            operation: ProcessExecutionTraceWireNames.Read,
            resultKind: traces.ResultKind,
            body: traces.Body));

        var continueProcess = await ProjectAndPostControlAsync(
                client: client,
                options: options,
                artifacts: artifacts,
                operation: ExecutionControlWireNames.Continue,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        operations.Add(MaterializationHarnessControlObservation.Create(
            operation: ExecutionControlWireNames.Continue,
            resultKind: continueProcess.ResultKind,
            body: continueProcess.Body));
        var continued = await WaitForEvidenceAsync(
                client: client,
                options: options,
                host: host,
                predicate: evidence => evidence.ControlMode == ProcessControlMode.Running
                    && string.Equals(evidence.CurrentGeneration, initialGeneration, StringComparison.Ordinal)
                    && !evidence.SelectedControlEpochs.IsDefaultOrEmpty,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var limits = await ProjectAndPostAsync<ControlLimitUpdateResult>(
                client: client,
                options: options,
                artifacts: artifacts,
                operation: ControlLimitUpdateWireNames.UpdateLimits,
                maximumBatchItems: 1,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        operations.Add(MaterializationHarnessControlObservation.Create(
            operation: ControlLimitUpdateWireNames.UpdateLimits,
            resultKind: limits.ResultKind,
            body: limits.Body));
        var limitBoundExactGeneration = continued.SelectedControlEpochs.Contains(
            limits.Body.Epoch.Value,
            StringComparer.Ordinal);

        var restart = await ProjectAndPostControlAsync(
                client: client,
                options: options,
                artifacts: artifacts,
                operation: ExecutionControlWireNames.RestartAttempt,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        operations.Add(MaterializationHarnessControlObservation.Create(
            operation: ExecutionControlWireNames.RestartAttempt,
            resultKind: restart.ResultKind,
            body: restart.Body));
        var restarted = await WaitForEvidenceAsync(
                client: client,
                options: options,
                host: host,
                predicate: evidence => evidence.CurrentGeneration is not null
                    && !string.Equals(evidence.CurrentGeneration, initialGeneration, StringComparison.Ordinal),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var restartedGeneration = RequireGeneration(restarted);

        var cancel = await ProjectAndPostControlAsync(
                client: client,
                options: options,
                artifacts: artifacts,
                operation: ExecutionControlWireNames.Cancel,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        operations.Add(MaterializationHarnessControlObservation.Create(
            operation: ExecutionControlWireNames.Cancel,
            resultKind: cancel.ResultKind,
            body: cancel.Body));
        var cancelled = await WaitForEvidenceAsync(
                client: client,
                options: options,
                host: host,
                predicate: static evidence => evidence.ControlMode == ProcessControlMode.Cancelled
                    && evidence.TerminalOutcome == ExecutionTerminalOutcomeKind.Cancelled
                    && evidence.SelectedGenerationState is null or MaterializationGenerationState.Retired
                    && evidence.ActiveGeneration is null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var interrupted = await GetEvidenceAsync(
                client: client,
                options: options,
                selectedGeneration: initialGeneration,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var cancelledCandidate = await GetEvidenceAsync(
                client: client,
                options: options,
                selectedGeneration: restartedGeneration,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var result = new MaterializationHarnessControlScenarioResult(
            SchemaVersion: MaterializationHarnessControlScenarioResult.CurrentSchemaVersion,
            Transport: MaterializationHarnessControlTransportKind.Http,
            Provider: options.Provider,
            Operations: operations.MoveToImmutable(),
            InitialAttempt: initial.CurrentAttemptId,
            InitialGeneration: initialGeneration,
            PausedAttempt: paused.CurrentAttemptId,
            PausedGeneration: RequireGeneration(paused),
            ContinuedAttempt: continued.CurrentAttemptId,
            ContinuedGeneration: RequireGeneration(continued),
            RestartedAttempt: restarted.CurrentAttemptId,
            RestartedGeneration: restartedGeneration,
            CancelledAttempt: cancelled.CurrentAttemptId,
            CancelledGeneration: RequireGeneration(cancelled),
            LimitUpdateBoundExactGeneration: limitBoundExactGeneration,
            InterruptedGenerationState: interrupted.SelectedGenerationState,
            CancelledGenerationState: cancelledCandidate.SelectedGenerationState,
            ActiveGeneration: cancelled.ActiveGeneration,
            FinalControlMode: cancelled.ControlMode,
            FinalTerminalOutcome: cancelled.TerminalOutcome);
        _ = result.ValidateAndProjectSemantics();
        return result;
    }

    static async Task<HttpResult<TBody>> ProjectAndPostAsync<TBody>(
        HttpClient client,
        ControlEquivalenceOptions options,
        BoundedArtifactWriter artifacts,
        string operation,
        long? maximumBatchItems,
        CancellationToken cancellationToken)
    {
        var projectionRoute = MaterializationHarnessExecutionRoutes.ProjectRequest(options.Provider, operation);
        if (maximumBatchItems.HasValue)
            projectionRoute += $"?maximumBatchItems={maximumBatchItems.Value}";
        using var projectionResponse = await client.GetAsync(projectionRoute, cancellationToken).ConfigureAwait(false);
        var projectionJson = await projectionResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        await artifacts.WriteTextAsync($"http-{operation}-projection.json", projectionJson).ConfigureAwait(false);
        projectionResponse.EnsureSuccessStatusCode();
        using var projection = JsonDocument.Parse(projectionJson);
        var root = projection.RootElement;
        var projectedOperation = root.GetProperty("operation").GetString();
        var method = root.GetProperty("method").GetString();
        var route = root.GetProperty("route").GetString();
        if (!string.Equals(projectedOperation, operation, StringComparison.Ordinal)
            || !string.Equals(method, "POST", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(route))
        {
            throw new InvalidOperationException("The host returned an incoherent canonical request projection.");
        }
        using var content = new StringContent(
            root.GetProperty("request").GetRawText(),
            Encoding.UTF8,
            "application/json");
        using var response = await client.PostAsync(route, content, cancellationToken).ConfigureAwait(false);
        var bodyJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        await artifacts.WriteTextAsync($"http-{operation}-response.json", bodyJson).ConfigureAwait(false);
        var body = JsonSerializer.Deserialize<TBody>(bodyJson, WebJson)
            ?? throw new InvalidOperationException($"HTTP operation '{operation}' returned no canonical body.");
        return new(ResultKind(response.StatusCode), body);
    }

    static async Task<HttpResult<ExecutionControlResult>> ProjectAndPostControlAsync(
        HttpClient client,
        ControlEquivalenceOptions options,
        BoundedArtifactWriter artifacts,
        string operation,
        CancellationToken cancellationToken)
    {
        var retries = 0;
        while (true)
        {
            var result = await ProjectAndPostAsync<ExecutionControlResult>(
                    client: client,
                    options: options,
                    artifacts: artifacts,
                    operation: operation,
                    maximumBatchItems: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!MaterializationHarnessControlRetry.IsTransient(result.Body))
            {
                await artifacts.WriteTextAsync(
                    $"http-{operation}-retries.json",
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        retries,
                        reason = "durable worker ownership handoff"
                    }, WebJson)).ConfigureAwait(false);
                return result;
            }
            retries++;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    static async Task<HttpResult<TBody>> GetAsync<TBody>(
        HttpClient client,
        BoundedArtifactWriter artifacts,
        string artifactName,
        string route,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
        var bodyJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        await artifacts.WriteTextAsync(artifactName, bodyJson).ConfigureAwait(false);
        var body = JsonSerializer.Deserialize<TBody>(bodyJson, WebJson)
            ?? throw new InvalidOperationException($"HTTP route '{route}' returned no canonical body.");
        return new(ResultKind(response.StatusCode), body);
    }

    static async Task<MaterializationHarnessFailureEvidence> WaitForEvidenceAsync(
        HttpClient client,
        ControlEquivalenceOptions options,
        CapturedHost host,
        Func<MaterializationHarnessFailureEvidence, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (host.Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The HTTP control host exited with code {host.Process.ExitCode} before the scenario completed.");
            }
            try
            {
                var evidence = await GetEvidenceAsync(
                        client: client,
                        options: options,
                        selectedGeneration: null,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (predicate(evidence))
                    return evidence;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    static async Task<MaterializationHarnessFailureEvidence> GetEvidenceAsync(
        HttpClient client,
        ControlEquivalenceOptions options,
        string? selectedGeneration,
        CancellationToken cancellationToken)
    {
        var route = MaterializationHarnessExecutionRoutes.FailureEvidenceFor(options.Provider);
        if (selectedGeneration is not null)
            route += $"?generation={Uri.EscapeDataString(selectedGeneration)}";
        using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<MaterializationHarnessFailureEvidence>(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                WebJson)
            ?? throw new InvalidOperationException("The HTTP control host returned no failure evidence.");
    }

    static async Task WaitForHostAsync(
        HttpClient client,
        ControlEquivalenceOptions options,
        CapturedHost host,
        CancellationToken cancellationToken)
    {
        var route = MaterializationHarnessExecutionRoutes.ProjectRequest(
            options.Provider,
            ProcessStartWireNames.Start);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (host.Process.HasExited)
                throw new InvalidOperationException($"The HTTP control host exited with code {host.Process.ExitCode}.");
            try
            {
                using var response = await client.GetAsync(route, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
    }

    static CapturedHost StartHttpHost(ControlEquivalenceOptions options)
    {
        var process = new Process
        {
            StartInfo = CreateHostStartInfo(options, options.HttpProcessInstancePrefix),
            EnableRaisingEvents = true
        };
        var stdout = new BoundedLineCapture(SupervisorOptions.MaximumArtifactBytes);
        var stderr = new BoundedLineCapture(SupervisorOptions.MaximumArtifactBytes);
        process.OutputDataReceived += (_, eventArgs) => stdout.Add(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => stderr.Add(eventArgs.Data);
        if (!process.Start())
            throw new InvalidOperationException("The HTTP control host did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new(process, stdout, stderr);
    }

    static async Task StopHostAsync(
        CapturedHost host,
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

    static ProcessStartInfo CreateHostStartInfo(
        ControlEquivalenceOptions options,
        string processInstancePrefix)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = options.RepositoryRoot
        };
        start.ArgumentList.Add(options.HostAssemblyPath);
        start.Environment["COHESIVE_MATERIALIZATION_PROCESS_INSTANCE_ID"] = processInstancePrefix;
        start.Environment["COHESIVE_MATERIALIZATION_PAGE_DELAY_MS"] = "10000";
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

    static void PrepareArtifactDirectory(ControlEquivalenceOptions options)
    {
        if (Directory.Exists(options.ArtifactDirectory)
            && Directory.EnumerateFileSystemEntries(options.ArtifactDirectory).Any())
        {
            throw new InvalidOperationException(
                $"Artifact directory '{options.ArtifactDirectory}' must be absent or empty.");
        }
        Directory.CreateDirectory(options.ArtifactDirectory);
        if (!File.Exists(options.HostAssemblyPath))
            throw new FileNotFoundException("Build the Release materialization host first.", options.HostAssemblyPath);
    }

    static ApiResultKind ResultKind(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK => ApiResultKind.Success,
        HttpStatusCode.Accepted => ApiResultKind.Accepted,
        HttpStatusCode.BadRequest => ApiResultKind.ValidationFailed,
        HttpStatusCode.Forbidden => ApiResultKind.Forbidden,
        HttpStatusCode.NotFound => ApiResultKind.NotFound,
        HttpStatusCode.Conflict => ApiResultKind.Conflict,
        HttpStatusCode.PreconditionFailed => ApiResultKind.PreconditionFailed,
        _ => throw new InvalidOperationException($"HTTP status {(int)statusCode} has no control result mapping.")
    };

    static string RequireGeneration(MaterializationHarnessFailureEvidence evidence) =>
        evidence.CurrentGeneration
        ?? throw new InvalidOperationException("The materialization Process has no current generation.");

    sealed record HttpResult<TBody>(ApiResultKind ResultKind, TBody Body);

    sealed record CapturedHost(
        Process Process,
        BoundedLineCapture StandardOutput,
        BoundedLineCapture StandardError);
}
