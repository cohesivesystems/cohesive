using System.Collections.Immutable;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Control;
using Cohesive.Processes.Runtime;
using Cohesive.Storage.Materialization;
using ProcessStartResult = Cohesive.Execution.ProcessStartResult;

namespace Cohesive.MaterializationHarness.Host;

static class MaterializationHarnessSdkControlScenario
{
    internal static async Task<MaterializationHarnessControlScenarioResult> RunAsync(
        MaterializationHarnessExecutionController controller,
        string provider,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The scenario timeout must be positive.");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var token = timeoutSource.Token;
        var operations = ImmutableArray.CreateBuilder<MaterializationHarnessControlOperationObservation>(9);
        var worker = RunWorkerAsync(controller, token);
        try
        {
            var start = await controller.DispatchStartAsync(
                    provider: provider,
                    issuedAtUtc: DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            operations.Add(Observe(ProcessStartWireNames.Start, start));
            ReportPhase(ProcessStartWireNames.Start);

            var initial = await WaitForEvidenceAsync(
                    controller: controller,
                    provider: provider,
                    worker: worker,
                    predicate: static evidence => evidence.CurrentGeneration is not null,
                    cancellationToken: token)
                .ConfigureAwait(false);
            var initialGeneration = RequireGeneration(initial);

            var pause = await DispatchControlAsync(
                    controller: controller,
                    provider: provider,
                    endpoint: controller.Catalog.Pause,
                    cancellationToken: token)
                .ConfigureAwait(false);
            RequireAcceptedControl(ExecutionControlWireNames.Pause, pause);
            operations.Add(Observe(ExecutionControlWireNames.Pause, pause));
            ReportPhase(ExecutionControlWireNames.Pause);
            var paused = await WaitForEvidenceAsync(
                    controller: controller,
                    provider: provider,
                    worker: worker,
                    predicate: static evidence => evidence.ControlMode == ProcessControlMode.Paused,
                    cancellationToken: token)
                .ConfigureAwait(false);

            var inspect = await controller.DispatchOperatorAsync(
                    provider: provider,
                    endpoint: controller.Catalog.Inspect,
                    issuedAtUtc: DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            operations.Add(Observe(ExecutionControlWireNames.Inspect, inspect));
            var explain = await controller.DispatchOperatorAsync(
                    provider: provider,
                    endpoint: controller.Catalog.Explain,
                    issuedAtUtc: DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            operations.Add(Observe(ExecutionExplainWireNames.Explain, explain));
            var traces = await controller.DispatchOperatorAsync(
                    provider: provider,
                    endpoint: controller.Catalog.Traces,
                    issuedAtUtc: DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            operations.Add(Observe(ProcessExecutionTraceWireNames.Read, traces));
            ReportPhase("inspect-explain-traces");

            var continueProcess = await DispatchControlAsync(
                    controller: controller,
                    provider: provider,
                    endpoint: controller.Catalog.Continue,
                    cancellationToken: token)
                .ConfigureAwait(false);
            RequireAcceptedControl(ExecutionControlWireNames.Continue, continueProcess);
            operations.Add(Observe(ExecutionControlWireNames.Continue, continueProcess));
            ReportPhase(ExecutionControlWireNames.Continue);
            var continued = await WaitForEvidenceAsync(
                    controller: controller,
                    provider: provider,
                    worker: worker,
                    predicate: evidence => evidence.ControlMode == ProcessControlMode.Running
                        && string.Equals(evidence.CurrentGeneration, initialGeneration, StringComparison.Ordinal)
                        && !evidence.SelectedControlEpochs.IsDefaultOrEmpty,
                    cancellationToken: token)
                .ConfigureAwait(false);

            var limits = await controller.DispatchLimitUpdateAsync(
                    provider: provider,
                    maximumBatchItems: 1,
                    issuedAtUtc: DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            operations.Add(Observe(ControlLimitUpdateWireNames.UpdateLimits, limits));
            ReportPhase(ControlLimitUpdateWireNames.UpdateLimits);
            var limitResult = limits.Body as ControlLimitUpdateResult
                ?? throw new InvalidOperationException("The SDK limit update returned no canonical result.");
            var limitBoundExactGeneration = continued.SelectedControlEpochs.Contains(
                limitResult.Epoch.Value,
                StringComparer.Ordinal);

            var restart = await DispatchControlAsync(
                    controller: controller,
                    provider: provider,
                    endpoint: controller.Catalog.RestartAttempt,
                    cancellationToken: token)
                .ConfigureAwait(false);
            RequireAcceptedControl(ExecutionControlWireNames.RestartAttempt, restart);
            operations.Add(Observe(ExecutionControlWireNames.RestartAttempt, restart));
            ReportPhase(ExecutionControlWireNames.RestartAttempt);
            var restarted = await WaitForEvidenceAsync(
                    controller: controller,
                    provider: provider,
                    worker: worker,
                    predicate: evidence => evidence.CurrentGeneration is not null
                        && !string.Equals(evidence.CurrentGeneration, initialGeneration, StringComparison.Ordinal),
                    cancellationToken: token)
                .ConfigureAwait(false);
            var restartedGeneration = RequireGeneration(restarted);

            var cancel = await DispatchControlAsync(
                    controller: controller,
                    provider: provider,
                    endpoint: controller.Catalog.Cancel,
                    cancellationToken: token)
                .ConfigureAwait(false);
            RequireAcceptedControl(ExecutionControlWireNames.Cancel, cancel);
            operations.Add(Observe(ExecutionControlWireNames.Cancel, cancel));
            ReportPhase(ExecutionControlWireNames.Cancel);
            var cancelled = await WaitForEvidenceAsync(
                    controller: controller,
                    provider: provider,
                    worker: worker,
                    predicate: static evidence => evidence.ControlMode == ProcessControlMode.Cancelled
                        && evidence.TerminalOutcome == ExecutionTerminalOutcomeKind.Cancelled,
                    cancellationToken: token)
                .ConfigureAwait(false);
            var interrupted = await controller.CaptureFailureEvidenceAsync(
                    provider: provider,
                    selectedGeneration: new(initialGeneration),
                    context: OperationContext.Create(cancellationToken: token))
                .ConfigureAwait(false);
            var cancelledCandidate = await controller.CaptureFailureEvidenceAsync(
                    provider: provider,
                    selectedGeneration: new(restartedGeneration),
                    context: OperationContext.Create(cancellationToken: token))
                .ConfigureAwait(false);

            var result = new MaterializationHarnessControlScenarioResult(
                SchemaVersion: MaterializationHarnessControlScenarioResult.CurrentSchemaVersion,
                Transport: MaterializationHarnessControlTransportKind.Sdk,
                Provider: provider,
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
        finally
        {
            await timeoutSource.CancelAsync().ConfigureAwait(false);
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        }
    }

    static async Task RunWorkerAsync(
        MaterializationHarnessExecutionController controller,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await controller.RunReadyProcessesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await controller.WaitForWorkAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    static async Task<MaterializationHarnessFailureEvidence> WaitForEvidenceAsync(
        MaterializationHarnessExecutionController controller,
        string provider,
        Task worker,
        Func<MaterializationHarnessFailureEvidence, bool> predicate,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (worker.IsCompleted)
                await worker.ConfigureAwait(false);
            try
            {
                var evidence = await controller.CaptureFailureEvidenceAsync(
                        provider: provider,
                        selectedGeneration: null,
                        context: OperationContext.Create(cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                if (predicate(evidence))
                    return evidence;
            }
            catch (InvalidOperationException)
            {
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    static async Task<ExecutionApiDispatchResult> DispatchControlAsync(
        MaterializationHarnessExecutionController controller,
        string provider,
        ApiEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        var retries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await controller.DispatchOperatorAsync(
                    provider: provider,
                    endpoint: endpoint,
                    issuedAtUtc: DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            if (result.Body is not ExecutionControlResult control
                || !MaterializationHarnessControlRetry.IsTransient(control))
            {
                if (retries != 0)
                    ReportPhase($"{endpoint.Operation.Name}-retries-{retries}");
                return result;
            }
            retries++;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    static MaterializationHarnessControlOperationObservation Observe(
        string operation,
        ExecutionApiDispatchResult result) => MaterializationHarnessControlObservation.Create(
        operation: operation,
        resultKind: result.Result.Kind,
        body: result.Body
            ?? throw new InvalidOperationException("The SDK operation returned no canonical body."));

    static string RequireGeneration(MaterializationHarnessFailureEvidence evidence) =>
        evidence.CurrentGeneration
        ?? throw new InvalidOperationException("The materialization Process has no current generation.");

    static void RequireAcceptedControl(string operation, ExecutionApiDispatchResult result)
    {
        if (result.Body is ExecutionControlResult control
            && control.Disposition != ProcessControlDecisionDisposition.InvalidState)
        {
            return;
        }
        var diagnostics = result.Body is ExecutionControlResult rejected
            ? string.Join(",", rejected.DiagnosticCodes)
            : "missing-control-result";
        throw new InvalidOperationException(
            $"Control operation '{operation}' was not accepted: {diagnostics}.");
    }

    static void ReportPhase(string phase) => Console.Error.WriteLine($"control-phase={phase}");
}
