using System.Net;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Control;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Processes.IR;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Supervise;

static partial class MaterializationHarnessSupervisor
{
    internal static async Task<int> RunSourceMatrixAsync(string[] args)
    {
        var options = await SupervisorOptions.ParseSourceMatrixAsync(args).ConfigureAwait(false);
        PrepareArtifactDirectory(options);
        if (!File.Exists(options.SeedAssemblyPath))
            throw new FileNotFoundException("Build the Release materialization seed tool before supervising it.", options.SeedAssemblyPath);
        var artifacts = new BoundedArtifactWriter(
            directory: options.ArtifactDirectory,
            maximumBytes: SupervisorOptions.MaximumArtifactBytes);
        var journal = await FreightScenarioJournal.LoadAsync(
            path: Environment.GetEnvironmentVariable("COHESIVE_MATERIALIZATION_SCENARIO_PATH")
                ?? throw new InvalidOperationException("Set COHESIVE_MATERIALIZATION_SCENARIO_PATH."));
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

            host = StartHost(options, armFault: false);
            using (var baselineHttp = CreateHttpClient())
            {
                _ = await WaitForSourceConvergenceAsync(
                    client: baselineHttp,
                    options: options,
                    host: host,
                    generation: null,
                    expectedVisibleItemCount: journal.Baseline.Orders.Length,
                    cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            await StopHostAsync(host, artifacts, artifactPrefix: "baseline-host").ConfigureAwait(false);
            host = null;
            var baseline = await CaptureControlEvidenceAsync(
                options,
                artifacts,
                artifactPrefix: "baseline",
                cancellationToken: timeout.Token).ConfigureAwait(false);
            ValidateBaseline(options, baseline);

            var mutation = await ExecuteCliAsync(
                options: options,
                artifacts: artifacts,
                artifactPrefix: "apply-source-changes",
                arguments: ["--apply-changes"],
                cancellationToken: timeout.Token,
                assemblyPath: options.SeedAssemblyPath).ConfigureAwait(false);
            if (mutation.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The canonical mutation projection exited with code {mutation.ExitCode}: "
                    + mutation.StandardError
                    + mutation.StandardOutput);
            }

            host = StartHost(options, armFault: true);
            var interruptedMarker = await WaitForMarkerAsync(options, host, timeout.Token).ConfigureAwait(false);
            await StopHostAsync(host, artifacts, artifactPrefix: "interrupted-incremental-host").ConfigureAwait(false);
            host = null;
            var interrupted = await CaptureControlEvidenceAsync(
                options,
                artifacts,
                artifactPrefix: "interrupted",
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var pending = ValidateInterruptedReplay(options, baseline, interruptedMarker, interrupted);

            var incompatible = await RunProbeAsync<MaterializationHarnessIncompatibleReplayProbeResult>(
                options,
                artifacts,
                artifactPrefix: "incompatible-replay-probe",
                arguments: ["--probe-incompatible-replay", options.Provider],
                cancellationToken: timeout.Token).ConfigureAwait(false);
            ValidateIncompatibleReplay(interrupted, pending, incompatible);

            var replayMarkerPath = Path.Combine(options.ArtifactDirectory, "replay-completed-boundary.json");
            host = StartHost(
                options,
                new HostFaultPlan(
                    Point: MaterializationExecutionBoundaryPoint.BeforeSourceRead,
                    MarkerPath: replayMarkerPath,
                    ScopeIdentity: interruptedMarker.ScopeIdentity,
                    OperationIdentity: null));
            _ = await WaitForMarkerAsync(
                options,
                host,
                markerPath: replayMarkerPath,
                point: MaterializationExecutionBoundaryPoint.BeforeSourceRead,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            await StopHostAsync(host, artifacts, artifactPrefix: "replay-completed-host").ConfigureAwait(false);
            host = null;
            var replayed = await CaptureControlEvidenceAsync(
                options,
                artifacts,
                artifactPrefix: "replayed",
                cancellationToken: timeout.Token).ConfigureAwait(false);
            ValidateExactReplay(interrupted, pending, replayed);

            host = StartHost(options, armFault: false);
            using var http = CreateHttpClient();
            var converged = await WaitForSourceConvergenceAsync(
                http,
                options,
                host,
                generation: interruptedMarker.Generation,
                expectedVisibleItemCount: journal.Final.Orders.Length,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            await CaptureHttpEvidenceAsync(http, options, artifacts).ConfigureAwait(false);
            await StopHostAsync(host, artifacts, artifactPrefix: "converged-host").ConfigureAwait(false);
            host = null;
            await artifacts.WriteJsonAsync("converged-evidence.json", converged).ConfigureAwait(false);
            await artifacts.WriteJsonAsync("final-documents.json", converged.CanonicalDocuments).ConfigureAwait(false);

            var ordering = await RunProbeAsync<MaterializationHarnessTargetOrderingProbeResult>(
                options,
                artifacts,
                artifactPrefix: "target-ordering-probe",
                arguments:
                [
                    "--probe-target-ordering",
                    options.Provider,
                    interrupted.SynchronizationWork!.Fence
                ],
                cancellationToken: timeout.Token).ConfigureAwait(false);
            ValidateTargetOrdering(interruptedMarker.Generation, ordering);
            await CaptureElasticAsync(http, options, artifacts, "after-source-matrix").ConfigureAwait(false);

            var summary = new
            {
                schemaVersion = 1,
                options.RunIdentity,
                options.Provider,
                generation = interruptedMarker.Generation,
                replayedPreparation = pending.PreparationId,
                replayedVersion = pending.Version,
                incompatibleReplayDisposition = incompatible.Disposition.ToString(),
                incompatibleReplayRequiredControl = incompatible.RequiredControlAction.ToString(),
                staleWorkerDisposition = ordering.StaleWorkerDisposition.ToString(),
                staleVersionDisposition = ordering.StaleVersionItemDisposition.ToString(),
                visibleItemCount = converged.SelectedVisibleItemCount,
                documentCount = converged.CanonicalDocuments.Length,
                completed = true
            };
            await artifacts.WriteManifestAsync(summary).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(summary, WebJson));
            return 0;
        }
        catch (Exception exception)
        {
            if (host is not null)
                await StopHostAsync(host, artifacts, artifactPrefix: "failed-source-matrix-host").ConfigureAwait(false);
            await artifacts.WriteTextAsync("source-matrix-failure.txt", exception.ToString()).ConfigureAwait(false);
            await artifacts.WriteManifestAsync(new
            {
                schemaVersion = 1,
                options.RunIdentity,
                options.Provider,
                completed = false,
                failure = exception.GetType().FullName
            }).ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    static void ValidateBaseline(
        SupervisorOptions options,
        MaterializationHarnessFailureEvidence baseline)
    {
        if (baseline.TerminalOutcome != ExecutionTerminalOutcomeKind.Completed
            || baseline.CurrentGeneration is null
            || baseline.CurrentGeneration != baseline.ActiveGeneration
            || baseline.SelectedGenerationState != MaterializationGenerationState.Active
            || baseline.SelectedVisibleItemCount != options.ExpectedVisibleItemCount
            || baseline.SynchronizationWork?.PendingWork is not null)
        {
            throw new InvalidOperationException("The baseline Process did not establish one caught-up active generation.");
        }
    }

    static MaterializationHarnessPendingWorkEvidence ValidateInterruptedReplay(
        SupervisorOptions options,
        MaterializationHarnessFailureEvidence baseline,
        ReachedBoundary marker,
        MaterializationHarnessFailureEvidence interrupted)
    {
        if (marker.Point != MaterializationExecutionBoundaryPoint.AfterTargetBatch
            || interrupted.CurrentGeneration != marker.Generation
            || interrupted.ActiveGeneration != marker.Generation
            || interrupted.SelectedGenerationState != MaterializationGenerationState.Active
            || baseline.CurrentGeneration != marker.Generation)
        {
            throw new InvalidOperationException("The compatible incremental interruption escaped its active generation fence.");
        }
        var pending = interrupted.SynchronizationWork?.PendingWork
            ?? throw new InvalidOperationException("The post-target interruption did not retain exact pending synchronization work.");
        if (pending.Feed != marker.ScopeIdentity
            || pending.Mutations.IsDefaultOrEmpty
            || pending.Version is null
            || pending.Mutations.Any(mutation => mutation.Version != pending.Version))
        {
            throw new InvalidOperationException("The interrupted target batch is not coupled to one exact source page and item version.");
        }
        if (interrupted.SelectedVisibleItemCount is null
            || interrupted.SelectedVisibleItemCount < 0
            || interrupted.SelectedVisibleItemCount > options.ExpectedVisibleItemCount + 1)
        {
            throw new InvalidOperationException("The interrupted active generation exposed an invalid visible-item count.");
        }
        return pending;
    }

    static void ValidateIncompatibleReplay(
        MaterializationHarnessFailureEvidence interrupted,
        MaterializationHarnessPendingWorkEvidence pending,
        MaterializationHarnessIncompatibleReplayProbeResult probe)
    {
        if (probe.Generation != interrupted.CurrentGeneration
            || probe.PreparationId != pending.PreparationId
            || probe.OriginalPosition != pending.ThroughPosition
            || probe.ConflictingPosition == probe.OriginalPosition
            || probe.Disposition != MaterializationSynchronizationWorkMutationDisposition.IdentityConflict
            || probe.RequiredControlAction != ProcessRecoveryPolicy.RestartAttempt
            || probe.BeforeRevision != probe.AfterRevision
            || probe.BeforeFence != probe.AfterFence
            || !probe.PendingWorkPreserved)
        {
            throw new InvalidOperationException("Incompatible source cursor evidence did not fail closed with RestartAttempt guidance.");
        }
    }

    static void ValidateExactReplay(
        MaterializationHarnessFailureEvidence interrupted,
        MaterializationHarnessPendingWorkEvidence pending,
        MaterializationHarnessFailureEvidence replayed)
    {
        var work = replayed.SynchronizationWork
            ?? throw new InvalidOperationException("Exact replay recovery lost synchronization-work evidence.");
        if (replayed.CurrentGeneration != interrupted.CurrentGeneration
            || replayed.ActiveGeneration != interrupted.ActiveGeneration
            || replayed.SelectedGenerationState != MaterializationGenerationState.Active
            || work.PendingWork is not null
            || work.NextItemVersion != interrupted.SynchronizationWork!.NextItemVersion
            || !replayed.CanonicalDocuments.SequenceEqual(
                interrupted.CanonicalDocuments,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "A compatible retry changed generation, allocated another item version, or changed logical target state.");
        }
        var source = replayed.SourceHeads.Single(head => head.Feed == pending.Feed);
        var progress = replayed.Progress.Single(candidate =>
            candidate.Input == source.Input && candidate.Partition == source.Partition);
        if (progress.ChangeCheckpoint != pending.Checkpoint
            || progress.ChangePosition != pending.ThroughPosition
            || progress.AppliedDeliveryCount != pending.AppliedDeliveries.Length)
        {
            throw new InvalidOperationException("Exact target replay did not commit its coupled source checkpoint once.");
        }
    }

    static async Task<MaterializationHarnessFailureEvidence> WaitForSourceConvergenceAsync(
        HttpClient client,
        SupervisorOptions options,
        SupervisedHost host,
        string? generation,
        long expectedVisibleItemCount,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (host.Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The source-matrix recovery host exited with code {host.Process.ExitCode} before convergence.");
            }
            try
            {
                using var response = await client.GetAsync(ProviderFailureEvidenceUri(options), cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    var evidence = JsonSerializer.Deserialize<MaterializationHarnessFailureEvidence>(
                        await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false),
                        WebJson);
                    if (evidence?.TerminalOutcome is ExecutionTerminalOutcomeKind.Failed
                        or ExecutionTerminalOutcomeKind.Cancelled
                        or ExecutionTerminalOutcomeKind.Terminated)
                    {
                        throw new InvalidOperationException(
                            $"The source-matrix Process terminated as '{evidence.TerminalOutcome}'.");
                    }
                    var expectedGeneration = generation ?? evidence?.CurrentGeneration;
                    if (evidence is not null
                        && expectedGeneration is not null
                        && IsSourceConverged(evidence, expectedGeneration, expectedVisibleItemCount))
                    {
                        return evidence;
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

    static bool IsSourceConverged(
        MaterializationHarnessFailureEvidence evidence,
        string generation,
        long expectedVisibleItemCount)
    {
        if (evidence.TerminalOutcome != ExecutionTerminalOutcomeKind.Completed
            || evidence.CurrentGeneration != generation
            || evidence.ActiveGeneration != generation
            || evidence.SelectedGenerationState != MaterializationGenerationState.Active
            || evidence.SelectedVisibleItemCount != expectedVisibleItemCount
            || evidence.CanonicalDocuments.Length != expectedVisibleItemCount
            || evidence.SynchronizationWork is not { PendingWork: null }
            || evidence.SourceHeads.IsDefaultOrEmpty
            || evidence.LastSynchronization is not
            {
                Disposition: MaterializationSynchronizationRunDisposition.Converged,
                ReceiptFingerprint: not null
            } synchronization
            || synchronization.Generation != generation
            || synchronization.FeedCount != evidence.SourceHeads.Length)
        {
            return false;
        }
        return true;
    }

    static void ValidateTargetOrdering(
        string generation,
        MaterializationHarnessTargetOrderingProbeResult probe)
    {
        if (probe.Generation != generation
            || probe.StaleWorkerDisposition != MaterializationBatchDisposition.StaleFence
            || probe.StaleWorkerItemDisposition != MaterializationItemOutcomeDisposition.RetryableRejected
            || probe.StaleVersionDisposition != MaterializationBatchDisposition.Applied
            || probe.StaleVersionItemDisposition != MaterializationItemOutcomeDisposition.VersionConflict
            || !probe.LogicalDocumentsUnchanged)
        {
            throw new InvalidOperationException("Stale worker or out-of-order item versions were not rejected without logical regression.");
        }
    }

    static async Task<MaterializationHarnessFailureEvidence> CaptureControlEvidenceAsync(
        SupervisorOptions options,
        BoundedArtifactWriter artifacts,
        string artifactPrefix,
        CancellationToken cancellationToken) => await RunProbeAsync<MaterializationHarnessFailureEvidence>(
            options,
            artifacts,
            artifactPrefix,
            ["--failure-evidence", options.Provider],
            cancellationToken).ConfigureAwait(false);

    static async Task<T> RunProbeAsync<T>(
        SupervisorOptions options,
        BoundedArtifactWriter artifacts,
        string artifactPrefix,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(
            options,
            artifacts,
            artifactPrefix,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(result.StandardOutput, WebJson)
            ?? throw new InvalidOperationException($"The {artifactPrefix} command returned no evidence.");
    }
}
