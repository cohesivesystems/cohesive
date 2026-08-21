using System.Collections.Immutable;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Control;
using Cohesive.Processes.Runtime;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationHarnessControlScenarioResultTests
{
    [Fact]
    public void ValidateAndProjectSemantics_IgnoresTransportLocalIdentities()
    {
        var sdk = Create(MaterializationHarnessControlTransportKind.Sdk, "sdk");
        var http = Create(MaterializationHarnessControlTransportKind.Http, "http");

        var sdkSemantics = sdk.ValidateAndProjectSemantics();
        var httpSemantics = http.ValidateAndProjectSemantics();

        Assert.True(sdkSemantics.IsEquivalentTo(httpSemantics));
    }

    [Fact]
    public void ValidateAndProjectSemantics_RejectsGenerationReuseAndUnboundLimitEpoch()
    {
        var valid = Create(MaterializationHarnessControlTransportKind.Sdk, "sdk");

        var generationReuse = valid with { RestartedGeneration = valid.InitialGeneration };
        var unboundLimit = valid with { LimitUpdateBoundExactGeneration = false };

        Assert.Contains(
            "fresh attempt and generation",
            Assert.Throws<InvalidOperationException>(generationReuse.ValidateAndProjectSemantics).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "exact continued generation epoch",
            Assert.Throws<InvalidOperationException>(unboundLimit.ValidateAndProjectSemantics).Message,
            StringComparison.Ordinal);
    }

    static MaterializationHarnessControlScenarioResult Create(
        MaterializationHarnessControlTransportKind transport,
        string identityPrefix)
    {
        var initialAttempt = $"attempt/{identityPrefix}/initial";
        var initialGeneration = $"generation/{identityPrefix}/initial";
        var restartedAttempt = $"attempt/{identityPrefix}/restart";
        var restartedGeneration = $"generation/{identityPrefix}/restart";
        return new(
            SchemaVersion: MaterializationHarnessControlScenarioResult.CurrentSchemaVersion,
            Transport: transport,
            Provider: "postgres",
            Operations: Operations(),
            InitialAttempt: initialAttempt,
            InitialGeneration: initialGeneration,
            PausedAttempt: initialAttempt,
            PausedGeneration: initialGeneration,
            ContinuedAttempt: initialAttempt,
            ContinuedGeneration: initialGeneration,
            RestartedAttempt: restartedAttempt,
            RestartedGeneration: restartedGeneration,
            CancelledAttempt: restartedAttempt,
            CancelledGeneration: restartedGeneration,
            LimitUpdateBoundExactGeneration: true,
            InterruptedGenerationState: MaterializationGenerationState.Retired,
            CancelledGenerationState: MaterializationGenerationState.Retired,
            ActiveGeneration: null,
            FinalControlMode: ProcessControlMode.Cancelled,
            FinalTerminalOutcome: ExecutionTerminalOutcomeKind.Cancelled);
    }

    static ImmutableArray<MaterializationHarnessControlOperationObservation> Operations() =>
    [
        Observation(ProcessStartWireNames.Start, ApiResultKind.Success, "Accepted"),
        Observation(
            ExecutionControlWireNames.Pause,
            ApiResultKind.Success,
            "Applied",
            beforeRevision: 1,
            afterRevision: 2,
            controlMode: ProcessControlMode.Paused),
        Observation(
            ExecutionControlWireNames.Inspect,
            ApiResultKind.Success,
            "Inspected",
            controlMode: ProcessControlMode.Paused),
        Observation(ExecutionExplainWireNames.Explain, ApiResultKind.Success, "Available"),
        Observation(
            ProcessExecutionTraceWireNames.Read,
            ApiResultKind.Conflict,
            ExecutionApiProblemCodes.TraceInProgress),
        Observation(
            ExecutionControlWireNames.Continue,
            ApiResultKind.Success,
            "Applied",
            beforeRevision: 2,
            afterRevision: 3,
            controlMode: ProcessControlMode.Running),
        Observation(ControlLimitUpdateWireNames.UpdateLimits, ApiResultKind.Accepted, "Accepted"),
        Observation(
            ExecutionControlWireNames.RestartAttempt,
            ApiResultKind.Success,
            "Applied",
            beforeRevision: 3,
            afterRevision: 4,
            controlMode: ProcessControlMode.Running),
        Observation(
            ExecutionControlWireNames.Cancel,
            ApiResultKind.Success,
            "Applied",
            beforeRevision: 4,
            afterRevision: 5,
            controlMode: ProcessControlMode.Cancelled)
    ];

    static MaterializationHarnessControlOperationObservation Observation(
        string operation,
        ApiResultKind resultKind,
        string disposition,
        long? beforeRevision = null,
        long? afterRevision = null,
        ProcessControlMode? controlMode = null) => new(
        Operation: operation,
        ResultKind: resultKind,
        Disposition: disposition,
        BeforeRevision: beforeRevision,
        AfterRevision: afterRevision,
        CurrentRevision: afterRevision,
        ControlMode: controlMode);
}
