using System.Text.Json;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Tests.ExecutionKernel;
using Cohesive.Tests.Storage.Control;
using ExecutionProcessStartResult = Cohesive.Execution.ProcessStartResult;

namespace Cohesive.Tests.Api;

public sealed class InMemoryExecutionControlApiAdapterTests
{
    static readonly DateTimeOffset BaselineUtc =
        new(2026, 7, 29, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Start_ReplaysAndReportsPreciseIdentityIdempotencyAndInstanceConflicts()
    {
        var fixture = ProcessControlTestFixture.Create();
        var catalog = ExecutionControlApiCatalog.Create();
        var adapter = new InMemoryExecutionControlApiAdapter(fixture.Catalog, catalog);
        var initial = fixture.State();
        var original = StartRequest(initial, input: "original-input");

        var accepted = Dispatch<ExecutionProcessStartResult>(
            adapter,
            catalog.Start,
            original,
            Invocation(catalog, BaselineUtc.AddSeconds(1), BaselineUtc.AddSeconds(2)));
        var replayed = Dispatch<ExecutionProcessStartResult>(
            adapter,
            catalog.Start,
            original,
            Invocation(catalog, BaselineUtc.AddSeconds(3), BaselineUtc.AddSeconds(4)));
        var identityConflict = Dispatch<ExecutionProcessStartResult>(
            adapter,
            catalog.Start,
            StartRequest(
                initial,
                commandId: original.Context.CommandId.Value,
                idempotencyKey: original.Context.IdempotencyKey.Value,
                input: "different-by-command"),
            Invocation(catalog, BaselineUtc.AddSeconds(5), BaselineUtc.AddSeconds(6)),
            ApiResultKind.Conflict);
        var idempotencyConflict = Dispatch<ExecutionProcessStartResult>(
            adapter,
            catalog.Start,
            StartRequest(
                initial,
                commandId: "start/other-command",
                idempotencyKey: original.Context.IdempotencyKey.Value,
                input: "different-by-idempotency"),
            Invocation(catalog, BaselineUtc.AddSeconds(7), BaselineUtc.AddSeconds(8)),
            ApiResultKind.Conflict);
        var instanceConflict = Dispatch<ExecutionProcessStartResult>(
            adapter,
            catalog.Start,
            StartRequest(
                initial,
                commandId: "start/new-command",
                idempotencyKey: "start/new-idempotency",
                input: "different-instance-start"),
            Invocation(catalog, BaselineUtc.AddSeconds(9), BaselineUtc.AddSeconds(10)),
            ApiResultKind.Conflict);

        Assert.Equal(ProcessStartDisposition.Accepted, accepted.Disposition);
        Assert.Equal(ProcessStartDisposition.Replayed, replayed.Disposition);
        Assert.Equal(accepted.Admission, replayed.Admission);
        Assert.Equal(ProcessStartDisposition.CommandIdentityConflict, identityConflict.Disposition);
        Assert.Equal(ProcessStartDisposition.IdempotencyConflict, idempotencyConflict.Disposition);
        Assert.Equal(ProcessStartDisposition.InstanceConflict, instanceConflict.Disposition);
    }

    [Fact]
    public void AuthorizationRunsBeforeLookupAndCanonicalCommandsUseTrustedServerEvidence()
    {
        var fixture = ProcessControlTestFixture.Create();
        var catalog = ExecutionControlApiCatalog.Create();
        var adapter = new InMemoryExecutionControlApiAdapter(fixture.Catalog, catalog);
        var initial = fixture.State();
        var forged = StartRequest(
            initial,
            authorization: ClientAuthorization("client-forged-authority", "client-forged-policy"),
            provenance: ClientProvenance("client-forged-provenance"));
        var trusted = Invocation(
            catalog,
            BaselineUtc.AddSeconds(1),
            BaselineUtc.AddSeconds(2),
            authorization: TrustedAuthorization(),
            provenance: TrustedProvenance());

        var accepted = Dispatch<ExecutionProcessStartResult>(adapter, catalog.Start, forged, trusted);
        var inspect = new InspectProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            ClientContext(
                initial.ProcessInstanceId,
                "inspect/trusted-rebind",
                ClientAuthorization("another-client-authority", "another-client-policy"),
                ClientProvenance("another-client-provenance")),
            expectation: null);
        var inspected = Dispatch<ExecutionControlResult>(
            adapter,
            catalog.Inspect,
            inspect,
            Invocation(catalog, BaselineUtc.AddSeconds(3), BaselineUtc.AddSeconds(4)));

        Assert.Equal(ProcessStartDisposition.Accepted, accepted.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.Inspected, inspected.Disposition);
        Assert.Equal(initial.ProcessInstanceId, inspected.Status.ProcessInstanceId);

        var unauthorized = Invocation(
            catalog,
            BaselineUtc.AddSeconds(5),
            BaselineUtc.AddSeconds(6),
            grantedRequirements: []);
        var existingDenied = adapter.Dispatch(catalog.Inspect, inspect, unauthorized);
        var absentDenied = adapter.Dispatch(
            catalog.Inspect,
            new InspectProcessCommand(
                ProcessControlCommand.CurrentSchemaVersion,
                ClientContext(new("process/not-present"), "inspect/not-present"),
                expectation: null),
            unauthorized);
        var existingProblem = Assert.IsType<ExecutionApiProblem>(existingDenied.Body);
        var absentProblem = Assert.IsType<ExecutionApiProblem>(absentDenied.Body);

        Assert.Equal(ApiResultKind.Forbidden, existingDenied.Result.Kind);
        Assert.Equal(ApiResultKind.Forbidden, absentDenied.Result.Kind);
        Assert.Equal(ExecutionApiProblemCodes.Forbidden, existingProblem.Code);
        Assert.Equal(existingProblem, absentProblem);
    }

    [Fact]
    public async Task ConcurrentLifecycleCommandsWithTheSameFence_LinearizeToOneSuccessAndOnePreciseStaleResult()
    {
        var fixture = ProcessControlTestFixture.Create();
        var catalog = ExecutionControlApiCatalog.Create();
        var adapter = new InMemoryExecutionControlApiAdapter(fixture.Catalog, catalog);
        var initial = fixture.State();
        _ = Dispatch<ExecutionProcessStartResult>(
            adapter,
            catalog.Start,
            StartRequest(initial),
            Invocation(catalog, BaselineUtc.AddSeconds(1), BaselineUtc.AddSeconds(2)));
        var first = fixture.Pause(initial, id: "pause/concurrent-a");
        var second = fixture.Pause(initial, id: "pause/concurrent-b");
        var invocation = Invocation(catalog, BaselineUtc.AddSeconds(3), BaselineUtc.AddSeconds(4));

        var results = await Task.WhenAll(
            Task.Run(() => adapter.Dispatch(catalog.Pause, first, invocation)),
            Task.Run(() => adapter.Dispatch(catalog.Pause, second, invocation)));
        var bodies = results.Select(static result => Assert.IsType<ExecutionControlResult>(result.Body)).ToArray();

        Assert.Contains(bodies, static result => result.Disposition == ProcessControlDecisionDisposition.Applied);
        Assert.Contains(bodies, static result => result.Disposition == ProcessControlDecisionDisposition.StaleRevision);
        Assert.Contains(results, static result => result.Result.Kind == ApiResultKind.Success);
        Assert.Contains(results, static result => result.Result.Kind == ApiResultKind.PreconditionFailed);
        Assert.All(bodies, static result => Assert.Equal(new ProcessControlRevision("2"), result.Status.ControlRevision));
    }

    [Fact]
    public void ContinueRetainsCurrentAttemptWhileRestartCreatesAReplacementAttempt()
    {
        var fixture = ProcessControlTestFixture.Create();
        var catalog = ExecutionControlApiCatalog.Create();
        var adapter = new InMemoryExecutionControlApiAdapter(fixture.Catalog, catalog);
        var initial = fixture.State();
        _ = Dispatch<ExecutionProcessStartResult>(
            adapter,
            catalog.Start,
            StartRequest(initial),
            Invocation(catalog, BaselineUtc.AddSeconds(1), BaselineUtc.AddSeconds(2)));
        var paused = Dispatch<ExecutionControlResult>(
            adapter,
            catalog.Pause,
            fixture.Pause(initial),
            Invocation(catalog, BaselineUtc.AddSeconds(3), BaselineUtc.AddSeconds(4)));
        var continued = Dispatch<ExecutionControlResult>(
            adapter,
            catalog.Continue,
            new ContinueProcessCommand(
                ProcessControlCommand.CurrentSchemaVersion,
                ClientContext(initial.ProcessInstanceId, "continue/api"),
                Expectation(initial, paused.Status.ControlRevision)),
            Invocation(catalog, BaselineUtc.AddSeconds(5), BaselineUtc.AddSeconds(6)));
        ProcessAttemptId replacement = new("process-attempt/replacement");
        var restarted = Dispatch<ExecutionControlResult>(
            adapter,
            catalog.RestartAttempt,
            new RestartProcessAttemptCommand(
                ProcessControlCommand.CurrentSchemaVersion,
                ClientContext(initial.ProcessInstanceId, "restart/api"),
                Expectation(initial, continued.Status.ControlRevision),
                new(
                    replacement,
                    ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources,
                    new("operator.api-restart"))),
            Invocation(catalog, BaselineUtc.AddSeconds(7), BaselineUtc.AddSeconds(8)));

        Assert.Equal(ProcessControlMode.Running, continued.Status.ControlMode);
        Assert.Equal(initial.CurrentAttempt.AttemptId, continued.Status.CurrentAttemptId);
        Assert.Single(continued.Status.Attempts);
        Assert.Equal(replacement, restarted.Status.CurrentAttemptId);
        Assert.Equal(2, restarted.Status.Attempts.Length);
        Assert.Equal(ExecutionAttemptDisposition.Abandoned, restarted.Status.Attempts[0].Disposition);
        Assert.Equal(ExecutionAttemptDisposition.Current, restarted.Status.Attempts[1].Disposition);
    }

    [Fact]
    public void SafeLifecycleResponsesNeverSerializeSignalReasonAuthorizationOrProvenanceEvidence()
    {
        const string signalSecret = "signal-payload-very-secret";
        const string reasonSecret = "cancel-reason-very-secret";
        var fixture = ProcessControlTestFixture.Create();
        var catalog = ExecutionControlApiCatalog.Create();
        var signalAdapter = new InMemoryExecutionControlApiAdapter(fixture.Catalog, catalog);
        var initial = fixture.State();
        _ = Dispatch<ExecutionProcessStartResult>(
            signalAdapter,
            catalog.Start,
            StartRequest(initial),
            Invocation(catalog, BaselineUtc.AddSeconds(1), BaselineUtc.AddSeconds(2)));

        var signalTemplate = fixture.SignalCommand(initial, payload: signalSecret);
        var signal = signalTemplate.Signal;
        var trustedSignalProvenance = TrustedProvenance("trusted-runtime-provenance-secret");
        var forgedSignal = new SignalEnvelope(
            signal.SchemaVersion,
            new(
                signal.Context.EmissionId,
                new ProcessInteractionOrigin(
                    ProcessControlTestFixture.DefinitionReference("process/forged-origin", 'f'),
                    new("node/forged-origin"),
                    new(initial.ProcessInstanceId, initial.CurrentAttempt.AttemptId),
                    new("activation/forged-origin"),
                    new("token/forged-origin")),
                signal.Context.CorrelationId,
                signal.Context.CausationId,
                new("client-signal-authority-secret", "client-signal-tenant-secret"),
                signal.Context.IdempotencyKey,
                signal.Context.Ordering,
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AtomicWithOrigin),
                ClientProvenance("client-signal-provenance-secret")),
            signal.Contract,
            signal.Payload,
            signal.Target);
        var signalResult = Dispatch<ExecutionControlResult>(
            signalAdapter,
            catalog.Signal,
            new SignalProcessCommand(
                signalTemplate.SchemaVersion,
                ClientContext(
                    initial.ProcessInstanceId,
                    "signal/api-secret",
                    ClientAuthorization("client-command-authority-secret", "client-command-policy-secret"),
                    ClientProvenance("client-command-provenance-secret")),
                signalTemplate.Expectation!,
                forgedSignal),
            Invocation(
                catalog,
                BaselineUtc.AddSeconds(3),
                BaselineUtc.AddSeconds(4),
                provenance: trustedSignalProvenance,
                signalContext: TrustedSignalContext(
                    signal.Context,
                    TrustedAuthorization(),
                    trustedSignalProvenance)));
        var replayedSignal = Dispatch<ExecutionControlResult>(
            signalAdapter,
            catalog.Signal,
            new SignalProcessCommand(
                signalTemplate.SchemaVersion,
                ClientContext(
                    initial.ProcessInstanceId,
                    "signal/api-idempotent-retry",
                    idempotencyKey: "idempotency/signal/api-secret"),
                signalTemplate.Expectation!,
                new SignalEnvelope(
                    forgedSignal.SchemaVersion,
                    new(
                        new("emission/client-retry-forgery"),
                        forgedSignal.Context.Origin,
                        new("correlation/client-retry-forgery"),
                        causationId: null,
                        forgedSignal.Context.AuthorityScope,
                        new("idempotency/client-retry-forgery"),
                        ordering: null,
                        forgedSignal.Context.Delivery,
                        forgedSignal.Context.Provenance),
                    forgedSignal.Contract,
                    forgedSignal.Payload,
                    forgedSignal.Target)),
            Invocation(catalog, BaselineUtc.AddSeconds(5), BaselineUtc.AddSeconds(6)));

        var cancelAdapter = new InMemoryExecutionControlApiAdapter(fixture.Catalog, catalog);
        _ = Dispatch<ExecutionProcessStartResult>(
            cancelAdapter,
            catalog.Start,
            StartRequest(initial, commandId: "start/cancel", idempotencyKey: "start/cancel"),
            Invocation(catalog, BaselineUtc.AddSeconds(1), BaselineUtc.AddSeconds(2)));
        var cancelResult = Dispatch<ExecutionControlResult>(
            cancelAdapter,
            catalog.Cancel,
            new CancelProcessCommand(
                ProcessControlCommand.CurrentSchemaVersion,
                ClientContext(initial.ProcessInstanceId, "cancel/api-secret"),
                Expectation(initial, ProcessControlRevision.Initial),
                new(reasonSecret)),
            Invocation(catalog, BaselineUtc.AddSeconds(3), BaselineUtc.AddSeconds(4)));
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var json = JsonSerializer.Serialize(
            new object[] { signalResult, cancelResult },
            options);

        Assert.Equal(ProcessControlDecisionDisposition.SignalAccepted, signalResult.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.Replayed, replayedSignal.Disposition);
        Assert.Equal(signalResult.Receipt?.CommandId, replayedSignal.Receipt?.CommandId);
        Assert.Equal(signalResult.Status.ControlRevision, replayedSignal.Status.ControlRevision);
        Assert.Equal(ProcessControlMode.Cancelled, cancelResult.Status.ControlMode);
        Assert.DoesNotContain(signalSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain(reasonSecret, json, StringComparison.Ordinal);
        Assert.DoesNotContain("client-signal-authority-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("client-command-policy-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("client-signal-provenance-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("trusted-runtime-provenance-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateLimitsRemainsPendingUntilSafePointAndPreservesStaleAndOutOfBoundsSemantics()
    {
        var fixture = ProcessControlTestFixture.Create();
        var catalog = ExecutionControlApiCatalog.Create();
        var adapter = new InMemoryExecutionControlApiAdapter(fixture.Catalog, catalog);
        var definition = ControlDefinition();
        ControlEpochId epoch = new("generation/1");
        var authority = TrustedAuthorization("cohesive/control", "tenant-a");
        var initialRevision = adapter.RegisterControlLoop(
            definition,
            epoch,
            authority.AuthorityScope,
            BaselineUtc);
        var accepted = Dispatch<ControlLimitUpdateResult>(
            adapter,
            catalog.UpdateLimits,
            LimitUpdate(
                definition,
                epoch,
                initialRevision,
                concurrency: 6,
                commandId: "limits/accepted",
                idempotencyKey: "limits/accepted",
                authorization: ClientAuthorization("client-forged-control", "client-forged-control-policy")),
            Invocation(
                catalog,
                BaselineUtc.AddSeconds(1),
                BaselineUtc.AddSeconds(2),
                authorization: authority));

        var pending = Dispatch<ControlLimitUpdateResult>(
            adapter,
            catalog.UpdateLimits,
            LimitUpdate(
                definition,
                epoch,
                accepted.Revision,
                concurrency: 7,
                commandId: "limits/pending",
                idempotencyKey: "limits/pending"),
            Invocation(
                catalog,
                BaselineUtc.AddMilliseconds(2100),
                BaselineUtc.AddMilliseconds(2200),
                authorization: authority),
            ApiResultKind.Conflict);

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Accepted, accepted.Disposition);
        Assert.Equal(new ControlRevision("2"), accepted.Revision);
        Assert.Equal(ControlLimitUpdateResultDisclosure.Redacted, accepted.Disclosure);
        Assert.Null(accepted.RequestedOperatingPoint);
        Assert.Null(accepted.EffectiveOperatingPoint);
        Assert.Equal(ControlLimitUpdateDecisionDisposition.PendingConflict, pending.Disposition);

        var safePoint = new ControlApplicationPoint(
            ControlLoopDefinition.CurrentSchemaVersion,
            new("safe-point/apply-limits"),
            definition.Id,
            definition.Fingerprint,
            definition.Target,
            epoch,
            accepted.Revision,
            new("1"),
            ControlApplicationPointKind.WorkAdmissionBoundary,
            BaselineUtc.AddSeconds(3),
            definition.ApplicationAuthority,
            "process:safe-point/apply-limits");
        var applied = adapter.ApplyLimitsAtSafePoint(
            authority.AuthorityScope,
            safePoint,
            BaselineUtc.AddSeconds(3));

        Assert.Equal(ControlActuationDisposition.Applied, applied);

        var stale = Dispatch<ControlLimitUpdateResult>(
            adapter,
            catalog.UpdateLimits,
            LimitUpdate(
                definition,
                epoch,
                accepted.Revision,
                concurrency: 7,
                commandId: "limits/stale",
                idempotencyKey: "limits/stale"),
            Invocation(
                catalog,
                BaselineUtc.AddSeconds(4),
                BaselineUtc.AddSeconds(5),
                authorization: authority),
            ApiResultKind.PreconditionFailed);
        var next = Dispatch<ControlLimitUpdateResult>(
            adapter,
            catalog.UpdateLimits,
            LimitUpdate(
                definition,
                epoch,
                new("3"),
                concurrency: 7,
                commandId: "limits/next",
                idempotencyKey: "limits/next"),
            Invocation(
                catalog,
                BaselineUtc.AddSeconds(6),
                BaselineUtc.AddSeconds(7),
                authorization: authority));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Stale, stale.Disposition);
        Assert.Equal(new ControlRevision("3"), stale.Revision);
        Assert.Equal(ControlLimitUpdateDecisionDisposition.Accepted, next.Disposition);
        Assert.Equal(new ControlRevision("4"), next.Revision);

        var boundedAdapter = new InMemoryExecutionControlApiAdapter(fixture.Catalog, catalog);
        ControlEpochId boundedEpoch = new("generation/out-of-bounds");
        var boundedRevision = boundedAdapter.RegisterControlLoop(
            definition,
            boundedEpoch,
            authority.AuthorityScope,
            BaselineUtc);
        var outOfBounds = Dispatch<ControlLimitUpdateResult>(
            boundedAdapter,
            catalog.UpdateLimits,
            LimitUpdate(
                definition,
                boundedEpoch,
                boundedRevision,
                concurrency: 9,
                commandId: "limits/out-of-bounds",
                idempotencyKey: "limits/out-of-bounds"),
            Invocation(
                catalog,
                BaselineUtc.AddSeconds(1),
                BaselineUtc.AddSeconds(2),
                authorization: authority),
            ApiResultKind.ValidationFailed);

        Assert.Equal(ControlLimitUpdateDecisionDisposition.OutOfBounds, outOfBounds.Disposition);
        Assert.Equal(ControlRevision.Initial, outOfBounds.Revision);
        Assert.Contains(ControlDiagnosticCodes.WorkloadBudgetExceeded, outOfBounds.DiagnosticCodes);
    }

    static T Dispatch<T>(
        InMemoryExecutionControlApiAdapter adapter,
        ApiEndpoint endpoint,
        object request,
        ExecutionApiInvocationContext invocation,
        ApiResultKind expectedKind = ApiResultKind.Success)
    {
        var dispatched = adapter.Dispatch(endpoint, request, invocation);
        Assert.Equal(expectedKind, dispatched.Result.Kind);
        return Assert.IsType<T>(dispatched.Body);
    }

    static ProcessStartRequest StartRequest(
        ProcessControlState initial,
        string commandId = "start/1",
        string idempotencyKey = "start/logical-1",
        string input = "start-input",
        ProcessControlAuthorizationContext? authorization = null,
        ExecutionProvenance? provenance = null) =>
        new(
            ProcessStartRequest.CurrentSchemaVersion,
            initial.Definition,
            ClientContext(
                initial.ProcessInstanceId,
                commandId,
                authorization,
                provenance,
                idempotencyKey),
            new(initial.ProcessInstanceId, initial.CurrentAttempt.AttemptId),
            ProcessControlTestFixture.StringValue(input));

    static ProcessControlCommandContext ClientContext(
        ProcessInstanceId instance,
        string commandId,
        ProcessControlAuthorizationContext? authorization = null,
        ExecutionProvenance? provenance = null,
        string? idempotencyKey = null) =>
        new(
            new(commandId),
            new(idempotencyKey ?? $"idempotency/{commandId}"),
            instance,
            authorization ?? ClientAuthorization(),
            BaselineUtc,
            provenance ?? ClientProvenance());

    static ProcessControlExpectation Expectation(
        ProcessControlState initial,
        ProcessControlRevision revision) =>
        new(
            new(initial.ProcessInstanceId, initial.CurrentAttempt.AttemptId),
            revision);

    static ExecutionApiInvocationContext Invocation(
        ExecutionControlApiCatalog catalog,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset observedAtUtc,
        ProcessControlAuthorizationContext? authorization = null,
        ExecutionProvenance? provenance = null,
        IReadOnlyList<string>? grantedRequirements = null,
        InteractionEnvelopeContext? signalContext = null) =>
        new(
            authorization ?? TrustedAuthorization(),
            provenance ?? TrustedProvenance(),
            issuedAtUtc,
            observedAtUtc,
            grantedRequirements ??
            [.. catalog.Definition.Operations
                .SelectMany(static operation => operation.AuthorizationRequirements)
                .Select(static requirement => requirement.Id)],
            signalContext);

    static InteractionEnvelopeContext TrustedSignalContext(
        InteractionEnvelopeContext template,
        ProcessControlAuthorizationContext authorization,
        ExecutionProvenance provenance) =>
        new(
            template.EmissionId,
            template.Origin,
            template.CorrelationId,
            template.CausationId,
            authorization.AuthorityScope,
            template.IdempotencyKey,
            template.Ordering,
            new(
                InteractionDurabilityDemand.Durable,
                InteractionVisibilityDemand.AfterOriginCommit),
            provenance);

    static ProcessControlAuthorizationContext TrustedAuthorization(
        string authority = "authority/motion",
        string tenant = "tenant/acme") =>
        new("operator/trusted", new(authority, tenant), "trusted-policy-secret");

    static ProcessControlAuthorizationContext ClientAuthorization(
        string authority = "client/authority",
        string policy = "client-policy-secret") =>
        new("client/untrusted", new(authority, "client/tenant"), policy);

    static ExecutionProvenance TrustedProvenance(string source = "trusted-source-secret") =>
        new(new("execution-api-adapter", "1"), new(source), DocumentOrigin.Generated);

    static ExecutionProvenance ClientProvenance(string source = "client-provenance-secret") =>
        new(new("untrusted-client", "1"), new(source), DocumentOrigin.Generated);

    static ControlLoopDefinition ControlDefinition() =>
        ControlTestFixture.Definition(
            ControlTestFixture.Limits(
                ControlTestFixture.Limit(
                    ControlActuatorKind.Concurrency,
                    minimum: 1,
                    maximum: 10,
                    ControlHardLimitOrigin.Adapter,
                    "adapter/concurrency"),
                ControlTestFixture.Limit(
                    ControlActuatorKind.BatchItems,
                    minimum: 1,
                    maximum: 100,
                    ControlHardLimitOrigin.Semantic,
                    "process/batch")),
            ControlTestFixture.Point(
                (ControlActuatorKind.Concurrency, 4),
                (ControlActuatorKind.BatchItems, 20)),
            [ControlTestFixture.Budget(
                ControlActuatorKind.Concurrency,
                capacity: 10,
                reserved: 2)]);

    static ControlLimitUpdateCommand LimitUpdate(
        ControlLoopDefinition definition,
        ControlEpochId epoch,
        ControlRevision expectedRevision,
        long concurrency,
        string commandId,
        string idempotencyKey,
        ProcessControlAuthorizationContext? authorization = null) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new(commandId),
            new(idempotencyKey),
            definition.Id,
            definition.Fingerprint,
            definition.Target,
            epoch,
            expectedRevision,
            ControlTestFixture.Point(
                (ControlActuatorKind.Concurrency, concurrency),
                (ControlActuatorKind.BatchItems, 20)),
            authorization ?? ClientAuthorization(),
            BaselineUtc,
            ClientProvenance());
}
