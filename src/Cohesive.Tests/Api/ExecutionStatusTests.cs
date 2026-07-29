using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Tests.ExecutionKernel;

namespace Cohesive.Tests.Api;

public sealed class ExecutionStatusTests
{
    [Fact]
    public void Project_DefaultsRuntimeFacetsToUnknownAndStructurallyOmitsSensitiveState()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.Executor.Apply(
            fixture.State(),
            fixture.SignalCommand(fixture.State(), payload: "sensitive-signal-payload"),
            ProcessControlTestFixture.CreatedAtUtc.AddMinutes(1)).State;

        var status = ExecutionStatusProjector.Project(state);
        var json = JsonSerializer.Serialize(status, InteractionEnvelopeJsonSerializer.CreateOptions());

        Assert.Equal(state.Definition, status.Definition);
        Assert.Equal(state.ProcessInstanceId, status.ProcessInstanceId);
        Assert.Equal(state.Revision, status.ControlRevision);
        Assert.Equal(state.CurrentAttempt.AttemptId, status.CurrentAttempt.AttemptId);
        Assert.Equal(ExecutionStatusDisclosure.Unknown, status.Runtime.TokensDisclosure);
        Assert.Equal(ExecutionStatusDisclosure.Unknown, status.Runtime.WaitsDisclosure);
        Assert.Equal(ExecutionStatusDisclosure.Unknown, status.Runtime.ProgressDisclosure);
        Assert.DoesNotContain("sensitive-signal-payload", json, StringComparison.Ordinal);
        Assert.DoesNotContain("receipts", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("signals", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("affinity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reason", json, StringComparison.OrdinalIgnoreCase);

        var restored = JsonSerializer.Deserialize<ExecutionStatus>(
            json,
            InteractionEnvelopeJsonSerializer.CreateOptions());
        Assert.NotNull(restored);
        Assert.Equal(status.Definition, restored.Definition);
        Assert.Equal(status.CurrentAttemptId, restored.CurrentAttemptId);
        Assert.Equal(status.Runtime.TokensDisclosure, restored.Runtime.TokensDisclosure);
    }

    [Fact]
    public void Project_SummarizesActiveActivationWithoutItsRetainedControlEvidence()
    {
        var fixture = ProcessControlTestFixture.Create();
        var active = fixture.BeginActivation(fixture.State()).State;

        var status = ExecutionStatusProjector.Project(active);

        var activation = Assert.IsType<ExecutionActivationStatus>(status.ActiveActivation);
        Assert.Equal(active.CurrentAttempt.ActiveActivationId, activation.ActivationId);
        Assert.Equal(active.CurrentAttempt.AttemptId, activation.AttemptId);
        Assert.Equal(ProcessControlExecutionPhase.InActivation, status.CurrentAttempt.Phase);
    }

    [Fact]
    public void Status_RejectsActivationThatDidNotStartBeforeCurrentRevision()
    {
        var fixture = ProcessControlTestFixture.Create();
        var activeState = fixture.BeginActivation(fixture.State()).State;
        var current = ExecutionStatusProjector.Project(activeState);
        var active = Assert.IsType<ExecutionActivationStatus>(current.ActiveActivation);
        var invalidActive = new ExecutionActivationStatus(
            active.ActivationId,
            active.AttemptId,
            current.ControlRevision,
            active.StartedAtUtc);

        Assert.Throws<ArgumentException>(() => new ExecutionStatus(
            current.SchemaVersion,
            current.Definition,
            current.ProcessInstanceId,
            current.ControlRevision,
            current.ControlMode,
            current.Attempts,
            invalidActive,
            current.Runtime,
            current.TerminalOutcome,
            current.CreatedAtUtc,
            current.UpdatedAtUtc));
    }

    [Fact]
    public void Project_RedactsTerminalReasonRatherThanClaimingItIsUnknown()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var cancelled = fixture.Executor.Apply(
            initial,
            fixture.Cancel(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;

        var status = ExecutionStatusProjector.Project(cancelled);
        var json = JsonSerializer.Serialize(status, InteractionEnvelopeJsonSerializer.CreateOptions());

        Assert.Equal(ExecutionTerminalOutcomeKind.Cancelled, status.TerminalOutcome.Kind);
        Assert.Equal(ExecutionStatusDisclosure.Redacted, status.TerminalOutcome.Detail?.Disclosure);
        Assert.Null(status.TerminalOutcome.Detail?.Value);
        Assert.DoesNotContain("operator.cancel", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ExecutionTerminalOutcomeKind.Completed, ExecutionAttemptDisposition.Completed)]
    [InlineData(ExecutionTerminalOutcomeKind.Failed, ExecutionAttemptDisposition.Failed)]
    public void Project_ClosesFinalAttemptForExecutionOwnedTerminalOutcome(
        ExecutionTerminalOutcomeKind outcomeKind,
        ExecutionAttemptDisposition expectedDisposition)
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var occurredAtUtc = state.UpdatedAtUtc.AddSeconds(1);

        var status = ExecutionStatusProjector.Project(
            state,
            terminalOutcome: new(outcomeKind, occurredAtUtc));

        Assert.Equal(outcomeKind, status.TerminalOutcome.Kind);
        Assert.Equal(expectedDisposition, status.CurrentAttempt.Disposition);
        Assert.Equal(ProcessControlExecutionPhase.Stopped, status.CurrentAttempt.Phase);
        Assert.Equal(occurredAtUtc, status.CurrentAttempt.EndedAtUtc);
        Assert.Equal(occurredAtUtc, status.UpdatedAtUtc);
    }

    [Fact]
    public void Status_RejectsTerminalOutcomeWithCurrentAttemptDisposition()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var current = ExecutionStatusProjector.Project(state);

        Assert.Throws<ArgumentException>(() => new ExecutionStatus(
            current.SchemaVersion,
            current.Definition,
            current.ProcessInstanceId,
            current.ControlRevision,
            current.ControlMode,
            current.Attempts,
            current.ActiveActivation,
            current.Runtime,
            new(ExecutionTerminalOutcomeKind.Completed, current.UpdatedAtUtc),
            current.CreatedAtUtc,
            current.UpdatedAtUtc));
    }

    [Fact]
    public void Status_RejectsOverlappingAttemptLineage()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var restarted = fixture.Executor.Apply(
            initial,
            fixture.Restart(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var current = ExecutionStatusProjector.Project(restarted);
        var priorAttempt = current.Attempts[0];
        var finalAttempt = current.Attempts[1];
        var overlappingFinal = new ExecutionAttemptStatus(
            finalAttempt.AttemptId,
            priorAttempt.EndedAtUtc!.Value.AddTicks(-1),
            finalAttempt.EndedAtUtc,
            finalAttempt.Disposition,
            finalAttempt.Phase,
            finalAttempt.CompletedActivationCount,
            finalAttempt.LastSafePointId,
            finalAttempt.LastSafePointNode);

        Assert.Throws<ArgumentException>(() => new ExecutionStatus(
            current.SchemaVersion,
            current.Definition,
            current.ProcessInstanceId,
            current.ControlRevision,
            current.ControlMode,
            [priorAttempt, overlappingFinal],
            current.ActiveActivation,
            current.Runtime,
            current.TerminalOutcome,
            current.CreatedAtUtc,
            current.UpdatedAtUtc));
    }

    [Fact]
    public void Status_RejectsFutureOrTokenInconsistentDisclosedWaits()
    {
        var fixture = ProcessControlTestFixture.Create();
        var current = ExecutionStatusProjector.Project(fixture.State());
        TokenId tokenId = new("token/waiting");
        ExecutionNodeId node = new("node/waiting");
        var futureWait = new ExecutionWaitStatus(tokenId, node, current.UpdatedAtUtc.AddTicks(1));
        var futureRuntime = new ExecutionRuntimeStatusDetails(
            waitsDisclosure: ExecutionStatusDisclosure.Disclosed,
            waits: [futureWait]);

        Assert.Throws<ArgumentException>(() => WithRuntime(current, futureRuntime));

        var currentWait = new ExecutionWaitStatus(tokenId, node, current.UpdatedAtUtc);
        var inconsistentRuntime = new ExecutionRuntimeStatusDetails(
            tokensDisclosure: ExecutionStatusDisclosure.Disclosed,
            tokens: [new(tokenId, node, ExecutionTokenDisposition.Ready)],
            waitsDisclosure: ExecutionStatusDisclosure.Disclosed,
            waits: [currentWait]);
        Assert.Throws<ArgumentException>(() => WithRuntime(current, inconsistentRuntime));

        var missingWaitRuntime = new ExecutionRuntimeStatusDetails(
            tokensDisclosure: ExecutionStatusDisclosure.Disclosed,
            tokens: [new(tokenId, node, ExecutionTokenDisposition.Waiting)],
            waitsDisclosure: ExecutionStatusDisclosure.Disclosed);
        Assert.Throws<ArgumentException>(() => WithRuntime(current, missingWaitRuntime));

        var consistentRuntime = new ExecutionRuntimeStatusDetails(
            tokensDisclosure: ExecutionStatusDisclosure.Disclosed,
            tokens: [new(tokenId, node, ExecutionTokenDisposition.Waiting)],
            waitsDisclosure: ExecutionStatusDisclosure.Disclosed,
            waits: [currentWait]);
        Assert.Equal(consistentRuntime, WithRuntime(current, consistentRuntime).Runtime);
    }

    [Fact]
    public void RuntimeDetails_NormalizeIdentitiesAndKeepUnknownDistinctFromRedacted()
    {
        ExecutionRuntimeStatusExtension indexSync = new(
            new("cohesive.storage.index-sync.status"),
            new("index-sync-status/v1"),
            ExecutionStatusValue.Redacted(ProcessControlTestFixture.StringContract),
            Provenance("index-sync/runtime"));
        ExecutionRuntimeStatusExtension common = new(
            new("cohesive.execution.scheduler.status"),
            new("scheduler-status/v1"),
            ExecutionStatusValue.Unknown(ProcessControlTestFixture.StringContract),
            Provenance("scheduler/runtime"));
        ExecutionTokenStatus later = new(
            new("token/z"),
            new("node/z"),
            ExecutionTokenDisposition.Waiting);
        ExecutionTokenStatus earlier = new(
            new("token/a"),
            new("node/a"),
            ExecutionTokenDisposition.Ready);

        var runtime = new ExecutionRuntimeStatusDetails(
            tokensDisclosure: ExecutionStatusDisclosure.Disclosed,
            tokens: [later, earlier],
            health: ExecutionHealthStatus.Degraded,
            extensions: [indexSync, common]);

        Assert.True(runtime.Tokens.SequenceEqual([earlier, later]));
        Assert.Equal(common, runtime.Extensions[0]);
        Assert.Equal(indexSync, runtime.Extensions[1]);
        Assert.Equal(ExecutionStatusDisclosure.Unknown, common.Value.Disclosure);
        Assert.Equal(PortableValueState.Unknown, common.Value.Value?.State);
        Assert.Equal(ExecutionStatusDisclosure.Redacted, indexSync.Value.Disclosure);
        Assert.Null(indexSync.Value.Value);
    }

    [Fact]
    public void RuntimeDetails_RejectDuplicateExtensionAndHiddenCollectionContent()
    {
        ExecutionRuntimeStatusExtension extension = new(
            new("cohesive.storage.index-sync.status"),
            new("index-sync-status/v1"),
            ExecutionStatusValue.Disclose(ProcessControlTestFixture.StringValue("generation/42")),
            Provenance("index-sync/runtime"));

        Assert.Throws<ArgumentException>(() => new ExecutionRuntimeStatusDetails(
            extensions: [extension, extension]));
        Assert.Throws<ArgumentException>(() => new ExecutionRuntimeStatusDetails(
            tokensDisclosure: ExecutionStatusDisclosure.Redacted,
            tokens:
            [
                new(
                    new("token/secret"),
                    new("node/secret"),
                    ExecutionTokenDisposition.Waiting)
            ]));
    }

    [Fact]
    public void PortableCounters_RoundTripMaximumInt64AsJsonStrings()
    {
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var attempt = new ExecutionAttemptStatus(
            new("attempt/max-counter"),
            ProcessControlTestFixture.CreatedAtUtc,
            endedAtUtc: null,
            ExecutionAttemptDisposition.Current,
            ProcessControlExecutionPhase.Ready,
            long.MaxValue,
            new("safe-point/max-counter"),
            new("node/max-counter"));
        var runtime = new ExecutionRuntimeStatusDetails(
            progressDisclosure: ExecutionStatusDisclosure.Disclosed,
            progress: new(long.MaxValue, long.MaxValue, "facts"),
            demandDisclosure: ExecutionStatusDisclosure.Disclosed,
            demand: new(long.MaxValue, long.MaxValue),
            capacityDisclosure: ExecutionStatusDisclosure.Disclosed,
            capacity: new(long.MaxValue, long.MaxValue));

        var attemptJson = JsonSerializer.Serialize(attempt, options);
        var runtimeJson = JsonSerializer.Serialize(runtime, options);
        using var attemptDocument = JsonDocument.Parse(attemptJson);
        using var runtimeDocument = JsonDocument.Parse(runtimeJson);

        Assert.Equal(long.MaxValue.ToString(), attemptDocument.RootElement.GetProperty("completedActivationCount").GetString());
        Assert.Equal(long.MaxValue.ToString(), runtimeDocument.RootElement.GetProperty("progress").GetProperty("completed").GetString());
        Assert.Equal(long.MaxValue.ToString(), runtimeDocument.RootElement.GetProperty("progress").GetProperty("total").GetString());
        Assert.Equal(long.MaxValue.ToString(), runtimeDocument.RootElement.GetProperty("demand").GetProperty("ready").GetString());
        Assert.Equal(long.MaxValue.ToString(), runtimeDocument.RootElement.GetProperty("demand").GetProperty("delayed").GetString());
        Assert.Equal(long.MaxValue.ToString(), runtimeDocument.RootElement.GetProperty("capacity").GetProperty("active").GetString());
        Assert.Equal(long.MaxValue.ToString(), runtimeDocument.RootElement.GetProperty("capacity").GetProperty("limit").GetString());
        Assert.Equal(attempt, JsonSerializer.Deserialize<ExecutionAttemptStatus>(attemptJson, options));
        Assert.Equal(runtime, JsonSerializer.Deserialize<ExecutionRuntimeStatusDetails>(runtimeJson, options));
    }

    static ExecutionProvenance Provenance(string source) =>
        new(
            new("execution-status-tests", "1"),
            new(source),
            DocumentOrigin.Generated);

    static ExecutionStatus WithRuntime(ExecutionStatus status, ExecutionRuntimeStatusDetails runtime) =>
        new(
            status.SchemaVersion,
            status.Definition,
            status.ProcessInstanceId,
            status.ControlRevision,
            status.ControlMode,
            status.Attempts,
            status.ActiveActivation,
            runtime,
            status.TerminalOutcome,
            status.CreatedAtUtc,
            status.UpdatedAtUtc);
}
