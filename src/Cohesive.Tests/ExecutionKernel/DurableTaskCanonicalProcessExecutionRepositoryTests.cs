using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Runtime;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableTaskCanonicalProcessExecutionRepositoryTests
{
    [Fact]
    public async Task GetAsync_ProjectsCanonicalStatusWithoutRetainedPayloads()
    {
        var fixture = CreateFixture();
        var metadata = Metadata(
            fixture,
            fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Running,
            serializedOutput: "secret-output");
        var client = new FakeDurableTaskClient([metadata]);
        IProcessExecutionRepository repository = new DurableTaskProcessExecutionRepository(client);

        var record = await repository.GetAsync(OperationContext.Create(), fixture.PhysicalInstanceId);

        Assert.True(client.LastGetInputsAndOutputs);
        Assert.NotNull(record);
        Assert.Equal(fixture.PhysicalInstanceId, record.ProcessId);
        Assert.Equal(
            fixture.WaitingStatus.ProcessInstanceId,
            Assert.IsType<ExecutionStatus>(record.RuntimeStatus).ProcessInstanceId);
        Assert.NotEqual(record.ProcessId, record.RuntimeStatus.ProcessInstanceId.Value);
        Assert.Equal(fixture.WaitingStatus.Definition.DefinitionId.Value, record.ProcessName);
        Assert.Equal(ProcessExecutionStatus.Waiting, record.Status);
        Assert.Null(record.Parameters);
        Assert.Null(record.Output);
        Assert.Null(record.FailureMessage);
        Assert.Null(record.Error);
        Assert.DoesNotContain("secret-output", DurableTaskProcessDataConverter.Create().Serialize(record));
    }

    [Fact]
    public async Task GetAsync_AllowsOnlyPendingCurrentInstanceWithoutCustomStatus()
    {
        var fixture = CreateFixture();
        var pending = Metadata(fixture, status: null, OrchestrationRuntimeStatus.Pending);
        var repository = new DurableTaskProcessExecutionRepository(new FakeDurableTaskClient([pending]));

        var record = await repository.GetAsync(OperationContext.Create(), fixture.PhysicalInstanceId);

        Assert.NotNull(record);
        Assert.Equal(ProcessExecutionStatus.Pending, record.Status);
        Assert.Null(record.RuntimeStatus);
        Assert.Equal(fixture.WaitingStatus.Definition.DefinitionId.Value, record.ProcessName);

        var running = Metadata(fixture, status: null, OrchestrationRuntimeStatus.Running);
        var invalidRepository = new DurableTaskProcessExecutionRepository(new FakeDurableTaskClient([running]));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await invalidRepository.GetAsync(OperationContext.Create(), fixture.PhysicalInstanceId));
        Assert.Contains("outside the pending admission state", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_MapsCurrentFiltersAndPreservesProviderPage()
    {
        var fixture = CreateFixture();
        var current = Metadata(fixture, fixture.WaitingStatus, OrchestrationRuntimeStatus.Running);
        var unrelated = new OrchestrationMetadata("unrelated", "other-instance")
        {
            DataConverter = fixture.Converter,
            RuntimeStatus = OrchestrationRuntimeStatus.Running,
            CreatedAt = fixture.WaitingStatus.CreatedAtUtc,
            LastUpdatedAt = fixture.WaitingStatus.UpdatedAtUtc,
            SerializedInput = fixture.Converter.Serialize("unrelated"),
            SerializedCustomStatus = fixture.Converter.Serialize("unrelated")
        };
        var client = new FakeDurableTaskClient([current, unrelated], continuationToken: "ct-out");
        var repository = new DurableTaskProcessExecutionRepository(client, taskHubName: "arihub");

        var result = await repository.QueryAsync(OperationContext.Create(), new()
        {
            ProcessIdPrefix = "cohesive-process:v1:",
            ProcessName = fixture.WaitingStatus.Definition.DefinitionId.Value,
            Statuses = [ProcessExecutionStatus.Waiting],
            CreatedAfterUtc = fixture.WaitingStatus.CreatedAtUtc.AddMinutes(-1),
            CreatedBeforeUtc = fixture.WaitingStatus.CreatedAtUtc.AddMinutes(1),
            Limit = 25,
            ContinuationToken = "ct-in"
        });

        var query = Assert.IsType<OrchestrationQuery>(client.LastQuery);
        Assert.Equal("cohesive-process:v1:", query.InstanceIdPrefix);
        Assert.Equal(25, query.PageSize);
        Assert.True(query.FetchInputsAndOutputs);
        Assert.Equal("ct-in", query.ContinuationToken);
        Assert.Equal("ct-in", client.LastPageContinuationToken);
        Assert.Contains("arihub", Assert.IsAssignableFrom<IEnumerable<string>>(query.TaskHubNames));
        Assert.Contains(
            OrchestrationRuntimeStatus.Running,
            Assert.IsAssignableFrom<IEnumerable<OrchestrationRuntimeStatus>>(query.Statuses));
        Assert.Equal("ct-out", result.ContinuationToken);
        Assert.Equal(fixture.PhysicalInstanceId, Assert.Single(result.Items).ProcessId);
    }

    [Fact]
    public async Task CurrentProjection_FailsClosedOnMalformedOrConflictingEvidence()
    {
        var fixture = CreateFixture();
        var malformed = Metadata(
            fixture,
            fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Running,
            serializedCustomStatus: "{not-json");
        var malformedRepository = new DurableTaskProcessExecutionRepository(
            new FakeDurableTaskClient([malformed]));
        var malformedException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await malformedRepository.GetAsync(OperationContext.Create(), fixture.PhysicalInstanceId));
        Assert.Contains("malformed canonical custom status", malformedException.Message, StringComparison.Ordinal);

        var conflictingStatus = Copy(
            fixture.WaitingStatus,
            processInstanceId: new("process-instance/conflict"));
        var conflicting = Metadata(fixture, conflictingStatus, OrchestrationRuntimeStatus.Running);
        var conflictingRepository = new DurableTaskProcessExecutionRepository(
            new FakeDurableTaskClient([conflicting]));
        var conflictException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await conflictingRepository.GetAsync(OperationContext.Create(), fixture.PhysicalInstanceId));
        Assert.Contains("conflicts with its start receipt", conflictException.Message, StringComparison.Ordinal);

        var wrongPhysical = Metadata(
            fixture,
            fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Running,
            instanceId: "wrong-physical-id");
        var wrongPhysicalRepository = new DurableTaskProcessExecutionRepository(
            new FakeDurableTaskClient([wrongPhysical]));
        var physicalException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await wrongPhysicalRepository.GetAsync(OperationContext.Create(), "wrong-physical-id"));
        Assert.Contains("physical identity derived", physicalException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentProjection_RejectsContradictoryTerminalEvidence()
    {
        var fixture = CreateFixture();
        var completed = Terminal(
            fixture.WaitingStatus,
            ExecutionTerminalOutcomeKind.Completed,
            ExecutionAttemptDisposition.Completed,
            ProcessControlMode.Running);
        var metadata = Metadata(fixture, completed, OrchestrationRuntimeStatus.Failed);
        var repository = new DurableTaskProcessExecutionRepository(new FakeDurableTaskClient([metadata]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.GetAsync(OperationContext.Create(), fixture.PhysicalInstanceId));

        Assert.Contains("contradictory terminal states", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentLifecycleProjection_PreservesCanonicalSemanticOutcomes()
    {
        var fixture = CreateFixture();
        var running = Copy(
            fixture.WaitingStatus,
            runtime: ExecutionRuntimeStatusDetails.Unknown);
        var paused = Copy(
            running,
            controlMode: ProcessControlMode.Paused);

        Assert.Equal(
            ProcessExecutionStatus.Running,
            DurableTaskProcessStatus.ResolveStatus(OrchestrationRuntimeStatus.Running, running));
        Assert.Equal(
            ProcessExecutionStatus.Waiting,
            DurableTaskProcessStatus.ResolveStatus(
                OrchestrationRuntimeStatus.Running,
                fixture.WaitingStatus));
        Assert.Equal(
            ProcessExecutionStatus.Suspended,
            DurableTaskProcessStatus.ResolveStatus(OrchestrationRuntimeStatus.Running, paused));
        Assert.Equal(
            ProcessExecutionStatus.Completed,
            DurableTaskProcessStatus.ResolveStatus(OrchestrationRuntimeStatus.Completed, running));
        Assert.Equal(
            ProcessExecutionStatus.Completed,
            DurableTaskProcessStatus.ResolveStatus(
                OrchestrationRuntimeStatus.Completed,
                Terminal(
                    running,
                    ExecutionTerminalOutcomeKind.Completed,
                    ExecutionAttemptDisposition.Completed,
                    ProcessControlMode.Running)));
        Assert.Equal(
            ProcessExecutionStatus.Failed,
            DurableTaskProcessStatus.ResolveStatus(
                OrchestrationRuntimeStatus.Failed,
                running));
        Assert.Equal(
            ProcessExecutionStatus.Cancelled,
            DurableTaskProcessStatus.ResolveStatus(
                OrchestrationRuntimeStatus.Completed,
                Terminal(
                    running,
                    ExecutionTerminalOutcomeKind.Cancelled,
                    ExecutionAttemptDisposition.Cancelled,
                    ProcessControlMode.Cancelled)));
        Assert.Equal(
            ProcessExecutionStatus.Terminated,
            DurableTaskProcessStatus.ResolveStatus(
                OrchestrationRuntimeStatus.Completed,
                Terminal(
                    running,
                    ExecutionTerminalOutcomeKind.Terminated,
                    ExecutionAttemptDisposition.Terminated,
                    ProcessControlMode.Terminated)));
    }

    static CurrentFixture CreateFixture()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/repository-current-tests");
        var start = new DurableTaskSequentialProcessStart(fixture.Start, fixture.Activation.Context);
        var status = ProcessExecutionStatusProjector.Project(
            fixture.Checkpoint.Continuation,
            fixture.Checkpoint.Control,
            fixture.Checkpoint.DurableOperations);
        return new(
            start,
            status,
            DurableTaskSequentialProcessIdentities.OrchestrationInstance(start),
            DurableTaskProcessDataConverter.Create());
    }

    static OrchestrationMetadata Metadata(
        CurrentFixture fixture,
        ExecutionStatus? status,
        OrchestrationRuntimeStatus runtimeStatus,
        string? serializedOutput = null,
        string? instanceId = null,
        string? serializedCustomStatus = null) => new(
            DurableTaskSequentialProcessNames.Orchestration,
            instanceId ?? fixture.PhysicalInstanceId)
        {
            DataConverter = fixture.Converter,
            RuntimeStatus = runtimeStatus,
            CreatedAt = fixture.WaitingStatus.CreatedAtUtc,
            LastUpdatedAt = status?.UpdatedAtUtc ?? fixture.WaitingStatus.CreatedAtUtc,
            SerializedInput = fixture.Converter.Serialize(fixture.Start),
            SerializedOutput = serializedOutput,
            SerializedCustomStatus = serializedCustomStatus
                ?? (status is null ? null : fixture.Converter.Serialize(status))
        };

    static ExecutionStatus Copy(
        ExecutionStatus source,
        ProcessInstanceId? processInstanceId = null,
        ProcessControlMode? controlMode = null,
        ExecutionRuntimeStatusDetails? runtime = null) => new(
        source.SchemaVersion,
        source.Definition,
        processInstanceId ?? source.ProcessInstanceId,
        source.ControlRevision,
        controlMode ?? source.ControlMode,
        source.Attempts,
        source.ActiveActivation,
        runtime ?? source.Runtime,
        source.TerminalOutcome,
        source.CreatedAtUtc,
        source.UpdatedAtUtc);

    static ExecutionStatus Terminal(
        ExecutionStatus source,
        ExecutionTerminalOutcomeKind kind,
        ExecutionAttemptDisposition disposition,
        ProcessControlMode controlMode)
    {
        var occurredAtUtc = source.UpdatedAtUtc.AddMinutes(1);
        var current = source.CurrentAttempt;
        ImmutableArray<ExecutionAttemptStatus> attempts =
        [
            new(
                current.AttemptId,
                current.StartedAtUtc,
                occurredAtUtc,
                disposition,
                ProcessControlExecutionPhase.Stopped,
                current.CompletedActivationCount,
                current.LastSafePointId,
                current.LastSafePointNode)
        ];
        return new(
            source.SchemaVersion,
            source.Definition,
            source.ProcessInstanceId,
            source.ControlRevision,
            controlMode,
            attempts,
            activeActivation: null,
            ExecutionRuntimeStatusDetails.Unknown,
            new(kind, occurredAtUtc),
            source.CreatedAtUtc,
            occurredAtUtc);
    }

    sealed record CurrentFixture(
        DurableTaskSequentialProcessStart Start,
        ExecutionStatus WaitingStatus,
        string PhysicalInstanceId,
        DataConverter Converter);

    sealed class FakeDurableTaskClient(
        IReadOnlyList<OrchestrationMetadata> metadata,
        string? continuationToken = null) : DurableTaskClient("fake")
    {
        public OrchestrationQuery? LastQuery { get; private set; }

        public string? LastPageContinuationToken { get; private set; }

        public bool LastGetInputsAndOutputs { get; private set; }

        public override Task<OrchestrationMetadata?> GetInstancesAsync(
            string instanceId,
            bool getInputsAndOutputs = false,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            LastGetInputsAndOutputs = getInputsAndOutputs;
            return Task.FromResult(metadata.FirstOrDefault(candidate =>
                string.Equals(candidate.InstanceId, instanceId, StringComparison.Ordinal)));
        }

        public override AsyncPageable<OrchestrationMetadata> GetAllInstancesAsync(
            OrchestrationQuery? filter = null)
        {
            LastQuery = filter;
            return Pageable.Create<OrchestrationMetadata>((continuation, pageSize, cancellation) =>
            {
                cancellation.ThrowIfCancellationRequested();
                LastPageContinuationToken = continuation ?? filter?.ContinuationToken;
                return Task.FromResult(new Page<OrchestrationMetadata>(metadata, continuationToken));
            });
        }

        public override Task<string> ScheduleNewOrchestrationInstanceAsync(
            TaskName orchestratorName,
            object? input = null,
            StartOrchestrationOptions? options = null,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public override Task RaiseEventAsync(
            string instanceId,
            string eventName,
            object? eventPayload = null,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public override Task<OrchestrationMetadata> WaitForInstanceStartAsync(
            string instanceId,
            bool getInputsAndOutputs = false,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public override Task<OrchestrationMetadata> WaitForInstanceCompletionAsync(
            string instanceId,
            bool getInputsAndOutputs = false,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public override Task SuspendInstanceAsync(
            string instanceId,
            string? reason = null,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public override Task ResumeInstanceAsync(
            string instanceId,
            string? reason = null,
            CancellationToken cancellation = default) => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
