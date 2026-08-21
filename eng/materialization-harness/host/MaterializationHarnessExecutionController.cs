using System.Collections.Immutable;
using Cohesive.Adapters.Postgres;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Control;
using Cohesive.MaterializationHarness.Materialize;
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
    IAsyncDisposable
{
    const string ProcessDispositionDiagnosticPrefix = "materialization-harness.process-disposition";
    const int ProviderMismatchFailureThreshold = 2;

    readonly NpgsqlDataSource dataSource;
    readonly HarnessHostOptions options;
    readonly PostgresMaterializationStateStore materializationStore;
    readonly ProcessStartReferenceEvaluator startEvaluator = new();
    readonly SemaphoreSlim workSignal = new(initialCount: 0, maxCount: 1);
    readonly string workerIncarnation = $"host-{Guid.NewGuid():N}";
    ImmutableDictionary<string, MaterializationHarnessProviderProcess> processesByProvider =
        ImmutableDictionary<string, MaterializationHarnessProviderProcess>.Empty.WithComparers(StringComparer.Ordinal);
    ImmutableDictionary<ProcessInstanceId, MaterializationHarnessProviderProcess> processesByInstance =
        ImmutableDictionary<ProcessInstanceId, MaterializationHarnessProviderProcess>.Empty;
    ImmutableDictionary<string, ProviderExecutionState> executionStates =
        ImmutableDictionary<string, ProviderExecutionState>.Empty.WithComparers(StringComparer.Ordinal);
    ImmutableDictionary<string, ImmutableArray<string>> previousProviderMismatch =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty.WithComparers(StringComparer.Ordinal);
    int providerMismatchObservationCount;
    FreightOrderRebuildRuntimeCatalog? runtimeCatalog;

    internal MaterializationHarnessExecutionController(
        NpgsqlDataSource dataSource,
        HarnessHostOptions options,
        ExecutionControlApiCatalog catalog)
    {
        this.dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        materializationStore = new(
            dataSource: dataSource,
            options: new PostgresMaterializationStateStoreOptions(
                authorityId: "materialization-harness/freight-rebuild/state"));
    }

    public ExecutionControlApiCatalog Catalog { get; }

    internal ImmutableArray<string> Providers => [.. processesByProvider.Keys.Order(StringComparer.Ordinal)];

    internal async Task EnsureCreatedAsync(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        await materializationStore.EnsureCreatedAsync(context).ConfigureAwait(false);
        runtimeCatalog = await FreightOrderRebuildRuntimeCatalog.CreateAsync(
                dataSource: dataSource,
                stateStore: materializationStore,
                authorityScope: options.AuthorityScope,
                cancellationToken: context.CancellationToken)
            .ConfigureAwait(false);
        var byProvider = ImmutableDictionary.CreateBuilder<string, MaterializationHarnessProviderProcess>(
            StringComparer.Ordinal);
        var byInstance = ImmutableDictionary.CreateBuilder<ProcessInstanceId, MaterializationHarnessProviderProcess>();
        var states = ImmutableDictionary.CreateBuilder<string, ProviderExecutionState>(StringComparer.Ordinal);
        if (options.BoundaryFaultPlan is { } faultPlan
            && !runtimeCatalog.Providers.Any(provider => string.Equals(
                provider.Provider,
                faultPlan.Provider,
                StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"The materialization boundary fault plan selects unknown provider '{faultPlan.Provider}'.");
        }
        foreach (var provider in runtimeCatalog.Providers)
        {
            var processStore = new PostgresProcessDurableStore(
                dataSource: dataSource,
                options: new(
                    authorityId: $"{options.ProcessInstance(provider.Provider).Value}/durability"));
            await processStore.EnsureCreatedAsync(context).ConfigureAwait(false);
            var process = await MaterializationHarnessProviderProcess.CreateAsync(
                    provider: provider,
                    processInstanceId: options.ProcessInstance(provider.Provider),
                    dataSource: dataSource,
                    processStore: processStore,
                    materializationStore: materializationStore,
                    authorityScope: options.AuthorityScope,
                    workerIncarnation: workerIncarnation,
                    boundaryObserver: MaterializationHarnessBoundaryObserver.Create(
                        provider: provider.Provider,
                        delay: options.OperationBoundaryDelay,
                        faultPlan: options.BoundaryFaultPlan),
                    context: context)
                .ConfigureAwait(false);
            byProvider.Add(provider.Provider, process);
            byInstance.Add(process.ProcessInstanceId, process);
            states.Add(provider.Provider, new());
        }
        processesByProvider = byProvider.ToImmutable();
        processesByInstance = byInstance.ToImmutable();
        executionStates = states.ToImmutable();
    }

    internal ProcessStartRequest CreateStartRequest(
        string provider,
        ProcessAttemptId attemptId,
        DateTimeOffset issuedAtUtc) => GetProvider(provider).CreateStartRequest(attemptId, issuedAtUtc);

    internal async Task<ExecutionApiDispatchResult> DispatchStartAsync(
        string provider,
        DateTimeOffset issuedAtUtc)
    {
        var suffix = issuedAtUtc.ToString(
            "yyyyMMddHHmmssfffffff",
            System.Globalization.CultureInfo.InvariantCulture);
        var request = CreateStartRequest(
            provider: provider,
            attemptId: new($"attempt/{provider}/{suffix}"),
            issuedAtUtc: issuedAtUtc);
        var process = GetProvider(provider);
        return await DispatchAsync(
            context: OperationContext.Create(),
            endpoint: Catalog.Start,
            request: request,
            invocation: Invocation(Catalog.Start, process, issuedAtUtc));
    }

    internal async Task<MaterializationHarnessControlRequestProjection> ProjectControlRequestAsync(
        string provider,
        string operation,
        long? maximumBatchItems,
        DateTimeOffset issuedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        var endpoint = ResolveCommandEndpoint(operation);
        object request;
        if (ReferenceEquals(endpoint, Catalog.Start))
        {
            var suffix = issuedAtUtc.ToString(
                "yyyyMMddHHmmssfffffff",
                System.Globalization.CultureInfo.InvariantCulture);
            request = CreateStartRequest(
                provider: provider,
                attemptId: new($"attempt/{provider}/{suffix}"),
                issuedAtUtc: issuedAtUtc);
        }
        else if (ReferenceEquals(endpoint, Catalog.UpdateLimits))
        {
            request = await CreateLimitUpdateRequestAsync(
                    provider: provider,
                    maximumBatchItems: maximumBatchItems
                        ?? throw new ArgumentException(
                            "The updateLimits request projection requires maximumBatchItems.",
                            nameof(maximumBatchItems)),
                    issuedAtUtc: issuedAtUtc)
                .ConfigureAwait(false);
        }
        else
        {
            if (maximumBatchItems.HasValue)
            {
                throw new ArgumentException(
                    "Only updateLimits accepts maximumBatchItems.",
                    nameof(maximumBatchItems));
            }
            request = await CreateOperatorRequestAsync(
                    provider: provider,
                    endpoint: endpoint,
                    issuedAtUtc: issuedAtUtc)
                .ConfigureAwait(false);
        }

        return new(
            Operation: endpoint.Operation.Name,
            Method: "POST",
            Route: MaterializationHarnessExecutionRoutes.Command(endpoint.Operation.Name),
            Request: request);
    }

    internal async Task<ExecutionApiDispatchResult> DispatchOperatorAsync(
        string provider,
        ApiEndpoint endpoint,
        DateTimeOffset issuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EnsureOwned(endpoint);
        if (ReferenceEquals(endpoint, Catalog.Start))
            throw new ArgumentException("Use CreateStartRequest for Process start admission.", nameof(endpoint));
        if (ReferenceEquals(endpoint, Catalog.UpdateLimits))
            throw new ArgumentException("Use DispatchLimitUpdateAsync for Control limit updates.", nameof(endpoint));
        var request = await CreateOperatorRequestAsync(
                provider: provider,
                endpoint: endpoint,
                issuedAtUtc: issuedAtUtc)
            .ConfigureAwait(false);
        var process = GetProvider(provider);
        return await DispatchAsync(
            context: OperationContext.Create(),
            endpoint: endpoint,
            request: request,
            invocation: Invocation(endpoint, process, issuedAtUtc));
    }

    async Task<ProcessControlCommand> CreateOperatorRequestAsync(
        string provider,
        ApiEndpoint endpoint,
        DateTimeOffset issuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        EnsureOwned(endpoint);
        var process = GetProvider(provider);
        var suffix = $"{endpoint.Operation.Name}/{issuedAtUtc:yyyyMMddHHmmssfffffff}";
        var commandContext = new ProcessControlCommandContext(
            commandId: new($"command/materialization-harness/{provider}/{suffix}"),
            idempotencyKey: new($"idempotency/materialization-harness/{provider}/{suffix}"),
            processInstanceId: process.ProcessInstanceId,
            authorization: process.Authorization(),
            issuedAtUtc: issuedAtUtc,
            provenance: process.Provenance($"sdk-{endpoint.Operation.Name}"));
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
            var snapshot = await process.LoadAsync(OperationContext.Create()).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Start the {provider} materialization Process before controlling it.");
            var state = snapshot.Checkpoint.Control;
            var expectation = new ProcessControlExpectation(
                continuation: snapshot.Checkpoint.ContinuationIdentity,
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
                                newAttemptId: new($"attempt/{provider}/{issuedAtUtc:yyyyMMddHHmmssfffffff}"),
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

        return request;
    }

    internal async Task<ExecutionApiDispatchResult> DispatchLimitUpdateAsync(
        string provider,
        long maximumBatchItems,
        DateTimeOffset issuedAtUtc)
    {
        var command = await CreateLimitUpdateRequestAsync(
                provider: provider,
                maximumBatchItems: maximumBatchItems,
                issuedAtUtc: issuedAtUtc)
            .ConfigureAwait(false);
        var process = GetProvider(provider);
        return await DispatchAsync(
            context: OperationContext.Create(),
            endpoint: Catalog.UpdateLimits,
            request: command,
            invocation: Invocation(Catalog.UpdateLimits, process, issuedAtUtc));
    }

    async Task<ControlLimitUpdateCommand> CreateLimitUpdateRequestAsync(
        string provider,
        long maximumBatchItems,
        DateTimeOffset issuedAtUtc)
    {
        if (maximumBatchItems <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBatchItems),
                maximumBatchItems,
                "Batch items must be positive.");
        }
        var process = GetProvider(provider);
        var context = OperationContext.Create();
        var execution = await process.ResolveCurrentExecutionAsync(context).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The {provider} rebuild has not allocated a generation.");
        var snapshot = (await process.Provider.ControlRuntimeProvider
                .ForGeneration(execution.Generation)
                .GetSnapshotsAsync(context)
                .ConfigureAwait(false))
            .Single(static candidate =>
                candidate.Key.Workload == MaterializationIndexSyncWorkloadKind.Rebuild);
        var requested = snapshot.State.OperatingPoint.With(new(
            actuator: ControlActuatorKind.BatchItems,
            quantity: new(maximumBatchItems, ControlUnit.Count)));
        var suffix = issuedAtUtc.ToString("yyyyMMddHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture);
        return new ControlLimitUpdateCommand(
            schemaVersion: ControlLoopDefinition.CurrentSchemaVersion,
            commandId: new($"command/materialization-harness/{provider}/update-limits/{suffix}"),
            idempotencyKey: new($"idempotency/materialization-harness/{provider}/update-limits/{suffix}"),
            loopId: snapshot.State.LoopId,
            definitionFingerprint: snapshot.State.DefinitionFingerprint,
            target: snapshot.State.Target,
            epoch: snapshot.State.Epoch,
            expectedRevision: snapshot.State.Revision,
            requestedOperatingPoint: requested,
            authorization: process.Authorization(),
            issuedAtUtc: issuedAtUtc,
            provenance: process.Provenance("sdk-update-limits"));
    }

    internal async Task<MaterializationHarnessFailureEvidence> CaptureFailureEvidenceAsync(
        string provider,
        MaterializationGenerationId? selectedGeneration,
        OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var process = GetProvider(provider);
        var snapshot = await process.LoadAsync(context).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The {provider} materialization Process has not been started.");
        var execution = await process.ResolveCurrentExecutionAsync(context).ConfigureAwait(false);
        var generation = selectedGeneration ?? execution?.Generation;
        var target = await process.Provider.ResolvedPlan.Target.InspectAsync(context).ConfigureAwait(false);
        var candidate = generation is { } exactGeneration
            ? await process.Provider.ResolvedPlan.Target.InspectGenerationAsync(context, exactGeneration)
                .ConfigureAwait(false)
            : null;
        var progress = ImmutableArray<MaterializationHarnessProgressEvidence>.Empty;
        var controlEpochs = ImmutableArray<string>.Empty;
        if (generation is { } progressGeneration)
        {
            controlEpochs =
            [
                .. (await process.Provider.ControlRuntimeProvider
                        .ForGeneration(progressGeneration)
                        .GetSnapshotsAsync(context)
                        .ConfigureAwait(false))
                    .Select(static snapshot => snapshot.State.Epoch.Value)
                    .Order(StringComparer.Ordinal)
            ];
            var scopes = process.Provider.Compilation.Plan.Shards.Select(static shard => shard.Scope)
                .Concat(process.Provider.Compilation.Plan.ChangeFeeds.Select(static feed => feed.Scope))
                .Distinct()
                .OrderBy(static scope => scope.Input.Value, StringComparer.Ordinal)
                .ThenBy(static scope => scope.Partition.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            var progressBuilder = ImmutableArray.CreateBuilder<MaterializationHarnessProgressEvidence>(scopes.Length);
            foreach (var scope in scopes)
            {
                var key = new MaterializationProgressKey(
                    materialization: process.Provider.Compilation.Plan.Materialization.Definition.Id,
                    definitionFingerprint: process.Provider.Compilation.Plan.Materialization.DefinitionFingerprint,
                    generation: progressGeneration,
                    scope: scope);
                var retained = await process.Provider.ResolvedPlan.ProgressStore.LoadAsync(context, key)
                    .ConfigureAwait(false);
                progressBuilder.Add(MaterializationHarnessProgressEvidence.From(scope, retained));
            }
            progress = progressBuilder.MoveToImmutable();
        }

        return new(
            Provider: provider,
            ProcessInstanceId: process.ProcessInstanceId.Value,
            CurrentAttemptId: snapshot.Checkpoint.ContinuationIdentity.ProcessAttemptId.Value,
            ControlRevision: snapshot.Checkpoint.Control.Revision.Value,
            ControlMode: snapshot.Checkpoint.Control.Mode,
            TerminalOutcome: snapshot.Checkpoint.Continuation.Terminal.Kind,
            CurrentGeneration: execution?.Generation.Value,
            SelectedGeneration: generation?.Value,
            TargetRevision: target.Revision.Value,
            ActiveGeneration: target.ActiveGenerationId?.Value,
            SelectedGenerationState: candidate?.State,
            SelectedGenerationRevision: candidate?.Revision.Value,
            SelectedVisibleItemCount: candidate?.VisibleItemCount,
            SelectedTombstoneCount: candidate?.TombstoneCount,
            SelectedControlEpochs: controlEpochs,
            DurableOperations:
            [
                .. snapshot.Checkpoint.DurableOperations.Select(
                    MaterializationHarnessDurableOperationEvidence.From)
            ],
            Progress: progress,
            CapturedAtUtc: context.UtcNow);
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
                ? await StartAsync(context, start, invocation).ConfigureAwait(false)
                : Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
        }
        if (ReferenceEquals(endpoint, Catalog.Inspect))
        {
            return request is InspectProcessCommand inspect
                ? await InspectAsync(context, inspect, invocation).ConfigureAwait(false)
                : Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
        }
        if (ReferenceEquals(endpoint, Catalog.Explain))
        {
            if (request is not InspectProcessCommand explain)
                return Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
            var artifact = await GetExplainAsync(
                    context,
                    invocation.Authorization.AuthorityScope,
                    explain.Context.ProcessInstanceId)
                .ConfigureAwait(false);
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
                    traces.Context.ProcessInstanceId)
                .ConfigureAwait(false);
            var resultKind = ExecutionControlApiCatalog.TraceResultKind(read.State);
            return read.Artifact is null
                ? Problem(endpoint, resultKind, ExecutionApiProblemCodes.ForTraceReadState(read.State))
                : Result(endpoint, resultKind, read.Artifact);
        }
        if (ReferenceEquals(endpoint, Catalog.UpdateLimits))
        {
            return request is ControlLimitUpdateCommand update
                ? await UpdateLimitsAsync(context, update, invocation).ConfigureAwait(false)
                : Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
        }
        if (ReferenceEquals(endpoint, Catalog.Pause)
            || ReferenceEquals(endpoint, Catalog.Continue)
            || ReferenceEquals(endpoint, Catalog.RestartAttempt)
            || ReferenceEquals(endpoint, Catalog.Cancel))
        {
            return request is ProcessControlCommand command
                ? await ControlAsync(context, endpoint, command, invocation).ConfigureAwait(false)
                : Problem(endpoint, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
        }
        return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
    }

    internal async Task RunReadyProcessesAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(processesByProvider.Values.Select(process =>
            RunProviderAsync(process, cancellationToken))).ConfigureAwait(false);
        await CompareCompletedProvidersAsync(cancellationToken).ConfigureAwait(false);
    }

    internal Task WaitForWorkAsync(CancellationToken cancellationToken) =>
        workSignal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);

    async Task RunProviderAsync(
        MaterializationHarnessProviderProcess process,
        CancellationToken cancellationToken)
    {
        var state = executionStates[process.Provider.Provider];
        await state.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        state.SetCurrent(linked);
        try
        {
            await process.DriveAsync(linked.Token).ConfigureAwait(false);
            await process.MaintainActiveGenerationAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            state.ClearCurrent(linked);
            state.Gate.Release();
        }
    }

    async ValueTask<ExecutionApiDispatchResult> StartAsync(
        OperationContext context,
        ProcessStartRequest request,
        ExecutionApiInvocationContext invocation)
    {
        if (!processesByInstance.TryGetValue(request.InitialContinuation.ProcessInstanceId, out var process)
            || request.Definition != process.Artifacts.ParentPlan.DefinitionReference)
        {
            return Problem(Catalog.Start, ApiResultKind.ValidationFailed, ExecutionApiProblemCodes.RequestTypeMismatch);
        }
        var state = executionStates[process.Provider.Provider];
        await state.Gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await process.LoadAsync(context).ConfigureAwait(false);
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
            var initialized = await process.InitializeAsync(context, receipt).ConfigureAwait(false);
            var result = initialized.ProcessDisposition switch
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
        finally
        {
            state.Gate.Release();
        }
    }

    async ValueTask<ExecutionApiDispatchResult> InspectAsync(
        OperationContext context,
        InspectProcessCommand request,
        ExecutionApiInvocationContext invocation)
    {
        if (!processesByInstance.TryGetValue(request.Context.ProcessInstanceId, out var process))
            return Problem(Catalog.Inspect, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        var snapshot = await process.LoadAsync(context).ConfigureAwait(false);
        if (snapshot is null || snapshot.Checkpoint.Control.AuthorityScope != invocation.Authorization.AuthorityScope)
            return Problem(Catalog.Inspect, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        return Result(
            Catalog.Inspect,
            ApiResultKind.Success,
            new ExecutionControlResult(
                disposition: ProcessControlDecisionDisposition.Inspected,
                status: Status(process, snapshot, "inspect")));
    }

    async ValueTask<ExecutionApiDispatchResult> ControlAsync(
        OperationContext context,
        ApiEndpoint endpoint,
        ProcessControlCommand request,
        ExecutionApiInvocationContext invocation)
    {
        if (!processesByInstance.TryGetValue(request.Context.ProcessInstanceId, out var process))
            return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        var state = executionStates[process.Provider.Provider];
        if (request is PauseProcessCommand or RestartProcessAttemptCommand or CancelProcessCommand)
            state.CancelCurrent();
        await state.Gate.WaitAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await process.LoadAsync(context).ConfigureAwait(false);
            if (loaded is null || loaded.Checkpoint.Control.AuthorityScope != invocation.Authorization.AuthorityScope)
                return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
            var canonical = Rebind(
                command: request,
                invocation: invocation,
                prior: FindPriorCommand(loaded.Checkpoint.Control, request.Context));
            MaterializationRebuildPlanSetProcessLifecycleResult lifecycle;
            if (canonical is CancelProcessCommand cancel)
            {
                lifecycle = await process.CancelAsync(
                        context: context,
                        command: cancel,
                        activationContext: new(
                            authorityScope: invocation.Authorization.AuthorityScope,
                            correlationId: new($"correlation/{cancel.Context.CommandId.Value}"),
                            delivery: new(
                                durability: InteractionDurabilityDemand.Durable,
                                visibility: InteractionVisibilityDemand.AfterOriginCommit),
                            // Command provenance remains on the retained Cancel command. The interpreter-owned
                            // terminal activation must retain the canonical Process document's provenance.
                            provenance: process.Artifacts.ParentPlan.Document.Metadata.Provenance))
                    .ConfigureAwait(false);
            }
            else
            {
                lifecycle = await process.ApplyControlAsync(context, canonical).ConfigureAwait(false);
            }
            if (lifecycle.ProcessDisposition == ProcessDurableRuntimeDisposition.NotFound)
                return Problem(endpoint, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
            var snapshot = lifecycle.Snapshot ?? await process.LoadAsync(context).ConfigureAwait(false) ?? loaded;
            if (lifecycle.ProcessDisposition is not (
                    ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed)
                || lifecycle.Realization == MaterializationRebuildPlanSetProcessRealization.Rejected)
            {
                var rejected = new ExecutionControlResult(
                    disposition: ProcessControlDecisionDisposition.InvalidState,
                    status: Status(process, snapshot, "control-rejected"),
                    receipt: null,
                    diagnosticCodes:
                    [
                        .. lifecycle.Diagnostics.Select(static diagnostic => diagnostic.Code).DefaultIfEmpty(
                            $"{ProcessDispositionDiagnosticPrefix}.{lifecycle.ProcessDisposition?.ToString() ?? "missing"}")
                    ]);
                return Result(endpoint, rejected.ResultKind, rejected);
            }
            var receipt = snapshot.Checkpoint.Control.FindReceipt(canonical.Context.CommandId);
            if (receipt is null)
            {
                var rejected = new ExecutionControlResult(
                    disposition: ProcessControlDecisionDisposition.InvalidState,
                    status: Status(process, snapshot, "control-missing-receipt"),
                    receipt: null,
                    diagnosticCodes: [ProcessDurableRuntimeDiagnosticCodes.ActivationLifecycleBlocked]);
                return Result(endpoint, rejected.ResultKind, rejected);
            }
            var result = new ExecutionControlResult(
                disposition: DecisionDisposition(receipt.Disposition),
                status: Status(process, snapshot, "control"),
                receipt: new(
                    commandId: receipt.Command.Context.CommandId,
                    disposition: receipt.Disposition,
                    beforeRevision: receipt.BeforeRevision,
                    afterRevision: receipt.AfterRevision,
                    recordedAtUtc: receipt.RecordedAtUtc));
            if (canonical is ContinueProcessCommand or RestartProcessAttemptCommand)
                SignalWork();
            return Result(endpoint, result.ResultKind, result);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    async ValueTask<ExecutionApiDispatchResult> UpdateLimitsAsync(
        OperationContext context,
        ControlLimitUpdateCommand request,
        ExecutionApiInvocationContext invocation)
    {
        List<(MaterializationHarnessProviderProcess Process, ControlLoopState State)> matches = [];
        foreach (var candidate in processesByProvider.Values)
        {
            if (!candidate.Provider.Compilation.Plan.ControlRealizations.Any(realization =>
                    realization.EffectiveDefinition.Id == request.LoopId
                    && string.Equals(realization.EffectiveDefinition.Target, request.Target, StringComparison.Ordinal)))
            {
                continue;
            }
            var execution = await candidate.ResolveCurrentExecutionAsync(context).ConfigureAwait(false);
            if (execution is null)
                continue;
            var snapshots = await candidate.Provider.ControlRuntimeProvider
                .ForGeneration(execution.Generation)
                .GetSnapshotsAsync(context)
                .ConfigureAwait(false);
            var exact = snapshots.SingleOrDefault(snapshot => snapshot.State.LoopId == request.LoopId
                && string.Equals(snapshot.State.Target, request.Target, StringComparison.Ordinal)
                && snapshot.State.Epoch == request.Epoch);
            if (exact is not null)
                matches.Add((candidate, exact.State));
        }
        if (matches.Count != 1)
            return Problem(Catalog.UpdateLimits, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        var canonical = Rebind(request, invocation);
        var decidedAtUtc = context.UtcNow < matches[0].State.UpdatedAtUtc
            ? matches[0].State.UpdatedAtUtc
            : context.UtcNow;
        ControlLimitUpdateDecision decision;
        try
        {
            decision = await matches[0].Process.SubmitLimitUpdateAsync(
                    context: context,
                    command: canonical,
                    decidedAtUtc: decidedAtUtc)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return Problem(Catalog.UpdateLimits, ApiResultKind.NotFound, ExecutionApiProblemCodes.NotFound);
        }
        if (decision.Disposition == ControlLimitUpdateDecisionDisposition.Unauthorized)
            return Problem(Catalog.UpdateLimits, ApiResultKind.Forbidden, ExecutionApiProblemCodes.Forbidden);
        var kind = decision.Disposition switch
        {
            ControlLimitUpdateDecisionDisposition.Accepted => ApiResultKind.Accepted,
            ControlLimitUpdateDecisionDisposition.Replayed
                when decision.Receipt is not null
                     && decision.State.PendingLimitUpdate == decision.Receipt => ApiResultKind.Accepted,
            ControlLimitUpdateDecisionDisposition.Stale => ApiResultKind.PreconditionFailed,
            ControlLimitUpdateDecisionDisposition.IdentityConflict
                or ControlLimitUpdateDecisionDisposition.IdempotencyConflict
                or ControlLimitUpdateDecisionDisposition.PendingConflict => ApiResultKind.Conflict,
            ControlLimitUpdateDecisionDisposition.OutOfBounds
                or ControlLimitUpdateDecisionDisposition.Invalid => ApiResultKind.ValidationFailed,
            _ => ApiResultKind.Success
        };
        return Result(Catalog.UpdateLimits, kind, ControlLimitUpdateResult.FromDecision(decision));
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
        if (authorityScope != options.AuthorityScope
            || !processesByInstance.TryGetValue(processInstanceId, out var process))
        {
            return null;
        }
        var snapshot = await process.LoadAsync(context).ConfigureAwait(false);
        if (snapshot is null || snapshot.Checkpoint.Control.AuthorityScope != authorityScope)
            return null;
        var checkpoint = snapshot.Checkpoint;
        return new(
            ProcessId: processInstanceId.Value,
            ProcessName: checkpoint.Definition.DefinitionId.Value,
            Status: ExecutionStatus(checkpoint),
            StartedAtUtc: checkpoint.CreatedAtUtc,
            UpdatedAtUtc: checkpoint.UpdatedAtUtc,
            CompletedAtUtc: checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None
                ? null
                : checkpoint.Continuation.Terminal.OccurredAtUtc,
            RuntimeStatus: Status(process, snapshot, "repository-inspect"),
            Definition: checkpoint.Definition);
    }

    public ValueTask<ProcessExecutionQueryResult> QueryAsync(
        OperationContext context,
        ProcessExecutionQuery query) =>
        throw new NotSupportedException(
            "The local materialization harness exposes exact provider Process instances rather than an execution index.");

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
        if (authorityScope != options.AuthorityScope
            || !processesByInstance.TryGetValue(processInstanceId, out var process))
        {
            return null;
        }
        var snapshot = await process.LoadAsync(context).ConfigureAwait(false);
        if (snapshot is null || snapshot.Checkpoint.Control.AuthorityScope != authorityScope)
            return null;
        var projection = ProcessDurableExecutionExplainProjector.Project(
            compilation: process.Artifacts.ParentCompilation,
            checkpoint: snapshot.Checkpoint,
            runtimeExtensions: process.RuntimeStatus(snapshot, "repository-explain").Extensions);
        return projection.Artifact
            ?? throw new InvalidOperationException(
                "The retained Process checkpoint could not be explained: "
                + string.Join(", ", projection.Validation.Diagnostics.Select(static diagnostic => diagnostic.Code)));
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
        if (authorityScope != options.AuthorityScope
            || !processesByInstance.TryGetValue(processInstanceId, out var process))
        {
            return ProcessExecutionTraceReadResult.NotFound();
        }
        var snapshot = await process.LoadAsync(context).ConfigureAwait(false);
        if (snapshot is null || snapshot.Checkpoint.Control.AuthorityScope != authorityScope)
            return ProcessExecutionTraceReadResult.NotFound();
        if (snapshot.Checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None)
            return ProcessExecutionTraceReadResult.InProgress();
        if (snapshot.Checkpoint.Activations.IsDefaultOrEmpty)
            return ProcessExecutionTraceReadResult.TerminalArtifactUnavailable();
        var traces = ProcessDurableExecutionTraceProjector.Project(snapshot.Checkpoint);
        var failures = traces.Where(static result => !result.IsSuccessful).ToArray();
        if (failures.Length != 0)
        {
            throw new InvalidOperationException(
                "The retained Process traces could not be projected: "
                + string.Join(", ", failures.SelectMany(static result => result.Validation.Diagnostics)
                    .Select(static diagnostic => diagnostic.Code)));
        }
        return ProcessExecutionTraceReadResult.Available(new(
            schemaVersion: ProcessExecutionTraceArtifact.CurrentSchemaVersion,
            definition: snapshot.Checkpoint.Definition,
            processInstanceId: processInstanceId,
            missingTracePrefixCount: 0,
            traces: [.. traces.Select(static result => result.Trace!)]));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var state in executionStates.Values)
        {
            state.CancelCurrent();
            state.Dispose();
        }
        workSignal.Dispose();
        if (runtimeCatalog is not null)
            await runtimeCatalog.DisposeAsync().ConfigureAwait(false);
    }

    async Task CompareCompletedProvidersAsync(CancellationToken cancellationToken)
    {
        if (runtimeCatalog is null)
            return;
        foreach (var process in processesByProvider.Values)
        {
            var snapshot = await process.LoadAsync(OperationContext.Create(cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (snapshot?.Checkpoint.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.Completed)
                return;
        }
        var documents = await Task.WhenAll(runtimeCatalog.Providers.Select(async provider =>
            (provider.Provider, Documents: await runtimeCatalog.ReadCanonicalDocumentsAsync(provider.Provider)
                .ConfigureAwait(false)))).ConfigureAwait(false);
        var expected = documents[0].Documents;
        var differs = false;
        foreach (var candidate in documents.Skip(1))
        {
            if (!expected.SequenceEqual(candidate.Documents, StringComparer.Ordinal))
            {
                differs = true;
                break;
            }
        }
        if (!differs)
        {
            previousProviderMismatch = previousProviderMismatch.Clear();
            providerMismatchObservationCount = 0;
            return;
        }

        var sameMismatch = documents.Length == previousProviderMismatch.Count
            && documents.All(candidate =>
                previousProviderMismatch.TryGetValue(candidate.Provider, out var previous)
                && previous.SequenceEqual(candidate.Documents, StringComparer.Ordinal));
        if (!sameMismatch)
        {
            previousProviderMismatch = documents.ToImmutableDictionary(
                static candidate => candidate.Provider,
                static candidate => candidate.Documents,
                StringComparer.Ordinal);
            providerMismatchObservationCount = 1;
            return;
        }

        providerMismatchObservationCount++;
        if (providerMismatchObservationCount >= ProviderMismatchFailureThreshold)
        {
            throw new InvalidOperationException(
                $"Completed providers retained the same logical-document mismatch across "
                + $"{providerMismatchObservationCount} complete synchronization cycles.");
        }
    }

    MaterializationHarnessProviderProcess GetProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return processesByProvider.TryGetValue(provider, out var process)
            ? process
            : throw new KeyNotFoundException($"Materialization provider '{provider}' is not configured.");
    }

    void SignalWork()
    {
        if (workSignal.CurrentCount == 0)
            workSignal.Release();
    }

    static ExecutionStatus Status(
        MaterializationHarnessProviderProcess process,
        ProcessDurableStoreSnapshot snapshot,
        string source) => ExecutionStatusProjector.Project(
        state: snapshot.Checkpoint.Control,
        runtime: process.RuntimeStatus(snapshot, source),
        terminalOutcome: snapshot.Checkpoint.Continuation.Terminal);

    static ProcessExecutionStatus ExecutionStatus(ProcessDurableCheckpoint checkpoint)
    {
        if (checkpoint.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.None)
        {
            return checkpoint.Continuation.Terminal.Kind switch
            {
                ExecutionTerminalOutcomeKind.Completed => ProcessExecutionStatus.Completed,
                ExecutionTerminalOutcomeKind.Failed => ProcessExecutionStatus.Failed,
                ExecutionTerminalOutcomeKind.Cancelled => ProcessExecutionStatus.Cancelled,
                _ => ProcessExecutionStatus.Terminated
            };
        }
        return checkpoint.Control.Mode == ProcessControlMode.Paused
            ? ProcessExecutionStatus.Suspended
            : ProcessExecutionStatus.Running;
    }

    static bool IsAuthorized(ApiEndpoint endpoint, ExecutionApiInvocationContext invocation) =>
        endpoint.Operation.AuthorizationRequirements.All(requirement =>
            invocation.GrantedRequirements.Contains(requirement.Id, StringComparer.Ordinal));

    ApiEndpoint ResolveCommandEndpoint(string operation) => operation switch
    {
        ProcessStartWireNames.Start => Catalog.Start,
        ExecutionControlWireNames.Pause => Catalog.Pause,
        ExecutionControlWireNames.Continue => Catalog.Continue,
        ExecutionControlWireNames.RestartAttempt => Catalog.RestartAttempt,
        ExecutionControlWireNames.Cancel => Catalog.Cancel,
        ControlLimitUpdateWireNames.UpdateLimits => Catalog.UpdateLimits,
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation),
            operation,
            "The materialization host cannot project this execution-control command.")
    };

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

    static ControlLimitUpdateCommand Rebind(
        ControlLimitUpdateCommand command,
        ExecutionApiInvocationContext invocation) => new(
        schemaVersion: command.SchemaVersion,
        commandId: command.CommandId,
        idempotencyKey: command.IdempotencyKey,
        loopId: command.LoopId,
        definitionFingerprint: command.DefinitionFingerprint,
        target: command.Target,
        epoch: command.Epoch,
        expectedRevision: command.ExpectedRevision,
        requestedOperatingPoint: command.RequestedOperatingPoint,
        authorization: invocation.Authorization,
        issuedAtUtc: invocation.IssuedAtUtc,
        provenance: invocation.Provenance);

    static ProcessControlDecisionDisposition DecisionDisposition(
        ProcessControlReceiptDisposition disposition) => disposition switch
        {
            ProcessControlReceiptDisposition.Applied => ProcessControlDecisionDisposition.Applied,
            ProcessControlReceiptDisposition.DeferredToSafePoint =>
                ProcessControlDecisionDisposition.DeferredToSafePoint,
            ProcessControlReceiptDisposition.AlreadySatisfied =>
                ProcessControlDecisionDisposition.AlreadySatisfied,
            ProcessControlReceiptDisposition.AlreadyRequested =>
                ProcessControlDecisionDisposition.AlreadyRequested,
            ProcessControlReceiptDisposition.SignalAccepted =>
                ProcessControlDecisionDisposition.SignalAccepted,
            ProcessControlReceiptDisposition.SignalBuffered =>
                ProcessControlDecisionDisposition.SignalBuffered,
            ProcessControlReceiptDisposition.SignalDuplicate =>
                ProcessControlDecisionDisposition.SignalDuplicate,
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported receipt.")
        };

    static ExecutionApiInvocationContext Invocation(
        ApiEndpoint endpoint,
        MaterializationHarnessProviderProcess process,
        DateTimeOffset now) => new(
        authorization: process.Authorization(),
        provenance: process.Provenance($"sdk-{endpoint.Operation.Name}"),
        issuedAtUtc: now,
        observedAtUtc: now,
        grantedRequirements:
        [
            .. endpoint.Operation.AuthorizationRequirements.Select(static requirement => requirement.Id)
        ]);

    sealed class ProviderExecutionState : IDisposable
    {
        readonly object stateGate = new();
        CancellationTokenSource? current;

        internal SemaphoreSlim Gate { get; } = new(initialCount: 1, maxCount: 1);

        internal void SetCurrent(CancellationTokenSource source)
        {
            lock (stateGate)
                current = source;
        }

        internal void ClearCurrent(CancellationTokenSource source)
        {
            lock (stateGate)
            {
                if (ReferenceEquals(current, source))
                    current = null;
            }
        }

        internal void CancelCurrent()
        {
            lock (stateGate)
                current?.Cancel();
        }

        public void Dispose()
        {
            CancelCurrent();
            Gate.Dispose();
        }
    }
}
