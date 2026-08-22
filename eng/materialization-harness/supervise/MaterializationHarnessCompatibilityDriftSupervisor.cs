using System.Text.Json;
using Cohesive.MaterializationHarness.Control;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Supervise;

static partial class MaterializationHarnessSupervisor
{
    internal static async Task<int> RunCompatibilityDriftAsync(string[] args)
    {
        var options = await SupervisorOptions.ParseCompatibilityDriftAsync(args).ConfigureAwait(false);
        PrepareArtifactDirectory(options);
        if (!File.Exists(options.SeedAssemblyPath))
            throw new FileNotFoundException("Build the Release materialization seed tool before supervising it.", options.SeedAssemblyPath);
        var setupDirectory = Path.Combine(options.ArtifactDirectory, "setup");
        Directory.CreateDirectory(setupDirectory);
        var setupArtifacts = new BoundedArtifactWriter(
            directory: setupDirectory,
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
                setupArtifacts,
                artifactPrefix: "sdk-start",
                arguments: ["--start", options.Provider],
                cancellationToken: timeout.Token).ConfigureAwait(false);

            host = StartHost(options, armFault: false);
            using (var client = CreateHttpClient())
            {
                _ = await WaitForSourceConvergenceAsync(
                    client: client,
                    options: options,
                    host: host,
                    generation: null,
                    expectedVisibleItemCount: journal.Baseline.Orders.Length,
                    cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            await StopHostAsync(host, setupArtifacts, artifactPrefix: "baseline-host").ConfigureAwait(false);
            host = null;
            var baseline = await CaptureControlEvidenceAsync(
                options,
                setupArtifacts,
                artifactPrefix: "baseline",
                cancellationToken: timeout.Token).ConfigureAwait(false);
            ValidateBaseline(options, baseline);

            var mutation = await ExecuteCliAsync(
                options: options,
                artifacts: setupArtifacts,
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
            var marker = await WaitForMarkerAsync(options, host, timeout.Token).ConfigureAwait(false);
            await StopHostAsync(host, setupArtifacts, artifactPrefix: "pending-work-host").ConfigureAwait(false);
            host = null;
            var pending = await CaptureControlEvidenceAsync(
                options,
                setupArtifacts,
                artifactPrefix: "pending-work",
                cancellationToken: timeout.Token).ConfigureAwait(false);
            _ = ValidateInterruptedReplay(options, baseline, marker, pending);

            var completedCells = new List<string>(MaterializationHarnessMatrixCatalog.CompatibilityDrifts.Length);
            foreach (var cell in MaterializationHarnessMatrixCatalog.CompatibilityDrifts)
            {
                var cellDirectory = Path.Combine(options.ArtifactDirectory, cell.WireName);
                Directory.CreateDirectory(cellDirectory);
                var cellArtifacts = new BoundedArtifactWriter(
                    directory: cellDirectory,
                    maximumBytes: SupervisorOptions.MaximumArtifactBytes);
                var before = await CaptureControlEvidenceAsync(
                    options,
                    cellArtifacts,
                    artifactPrefix: "before",
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                var probe = await RunProbeAsync<MaterializationHarnessCompatibilityDriftProbeResult>(
                    options,
                    cellArtifacts,
                    artifactPrefix: "probe",
                    arguments: ["--probe-compatibility-drift", options.Provider, cell.WireName],
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                var after = await CaptureControlEvidenceAsync(
                    options,
                    cellArtifacts,
                    artifactPrefix: "after",
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                ValidateCompatibilityDrift(options, cell, probe, before, after);
                await cellArtifacts.WriteJsonAsync("probe.json", probe).ConfigureAwait(false);
                var cellId = $"drift/{options.Provider}/{cell.WireName}";
                await cellArtifacts.WriteManifestAsync(new
                {
                    schemaVersion = 1,
                    manifestKind = "cell",
                    cellId,
                    expectedOutcome = MaterializationHarnessExpectedOutcome.ExpectedFailure.ToString(),
                    expectedDisposition = cell.ExpectedDisposition,
                    actualDisposition = probe.ActualDisposition,
                    requiredControlAction = cell.RequiredControlAction.ToString(),
                    generation = probe.CanonicalGeneration,
                    completed = true
                }).ConfigureAwait(false);
                completedCells.Add(cellId);
            }

            var summary = new
            {
                schemaVersion = 1,
                options.RunIdentity,
                options.Provider,
                generation = marker.Generation,
                cells = completedCells,
                completed = true
            };
            await setupArtifacts.WriteManifestAsync(new
            {
                schemaVersion = 1,
                manifestKind = "support",
                supportId = $"drift/{options.Provider}/setup",
                summary.cells,
                completed = true
            }).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(summary, WebJson));
            return 0;
        }
        catch (Exception exception)
        {
            if (host is not null)
                await StopHostAsync(host, setupArtifacts, artifactPrefix: "failed-drift-host").ConfigureAwait(false);
            await setupArtifacts.WriteTextAsync("compatibility-drift-failure.txt", exception.ToString())
                .ConfigureAwait(false);
            await setupArtifacts.WriteManifestAsync(new
            {
                schemaVersion = 1,
                manifestKind = "support",
                supportId = $"drift/{options.Provider}/setup",
                completed = false,
                failure = exception.GetType().FullName
            }).ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    static void ValidateCompatibilityDrift(
        SupervisorOptions options,
        MaterializationHarnessCompatibilityDriftCell cell,
        MaterializationHarnessCompatibilityDriftProbeResult probe,
        MaterializationHarnessFailureEvidence before,
        MaterializationHarnessFailureEvidence after)
    {
        if (probe.SchemaVersion != 1
            || probe.Provider != options.Provider
            || probe.Kind != cell.Kind
            || probe.Authority != cell.Authority
            || probe.ExpectedDisposition != cell.ExpectedDisposition
            || probe.ActualDisposition != cell.ExpectedDisposition
            || !probe.DiagnosticCodes.Contains(cell.ExpectedDiagnosticCode, StringComparer.Ordinal)
            || probe.RequiredControlAction != cell.RequiredControlAction
            || probe.CanonicalGeneration != before.CurrentGeneration
            || probe.CanonicalGeneration != after.CurrentGeneration
            || !probe.CanonicalAuthorityPreserved
            || !probe.DriftedAuthorityAbsent
            || !probe.TargetAuthorityPreserved
            || probe.BeforeAuthorityRevision != probe.AfterAuthorityRevision
            || probe.BeforeAuthorityFence != probe.AfterAuthorityFence
            || probe.BeforeTargetRevision != probe.AfterTargetRevision
            || probe.BeforeActiveGeneration != probe.AfterActiveGeneration
            || !SameRetainedEvidence(before, after))
        {
            throw new InvalidOperationException(
                $"Compatibility drift '{cell.WireName}' did not fail closed without advancing retained state.");
        }
    }

    static bool SameRetainedEvidence(
        MaterializationHarnessFailureEvidence before,
        MaterializationHarnessFailureEvidence after) =>
        before.Provider == after.Provider
        && before.ProcessInstanceId == after.ProcessInstanceId
        && before.CurrentAttemptId == after.CurrentAttemptId
        && before.ControlRevision == after.ControlRevision
        && before.ControlMode == after.ControlMode
        && before.RecoveryPolicy == after.RecoveryPolicy
        && before.TerminalOutcome == after.TerminalOutcome
        && before.CurrentGeneration == after.CurrentGeneration
        && before.SelectedGeneration == after.SelectedGeneration
        && before.TargetRevision == after.TargetRevision
        && before.ActiveGeneration == after.ActiveGeneration
        && before.LatestPromotionFence == after.LatestPromotionFence
        && before.RetainedGenerationCount == after.RetainedGenerationCount
        && before.SelectedGenerationState == after.SelectedGenerationState
        && before.SelectedGenerationRevision == after.SelectedGenerationRevision
        && before.SelectedPhysicalIndex == after.SelectedPhysicalIndex
        && before.SelectedHasPermanentFailures == after.SelectedHasPermanentFailures
        && before.SelectedPendingRetryableMutationCount == after.SelectedPendingRetryableMutationCount
        && before.SelectedValidationIsValid == after.SelectedValidationIsValid
        && before.SelectedVisibleItemCount == after.SelectedVisibleItemCount
        && before.SelectedTombstoneCount == after.SelectedTombstoneCount
        && before.ReadAlias == after.ReadAlias
        && JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(before.ReadAliasIndices),
            JsonSerializer.SerializeToElement(after.ReadAliasIndices))
        && JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(before.SelectedControlEpochs),
            JsonSerializer.SerializeToElement(after.SelectedControlEpochs))
        && JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(before.SynchronizationWork),
            JsonSerializer.SerializeToElement(after.SynchronizationWork))
        && JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(before.DurableOperations),
            JsonSerializer.SerializeToElement(after.DurableOperations))
        && JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(before.Progress),
            JsonSerializer.SerializeToElement(after.Progress))
        && SameSourceHeadAuthorities(before.SourceHeads, after.SourceHeads)
        && JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(before.CanonicalDocuments),
            JsonSerializer.SerializeToElement(after.CanonicalDocuments));

    static bool SameSourceHeadAuthorities(
        IReadOnlyList<MaterializationHarnessSourceHeadEvidence> before,
        IReadOnlyList<MaterializationHarnessSourceHeadEvidence> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }

        for (var index = 0; index < before.Count; index++)
        {
            var beforeHead = before[index];
            var afterHead = after[index];
            if (beforeHead.Feed != afterHead.Feed
                || beforeHead.Input != afterHead.Input
                || beforeHead.Partition != afterHead.Partition
                || beforeHead.FormatVersion != afterHead.FormatVersion)
            {
                return false;
            }
        }

        return true;
    }
}
