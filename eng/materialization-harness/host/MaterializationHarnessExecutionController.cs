using Cohesive.Adapters.Postgres;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Materialize;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Runtime;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Processes;
using Npgsql;
using ProcessStartResult = Cohesive.Execution.ProcessStartResult;

namespace Cohesive.MaterializationHarness.Host;

sealed class MaterializationHarnessExecutionController :
    IExecutionControlApiDispatcher,
    IProcessExecutionRepository,
    IProcessExecutionExplainRepository,
    IProcessExecutionTraceRepository,
    IMaterializationHarnessRunControl
{
    readonly HarnessHostOptions options;
    readonly PostgresProcessDurableStore processStore;
    readonly PostgresMaterializationStateStore materializationStore;
    readonly MaterializationRebuildProcessArtifacts artifacts;
    readonly ProcessDurableRuntime runtime;
    readonly ProcessStartReferenceEvaluator startEvaluator = new();
    readonly SemaphoreSlim workSignal = new(initialCount: 0, maxCount: 1);
    string? completedRunId;

    internal MaterializationHarnessExecutionController(
        NpgsqlDataSource dataSource,
        HarnessHostOptions options,
        ExecutionControlApiCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        processStore = new(
            dataSource: dataSource,
            options: new PostgresProcessDurableStoreOptions(
                authorityId: "materialization-harness/freight-rebuild"));
        materializationStore = new(
            dataSource: dataSource,
            options: new PostgresMaterializationStateStoreOptions(
                authorityId: "materialization-harness/freight-rebuild"));
        artifacts = MaterializationRebuildProcessFactory.Create();
        runtime = new(
            store: processStore,
            host: RejectingProcessHost.Instance,
            options: new(
                workerId: "worker/materialization-harness",
                workerLease: TimeSpan.FromMinutes(5),
                maxAmbiguousStoreMutationAttempts: 3));
    }

    public ExecutionControlApiCatalog Catalog { get; }

    internal async Task EnsureCreatedAsync(OperationContext context)
    {
        await processStore.EnsureCreatedAsync(context);
        await materializationStore.EnsureCreatedAsync(context);
    }

    internal ProcessStartRequest CreateStartRequest(
        ProcessAttemptId attemptId,
        DateTimeOffset issuedAtUtc)
    {
        var context = new ProcessControlCommandContext(
            commandId: new($"command/materialization-harness/start/{attemptId.Value}"),
            idempotencyKey: new($"idempotency/materialization-harness/start/{attemptId.Value}"),
            processInstanceId: options.ProcessInstanceId,
            authorization: Authorization(),
            issuedAtUtc: issuedAtUtc,
            provenance: Provenance("sdk-start"));
        return new(
            schemaVersion: ProcessStartRequest.CurrentSchemaVersion,
            definition: artifacts.CoordinatorPlan.DefinitionReference,
            context: context,
            initialContinuation: new(options.ProcessInstanceId, attemptId),
            input: PortableValue.Concrete(
                artifacts.CoordinatorPlan.Definition.Input,
                ObservationValue.FromString("freight/order-search")));
    }

    internal async Task<ExecutionApiDispatchResult> DispatchOperatorAsync(
        ApiEndpoint endpoint,
        DateTimeOffset issuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EnsureOwned(endpoint);
        if (ReferenceEquals(endpoint, Catalog.Start))
            throw new ArgumentException("Use CreateStartRequest for Process start admission.", nameof(endpoint));

        var suffix = $"{endpoint.Operation.Name}/{issuedAtUtc:yyyyMMddHHmmssfffffff}";
        var commandContext = new ProcessControlCommandContext(
            commandId: new($"command/materialization-harness/{suffix}"),
            idempotencyKey: new($"idempotency/materialization-harness/{suffix}"),
            processInstanceId: options.ProcessInstanceId,
            authorization: Authorization(),
            issuedAtUtc: issuedAtUtc,
            provenance: Provenance($"sdk-{endpoint.Operation.Name}"));
        ProcessControlCommand request;
        if (ReferenceEquals(endpoint, Catalog.Inspect)
            || ReferenceEquals(endpoint, Catalog.Explain)
            || ReferenceEquals(endpoint, Catalog.Traces))
        {
            request = new InspectProcessCommand(
                schemaVersion: ProcessControlCommand.CurrentSchemaVersion,
                context: commandContext);
        }
        else
        {
            var snapshot = await processStore.LoadAsync(
                OperationContext.Create(),
                options.ProcessInstanceId)
                ?? throw new InvalidOperationException("Start the materialization Process before controlling it.");
            var state = snapshot.Checkpoint.Control;
            var expectation = new ProcessControlExpectation(
                continuation: new(options.ProcessInstanceId, state.CurrentAttempt.AttemptId),
                revision: state.Revision);
            request = ReferenceEquals(endpoint, Catalog.Pause)
                ? new PauseProcessCommand(
                    schemaVersion: ProcessControlCommand.CurrentSchemaVersion,
                    context: commandContext,
                    expectation: expectation)
                : ReferenceEquals(endpoint, Catalog.Continue)
                    ? new ContinueProcessCommand(
                        schemaVersion: ProcessControlCommand.CurrentSchemaVersion,
                        context: commandContext,
                        expectation: expectation)
                    : ReferenceEquals(endpoint, Catalog.RestartAttempt)
                        ? new RestartProcessAttemptCommand(
                            schemaVersion: ProcessControlCommand.CurrentSchemaVersion,
                            context: commandContext,
                            expectation: expectation,
                            plan: new(
                                newAttemptId: new($"attempt/{issuedAtUtc:yyyyMMddHHmmssfffffff}"),
                                cleanup: ProcessAttemptCleanupRequirement.AbandonAffinitiesAndReleaseResources,
                                reason: new("operator.materialization-rebuild-restart")))
                        : ReferenceEquals(endpoint, Catalog.Cancel)
                            ? new CancelProcessCommand(
                                schemaVersion: ProcessControlCommand.CurrentSchemaVersion,
                                context: commandContext,
                                expectation: expectation,
                                reason: new("operator.materialization-rebuild-cancel"))
                            : throw new NotSupportedException(
                                $"The local SDK helper does not construct '{endpoint.Operation.Name}'.");
        }

        var invocation = new ExecutionApiInvocationContext(
            authorization: Authorization(),
            provenance: Provenance($"sdk-{endpoint.Operation.Name}"),
            issuedAtUtc: issuedAtUtc,
            observedAtUtc: issuedAtUtc,
            grantedRequirements:
            [
                .. endpoint.Operation.AuthorizationRequirements.Select(static requirement => requirement.Id)
            ]);
        return await DispatchAsync(
            context: OperationContext.Create(),
            endpoint: endpoint,
            request: request,
            invocation: invocation);
    }

    public async ValueTask<ExecutionApiDispatchResult> DispatchAsync(
        OperationContext context,
        ApiEndpoint endpoint,
        object request,
        ExecutionApiInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(invocation);
        context.ThrowIfCancellationRequested();
        EnsureOwned(endpoint);
        if (!IsAuthorized(endpoint, invocation))
            return Problem(endpoint, ApiResultKind.Forbidden, ExecutionApiProblemCodes.Forbidden);

        if (ReferenceEquals(endpoint, Catalog.Start))
        {
            return request is ProcessStartRequest start
                ? await StartAsync(context, start, invocation)
                : Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
        }
        if (ReferenceEquals(endpoint, Catalog.Inspect))
        {
            return request is InspectProcessCommand inspect
                ? await InspectAsync(context, inspect, invocation)
                : Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
        }
        if (ReferenceEquals(endpoint, Catalog.Explain))
        {
            if (request is not InspectProcessCommand explain)
                return Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
            var artifact = await GetExplainAsync(
                context,
                invocation.Authorization.AuthorityScope,
                explain.Context.ProcessInstanceId);
            return artifact is null
                ? Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound)
                : Result(endpoint, ApiResultKind.Success, artifact);
        }
        if (ReferenceEquals(endpoint, Catalog.Traces))
        {
            if (request is not InspectProcessCommand traces)
                return Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
            var read = await GetTracesAsync(
                context,
                invocation.Authorization.AuthorityScope,
                traces.Context.ProcessInstanceId);
            var resultKind = ExecutionControlApiCatalog.TraceResultKind(read.State);
            return read.Artifact is null
                ? Problem(endpoint, resultKind, ExecutionApiProblemCodes.ForTraceReadState(read.State))
                : Result(endpoint, resultKind, read.Artifact);
        }
        if (ReferenceEquals(endpoint, Catalog.Pause)
            || ReferenceEquals(endpoint, Catalog.Continue)
            || ReferenceEquals(endpoint, Catalog.RestartAttempt)
            || ReferenceEquals(endpoint, Catalog.Cancel))
        {
            return request is ProcessControlCommand command
                ? await ControlAsync(context, endpoint, command, invocation)
                : Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
        }

        return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
    }

    public async ValueTask BeforePageAsync(
        OperationContext context,
        string provider,
        string tenant,
        int pageOrdinal)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        if (pageOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(pageOrdinal), pageOrdinal, "A page ordinal cannot be negative.");

        if (options.PageDelay > TimeSpan.Zero)
            await Task.Delay(options.PageDelay, context.CancellationToken);
        var snapshot = await processStore.LoadAsync(context, options.ProcessInstanceId);
        if (snapshot is null)
            throw new MaterializationHarnessRunSuspendedException("The durable Process instance no longer exists.");
        var control = snapshot.Checkpoint.Control;
        if (control.CurrentAttempt.AttemptId.Value is not { } attempt
            || !string.Equals(attempt, CurrentRunId, StringComparison.Ordinal))
        {
            throw new MaterializationHarnessRunSuspendedException(
                "The materialization attempt was superseded at a page boundary.");
        }
        if (control.IsTerminal)
            throw new OperationCanceledException("The materialization Process is terminal.");
        if (control.Mode != ProcessControlMode.Running)
        {
            throw new MaterializationHarnessRunSuspendedException(
                $"The materialization Process is blocked in '{control.Mode}' mode.");
        }
    }

    string? CurrentRunId { get; set; }

    internal async Task RunCurrentAttemptAsync(CancellationToken cancellationToken)
    {
        var context = OperationContext.Create(cancellationToken: cancellationToken);
        var snapshot = await processStore.LoadAsync(context, options.ProcessInstanceId);
        if (snapshot is null
            || snapshot.Checkpoint.Control.Mode != ProcessControlMode.Running
            || snapshot.Checkpoint.Control.IsTerminal)
        {
            return;
        }

        var runId = snapshot.Checkpoint.Control.CurrentAttempt.AttemptId.Value;
        if (string.Equals(runId, completedRunId, StringComparison.Ordinal))
            return;
        CurrentRunId = runId;
        try
        {
            await Cohesive.MaterializationHarness.Materialize.Program.RunAsync(new(
                runId: CurrentRunId,
                startedAtUtc: snapshot.Checkpoint.Control.CurrentAttempt.StartedAtUtc,
                control: this,
                progressStore: materializationStore,
                progressOwner: "worker/materialization-harness",
                cancellationToken: cancellationToken));
            completedRunId = CurrentRunId;
        }
        finally
        {
            CurrentRunId = null;
        }
    }

    internal async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        await workSignal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
    }

    async ValueTask<ExecutionApiDispatchResult> StartAsync(
        OperationContext context,
        ProcessStartRequest request,
        ExecutionApiInvocationContext invocation)
    {
        if (request.InitialContinuation.ProcessInstanceId != options.ProcessInstanceId
            || request.Definition != artifacts.CoordinatorPlan.DefinitionReference)
        {
            return Problem(Catalog.Start, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
        }

        var existing = await processStore.LoadAsync(context, options.ProcessInstanceId);
        var prior = existing?.Checkpoint.Start;
        var canonical = new ProcessStartRequest(
            schemaVersion: request.SchemaVersion,
            definition: request.Definition,
            context: Rebind(request.Context, invocation, prior?.Request.Context),
            initialContinuation: request.InitialContinuation,
            input: request.Input);
        var evidence = new ProcessStartRegistryEvidence(
            sameCommandIdentity: prior?.Request.Context.CommandId == canonical.Context.CommandId ? prior : null,
            sameIdempotencyKey: prior?.Request.Context.IdempotencyKey == canonical.Context.IdempotencyKey ? prior : null,
            existingInstanceReceipt: prior,
            existingInstanceState: existing?.Checkpoint.Control);
        var decision = startEvaluator.Evaluate(canonical, evidence, invocation.ObservedAtUtc);
        if (!decision.RequiresPersistence)
        {
            var kind = decision.Result.IsConflict ? ApiResultKind.Conflict : ApiResultKind.Success;
            return Result(Catalog.Start, kind, decision.Result);
        }

        var receipt = decision.Receipt
            ?? throw new InvalidOperationException("An accepted Process start returned no durable receipt.");
        var initialized = await runtime.InitializeAsync(context, artifacts.CoordinatorPlan, receipt);
        var result = initialized.Disposition switch
        {
            ProcessDurableRuntimeDisposition.Applied => ProcessStartResult.Accepted(receipt),
            ProcessDurableRuntimeDisposition.Replayed => ProcessStartResult.Replayed(
                initialized.Snapshot?.Checkpoint.Start ?? receipt),
            _ => ProcessStartResult.Conflict(ProcessStartDisposition.InstanceConflict)
        };
        if (!result.IsConflict)
            SignalWork();
        return Result(
            Catalog.Start,
            result.IsConflict ? ApiResultKind.Conflict : ApiResultKind.Success,
            result);
    }

    async ValueTask<ExecutionApiDispatchResult> InspectAsync(
        OperationContext context,
        InspectProcessCommand request,
        ExecutionApiInvocationContext invocation)
    {
        if (request.Context.ProcessInstanceId != options.ProcessInstanceId)
            return Problem(Catalog.Inspect, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        var snapshot = await processStore.LoadAsync(context, options.ProcessInstanceId);
        if (snapshot is null || snapshot.Checkpoint.Control.AuthorityScope != invocation.Authorization.AuthorityScope)
            return Problem(Catalog.Inspect, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        return Result(
            Catalog.Inspect,
            ApiResultKind.Success,
            new ExecutionControlResult(
                ProcessControlDecisionDisposition.Inspected,
                ProcessDurableExecutionStatusProjector.Project(snapshot.Checkpoint)));
    }

    async ValueTask<ExecutionApiDispatchResult> ControlAsync(
        OperationContext context,
        ApiEndpoint endpoint,
        ProcessControlCommand request,
        ExecutionApiInvocationContext invocation)
    {
        if (request.Context.ProcessInstanceId != options.ProcessInstanceId)
            return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        var loaded = await processStore.LoadAsync(context, options.ProcessInstanceId);
        if (loaded is null || loaded.Checkpoint.Control.AuthorityScope != invocation.Authorization.AuthorityScope)
            return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);

        var prior = FindPriorCommand(loaded.Checkpoint.Control, request.Context);
        var canonical = Rebind(request, invocation, prior);
        var supersededRunId = loaded.Checkpoint.Control.CurrentAttempt.AttemptId.Value;
        ProcessDurableControlResult durable;
        if (canonical is CancelProcessCommand cancel)
        {
            durable = await runtime.CancelAsync(
                context,
                artifacts.CoordinatorPlan,
                cancel,
                new(
                    authorityScope: invocation.Authorization.AuthorityScope,
                    correlationId: new($"correlation/{cancel.Context.CommandId.Value}"),
                    delivery: new(
                        InteractionDurabilityDemand.Durable,
                        InteractionVisibilityDemand.AfterOriginCommit),
                    provenance: invocation.Provenance));
        }
        else
        {
            durable = await runtime.ApplyControlAsync(context, artifacts.CoordinatorPlan, canonical);
        }

        if (durable.Disposition == ProcessDurableRuntimeDisposition.NotFound)
            return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        if (durable.Decision is null)
        {
            var retained = durable.Snapshot?.Checkpoint ?? loaded.Checkpoint;
            var rejected = new ExecutionControlResult(
                ProcessControlDecisionDisposition.InvalidState,
                ProcessDurableExecutionStatusProjector.Project(retained),
                receipt: null,
                diagnosticCodes: [ProcessDurableRuntimeDiagnosticCodes.ActivationLifecycleBlocked]);
            return Result(endpoint, rejected.ResultKind, rejected);
        }

        var result = ExecutionControlResult.FromDecision(
            durable.Decision,
            ExecutionRuntimeStatusDetails.Unknown);
        if (durable.Disposition is ProcessDurableRuntimeDisposition.Applied
            or ProcessDurableRuntimeDisposition.Replayed)
        {
            if (canonical is RestartProcessAttemptCommand or CancelProcessCommand)
            {
                await Cohesive.MaterializationHarness.Materialize.Program.AbandonRunAsync(
                    runId: supersededRunId,
                    abandonedAtUtc: canonical.Context.IssuedAtUtc,
                    cancellationToken: context.CancellationToken);
            }
            SignalWork();
        }
        return Result(endpoint, result.ResultKind, result);
    }

    public ValueTask<ProcessExecutionRecord?> GetAsync(OperationContext context, string processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        return GetAsync(context, options.AuthorityScope, new(processId));
    }

    public async ValueTask<ProcessExecutionRecord?> GetAsync(
        OperationContext context,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorityScope);
        if (processInstanceId != options.ProcessInstanceId || authorityScope != options.AuthorityScope)
            return null;
        var snapshot = await processStore.LoadAsync(context, processInstanceId);
        if (snapshot is null || snapshot.Checkpoint.Control.AuthorityScope != authorityScope)
            return null;
        var checkpoint = snapshot.Checkpoint;
        var status = ProcessDurableExecutionStatusProjector.Project(checkpoint);
        return new(
            ProcessId: processInstanceId.Value,
            ProcessName: checkpoint.Definition.DefinitionId.Value,
            Status: ExecutionStatus(checkpoint.Control),
            StartedAtUtc: checkpoint.CreatedAtUtc,
            UpdatedAtUtc: checkpoint.UpdatedAtUtc,
            CompletedAtUtc: checkpoint.Control.IsTerminal ? checkpoint.Control.UpdatedAtUtc : null,
            RuntimeStatus: status,
            Definition: checkpoint.Definition);
    }

    public ValueTask<ProcessExecutionQueryResult> QueryAsync(
        OperationContext context,
        ProcessExecutionQuery query) =>
        throw new NotSupportedException(
            "The single-instance local materialization harness does not expose a process execution index.");

    public ValueTask<ExecutionExplainArtifact?> GetExplainAsync(
        OperationContext context,
        string processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        return GetExplainAsync(context, options.AuthorityScope, new(processId));
    }

    public async ValueTask<ExecutionExplainArtifact?> GetExplainAsync(
        OperationContext context,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorityScope);
        if (authorityScope != options.AuthorityScope || processInstanceId != options.ProcessInstanceId)
            return null;
        var snapshot = await processStore.LoadAsync(context, processInstanceId);
        if (snapshot is null || snapshot.Checkpoint.Control.AuthorityScope != authorityScope)
            return null;
        var projection = ProcessDurableExecutionExplainProjector.Project(
            compilation: artifacts.CoordinatorCompilation,
            checkpoint: snapshot.Checkpoint);
        return projection.Artifact
            ?? throw new InvalidOperationException(
                $"The retained Process checkpoint could not be explained: {string.Join(", ", projection.Validation.Diagnostics.Select(static diagnostic => diagnostic.Code))}.");
    }

    public ValueTask<ProcessExecutionTraceReadResult> GetTracesAsync(
        OperationContext context,
        string processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        return GetTracesAsync(context, options.AuthorityScope, new(processId));
    }

    public async ValueTask<ProcessExecutionTraceReadResult> GetTracesAsync(
        OperationContext context,
        InteractionAuthorityScope authorityScope,
        ProcessInstanceId processInstanceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorityScope);
        if (authorityScope != options.AuthorityScope || processInstanceId != options.ProcessInstanceId)
            return ProcessExecutionTraceReadResult.NotFound();
        var snapshot = await processStore.LoadAsync(context, processInstanceId);
        if (snapshot is null || snapshot.Checkpoint.Control.AuthorityScope != authorityScope)
            return ProcessExecutionTraceReadResult.NotFound();
        if (!snapshot.Checkpoint.Control.IsTerminal)
            return ProcessExecutionTraceReadResult.InProgress();
        if (snapshot.Checkpoint.Activations.IsDefaultOrEmpty)
            return ProcessExecutionTraceReadResult.TerminalArtifactUnavailable();

        var traces = ProcessDurableExecutionTraceProjector.Project(snapshot.Checkpoint);
        var failures = traces.Where(static result => !result.IsSuccessful).ToArray();
        if (failures.Length != 0)
        {
            throw new InvalidOperationException(
                $"The retained Process traces could not be projected: {string.Join(", ", failures.SelectMany(static result => result.Validation.Diagnostics).Select(static diagnostic => diagnostic.Code))}.");
        }
        return ProcessExecutionTraceReadResult.Available(new(
            schemaVersion: ProcessExecutionTraceArtifact.CurrentSchemaVersion,
            definition: snapshot.Checkpoint.Definition,
            processInstanceId: processInstanceId,
            missingTracePrefixCount: 0,
            traces: [.. traces.Select(static result => result.Trace!)]));
    }

    void SignalWork()
    {
        if (workSignal.CurrentCount == 0)
            workSignal.Release();
    }

    ProcessControlAuthorizationContext Authorization() => new(
        actor: "operator/materialization-harness",
        authorityScope: options.AuthorityScope,
        evidenceReference: "policy/materialization-harness/local-allow");

    static ExecutionProvenance Provenance(string source) => new(
        new("cohesive-materialization-harness", "1"),
        new($"eng/materialization-harness/host/{source}"),
        DocumentOrigin.Generated);

    static ProcessExecutionStatus ExecutionStatus(ProcessControlState state)
    {
        if (state.IsTerminal)
        {
            return state.Mode switch
            {
                ProcessControlMode.Cancelled => ProcessExecutionStatus.Cancelled,
                ProcessControlMode.Terminated => ProcessExecutionStatus.Terminated,
                _ => throw new ArgumentOutOfRangeException(nameof(state), state.Mode, "Unsupported terminal mode.")
            };
        }
        return state.Mode == ProcessControlMode.Paused
            ? ProcessExecutionStatus.Suspended
            : ProcessExecutionStatus.Running;
    }

    bool IsAuthorized(ApiEndpoint endpoint, ExecutionApiInvocationContext invocation)
    {
        foreach (var requirement in endpoint.Operation.AuthorizationRequirements)
        {
            if (!invocation.GrantedRequirements.Contains(requirement.Id, StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    void EnsureOwned(ApiEndpoint endpoint)
    {
        if (!ReferenceEquals(endpoint, Catalog.Start)
            && !ReferenceEquals(endpoint, Catalog.Inspect)
            && !ReferenceEquals(endpoint, Catalog.Explain)
            && !ReferenceEquals(endpoint, Catalog.Traces)
            && !ReferenceEquals(endpoint, Catalog.Pause)
            && !ReferenceEquals(endpoint, Catalog.Continue)
            && !ReferenceEquals(endpoint, Catalog.RestartAttempt)
            && !ReferenceEquals(endpoint, Catalog.Cancel)
            && !ReferenceEquals(endpoint, Catalog.UpdateLimits))
        {
            throw new InvalidOperationException("The endpoint handle is not owned by this materialization host.");
        }
    }

    ExecutionApiDispatchResult Problem(ApiEndpoint endpoint, ApiResultKind kind, string code) =>
        Result(endpoint, kind, new ExecutionApiProblem(code));

    ExecutionApiDispatchResult Result(ApiEndpoint endpoint, ApiResultKind kind, object body) =>
        new(endpoint, Catalog.GetResult(endpoint, kind), body);

    static ProcessControlCommand? FindPriorCommand(
        ProcessControlState state,
        ProcessControlCommandContext context)
    {
        if (state.FindReceipt(context.CommandId) is { } sameCommand)
            return sameCommand.Command;
        return state.Receipts
            .Select(static receipt => receipt.Command)
            .FirstOrDefault(candidate => candidate.Context.IdempotencyKey == context.IdempotencyKey);
    }

    static ProcessControlCommand Rebind(
        ProcessControlCommand command,
        ExecutionApiInvocationContext invocation,
        ProcessControlCommand? prior)
    {
        var context = Rebind(command.Context, invocation, prior?.Context);
        return command switch
        {
            InspectProcessCommand inspect => new InspectProcessCommand(
                schemaVersion: inspect.SchemaVersion,
                context: context,
                expectation: inspect.Expectation),
            PauseProcessCommand pause => new PauseProcessCommand(
                schemaVersion: pause.SchemaVersion,
                context: context,
                expectation: pause.Expectation!),
            ContinueProcessCommand continueProcess => new ContinueProcessCommand(
                schemaVersion: continueProcess.SchemaVersion,
                context: context,
                expectation: continueProcess.Expectation!),
            RestartProcessAttemptCommand restart => new RestartProcessAttemptCommand(
                schemaVersion: restart.SchemaVersion,
                context: context,
                expectation: restart.Expectation!,
                plan: restart.Plan),
            CancelProcessCommand cancel => new CancelProcessCommand(
                schemaVersion: cancel.SchemaVersion,
                context: context,
                expectation: cancel.Expectation!,
                reason: cancel.Reason),
            _ => throw new ArgumentOutOfRangeException(
                nameof(command),
                command.GetType(),
                "Unsupported materialization-host command.")
        };
    }

    static ProcessControlCommandContext Rebind(
        ProcessControlCommandContext context,
        ExecutionApiInvocationContext invocation,
        ProcessControlCommandContext? prior) => new(
        commandId: context.CommandId,
        idempotencyKey: context.IdempotencyKey,
        processInstanceId: context.ProcessInstanceId,
        authorization: prior?.Authorization ?? invocation.Authorization,
        issuedAtUtc: prior?.IssuedAtUtc ?? invocation.IssuedAtUtc,
        provenance: prior?.Provenance ?? invocation.Provenance);

    sealed class RejectingProcessHost : IProcessReferenceHost
    {
        internal static RejectingProcessHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}
