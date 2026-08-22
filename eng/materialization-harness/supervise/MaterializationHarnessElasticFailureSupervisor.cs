using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Control;
using Cohesive.Processes.IR;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Supervise;

static partial class MaterializationHarnessSupervisor
{
    internal static async Task<int> RunElasticFailureAsync(string[] args)
    {
        var (options, fault) = await SupervisorOptions.ParseElasticFailureAsync(args).ConfigureAwait(false);
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

            host = StartHost(options, fault);
            _ = await WaitForElasticFaultAsync(options, host, timeout.Token).ConfigureAwait(false);
            using var http = CreateHttpClient();
            var live = await WaitForElasticOutcomeAsync(
                client: http,
                options: options,
                host: host,
                fault: fault,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            await CaptureHttpEvidenceAsync(http, options, artifacts).ConfigureAwait(false);
            await StopHostAsync(host, artifacts, artifactPrefix: "faulted-host").ConfigureAwait(false);
            host = null;

            var observation = await ReadElasticFaultObservationAsync(options, timeout.Token).ConfigureAwait(false);
            var retained = await CaptureControlEvidenceAsync(
                options,
                artifacts,
                artifactPrefix: "retained",
                cancellationToken: timeout.Token).ConfigureAwait(false);
            ValidateElasticFailure(options, fault, observation, live, retained);
            await artifacts.WriteJsonAsync("elastic-fault-observation.json", observation).ConfigureAwait(false);
            await artifacts.WriteJsonAsync("retained-evidence.json", retained).ConfigureAwait(false);
            await CaptureElasticAsync(http, options, artifacts, "after-elastic-failure").ConfigureAwait(false);

            var summary = new
            {
                schemaVersion = 1,
                options.RunIdentity,
                options.Provider,
                fault = fault.ToString(),
                generation = retained.CurrentGeneration,
                retained.TerminalOutcome,
                retained.ActiveGeneration,
                retained.TargetRevision,
                retained.LatestPromotionFence,
                retained.RetainedGenerationCount,
                readAliasIndexCount = retained.ReadAliasIndices.Length,
                observation.MatchingRequestCount,
                observation.InjectedRequestFingerprint,
                observation.ExactRetryRequestFingerprint,
                observation.ReconciliationRequestPath,
                completed = true
            };
            await artifacts.WriteManifestAsync(summary).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(summary, WebJson));
            return 0;
        }
        catch (Exception exception)
        {
            if (host is not null)
                await StopHostAsync(host, artifacts, artifactPrefix: "failed-elastic-host").ConfigureAwait(false);
            await artifacts.WriteTextAsync("elastic-failure.txt", exception.ToString()).ConfigureAwait(false);
            await artifacts.WriteManifestAsync(new
            {
                schemaVersion = 1,
                options.RunIdentity,
                options.Provider,
                fault = fault.ToString(),
                completed = false,
                failure = exception.GetType().FullName
            }).ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    static async Task<MaterializationHarnessElasticFaultObservation> WaitForElasticFaultAsync(
        SupervisorOptions options,
        SupervisedHost host,
        CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient();
        while (!File.Exists(options.ElasticFaultMarkerPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (host.Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The armed Elastic host exited with code {host.Process.ExitCode} before injecting its fault.");
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
                    if (evidence is { TerminalOutcome: not ExecutionTerminalOutcomeKind.None })
                    {
                        throw new InvalidOperationException(
                            $"The Elastic failure Process reached '{evidence.TerminalOutcome}' before the armed fault was observed.");
                    }
                }
            }
            catch (HttpRequestException)
            {
                // The armed host may still be binding its HTTP endpoint.
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        return await ReadElasticFaultObservationAsync(options, cancellationToken).ConfigureAwait(false);
    }

    static async Task<MaterializationHarnessElasticFaultObservation> ReadElasticFaultObservationAsync(
        SupervisorOptions options,
        CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<MaterializationHarnessElasticFaultObservation>(
            await File.ReadAllTextAsync(options.ElasticFaultMarkerPath, cancellationToken).ConfigureAwait(false),
            WebJson) ?? throw new InvalidOperationException("The Elastic fault marker was empty.");

    static async Task<MaterializationHarnessFailureEvidence> WaitForElasticOutcomeAsync(
        HttpClient client,
        SupervisorOptions options,
        SupervisedHost host,
        MaterializationHarnessElasticFaultKind fault,
        CancellationToken cancellationToken)
    {
        var expectedTerminal = fault == MaterializationHarnessElasticFaultKind.PermanentBulkItemFailure
            ? ExecutionTerminalOutcomeKind.Failed
            : ExecutionTerminalOutcomeKind.Completed;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (host.Process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The Elastic failure host exited with code {host.Process.ExitCode} before '{expectedTerminal}'.");
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
                    if (evidence?.TerminalOutcome == expectedTerminal)
                        return evidence;
                    if (evidence?.TerminalOutcome is ExecutionTerminalOutcomeKind.Cancelled
                        or ExecutionTerminalOutcomeKind.Terminated
                        || evidence?.TerminalOutcome == ExecutionTerminalOutcomeKind.Failed
                        && expectedTerminal != ExecutionTerminalOutcomeKind.Failed)
                    {
                        throw new InvalidOperationException(
                            $"The Elastic failure Process terminated unexpectedly as '{evidence.TerminalOutcome}'.");
                    }
                }
            }
            catch (HttpRequestException)
            {
                // The armed host may still be binding its HTTP endpoint.
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    static void ValidateElasticFailure(
        SupervisorOptions options,
        MaterializationHarnessElasticFaultKind fault,
        MaterializationHarnessElasticFaultObservation observation,
        MaterializationHarnessFailureEvidence live,
        MaterializationHarnessFailureEvidence retained)
    {
        if (observation.SchemaVersion != 1
            || observation.RunIdentity != options.RunIdentity
            || observation.Provider != options.Provider
            || observation.Kind != fault
            || observation.HostProcessId <= 0
            || retained.ProcessInstanceId != options.ProcessInstanceId
            || retained.RecoveryPolicy != ProcessRecoveryPolicy.RestartAttempt
            || retained.CurrentGeneration != retained.SelectedGeneration
            || retained.CurrentGeneration != live.CurrentGeneration)
        {
            throw new InvalidOperationException("Elastic failure evidence is not bound to the exact Process and fault plan.");
        }

        switch (fault)
        {
            case MaterializationHarnessElasticFaultKind.RetryableBulkRejection:
                ValidatePartialBulkFault(observation, retryExpected: true, permanent: false);
                ValidatePublishedGeneration(options, retained);
                break;
            case MaterializationHarnessElasticFaultKind.PermanentBulkItemFailure:
                ValidatePartialBulkFault(observation, retryExpected: false, permanent: true);
                if (retained.TerminalOutcome != ExecutionTerminalOutcomeKind.Failed
                    || retained.ActiveGeneration is not null
                    || retained.TargetRevision != MaterializationTargetRevision.Initial.Value
                    || retained.LatestPromotionFence is not null
                    || retained.RetainedGenerationCount != 1
                    || retained.ReadAliasIndices.Length != 0
                    || retained.CanonicalDocuments.Length != 0
                    || retained.SelectedGenerationState != MaterializationGenerationState.Loading
                    || retained.SelectedHasPermanentFailures is not true
                    || retained.SelectedPendingRetryableMutationCount != 0
                    || retained.SelectedValidationIsValid is not null
                    || retained.SelectedVisibleItemCount < observation.AppliedItems.Length
                    || retained.SelectedVisibleItemCount >= options.ExpectedVisibleItemCount)
                {
                    throw new InvalidOperationException(
                        "A permanent Elastic item failure did not halt before validation and promotion without visibility.");
                }
                break;
            case MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss:
                if (observation.RequestPath != "/_aliases"
                    || !observation.ResponseLostAfterApply
                    || observation.MatchingRequestCount < 2
                    || observation.ReconciliationRequestPath is null
                    || !Uri.UnescapeDataString(observation.ReconciliationRequestPath)
                        .Contains(retained.ReadAlias, StringComparison.Ordinal)
                    || observation.ExactRetryRequestFingerprint is not null
                    || !observation.InjectedItems.IsEmpty
                    || !observation.AppliedItems.IsEmpty
                    || !observation.RejectedItems.IsEmpty
                    || !observation.ExactRetryItems.IsEmpty)
                {
                    throw new InvalidOperationException(
                        "The applied alias response was not reconciled through observed state without replaying the transaction.");
                }
                ValidatePublishedGeneration(options, retained);
                break;
            default:
                throw new InvalidOperationException($"Unsupported Elastic failure scenario '{fault}'.");
        }
    }

    static void ValidatePartialBulkFault(
        MaterializationHarnessElasticFaultObservation observation,
        bool retryExpected,
        bool permanent)
    {
        if (observation.RequestPath != "/_bulk"
            || observation.ResponseLostAfterApply
            || observation.InjectedItems.Length < 2
            || observation.AppliedItems.IsEmpty
            || observation.RejectedItems.IsEmpty
            || observation.AppliedItems.Length + observation.RejectedItems.Length != observation.InjectedItems.Length
            || permanent && observation.RejectedItems.Length != 1)
        {
            throw new InvalidOperationException("The Elastic partial-bulk fault did not retain exact applied and rejected subsets.");
        }
        if (retryExpected)
        {
            if (observation.MatchingRequestCount < 2
                || observation.ExactRetryRequestFingerprint is null
                || !observation.ExactRetryItems.SequenceEqual(observation.RejectedItems))
            {
                throw new InvalidOperationException("The retryable Elastic fault did not retry only unresolved mutations.");
            }
        }
        else if (observation.ExactRetryRequestFingerprint is not null
                 || !observation.ExactRetryItems.IsEmpty)
        {
            throw new InvalidOperationException("A permanent Elastic item failure was retried.");
        }
    }

    static void ValidatePublishedGeneration(
        SupervisorOptions options,
        MaterializationHarnessFailureEvidence evidence)
    {
        if (evidence.TerminalOutcome != ExecutionTerminalOutcomeKind.Completed
            || evidence.CurrentGeneration is null
            || evidence.ActiveGeneration != evidence.CurrentGeneration
            || evidence.TargetRevision != "1"
            || evidence.LatestPromotionFence is null
            || evidence.RetainedGenerationCount != 1
            || evidence.SelectedGenerationState != MaterializationGenerationState.Active
            || evidence.SelectedHasPermanentFailures is not false
            || evidence.SelectedPendingRetryableMutationCount != 0
            || evidence.SelectedValidationIsValid is not true
            || evidence.SelectedVisibleItemCount != options.ExpectedVisibleItemCount
            || evidence.CanonicalDocuments.Length != options.ExpectedVisibleItemCount
            || evidence.ReadAliasIndices is not [var published]
            || published != evidence.SelectedPhysicalIndex)
        {
            throw new InvalidOperationException("Elastic recovery did not publish exactly one complete active generation.");
        }
    }
}
