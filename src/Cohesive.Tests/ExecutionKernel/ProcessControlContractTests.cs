using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessControlContractTests
{
    [Fact]
    public void ClosedCommandFamily_RoundTripsWithStableProtocolDiscriminators()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        ProcessControlCommand[] commands =
        [
            fixture.Inspect(state, id: "inspect/wire"),
            fixture.SignalCommand(state, id: "signal/wire"),
            fixture.Pause(state, id: "pause/wire"),
            fixture.Continue(state, id: "continue/wire"),
            fixture.Restart(state, id: "restart/wire"),
            fixture.Cancel(state, id: "cancel/wire"),
            fixture.Terminate(state, id: "terminate/wire")
        ];
        string[] discriminators =
        [
            ExecutionControlWireNames.Inspect,
            ExecutionControlWireNames.Signal,
            ExecutionControlWireNames.Pause,
            ExecutionControlWireNames.Continue,
            ExecutionControlWireNames.RestartAttempt,
            ExecutionControlWireNames.Cancel,
            ExecutionControlWireNames.Terminate
        ];
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();

        for (var index = 0; index < commands.Length; index++)
        {
            var json = JsonSerializer.Serialize<ProcessControlCommand>(commands[index], options);
            var restored = JsonSerializer.Deserialize<ProcessControlCommand>(json, options);

            Assert.Contains(
                $"\"{ExecutionControlWireNames.CommandDiscriminator}\":\"{discriminators[index]}\"",
                json,
                StringComparison.Ordinal);
            Assert.NotNull(restored);
            Assert.Equal(commands[index].GetType(), restored.GetType());
            Assert.Equal(commands[index], restored);
        }
    }

    [Fact]
    public void PersistedState_RoundTripsWithLineageReceiptsSignalsAndAffinities()
    {
        var fixture = ProcessControlTestFixture.Create();
        var bound = fixture.BindAffinity(fixture.State()).State;
        var signalled = fixture.Executor.Apply(
            bound,
            fixture.SignalCommand(bound),
            bound.UpdatedAtUtc.AddMinutes(1)).State;
        var restarted = fixture.Executor.Apply(
            signalled,
            fixture.Restart(signalled),
            signalled.UpdatedAtUtc.AddMinutes(1)).State;
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(restarted, options);
        var restored = JsonSerializer.Deserialize<ProcessControlState>(json, options);
        var restoredJson = JsonSerializer.Serialize(restored, options);

        Assert.NotNull(restored);
        Assert.Equal(restarted, restored);
        Assert.Equal(restarted.GetHashCode(), restored.GetHashCode());
        Assert.Equal(json, restoredJson);
        Assert.Equal(2, restored.Attempts.Length);
        Assert.Equal(ProcessControlAttemptDisposition.Abandoned, restored.Attempts[0].Disposition);
        Assert.Empty(restored.CurrentAttempt.AffinityBindings);
        Assert.Single(restored.SignalAdmissions);
        Assert.Equal(2, restored.Receipts.Length);
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("cancel")]
    [InlineData("terminate")]
    public void AttemptClosureCauses_RoundTripThroughTheCanonicalWire(string action)
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        ProcessControlCommand command = action switch
        {
            "restart" => fixture.Restart(initial, id: "restart/closure-wire"),
            "cancel" => fixture.Cancel(initial, id: "cancel/closure-wire"),
            "terminate" => fixture.Terminate(initial, id: "terminate/closure-wire"),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown closure action.")
        };
        var applied = fixture.Executor.Apply(
            initial,
            command,
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();

        var restored = JsonSerializer.Deserialize<ProcessControlState>(
            JsonSerializer.Serialize(applied, options),
            options);

        Assert.Equal(applied, restored);
        Assert.Equal(command.Context.CommandId, restored?.Attempts[0].Closure?.CommandId);
        Assert.Equal(applied.Attempts[0].EndedAtUtc, restored?.Attempts[0].EndedAtUtc);
    }

    [Fact]
    public void StrictWireContract_RejectsUnknownMembersAndCommandDiscriminators()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var stateNode = Assert.IsType<JsonObject>(
            JsonNode.Parse(JsonSerializer.Serialize(state, options)));
        stateNode["unexpected"] = true;

        var unknownMember = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProcessControlState>(stateNode.ToJsonString(), options));

        var commandNode = Assert.IsType<JsonObject>(JsonNode.Parse(
            JsonSerializer.Serialize<ProcessControlCommand>(fixture.Pause(state), options)));
        commandNode[ExecutionControlWireNames.CommandDiscriminator] = "futureCommand";
        var unknownCommand = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProcessControlCommand>(commandNode.ToJsonString(), options));

        Assert.Contains("unexpected", unknownMember.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("futureCommand", unknownCommand.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlRevision_UsesCanonicalPositiveStringEncoding()
    {
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(new ProcessControlRevision("42"), options);

        Assert.Equal("\"42\"", json);
        Assert.Equal(
            new ProcessControlRevision("42"),
            JsonSerializer.Deserialize<ProcessControlRevision>(json, options));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ProcessControlRevision>("42", options));
        Assert.ThrowsAny<Exception>(() => new ProcessControlRevision("01"));
        Assert.ThrowsAny<Exception>(() => new ProcessControlRevision("0"));
    }

    [Fact]
    public void DefaultRevision_IsRejectedAndReceiptDerivationHandlesTheMaximumRevision()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        Assert.Throws<ArgumentException>(() => new ProcessControlExpectation(
            new(initial.ProcessInstanceId, initial.CurrentAttempt.AttemptId),
            default));
        Assert.Throws<ArgumentException>(() => new ProcessControlState(
            initial.SchemaVersion,
            initial.Definition,
            initial.AuthorityScope,
            initial.ProcessInstanceId,
            default,
            initial.Mode,
            initial.Attempts,
            initial.PendingCommandId,
            initial.Receipts,
            initial.CreatedAtUtc,
            initial.UpdatedAtUtc));

        ProcessControlRevision maximum = new(
            long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var expectation = new ProcessControlExpectation(
            new(initial.ProcessInstanceId, initial.CurrentAttempt.AttemptId),
            maximum);
        var canonicalContinue = fixture.Continue(initial);
        var continued = new ContinueProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            canonicalContinue.Context,
            expectation);
        var noOp = new ProcessControlCommandReceipt(
            continued,
            ProcessControlReceiptDisposition.AlreadySatisfied,
            initial.UpdatedAtUtc.AddMinutes(1));
        var canonicalPause = fixture.Pause(initial);
        var pause = new PauseProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            canonicalPause.Context,
            expectation);

        Assert.Equal(maximum, noOp.BeforeRevision);
        Assert.Equal(maximum, noOp.AfterRevision);
        Assert.Throws<OverflowException>(() => new ProcessControlCommandReceipt(
            pause,
            ProcessControlReceiptDisposition.Applied,
            initial.UpdatedAtUtc.AddMinutes(1)));
    }

    [Fact]
    public void UnsupportedSchemaAuthorityAndTarget_AreRejectedWithDistinctDiagnostics()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var canonical = fixture.Pause(state);
        var unsupportedSchema = new PauseProcessCommand(
            new("cohesive-process-control-command/v2"),
            canonical.Context,
            Assert.IsType<ProcessControlExpectation>(canonical.Expectation));
        var wrongAuthorityContext = new ProcessControlCommandContext(
            new("pause/wrong-authority"),
            new("idempotency/pause/wrong-authority"),
            state.ProcessInstanceId,
            new("operator/alice", new("authority/other"), "policy/other/allow"),
            state.UpdatedAtUtc,
            ProcessControlTestFixture.Provenance());
        var wrongAuthority = new PauseProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            wrongAuthorityContext,
            fixture.Expectation(state));
        ProcessInstanceId otherInstance = new("process/other");
        var wrongTarget = new PauseProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new("pause/wrong-target"),
                new("idempotency/pause/wrong-target"),
                otherInstance,
                new("operator/alice", state.AuthorityScope, "policy/control-test/allow"),
                state.UpdatedAtUtc,
                ProcessControlTestFixture.Provenance()),
            new(
                new(otherInstance, state.CurrentAttempt.AttemptId),
                state.Revision));

        var schemaDecision = fixture.Executor.Apply(
            state,
            unsupportedSchema,
            state.UpdatedAtUtc.AddMinutes(1));
        var authorityDecision = fixture.Executor.Apply(
            state,
            wrongAuthority,
            state.UpdatedAtUtc.AddMinutes(1));
        var targetDecision = fixture.Executor.Apply(
            state,
            wrongTarget,
            state.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, schemaDecision.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.InvalidCommand, Assert.Single(schemaDecision.Diagnostics).Code);
        Assert.Equal(ProcessControlDecisionDisposition.Unauthorized, authorityDecision.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.AuthorityMismatch, Assert.Single(authorityDecision.Diagnostics).Code);
        Assert.Equal(ProcessControlDecisionDisposition.TargetMismatch, targetDecision.Disposition);
        Assert.Equal(ProcessControlDiagnosticCodes.TargetMismatch, Assert.Single(targetDecision.Diagnostics).Code);
    }

    [Fact]
    public void AffinityBearingAttempt_RequiresExplicitAbandonmentCleanup()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.BindAffinity(fixture.State()).State;

        var restart = fixture.Executor.Apply(
            state,
            fixture.Restart(
                state,
                cleanup: ProcessAttemptCleanupRequirement.ReleaseAttemptResources),
            state.UpdatedAtUtc.AddMinutes(1));
        var terminate = fixture.Executor.Apply(
            state,
            fixture.Terminate(
                state,
                cleanup: ProcessAttemptCleanupRequirement.ReleaseAttemptResources),
            state.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, restart.Disposition);
        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, terminate.Disposition);
        Assert.All(
            restart.Diagnostics.Concat(terminate.Diagnostics),
            static diagnostic => Assert.Equal(ProcessControlDiagnosticCodes.InvalidCommand, diagnostic.Code));
        Assert.Same(state, restart.State);
        Assert.Same(state, terminate.State);
    }

    [Fact]
    public void ReceiptConstructor_RejectsImpossibleCommandDispositions()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var pause = fixture.Pause(state);

        var exception = Assert.Throws<ArgumentException>(() => new ProcessControlCommandReceipt(
            pause,
            ProcessControlReceiptDisposition.SignalAccepted,
            state.UpdatedAtUtc.AddMinutes(1)));

        Assert.Equal("disposition", exception.ParamName);
    }

    [Fact]
    public void RestartReceipt_RequiresADistinctPlannedReplacement()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var restart = fixture.Restart(
            state,
            newAttemptId: state.CurrentAttempt.AttemptId.Value);

        var exception = Assert.Throws<ArgumentException>(() => new ProcessControlCommandReceipt(
            restart,
            ProcessControlReceiptDisposition.Applied,
            state.UpdatedAtUtc.AddMinutes(1)));

        Assert.Equal("command", exception.ParamName);
    }

    [Fact]
    public void ClosedAttempt_RequiresAMatchingCausalReceipt()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var cancel = fixture.Cancel(initial);
        var cancelled = fixture.Executor.Apply(
            initial,
            cancel,
            initial.UpdatedAtUtc.AddMinutes(1)).State;

        var missingReceipt = Assert.Throws<ArgumentException>(() => new ProcessControlState(
            cancelled.SchemaVersion,
            cancelled.Definition,
            cancelled.AuthorityScope,
            cancelled.ProcessInstanceId,
            cancelled.Revision,
            cancelled.Mode,
            cancelled.Attempts,
            cancelled.PendingCommandId,
            receipts: [],
            cancelled.CreatedAtUtc,
            cancelled.UpdatedAtUtc));

        var current = cancelled.CurrentAttempt;
        var mismatchedAttempt = new ProcessControlAttemptState(
            current.AttemptId,
            current.StartedAtUtc,
            current.Disposition,
            current.Phase,
            current.ActiveActivation,
            current.SafePoints,
            current.AffinityBindings,
            new ProcessAttemptClosure(
                new("cancel/missing-causal-receipt"),
                Assert.IsType<DateTimeOffset>(current.EndedAtUtc)));
        var mismatchedReceipt = Assert.Throws<ArgumentException>(() => new ProcessControlState(
            cancelled.SchemaVersion,
            cancelled.Definition,
            cancelled.AuthorityScope,
            cancelled.ProcessInstanceId,
            cancelled.Revision,
            cancelled.Mode,
            [mismatchedAttempt],
            cancelled.PendingCommandId,
            cancelled.Receipts,
            cancelled.CreatedAtUtc,
            cancelled.UpdatedAtUtc));

        Assert.Equal("Attempts", missingReceipt.ParamName);
        Assert.Equal("Attempts", mismatchedReceipt.ParamName);
    }

    [Fact]
    public void ReplacementAttempt_MustStartAtTheExactAbandonmentCut()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var restarted = fixture.Executor.Apply(
            initial,
            fixture.Restart(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var replacement = restarted.CurrentAttempt;
        var chronologicallyInvalid = new ProcessControlAttemptState(
            replacement.AttemptId,
            restarted.CreatedAtUtc.AddSeconds(30),
            replacement.Disposition,
            replacement.Phase,
            replacement.ActiveActivation,
            replacement.SafePoints,
            replacement.AffinityBindings,
            replacement.Closure);

        var exception = Assert.Throws<ArgumentException>(() => new ProcessControlState(
            restarted.SchemaVersion,
            restarted.Definition,
            restarted.AuthorityScope,
            restarted.ProcessInstanceId,
            restarted.Revision,
            restarted.Mode,
            [restarted.Attempts[0], chronologicallyInvalid],
            restarted.PendingCommandId,
            restarted.Receipts,
            restarted.CreatedAtUtc,
            restarted.UpdatedAtUtc));

        Assert.Equal("Attempts", exception.ParamName);
    }

    [Fact]
    public void PortableWire_OmitsAllDerivedControlConveniences()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var signalled = fixture.Executor.Apply(
            initial,
            fixture.SignalCommand(initial),
            initial.UpdatedAtUtc.AddMinutes(1)).State;
        var activation = fixture.BeginActivation(signalled).State;
        var restarting = fixture.Executor.Apply(
            activation,
            fixture.Restart(activation),
            activation.UpdatedAtUtc.AddMinutes(1)).State;
        var restarted = fixture.ReachSafePoint(restarting).State;
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();

        var root = Assert.IsType<JsonObject>(JsonNode.Parse(
            JsonSerializer.Serialize(restarted, options)));

        Assert.False(root.ContainsKey("currentAttempt"));
        Assert.False(root.ContainsKey("isTerminal"));
        Assert.False(root.ContainsKey("signalAdmissions"));
        var attempts = Assert.IsType<JsonArray>(root["attempts"]);
        var abandoned = Assert.IsType<JsonObject>(attempts[0]);
        Assert.False(abandoned.ContainsKey("activeActivationId"));
        Assert.False(abandoned.ContainsKey("lastSafePoint"));
        Assert.False(abandoned.ContainsKey("endedAtUtc"));
        var safePoints = Assert.IsType<JsonArray>(abandoned["safePoints"]);
        var safePoint = Assert.IsType<JsonObject>(safePoints[0]);
        Assert.False(safePoint.ContainsKey("safePointId"));
        Assert.False(safePoint.ContainsKey("activationId"));
        Assert.False(safePoint.ContainsKey("node"));
        Assert.False(safePoint.ContainsKey("observedAtUtc"));
        var closure = Assert.IsType<JsonObject>(abandoned["closure"]);
        Assert.True(closure.ContainsKey("commandId"));
        Assert.True(closure.ContainsKey("occurredAtUtc"));
        Assert.False(closure.ContainsKey("reason"));
        Assert.False(closure.ContainsKey("cleanup"));
        Assert.False(closure.ContainsKey("replacementAttemptId"));
        foreach (var node in Assert.IsType<JsonArray>(root["receipts"]))
        {
            var receipt = Assert.IsType<JsonObject>(node);
            Assert.False(receipt.ContainsKey("beforeRevision"));
            Assert.False(receipt.ContainsKey("afterRevision"));
            Assert.False(receipt.ContainsKey("beforeAttemptId"));
            Assert.False(receipt.ContainsKey("afterAttemptId"));
        }
    }
}
