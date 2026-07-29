using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessControlWireContractTests
{
    [Fact]
    public void Commands_RoundTripThroughTheStrictCanonicalWire()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        ProcessControlCommand[] commands =
        [
            fixture.Inspect(state, id: "inspect/strict-wire"),
            fixture.SignalCommand(state, id: "signal/strict-wire"),
            fixture.Pause(state, id: "pause/strict-wire"),
            fixture.Continue(state, id: "continue/strict-wire"),
            fixture.Restart(state, id: "restart/strict-wire"),
            fixture.Cancel(state, id: "cancel/strict-wire"),
            fixture.Terminate(state, id: "terminate/strict-wire")
        ];

        foreach (var command in commands)
        {
            var canonical = ProcessControlJsonSerializer.GetCanonicalBytes(command);
            var restored = ProcessControlJsonSerializer.DeserializeCommand(
                Encoding.UTF8.GetString(canonical),
                fixture.Catalog);

            Assert.Equal(command.GetType(), restored.GetType());
            Assert.Equal(command, restored);
            Assert.Equal(canonical, ProcessControlJsonSerializer.GetCanonicalBytes(restored));
        }
    }

    [Fact]
    public void State_RoundTripsCompleteSignalAffinityAndAttemptLineage()
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

        var canonical = ProcessControlJsonSerializer.GetCanonicalBytes(restarted);
        var restored = ProcessControlJsonSerializer.DeserializeState(
            Encoding.UTF8.GetString(canonical),
            fixture.Catalog);

        Assert.Equal(restarted, restored);
        Assert.Equal(restarted.GetHashCode(), restored.GetHashCode());
        Assert.Equal(canonical, ProcessControlJsonSerializer.GetCanonicalBytes(restored));
        Assert.Equal(ProcessControlAttemptDisposition.Abandoned, restored.Attempts[0].Disposition);
        Assert.Single(restored.Attempts[0].AffinityBindings);
        Assert.Single(restored.SignalAdmissions);
    }

    [Fact]
    public void DecisionResults_RoundTripEveryIntentAndDiagnosticShape()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var activation = fixture.BeginActivation(initial);
        var inActivation = activation.State;
        var safePoint = fixture.ReachSafePoint(inActivation);
        var affinity = fixture.BindAffinity(initial);
        var cancelled = fixture.Executor.Apply(
            initial,
            fixture.Cancel(initial, id: "cancel/result"),
            initial.UpdatedAtUtc.AddMinutes(1));
        ProcessControlDecision[] decisions =
        [
            fixture.Executor.Apply(
                initial,
                fixture.Inspect(initial, id: "inspect/result"),
                initial.UpdatedAtUtc),
            fixture.Executor.Apply(
                initial,
                fixture.SignalCommand(initial, id: "signal/result"),
                initial.UpdatedAtUtc.AddMinutes(1)),
            fixture.Executor.Apply(
                inActivation,
                fixture.Pause(inActivation, id: "pause/result"),
                inActivation.UpdatedAtUtc.AddMinutes(1)),
            fixture.Executor.Apply(
                initial,
                fixture.Restart(initial, id: "restart/result"),
                initial.UpdatedAtUtc.AddMinutes(1)),
            cancelled,
            fixture.Executor.Apply(
                initial,
                fixture.Terminate(initial, id: "terminate/result"),
                initial.UpdatedAtUtc.AddMinutes(1)),
            fixture.Executor.Apply(
                cancelled.State,
                fixture.Terminate(cancelled.State, id: "terminate/rejected-result"),
                cancelled.State.UpdatedAtUtc.AddMinutes(1)),
            activation,
            safePoint,
            affinity
        ];

        Assert.Collection(
            decisions.Select(static decision => decision.Intent).OfType<ProcessControlIntent>(),
            intent => Assert.IsType<ProcessSignalAdmissionIntent>(intent),
            intent => Assert.IsType<ProcessReachSafePointIntent>(intent),
            intent => Assert.IsType<ProcessAttemptRestartIntent>(intent),
            intent => Assert.IsType<ProcessCancellationIntent>(intent),
            intent => Assert.IsType<ProcessTerminationIntent>(intent));
        Assert.Contains(decisions, static decision => !decision.Diagnostics.IsEmpty);

        foreach (var decision in decisions)
        {
            var canonical = ProcessControlJsonSerializer.GetCanonicalBytes(decision);
            var restored = ProcessControlJsonSerializer.DeserializeDecision(
                Encoding.UTF8.GetString(canonical),
                fixture.Catalog);

            Assert.Equal(ProcessControlDecision.CurrentSchemaVersion, restored.SchemaVersion);
            Assert.Equal(decision, restored);
            Assert.Equal(decision.GetHashCode(), restored.GetHashCode());
            Assert.Equal(canonical, ProcessControlJsonSerializer.GetCanonicalBytes(restored));
        }
    }

    [Fact]
    public void Reader_RejectsRecursiveDuplicateProperties()
    {
        var fixture = ProcessControlTestFixture.Create();
        var canonical = ProcessControlJsonSerializer.Serialize(
            fixture.Pause(fixture.State(), id: "pause/duplicate-wire"));
        var duplicated = canonical.Replace(
            "\"actor\":\"operator/alice\"",
            "\"actor\":\"operator/alice\",\"actor\":\"operator/mallory\"",
            StringComparison.Ordinal);

        Assert.NotEqual(canonical, duplicated);
        var exception = Assert.Throws<JsonException>(() =>
            ProcessControlJsonSerializer.DeserializeCommand(duplicated, fixture.Catalog));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/context/authorization/actor", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsUnknownCommandAndIntentDiscriminators()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var command = JsonNode.Parse(ProcessControlJsonSerializer.Serialize(fixture.Pause(state)))!.AsObject();
        command[ExecutionControlWireNames.CommandDiscriminator] = "futureCommand";
        var signalDecision = fixture.Executor.Apply(
            state,
            fixture.SignalCommand(state),
            state.UpdatedAtUtc.AddMinutes(1));
        var decision = JsonNode.Parse(ProcessControlJsonSerializer.Serialize(signalDecision))!.AsObject();
        decision["intent"]![ExecutionControlWireNames.IntentDiscriminator] = "futureIntent";

        Assert.Throws<JsonException>(() =>
            ProcessControlJsonSerializer.DeserializeCommand(command.ToJsonString(), fixture.Catalog));
        Assert.Throws<JsonException>(() =>
            ProcessControlJsonSerializer.DeserializeDecision(decision.ToJsonString(), fixture.Catalog));
    }

    [Fact]
    public void Reader_RejectsUnknownMembersAtNestedAndRootBoundaries()
    {
        var fixture = ProcessControlTestFixture.Create();
        var command = JsonNode.Parse(
            ProcessControlJsonSerializer.Serialize(fixture.Pause(fixture.State())))!.AsObject();
        command["context"]!["authorization"]!["futureEvidence"] = "unsupported";
        var state = JsonNode.Parse(
            ProcessControlJsonSerializer.Serialize(fixture.State()))!.AsObject();
        state["futureState"] = true;

        Assert.Throws<JsonException>(() =>
            ProcessControlJsonSerializer.DeserializeCommand(command.ToJsonString(), fixture.Catalog));
        Assert.Throws<JsonException>(() =>
            ProcessControlJsonSerializer.DeserializeState(state.ToJsonString(), fixture.Catalog));
    }

    [Fact]
    public void Reader_RejectsUnsupportedDecisionSchemaVersion()
    {
        var fixture = ProcessControlTestFixture.Create();
        var decision = fixture.Executor.Apply(
            fixture.State(),
            fixture.Inspect(fixture.State()),
            ProcessControlTestFixture.CreatedAtUtc);
        var root = JsonNode.Parse(ProcessControlJsonSerializer.Serialize(decision))!.AsObject();
        root["schemaVersion"] = "cohesive-process-control-decision/v2";

        Assert.Throws<JsonException>(() =>
            ProcessControlJsonSerializer.DeserializeDecision(root.ToJsonString(), fixture.Catalog));
    }

    [Fact]
    public void Reader_RejectsNonportableReasonDetailUsingCatalogShapeContext()
    {
        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var baseline = fixture.Cancel(state, id: "cancel/opaque-reason");
        var opaqueDetail = PortableValue.Concrete(
            new(new OpaqueRuntimeTypeRef("Example.RuntimeOnlyReason, Example.Runtime")),
            ObservationValue.FromString("runtime-only"));
        var command = new CancelProcessCommand(
            baseline.SchemaVersion,
            baseline.Context,
            baseline.Expectation!,
            new("operator.cancel", opaqueDetail));

        var exception = Assert.Throws<JsonException>(() =>
            ProcessControlJsonSerializer.DeserializeCommand(
                ProcessControlJsonSerializer.Serialize(command),
                fixture.Catalog));

        Assert.Contains(PortableExecutionDiagnosticCodes.OpaqueRuntimeType, exception.Message, StringComparison.Ordinal);
        Assert.Contains("/reason/detail", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_ResolvesNamedReasonDetailFromTheCatalogShapeGraph()
    {
        TypeId reasonTypeId = new("process-control/operator-reason");
        var graph = new ShapeGraph(
            new("process-control/wire-tests"),
            [],
            [
                new TypeDefinition.Structural(
                    reasonTypeId,
                    [new(new("note"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]);
        var catalogValidation = InteractionContractCatalog.TryCreate([], graph, out var catalog);
        Assert.True(catalogValidation.IsValid, ProcessControlTestFixture.FormatDiagnostics(catalogValidation));

        var fixture = ProcessControlTestFixture.Create();
        var state = fixture.State();
        var baseline = fixture.Cancel(state, id: "cancel/named-reason");
        var detail = PortableValue.Concrete(
            new(new NamedTypeRef(reasonTypeId)),
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["note"] = ObservationValue.FromString("requested by operator")
            }));
        var command = new CancelProcessCommand(
            baseline.SchemaVersion,
            baseline.Context,
            baseline.Expectation!,
            new("operator.cancel", detail));

        var restored = ProcessControlJsonSerializer.DeserializeCommand(
            ProcessControlJsonSerializer.Serialize(command),
            Assert.IsType<InteractionContractCatalog>(catalog));

        Assert.Equal(command, restored);
    }

    [Fact]
    public void DecisionContract_RejectsReceiptDispositionAndIntentMismatches()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var signal = fixture.Executor.Apply(
            initial,
            fixture.SignalCommand(initial, id: "signal/malformed-result"),
            initial.UpdatedAtUtc.AddMinutes(1));
        var inActivation = fixture.BeginActivation(initial).State;
        var deferred = fixture.Executor.Apply(
            inActivation,
            fixture.Pause(inActivation, id: "pause/malformed-result"),
            inActivation.UpdatedAtUtc.AddMinutes(1));

        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            signal.State,
            ProcessControlDecisionDisposition.Applied,
            signal.Receipt,
            signal.Intent));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            signal.State,
            ProcessControlDecisionDisposition.SignalAccepted,
            signal.Receipt,
            intent: null));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            deferred.State,
            ProcessControlDecisionDisposition.DeferredToSafePoint,
            deferred.Receipt,
            intent: null));
    }

    [Fact]
    public void DecisionContract_RejectsIncoherentReplayObservationAndRejectionResults()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var signal = fixture.Executor.Apply(
            initial,
            fixture.SignalCommand(initial, id: "signal/result-shape"),
            initial.UpdatedAtUtc.AddMinutes(1));
        DocumentValidationDiagnostic diagnostic = new(
            ProcessControlDiagnosticCodes.InvalidState,
            DiagnosticSeverity.Error,
            "Rejected for test.",
            "/mode");
        DocumentValidationDiagnostic warning = new(
            "execution.control.warning",
            DiagnosticSeverity.Warning,
            "Warning is not rejection evidence.",
            "/mode");
        DocumentValidationDiagnostic signalDurability = new(
            ProcessControlDiagnosticCodes.SignalDurabilityMismatch,
            DiagnosticSeverity.Error,
            "Signal is not durable.",
            "/signal/context/delivery/durability");
        DocumentValidationDiagnostic unknownError = new(
            "execution.control.unknown",
            DiagnosticSeverity.Error,
            "Unknown rejection semantics.",
            "/");

        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            signal.State,
            ProcessControlDecisionDisposition.Replayed,
            signal.Receipt,
            signal.Intent));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            signal.State,
            ProcessControlDecisionDisposition.ActivationStarted,
            signal.Receipt));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            initial,
            ProcessControlDecisionDisposition.SafePointReached));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            initial,
            ProcessControlDecisionDisposition.InvalidState));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            initial,
            ProcessControlDecisionDisposition.InvalidState,
            diagnostics: [warning]));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            signal.State,
            ProcessControlDecisionDisposition.InvalidState,
            signal.Receipt,
            diagnostics: [diagnostic]));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            initial,
            ProcessControlDecisionDisposition.Unauthorized,
            diagnostics: [diagnostic]));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            initial,
            ProcessControlDecisionDisposition.InvalidCommand,
            diagnostics: [unknownError]));

        var specialized = new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            initial,
            ProcessControlDecisionDisposition.InvalidCommand,
            diagnostics: [signalDurability]);
        Assert.Equal(ProcessControlDecisionDisposition.InvalidCommand, specialized.Disposition);
    }

    [Fact]
    public void DecisionContract_RejectsOldReceiptsAsFreshFirstTimeResults()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var signal = fixture.Executor.Apply(
            initial,
            fixture.SignalCommand(initial, id: "signal/old-result"),
            initial.UpdatedAtUtc.AddMinutes(1));
        var pausedAfterSignal = fixture.Executor.Apply(
            signal.State,
            fixture.Pause(signal.State, id: "pause/after-signal"),
            signal.State.UpdatedAtUtc.AddMinutes(1)).State;
        var active = fixture.BeginActivation(initial).State;
        var deferred = fixture.Executor.Apply(
            active,
            fixture.Pause(active, id: "pause/old-deferred-result"),
            active.UpdatedAtUtc.AddMinutes(1));
        var bufferedAfterDeferral = fixture.Executor.Apply(
            deferred.State,
            fixture.SignalCommand(
                deferred.State,
                id: "signal/after-deferral",
                emissionId: "emission/after-deferral"),
            deferred.State.UpdatedAtUtc.AddMinutes(1)).State;

        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            pausedAfterSignal,
            signal.Disposition,
            signal.Receipt,
            signal.Intent));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            bufferedAfterDeferral,
            deferred.Disposition,
            deferred.Receipt,
            deferred.Intent));
    }

    [Fact]
    public void DecisionContract_RejectsObservationResultsAfterLaterEvidence()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var activation = fixture.BeginActivation(initial);
        var signalAfterActivation = fixture.Executor.Apply(
            activation.State,
            fixture.SignalCommand(activation.State, id: "signal/after-activation"),
            activation.State.UpdatedAtUtc.AddMinutes(1)).State;
        var affinity = fixture.BindAffinity(initial);
        var signalAfterAffinity = fixture.Executor.Apply(
            affinity.State,
            fixture.SignalCommand(
                affinity.State,
                id: "signal/after-affinity",
                emissionId: "emission/after-affinity"),
            affinity.State.UpdatedAtUtc.AddMinutes(1)).State;
        var safePoint = fixture.ReachSafePoint(activation.State);
        var signalAfterSafePoint = fixture.Executor.Apply(
            safePoint.State,
            fixture.SignalCommand(
                safePoint.State,
                id: "signal/after-safe-point",
                emissionId: "emission/after-safe-point"),
            safePoint.State.UpdatedAtUtc.AddMinutes(1)).State;

        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            signalAfterActivation,
            ProcessControlDecisionDisposition.ActivationStarted));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            signalAfterAffinity,
            ProcessControlDecisionDisposition.AffinityBound));
        Assert.Throws<ArgumentException>(() => new ProcessControlDecision(
            ProcessControlDecision.CurrentSchemaVersion,
            signalAfterSafePoint,
            ProcessControlDecisionDisposition.SafePointReached));
    }

    [Fact]
    public void Reader_RejectsWireThatOmitsAProjectedCanonicalMember()
    {
        var fixture = ProcessControlTestFixture.Create();
        var command = JsonNode.Parse(
            ProcessControlJsonSerializer.Serialize(fixture.Inspect(fixture.State())))!.AsObject();
        Assert.True(command.Remove("expectation"));

        var exception = Assert.Throws<JsonException>(() =>
            ProcessControlJsonSerializer.DeserializeCommand(command.ToJsonString(), fixture.Catalog));

        Assert.Contains("canonical typed wire", exception.Message, StringComparison.Ordinal);
    }
}
