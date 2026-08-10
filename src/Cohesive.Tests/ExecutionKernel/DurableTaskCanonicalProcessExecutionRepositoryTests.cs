using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Processes.Runtime;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using CanonicalProcessDefinition = Cohesive.Processes.IR.ProcessDefinition;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableTaskCanonicalProcessExecutionRepositoryTests
{
    [Fact]
    public async Task ScheduleAsync_PublishesOnlyVersionedCanonicalDiscoveryTags()
    {
        var fixture = CreateFixture();
        var client = new FakeDurableTaskClient([]);

        var result = await client.ScheduleCohesiveProcessAsync(fixture.Start);

        Assert.Equal(fixture.PhysicalInstanceId, result.InstanceId);
        Assert.False(result.Replayed);
        Assert.Equal(1, client.ScheduleCount);
        var options = Assert.IsType<StartOrchestrationOptions>(client.LastStartOptions);
        Assert.Equal(fixture.PhysicalInstanceId, options.InstanceId);
        var expected = DurableTaskProcessTags.Create(fixture.Start.Receipt);
        Assert.Equal(expected, options.Tags);
        string[] expectedTagNames =
            [
                DurableTaskProcessTags.DefinitionFingerprintAlgorithmTagName,
                DurableTaskProcessTags.DefinitionFingerprintCanonicalizationTagName,
                DurableTaskProcessTags.DefinitionFingerprintValueTagName,
                DurableTaskProcessTags.DefinitionIdTagName,
                DurableTaskProcessTags.DefinitionRevisionIdTagName,
                DurableTaskProcessTags.ProcessInstanceIdTagName,
                DurableTaskProcessTags.ProjectionVersionTagName
            ];
        Assert.Equal(
            expectedTagNames.Order(StringComparer.Ordinal),
            options.Tags.Keys.Order(StringComparer.Ordinal));
        var serializedTags = fixture.Converter.Serialize(options.Tags);
        Assert.DoesNotContain(fixture.Scope.Authority, serializedTags, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Scope.Tenant!, serializedTags, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Start.Receipt.Request.Context.CommandId.Value,
            serializedTags,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Start.Receipt.Request.Context.IdempotencyKey.Value,
            serializedTags,
            StringComparison.Ordinal);
        Assert.DoesNotContain("input/durable-checkpoint-tests", serializedTags, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduleAsync_RejectsOversizedUtf8TagValueBeforeTransportAdmission()
    {
        var source = ProcessDurabilityTestFixture.Create(
            definitionId: "process/" + new string('\u00e9', 497));
        var start = new DurableTaskSequentialProcessStart(source.Start, source.Activation.Context);
        var client = new FakeDurableTaskClient([]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.ScheduleCohesiveProcessAsync(start));

        Assert.Contains(
            DurableTaskProcessTags.DefinitionIdTagName,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("1000 bytes", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, client.ScheduleCount);
    }

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
    public async Task GetAsync_ByLogicalIdentityDerivesOneAuthorityScopedExactLookup()
    {
        var fixture = CreateFixture();
        var metadata = Metadata(
            fixture,
            fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Running,
            tags: DurableTaskProcessTags.Create(fixture.Start.Receipt));
        var client = new FakeDurableTaskClient([metadata]);
        var repository = new DurableTaskProcessExecutionRepository(client);

        var record = await repository.GetAsync(
            OperationContext.Create(),
            fixture.Scope,
            fixture.LogicalInstanceId);

        Assert.NotNull(record);
        Assert.Equal(fixture.PhysicalInstanceId, record.ProcessId);
        Assert.Equal(fixture.PhysicalInstanceId, client.LastGetInstanceId);
        Assert.Equal(1, client.GetCount);
        Assert.Equal(0, client.QueryCount);
        Assert.Equal(
            fixture.PhysicalInstanceId,
            DurableTaskProcessExecutionIdentity.GetPhysicalInstanceId(
                fixture.Scope,
                fixture.LogicalInstanceId));
        Assert.NotEqual(
            fixture.PhysicalInstanceId,
            DurableTaskProcessExecutionIdentity.GetPhysicalInstanceId(
                new(fixture.Scope.Authority, "tenant/other"),
                fixture.LogicalInstanceId));
    }

    [Fact]
    public async Task GetTracesAsync_DistinguishesMissingActiveUnavailableAndAvailableArtifacts()
    {
        const string PrivateInput = "private-input-ari-318";
        const string PrivateOutput = "private-output-ari-318";
        var completed = await CreateCompletedFixtureAsync(
            "process/repository-trace-available",
            PrivateInput,
            PrivateOutput);
        var completedMetadata = Metadata(
            completed.Fixture,
            completed.Fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Completed,
            serializedOutput: completed.Fixture.Converter.Serialize(completed.Result));
        var completedClient = new FakeDurableTaskClient([completedMetadata]);
        IProcessExecutionTraceRepository completedRepository =
            new DurableTaskProcessExecutionRepository(completedClient);

        var available = await completedRepository.GetTracesAsync(
            OperationContext.Create(),
            completed.Fixture.PhysicalInstanceId);

        Assert.Equal(ProcessExecutionTraceReadState.Available, available.State);
        var record = Assert.IsType<ProcessExecutionTraceRecord>(available.Record);
        Assert.True(record.IsComplete);
        Assert.Equal(0, record.MissingTracePrefixCount);
        Assert.Equal(completed.Result.Evidence.Length, record.ActivationEvidenceCount);
        Assert.Equal(
            completed.Result.Traces.Select(static trace => ExecutionTraceJsonSerializer.Serialize(trace)),
            record.Traces.Select(static trace => ExecutionTraceJsonSerializer.Serialize(trace)));
        Assert.Equal(1, completedClient.GetCount);
        Assert.Equal(0, completedClient.QueryCount);
        var serializedRecord = completed.Fixture.Converter.Serialize(available);
        Assert.DoesNotContain(PrivateInput, serializedRecord, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateOutput, serializedRecord, StringComparison.Ordinal);

        var activeFixture = CreateFixture();
        var active = await new DurableTaskProcessExecutionRepository(new FakeDurableTaskClient([
            Metadata(activeFixture, activeFixture.WaitingStatus, OrchestrationRuntimeStatus.Running)
        ])).GetTracesAsync(OperationContext.Create(), activeFixture.PhysicalInstanceId);
        Assert.Equal(ProcessExecutionTraceReadState.InProgress, active.State);

        var terminalStatus = Terminal(
            activeFixture.WaitingStatus,
            ExecutionTerminalOutcomeKind.Completed,
            ExecutionAttemptDisposition.Completed,
            ProcessControlMode.Running);
        var unavailable = await new DurableTaskProcessExecutionRepository(new FakeDurableTaskClient([
            Metadata(activeFixture, terminalStatus, OrchestrationRuntimeStatus.Completed)
        ])).GetTracesAsync(OperationContext.Create(), activeFixture.PhysicalInstanceId);
        Assert.Equal(ProcessExecutionTraceReadState.TerminalArtifactUnavailable, unavailable.State);

        var missing = await new DurableTaskProcessExecutionRepository(new FakeDurableTaskClient([]))
            .GetTracesAsync(OperationContext.Create(), activeFixture.PhysicalInstanceId);
        Assert.Equal(ProcessExecutionTraceReadState.NotFound, missing.State);
    }

    [Fact]
    public async Task GetTracesAsync_ReportsLegacyPrefixAndUsesExactLogicalIdentityLookup()
    {
        var completed = await CreateCompletedFixtureAsync(
            "process/repository-trace-legacy",
            "legacy-input",
            "legacy-output");
        var result = completed.Result;
        var legacy = new DurableTaskSequentialProcessResult(
            result.Disposition,
            result.State,
            result.Control,
            result.LatestControlDecision,
            result.Emissions,
            result.InputAdmissions,
            result.Diagnostics,
            result.Evidence,
            result.DurableOperations);
        var metadata = Metadata(
            completed.Fixture,
            completed.Fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Completed,
            serializedOutput: completed.Fixture.Converter.Serialize(legacy));
        var client = new FakeDurableTaskClient([metadata]);
        var repository = new DurableTaskProcessExecutionRepository(client);

        var read = await repository.GetTracesAsync(
            OperationContext.Create(),
            completed.Fixture.Scope,
            completed.Fixture.LogicalInstanceId);

        var record = Assert.IsType<ProcessExecutionTraceRecord>(read.Record);
        Assert.False(record.IsComplete);
        Assert.Empty(record.Traces);
        Assert.Equal(result.Evidence.Length, record.MissingTracePrefixCount);
        Assert.Equal(completed.Fixture.PhysicalInstanceId, record.ProcessId);
        Assert.Equal(completed.Fixture.PhysicalInstanceId, client.LastGetInstanceId);
        Assert.Equal(1, client.GetCount);
        Assert.Equal(0, client.QueryCount);
    }

    [Fact]
    public async Task GetTracesAsync_FailsClosedOnMalformedOrConflictingTerminalResults()
    {
        var completed = await CreateCompletedFixtureAsync(
            "process/repository-trace-affinity",
            "input",
            "output");
        var malformed = Metadata(
            completed.Fixture,
            completed.Fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Completed,
            serializedOutput: "{not-json");
        var malformedRepository = new DurableTaskProcessExecutionRepository(
            new FakeDurableTaskClient([malformed]));

        var malformedException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await malformedRepository.GetTracesAsync(
                OperationContext.Create(),
                completed.Fixture.PhysicalInstanceId));
        Assert.Contains("malformed canonical result artifact", malformedException.Message, StringComparison.Ordinal);

        var other = await CreateCompletedFixtureAsync(
            "process/repository-trace-other",
            "input",
            "output");
        var conflicting = Metadata(
            completed.Fixture,
            completed.Fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Completed,
            serializedOutput: completed.Fixture.Converter.Serialize(other.Result));
        var conflictingRepository = new DurableTaskProcessExecutionRepository(
            new FakeDurableTaskClient([conflicting]));

        var conflictException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await conflictingRepository.GetTracesAsync(
                OperationContext.Create(),
                completed.Fixture.PhysicalInstanceId));
        Assert.Contains("conflicts with custom status", conflictException.Message, StringComparison.Ordinal);
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

        var expectedTags = DurableTaskProcessTags.Create(fixture.Start.Receipt);
        var partialTags = expectedTags
            .Where(pair => pair.Key != DurableTaskProcessTags.DefinitionRevisionIdTagName)
            .ToDictionary();
        var partial = Metadata(
            fixture,
            fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Running,
            tags: partialTags);
        var partialRepository = new DurableTaskProcessExecutionRepository(new FakeDurableTaskClient([partial]));
        var partialException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await partialRepository.GetAsync(OperationContext.Create(), fixture.PhysicalInstanceId));
        Assert.Contains("partial canonical Process tag projection", partialException.Message, StringComparison.Ordinal);

        var conflictingTags = expectedTags.ToDictionary();
        conflictingTags[DurableTaskProcessTags.ProcessInstanceIdTagName] = "process-instance/conflict";
        var conflictingTagMetadata = Metadata(
            fixture,
            fixture.WaitingStatus,
            OrchestrationRuntimeStatus.Running,
            tags: conflictingTags);
        var conflictingTagRepository = new DurableTaskProcessExecutionRepository(
            new FakeDurableTaskClient([conflictingTagMetadata]));
        var tagException = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await conflictingTagRepository.GetAsync(OperationContext.Create(), fixture.PhysicalInstanceId));
        Assert.Contains(
            $"tag '{DurableTaskProcessTags.ProcessInstanceIdTagName}'",
            tagException.Message,
            StringComparison.Ordinal);
        Assert.Contains("conflicts with its retained start receipt", tagException.Message, StringComparison.Ordinal);
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
            start.Receipt.Request.Context.Authorization.AuthorityScope,
            start.Receipt.Request.InitialContinuation.ProcessInstanceId,
            DurableTaskProcessDataConverter.Create());
    }

    static async Task<CompletedTraceFixture> CreateCompletedFixtureAsync(
        string definitionId,
        string input,
        string output)
    {
        var provenance = new ExecutionProvenance(
            new("durable-task-trace-read-tests", "1"),
            new("tests/execution-kernel/durable-task-trace-reads"),
            DocumentOrigin.Generated);
        CanonicalProcessDefinition definition = new(
            ProcessDurabilityTestFixture.StringContract,
            ProcessDurabilityTestFixture.StringContract,
            new("return"),
            [new ReturnProcessNode(new("return"), Expr.Const(output))],
            ProcessRecoveryPolicy.ContinueAttempt);
        var document = ProcessDefinitionDocuments.Create(
            new(definitionId),
            new("revision/1"),
            definition,
            provenance);
        var compilation = ProcessStaticCompiler.Compile(
            document,
            new ProcessDefinitionValidationContext());
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Validation.Diagnostics));
        var plan = Assert.IsType<CompiledProcessPlan>(compilation.Plan);
        var continuation = new ProcessContinuationIdentity(
            new($"process-instance/{definitionId}"),
            new("process-attempt/1"));
        var scope = new InteractionAuthorityScope("authority/tests", "tenant/cohesive");
        var startedAtUtc = ProcessDurabilityTestFixture.AcceptedAtUtc;
        var start = new DurableTaskSequentialProcessStart(
            new ProcessStartReceipt(
                new(
                    ProcessStartRequest.CurrentSchemaVersion,
                    plan.DefinitionReference,
                    new(
                        new("command/start"),
                        new("idempotency/start"),
                        continuation.ProcessInstanceId,
                        new("test-runner", scope, "authorization/tests"),
                        startedAtUtc,
                        provenance),
                    continuation,
                    PortableValue.Concrete(
                        ProcessDurabilityTestFixture.StringContract,
                        ObservationValue.FromString(input))),
                startedAtUtc),
            new(
                scope,
                new("correlation/durable-task-trace-read-tests"),
                new(
                    InteractionDurabilityDemand.Durable,
                    InteractionVisibilityDemand.AfterOriginCommit),
                provenance));
        var result = await DurableTaskSequentialProcessInterpreter.RunAsync(
            plan,
            start,
            EmptyDurableRequestBindingResolver.Instance,
            static operation => throw new InvalidOperationException($"Unexpected host operation '{operation.Kind}'."),
            static invocation => throw new InvalidOperationException(
                $"Unexpected durable operation '{invocation.Request.Context.EmissionId.Value}'."),
            static invocation => throw new InvalidOperationException(
                $"Unexpected child Process '{invocation.Request.Context.EmissionId.Value}'."),
            static operation => throw new InvalidOperationException(
                $"Unexpected reconciliation '{operation.OperationId.Value}'."),
            static () => throw new InvalidOperationException("Unexpected interaction wait."),
            static (delay, cancellationToken) => throw new InvalidOperationException(
                $"Unexpected durable timer '{delay}' ({cancellationToken.CanBeCanceled})."),
            () => startedAtUtc.AddSeconds(1));
        var status = DurableTaskProcessStatus.Project(result);
        var fixture = new CurrentFixture(
            start,
            status,
            DurableTaskSequentialProcessIdentities.OrchestrationInstance(start),
            scope,
            continuation.ProcessInstanceId,
            DurableTaskProcessDataConverter.Create());
        return new(fixture, result);
    }

    static OrchestrationMetadata Metadata(
        CurrentFixture fixture,
        ExecutionStatus? status,
        OrchestrationRuntimeStatus runtimeStatus,
        string? serializedOutput = null,
        string? instanceId = null,
        string? serializedCustomStatus = null,
        IReadOnlyDictionary<string, string>? tags = null) => new(
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
                ?? (status is null ? null : fixture.Converter.Serialize(status)),
            Tags = tags ?? ImmutableDictionary<string, string>.Empty
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
        InteractionAuthorityScope Scope,
        ProcessInstanceId LogicalInstanceId,
        DataConverter Converter);

    sealed record CompletedTraceFixture(
        CurrentFixture Fixture,
        DurableTaskSequentialProcessResult Result);

    sealed class FakeDurableTaskClient(
        IReadOnlyList<OrchestrationMetadata> metadata,
        string? continuationToken = null) : DurableTaskClient("fake")
    {
        public OrchestrationQuery? LastQuery { get; private set; }

        public string? LastPageContinuationToken { get; private set; }

        public bool LastGetInputsAndOutputs { get; private set; }

        public string? LastGetInstanceId { get; private set; }

        public int GetCount { get; private set; }

        public int QueryCount { get; private set; }

        public int ScheduleCount { get; private set; }

        public StartOrchestrationOptions? LastStartOptions { get; private set; }

        public override Task<OrchestrationMetadata?> GetInstancesAsync(
            string instanceId,
            bool getInputsAndOutputs = false,
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            GetCount++;
            LastGetInstanceId = instanceId;
            LastGetInputsAndOutputs = getInputsAndOutputs;
            return Task.FromResult(metadata.FirstOrDefault(candidate =>
                string.Equals(candidate.InstanceId, instanceId, StringComparison.Ordinal)));
        }

        public override AsyncPageable<OrchestrationMetadata> GetAllInstancesAsync(
            OrchestrationQuery? filter = null)
        {
            QueryCount++;
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
            CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            ScheduleCount++;
            LastStartOptions = options;
            return Task.FromResult(options?.InstanceId ?? "generated-instance");
        }

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
