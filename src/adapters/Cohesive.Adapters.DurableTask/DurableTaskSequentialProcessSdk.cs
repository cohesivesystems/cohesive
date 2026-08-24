using System.Text.Json;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Execution;
using DurableTask.Core.Exceptions;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using Microsoft.DurableTask.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Creates the portable JSON converter required by standalone Durable Task Process workers and clients.</summary>
public static class DurableTaskProcessDataConverter
{
    /// <summary>Creates a System.Text.Json converter with the canonical Cohesive portable-value converters.</summary>
    /// <returns>A new converter suitable for both standalone Durable Task workers and clients.</returns>
    public static DataConverter Create() => new DurableTaskPortableDataConverter(
        DurableTaskSystemTextJsonDataConverter.CreateJsonOptions(
            ExecutionDefinitionJsonSerializer.CreateOptions()));

    sealed class DurableTaskPortableDataConverter(JsonSerializerOptions options) : DataConverter
    {
        readonly JsonSerializerOptions options = options ?? throw new ArgumentNullException(nameof(options));

        public override string? Serialize(object? value)
        {
            if (value is null)
            {
                return null;
            }

            var contract = value is ProcessControlCommand
                ? typeof(ProcessControlCommand)
                : value.GetType();
            return JsonSerializer.Serialize(value, contract, options);
        }

        public override object? Deserialize(string? data, Type targetType)
        {
            ArgumentNullException.ThrowIfNull(targetType);
            if (data is null)
            {
                return null;
            }
            if (targetType != typeof(object)
                && DurableTaskSystemTextJsonDataConverter.TryExtractTypedValuePayload(data, out var payload))
            {
                data = payload;
            }
            return JsonSerializer.Deserialize(data, targetType, options);
        }
    }
}

/// <summary>Registers the generic bounded Process orchestration and its host-operation activities.</summary>
public static class DurableTaskSequentialProcessWorkerBuilderExtensions
{
    /// <summary>Adds the initial canonical Process executable slice to a standalone Durable Task worker.</summary>
    /// <remarks>
    /// The application must also register one <see cref="IAsyncProcessReferenceHost"/> for bounded activities. This method
    /// registers the immutable plan catalog and the same portable data converter for the worker and a client built
    /// from the same service collection unless the application supplied one explicitly.
    /// </remarks>
    /// <param name="builder">Standalone Durable Task worker builder.</param>
    /// <param name="catalog">Immutable exact-reference plan catalog deployed to this worker.</param>
    /// <returns><paramref name="builder"/> for composition.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="catalog"/> is <see langword="null"/>.
    /// </exception>
    public static IDurableTaskWorkerBuilder AddCohesiveSequentialProcesses(
        this IDurableTaskWorkerBuilder builder,
        DurableTaskSequentialProcessPlanCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return AddCohesiveSequentialProcesses(builder, _ => catalog);
    }

    /// <summary>
    /// Adds the canonical Process executable slice with one immutable plan catalog composed by dependency injection.
    /// </summary>
    /// <remarks>
    /// The factory is registered as a singleton and is evaluated when the host constructs its hosted services.
    /// Catalog construction and admission therefore complete before any worker starts processing. The same resolved
    /// catalog instance is constructor-injected into the orchestrator and every activity.
    /// </remarks>
    /// <param name="builder">Standalone Durable Task worker builder.</param>
    /// <param name="catalogFactory">
    /// Application composition factory for the exact worker catalog. It may resolve exact operation and publication
    /// adapters from <see cref="IServiceProvider"/> but must return a complete immutable admitted catalog.
    /// </param>
    /// <returns><paramref name="builder"/> for composition.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> or <paramref name="catalogFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A plan catalog was already registered. One worker must have exactly one catalog composition authority.
    /// </exception>
    public static IDurableTaskWorkerBuilder AddCohesiveSequentialProcesses(
        this IDurableTaskWorkerBuilder builder,
        Func<IServiceProvider, DurableTaskSequentialProcessPlanCatalog> catalogFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(catalogFactory);
        if (builder.Services.Any(static descriptor =>
                descriptor.ServiceType == typeof(DurableTaskSequentialProcessPlanCatalog)))
        {
            throw new InvalidOperationException(
                "A Durable Task Process worker can register exactly one plan catalog composition authority.");
        }

        var converter = DurableTaskProcessDataConverter.Create();
        var orchestratorActivation = new DurableTaskSequentialProcessOrchestratorActivation();
        builder.Services.AddSingleton(catalogFactory);
        builder.Services.AddSingleton(orchestratorActivation);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService,
            DurableTaskSequentialProcessPlanCatalogAdmissionHostedService>());
        builder.Services.TryAddSingleton(converter);
        builder.Services.TryAddSingleton<IDurableOperationAdapterResolver>(
            EmptyDurableOperationAdapterResolver.Instance);
        builder.Services.TryAddSingleton<IDurableOperationAdapterCapabilityResolver>(
            EmptyDurableOperationAdapterCapabilityResolver.Instance);
        builder.Services.TryAddSingleton<IDurableOperationExceptionClassifier>(
            ConservativeDurableOperationExceptionClassifier.Instance);
        builder.Services.TryAddSingleton<IInteractionAuthorityOperationContextProjector>(
            PassthroughInteractionAuthorityOperationContextProjector.Instance);
        builder.Configure(options =>
        {
            options.DataConverter = converter;
            options.EnableEntitySupport = true;
        });
        return builder.AddTasks(tasks =>
        {
            // Standalone Durable Task resolves activities through the service provider, but type-based orchestrator
            // activation uses Activator.CreateInstance and therefore cannot perform constructor injection. The SDK
            // factory closes over this worker registration's admitted catalog without introducing ambient state.
            tasks.AddOrchestrator(
                DurableTaskSequentialProcessNames.Orchestration,
                orchestratorActivation.Create);
            tasks.AddOrchestrator(
                DurableTaskSequentialProcessNames.StartAdmissionOrchestration,
                orchestratorActivation.CreateStartAdmission);
            tasks.AddOrchestrator(
                DurableTaskSequentialProcessNames.ControlAdmissionOrchestration,
                orchestratorActivation.CreateControlAdmission);
            tasks.AddEntity<DurableTaskProcessStartIndexEntity>(
                new(DurableTaskSequentialProcessNames.StartAdmissionIndexEntity));
            tasks.AddEntity<DurableTaskProcessControlResponseEntity>(
                new(DurableTaskSequentialProcessNames.ControlResponseEntity));
            tasks.AddEntity<DurableTaskTerminalProcessControlEntity>(
                new(DurableTaskSequentialProcessNames.TerminalControlEntity));
            tasks.AddActivity<DurableTaskProcessHostOperationActivity>();
            tasks.AddActivity<DurableTaskProcessSignalTargetActivity>();
            tasks.AddActivity<DurableTaskDomainEventPublicationActivity>();
            tasks.AddActivity<DurableTaskDurableOperationActivity>();
            tasks.AddActivity<DurableTaskDurableOperationReconciliationActivity>();
        });
    }
}

sealed class DurableTaskSequentialProcessPlanCatalogAdmissionHostedService(
    DurableTaskSequentialProcessPlanCatalog catalog,
    DurableTaskSequentialProcessOrchestratorActivation orchestratorActivation) : IHostedService
{
    readonly DurableTaskSequentialProcessPlanCatalog catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));
    readonly DurableTaskSequentialProcessOrchestratorActivation orchestratorActivation =
        orchestratorActivation ?? throw new ArgumentNullException(nameof(orchestratorActivation));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = catalog.Count;
        orchestratorActivation.Admit(catalog);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

sealed class DurableTaskSequentialProcessOrchestratorActivation
{
    DurableTaskSequentialProcessPlanCatalog? admittedCatalog;

    public void Admit(DurableTaskSequentialProcessPlanCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var existing = Interlocked.CompareExchange(ref admittedCatalog, catalog, null);
        if (existing is not null && !ReferenceEquals(existing, catalog))
        {
            throw new InvalidOperationException(
                "The Durable Task Process worker cannot replace its admitted exact plan catalog.");
        }
    }

    public ITaskOrchestrator Create() => new DurableTaskSequentialProcessOrchestrator(
        Volatile.Read(ref admittedCatalog)
        ?? throw new InvalidOperationException(
            "The Durable Task Process worker attempted orchestrator activation before exact plan catalog admission."));

    public ITaskOrchestrator CreateStartAdmission() => new DurableTaskProcessStartAdmissionOrchestrator(
        Volatile.Read(ref admittedCatalog)
        ?? throw new InvalidOperationException(
            "The Durable Task Process worker attempted start admission before exact plan catalog admission."));

    public ITaskOrchestrator CreateControlAdmission() => new DurableTaskProcessControlAdmissionOrchestrator();
}

/// <summary>Generic standalone Durable Task orchestration over one exact canonical Process plan.</summary>
[DurableTask(DurableTaskSequentialProcessNames.Orchestration)]
public sealed class DurableTaskSequentialProcessOrchestrator
    : TaskOrchestrator<DurableTaskSequentialProcessStart, DurableTaskSequentialProcessResult>
{
    readonly DurableTaskSequentialProcessPlanCatalog catalog;

    /// <summary>Creates an orchestrator over an immutable exact-reference plan catalog.</summary>
    /// <param name="catalog">Worker-deployed exact Process plans.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    public DurableTaskSequentialProcessOrchestrator(DurableTaskSequentialProcessPlanCatalog catalog) =>
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public override async Task<DurableTaskSequentialProcessResult> RunAsync(
        TaskOrchestrationContext context,
        DurableTaskSequentialProcessStart input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var physical = catalog.GetExact(input.Receipt.Request.Definition);
        Func<Task<ProcessChildCancellationIntent>>? waitForChildCancellation = input.ChildRequest is null
            ? null
            : () => context.WaitForExternalEvent<ProcessChildCancellationIntent>(
                DurableTaskSequentialProcessNames.ChildCancellationEvent);
        var result = await DurableTaskSequentialProcessInterpreter.RunAsync(
            physical.CanonicalPlan,
            input,
            catalog.BindingResolver,
            operation => context.CallActivityAsync<ProcessOperationResult>(
                DurableTaskSequentialProcessNames.HostOperationActivity,
                operation,
                DurableTaskActivityOperationContext.WorkerStoppingRetryOptions),
            invocation => context.CallActivityAsync<DurableTaskDurableOperationAttemptResult>(
                DurableTaskSequentialProcessNames.DurableOperationActivity,
                invocation,
                DurableTaskActivityOperationContext.WorkerStoppingRetryOptions),
            invocation => ExecuteChildProcessAsync(context, catalog, invocation),
            state => context.CallActivityAsync<DurableTaskDurableOperationReconciliationResult>(
                DurableTaskSequentialProcessNames.DurableOperationReconciliationActivity,
                state,
                DurableTaskActivityOperationContext.WorkerStoppingRetryOptions),
            () => context.WaitForExternalEvent<ProcessActivationInput>(
                DurableTaskSequentialProcessNames.InteractionEvent),
            (delay, cancellationToken) => context.CreateTimer(delay, cancellationToken),
            () => ToUtc(context.CurrentUtcDateTime),
            result => context.SetCustomStatus(DurableTaskProcessStatus.Project(result)),
            next =>
            {
                context.ContinueAsNew(next, preserveUnprocessedEvents: true);
                return Task.CompletedTask;
            },
            intent =>
            {
                context.SendEvent(
                    DurableTaskSequentialProcessIdentities.OrchestrationInstance(
                        input.ActivationContext.AuthorityScope,
                        intent.ChildContinuation.ProcessInstanceId),
                    DurableTaskSequentialProcessNames.ChildCancellationEvent,
                    intent);
                return Task.CompletedTask;
            },
            waitForChildCancellation,
            resolution => context.CallActivityAsync<ProcessSignalTargetResult>(
                DurableTaskSequentialProcessNames.SignalTargetResolutionActivity,
                resolution,
                DurableTaskActivityOperationContext.WorkerStoppingRetryOptions),
            signal => DeliverSignal(context, signal),
            waitForControl: null,
            publishDomainEvent: domainEvent => context.CallActivityAsync<DurableTaskDomainEventPublication>(
                DurableTaskSequentialProcessNames.DomainEventPublicationActivity,
                domainEvent),
            waitForControlRequest: () => context.WaitForExternalEvent<DurableTaskProcessControlRequest>(
                DurableTaskSequentialProcessNames.ControlEvent),
            retainControlResponse: (responseIdentity, response) =>
                context.Entities.CallEntityAsync<DurableTaskProcessControlResponse>(
                    new(
                        DurableTaskSequentialProcessNames.ControlResponseEntity,
                        responseIdentity),
                    nameof(DurableTaskProcessControlResponseEntity.Claim),
                    response,
                    new CallEntityOptions())).ConfigureAwait(true);

        if (result.Control.IsTerminal || result.State.Terminal.Kind != ExecutionTerminalOutcomeKind.None)
        {
            _ = await context.Entities.CallEntityAsync<DurableTaskTerminalProcessControlState>(
                    DurableTaskProcessControlProtocol.Terminal(
                        input.ActivationContext.AuthorityScope,
                        input.Receipt.Request.InitialContinuation.ProcessInstanceId),
                    nameof(DurableTaskTerminalProcessControlEntity.Handoff),
                    result,
                    new CallEntityOptions())
                .ConfigureAwait(true);
        }

        var blockedOperation = result.DurableOperations.FirstOrDefault(static operation =>
            operation.State.Status is not DurableOperationStatus.Dispositioned);
        if (blockedOperation is not null)
        {
            throw new DurableTaskDurableOperationRecoveryRequiredException(
                blockedOperation.State.OperationId,
                blockedOperation.Disposition,
                blockedOperation.State.Status,
                DurableOperationReferenceExecutor.GetRecoveryIntent(blockedOperation.State));
        }
        if (result.Disposition == ProcessActivationDisposition.Failed && input.ChildRequest is null)
        {
            var detail = result.Diagnostics.IsEmpty
                ? "Canonical Process execution reached a failed terminal."
                : string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message));
            throw new DurableTaskProcessFailedException(detail);
        }
        return result;
    }

    static async Task<DurableTaskDurableOperationAttemptResult> ExecuteChildProcessAsync(
        TaskOrchestrationContext context,
        DurableTaskSequentialProcessPlanCatalog catalog,
        DurableOperationInvocation invocation)
    {
        var request = invocation.Request;
        var target = request.ChildTarget
            ?? throw new InvalidOperationException("A child sub-orchestration requires an exact child Request target.");
        var child = catalog.GetExact(target.Definition).CanonicalPlan;
        var acceptedAtUtc = ToUtc(context.CurrentUtcDateTime);
        var authorization = new ProcessControlAuthorizationContext(
            "cohesive.adapters.durable-task.child-sub-orchestration",
            request.Context.AuthorityScope,
            $"request/{request.Context.EmissionId.Value}/"
            + InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(request).Value);
        var receipt = ProcessChildStartProjection.Create(request, target, authorization, acceptedAtUtc);
        var start = new DurableTaskSequentialProcessStart(
            receipt,
            new(
                request.Context.AuthorityScope,
                request.Context.CorrelationId,
                request.Context.Delivery,
                child.Document.Metadata.Provenance,
                causationId: request.Context.EmissionId,
                ordering: request.Context.Ordering),
            childRequest: request);
        var result = await context.CallSubOrchestratorAsync<DurableTaskSequentialProcessResult>(
            DurableTaskSequentialProcessNames.Orchestration,
            start,
            new TaskOptions().WithInstanceId(
                DurableTaskSequentialProcessIdentities.OrchestrationInstance(start))).ConfigureAwait(true);
        return ProjectChildTerminal(catalog, request, child, target, result);
    }

    static DurableTaskDurableOperationAttemptResult ProjectChildTerminal(
        DurableTaskSequentialProcessPlanCatalog catalog,
        RequestEnvelope request,
        Cohesive.Processes.Compilation.CompiledProcessPlan child,
        ProcessChildRequestTarget target,
        DurableTaskSequentialProcessResult result)
    {
        if (result.State.Definition != target.Definition
            || result.State.Continuation != target.Continuation)
        {
            throw new InvalidOperationException(
                "A child sub-orchestration returned another definition or continuation identity.");
        }
        var terminal = result.State.Terminal;
        if (terminal.Kind == ExecutionTerminalOutcomeKind.None
            || terminal.Detail is { Disclosure: not ExecutionStatusDisclosure.Disclosed })
        {
            throw new InvalidOperationException(
                "A child sub-orchestration did not return a materializable terminal outcome.");
        }
        var terminalEvidence = result.Evidence
            .Where(evidence => evidence.Definition == target.Definition)
            .Reverse()
            .FirstOrDefault(evidence => evidence.Trace.Any(static trace => trace.Kind is
                ProcessTraceEventKind.TerminalReached or ProcessTraceEventKind.CancellationApplied))
            ?? throw new InvalidOperationException(
                "A child sub-orchestration returned no attributable terminal trace.");
        var terminalTrace = terminalEvidence.Trace.Last(trace => trace.Kind is
            ProcessTraceEventKind.TerminalReached or ProcessTraceEventKind.CancellationApplied);
        var outcomeId = target.OutcomeMapping.For(terminal.Kind);
        var outcomeContract = ResolveChildOutcomeContract(catalog, request, outcomeId);
        RequestTerminalOutcome outcome = terminal.Kind switch
        {
            ExecutionTerminalOutcomeKind.Completed => new RequestResultOutcome(
                outcomeId,
                RequireCompletedChildResult(child, terminal, outcomeContract)),
            ExecutionTerminalOutcomeKind.Failed => new RequestFailureOutcome(
                outcomeId,
                PortableValue.Concrete(
                    outcomeContract,
                    ObservationValue.FromObject(new ProcessChildFailure(terminalTrace.Node, result.Diagnostics)))),
            ExecutionTerminalOutcomeKind.Cancelled or ExecutionTerminalOutcomeKind.Terminated =>
                new RequestFailureOutcome(
                    outcomeId,
                    PortableValue.Concrete(outcomeContract, ObservationValue.FromObject(terminal.Kind))),
            _ => throw new InvalidOperationException(
                "A child sub-orchestration returned an unsupported terminal outcome.")
        };
        var origin = new ProcessInteractionOrigin(
            target.Definition,
            terminalTrace.Node,
            target.Continuation,
            terminalEvidence.Activation,
            terminalTrace.Token,
            outcome: terminalTrace.Node);
        return new(
            new DurableOperationOutcomeObservation(outcome, replyOrigin: origin),
            deadlineElapsed: false);
    }

    static ValueContract ResolveChildOutcomeContract(
        DurableTaskSequentialProcessPlanCatalog catalog,
        RequestEnvelope request,
        RequestTerminalOutcomeId outcome)
    {
        if (request.Context.Origin is not ProcessInteractionOrigin parentOrigin)
        {
            throw new InvalidOperationException(
                "A child sub-orchestration Request requires an exact parent Process origin.");
        }
        var parent = catalog.GetExact(parentOrigin.Definition).CanonicalPlan;
        var contracts = parent.ValidationContext.InteractionContracts
            ?? throw new InvalidOperationException(
                "A child sub-orchestration parent has no exact interaction-contract catalog.");
        if (!contracts.TryResolve(request.Contract, out var definition)
            || definition is not RequestContractDefinition requestDefinition
            || requestDefinition.Response.Find(outcome) is not { } declared)
        {
            throw new InvalidOperationException(
                "A child sub-orchestration terminal outcome is absent from its exact parent Request contract.");
        }
        return declared.Schema.Contract;
    }

    static PortableValue RequireCompletedChildResult(
        Cohesive.Processes.Compilation.CompiledProcessPlan child,
        ExecutionTerminalOutcome terminal,
        ValueContract outcomeContract)
    {
        var value = terminal.Detail?.Value
            ?? throw new InvalidOperationException(
                "A completed child sub-orchestration did not return materializable terminal detail.");
        if (outcomeContract != child.Definition.Result
            || value.Contract != outcomeContract
            || value.State is PortableValueState.Unknown or PortableValueState.Failed)
        {
            throw new InvalidOperationException(
                "A completed child sub-orchestration returned terminal detail outside its exact result contract.");
        }
        return value;
    }

    static DateTimeOffset ToUtc(DateTime value) => new(
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc));

    static Task DeliverSignal(TaskOrchestrationContext context, SignalEnvelope signal)
    {
        var target = RequireDurableProcessSignalTarget(signal);
        context.SendEvent(
            DurableTaskSequentialProcessIdentities.OrchestrationInstance(
                signal.Context.AuthorityScope,
                target.Continuation.ProcessInstanceId),
            DurableTaskSequentialProcessNames.InteractionEvent,
            new ProcessActivationInput(target, signal));
        return Task.CompletedTask;
    }

    internal static ProcessTokenInteractionTarget RequireDurableProcessSignalTarget(SignalEnvelope signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.Context.Delivery.Durability != InteractionDurabilityDemand.Durable)
        {
            throw new InvalidOperationException(
                $"Durable Task cannot deliver activation-local Signal '{signal.Context.EmissionId.Value}' as an external event.");
        }
        if (signal.Target is not ProcessTokenInteractionTarget target)
        {
            throw new InvalidOperationException(
                $"Durable Task Signal '{signal.Context.EmissionId.Value}' resolved to unsupported target "
                + $"'{signal.Target.GetType().Name}'; this executable slice requires a Process-token target.");
        }
        return target;
    }
}

/// <summary>Activity boundary for one exact fenced canonical durable Request invocation.</summary>
[DurableTask(DurableTaskSequentialProcessNames.DurableOperationActivity)]
public sealed class DurableTaskDurableOperationActivity
    : TaskActivity<DurableOperationInvocation, DurableTaskDurableOperationAttemptResult>
{
    readonly IDurableOperationAdapterResolver resolver;
    readonly IDurableOperationExceptionClassifier exceptionClassifier;
    readonly IHostApplicationLifetime applicationLifetime;
    readonly IInteractionAuthorityOperationContextProjector contextProjector;

    /// <summary>Creates an activity over exact adapter resolution and explicit exception classification.</summary>
    /// <param name="resolver">Exact canonical Request adapter resolver.</param>
    /// <param name="exceptionClassifier">Provider-aware adapter exception classifier.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <exception cref="ArgumentNullException">Any dependency is <see langword="null"/>.</exception>
    public DurableTaskDurableOperationActivity(
        IDurableOperationAdapterResolver resolver,
        IDurableOperationExceptionClassifier exceptionClassifier,
        IHostApplicationLifetime applicationLifetime) : this(
            resolver,
            exceptionClassifier,
            applicationLifetime,
            PassthroughInteractionAuthorityOperationContextProjector.Instance)
    {
    }

    /// <summary>Creates an activity with explicit canonical-authority context projection.</summary>
    /// <param name="resolver">Exact canonical Request adapter resolver.</param>
    /// <param name="exceptionClassifier">Provider-aware adapter exception classifier.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <param name="contextProjector">Host interpretation of canonical interaction authority.</param>
    /// <exception cref="ArgumentNullException">Any dependency is <see langword="null"/>.</exception>
    public DurableTaskDurableOperationActivity(
        IDurableOperationAdapterResolver resolver,
        IDurableOperationExceptionClassifier exceptionClassifier,
        IHostApplicationLifetime applicationLifetime,
        IInteractionAuthorityOperationContextProjector contextProjector)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.exceptionClassifier = exceptionClassifier ?? throw new ArgumentNullException(nameof(exceptionClassifier));
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
        this.contextProjector = contextProjector ?? throw new ArgumentNullException(nameof(contextProjector));
    }

    /// <inheritdoc />
    public override async Task<DurableTaskDurableOperationAttemptResult> RunAsync(
        TaskActivityContext context,
        DurableOperationInvocation input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var adapter = Resolve(input.Request);
        DurableOperationReferenceExecutor.ValidateAdapterCapabilities(input.Binding, adapter.Capabilities);
        try
        {
            var observation = await DurableTaskActivityOperationContext.ExecuteAsync(
                    applicationLifetime,
                    input.Request.Context.AuthorityScope,
                    contextProjector,
                    operationContext => DurableOperationReferenceExecutor.ExecuteAsync(
                        operationContext,
                        input,
                        adapter))
                .ConfigureAwait(false);
            return new(observation, deadlineElapsed: false);
        }
        catch (DurableTaskWorkerStoppingException)
        {
            throw;
        }
        catch (DurableOperationDeadlineElapsedException)
        {
            return new(observation: null, deadlineElapsed: true);
        }
        catch (Exception exception)
        {
            return new(
                new DurableOperationFailureObservation(exceptionClassifier.Classify(exception)),
                deadlineElapsed: false);
        }
    }

    IDurableOperationAdapter Resolve(RequestEnvelope request) =>
        resolver.TryResolve(request, out var adapter) && adapter is not null
            ? adapter
            : throw new InvalidOperationException(
                "No durable operation adapter is registered for the exact Request contract.");
}

/// <summary>Activity boundary for explicit reconciliation of one failed ambiguous durable Request attempt.</summary>
[DurableTask(DurableTaskSequentialProcessNames.DurableOperationReconciliationActivity)]
public sealed class DurableTaskDurableOperationReconciliationActivity
    : TaskActivity<DurableOperationState, DurableTaskDurableOperationReconciliationResult>
{
    readonly IDurableOperationAdapterResolver resolver;
    readonly IHostApplicationLifetime applicationLifetime;
    readonly IInteractionAuthorityOperationContextProjector contextProjector;

    /// <summary>Creates an activity over exact adapter resolution.</summary>
    /// <param name="resolver">Exact canonical Request adapter resolver.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public DurableTaskDurableOperationReconciliationActivity(
        IDurableOperationAdapterResolver resolver,
        IHostApplicationLifetime applicationLifetime) : this(
            resolver,
            applicationLifetime,
            PassthroughInteractionAuthorityOperationContextProjector.Instance)
    {
    }

    /// <summary>Creates a reconciliation activity with explicit canonical-authority context projection.</summary>
    /// <param name="resolver">Exact canonical Request adapter resolver.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <param name="contextProjector">Host interpretation of canonical interaction authority.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public DurableTaskDurableOperationReconciliationActivity(
        IDurableOperationAdapterResolver resolver,
        IHostApplicationLifetime applicationLifetime,
        IInteractionAuthorityOperationContextProjector contextProjector)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
        this.contextProjector = contextProjector ?? throw new ArgumentNullException(nameof(contextProjector));
    }

    /// <inheritdoc />
    public override async Task<DurableTaskDurableOperationReconciliationResult> RunAsync(
        TaskActivityContext context,
        DurableOperationState input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var adapter = resolver.TryResolve(input.Request, out var resolved) && resolved is not null
            ? resolved
            : throw new InvalidOperationException(
                "No durable operation adapter is registered for the exact Request contract.");
        DurableOperationReferenceExecutor.ValidateReconciliationAdapterCapabilities(
            input.Binding,
            adapter.Capabilities);
        try
        {
            var observation = await DurableTaskActivityOperationContext.ExecuteAsync(
                    applicationLifetime,
                    input.Request.Context.AuthorityScope,
                    contextProjector,
                    operationContext => DurableOperationReferenceExecutor.ReconcileAsync(
                        operationContext,
                        input,
                        adapter))
                .ConfigureAwait(false);
            return new(observation, deadlineElapsed: false);
        }
        catch (DurableTaskWorkerStoppingException)
        {
            throw;
        }
        catch (DurableOperationDeadlineElapsedException)
        {
            return new(observation: null, deadlineElapsed: true);
        }
        catch
        {
            // A thrown reconciliation exception supplies no safe target-side evidence. Preserve ambiguity.
            return new(new DurableOperationUnresolved(), deadlineElapsed: false);
        }
    }
}

/// <summary>Activity boundary for one exact canonical Transition or Relation/Query host operation.</summary>
[DurableTask(DurableTaskSequentialProcessNames.HostOperationActivity)]
public sealed class DurableTaskProcessHostOperationActivity
    : TaskActivity<DurableTaskProcessHostOperation, ProcessOperationResult>
{
    readonly IAsyncProcessReferenceHost host;
    readonly IHostApplicationLifetime applicationLifetime;
    readonly IInteractionAuthorityOperationContextProjector contextProjector;

    /// <summary>Creates an activity over the application's canonical Process host.</summary>
    /// <param name="host">Asynchronous host that resolves exact Transition and Relation/Query operations.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public DurableTaskProcessHostOperationActivity(
        IAsyncProcessReferenceHost host,
        IHostApplicationLifetime applicationLifetime) : this(
            host,
            applicationLifetime,
            PassthroughInteractionAuthorityOperationContextProjector.Instance)
    {
    }

    /// <summary>Creates an activity with explicit canonical-authority context projection.</summary>
    /// <param name="host">Asynchronous host that resolves exact Transition and Relation/Query operations.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <param name="contextProjector">Host interpretation of canonical interaction authority.</param>
    /// <exception cref="ArgumentNullException">Any dependency is <see langword="null"/>.</exception>
    public DurableTaskProcessHostOperationActivity(
        IAsyncProcessReferenceHost host,
        IHostApplicationLifetime applicationLifetime,
        IInteractionAuthorityOperationContextProjector contextProjector)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
        this.contextProjector = contextProjector ?? throw new ArgumentNullException(nameof(contextProjector));
    }

    /// <inheritdoc />
    public override async Task<ProcessOperationResult> RunAsync(
        TaskActivityContext context,
        DurableTaskProcessHostOperation input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var result = await DurableTaskActivityOperationContext.ExecuteAsync(
            applicationLifetime,
            input.Kind switch
            {
                DurableTaskProcessHostOperationKind.Transition => input.Transition!.Context.AuthorityScope,
                DurableTaskProcessHostOperationKind.RelationQuery => input.RelationQuery!.Context.AuthorityScope,
                _ => throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "Unsupported host operation kind.")
            },
            contextProjector,
            operationContext => input.Kind switch
            {
                DurableTaskProcessHostOperationKind.Transition =>
                    host.InvokeTransitionAsync(operationContext, input.Transition!),
                DurableTaskProcessHostOperationKind.RelationQuery =>
                    host.EvaluateRelationAsync(operationContext, input.RelationQuery!),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(input),
                    input.Kind,
                    "Unsupported host operation kind.")
            }).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException(
            "The asynchronous Process host returned null operation evidence.");
    }
}

/// <summary>Activity boundary for exact canonical Signal-target resolution.</summary>
[DurableTask(DurableTaskSequentialProcessNames.SignalTargetResolutionActivity)]
public sealed class DurableTaskProcessSignalTargetActivity
    : TaskActivity<ProcessSignalTargetResolution, ProcessSignalTargetResult>
{
    readonly IAsyncProcessReferenceHost host;
    readonly IHostApplicationLifetime applicationLifetime;
    readonly IInteractionAuthorityOperationContextProjector contextProjector;

    /// <summary>Creates a Signal-target activity over the application's canonical Process host.</summary>
    /// <param name="host">Asynchronous host that resolves portable values into the closed canonical interaction-target union.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public DurableTaskProcessSignalTargetActivity(
        IAsyncProcessReferenceHost host,
        IHostApplicationLifetime applicationLifetime) : this(
            host,
            applicationLifetime,
            PassthroughInteractionAuthorityOperationContextProjector.Instance)
    {
    }

    /// <summary>Creates an activity with explicit canonical-authority context projection.</summary>
    /// <param name="host">Asynchronous host that resolves portable values into interaction targets.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <param name="contextProjector">Host interpretation of canonical interaction authority.</param>
    /// <exception cref="ArgumentNullException">Any dependency is <see langword="null"/>.</exception>
    public DurableTaskProcessSignalTargetActivity(
        IAsyncProcessReferenceHost host,
        IHostApplicationLifetime applicationLifetime,
        IInteractionAuthorityOperationContextProjector contextProjector)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
        this.contextProjector = contextProjector ?? throw new ArgumentNullException(nameof(contextProjector));
    }

    /// <inheritdoc />
    public override async Task<ProcessSignalTargetResult> RunAsync(
        TaskActivityContext context,
        ProcessSignalTargetResolution input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        return await DurableTaskActivityOperationContext.ExecuteAsync(
                applicationLifetime,
                input.Context.AuthorityScope,
                contextProjector,
                operationContext => host.ResolveSignalTargetAsync(operationContext, input))
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The asynchronous Process host returned null Signal-target evidence.");
    }
}

static class DurableTaskActivityOperationContext
{
    internal static TaskOptions WorkerStoppingRetryOptions { get; } = TaskOptions.FromRetryHandler(
        static context => IsWorkerStoppingFailure(context.LastFailure));

    internal static OperationContext Create(IHostApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        return OperationContext.Create(
            traceContext: System.Diagnostics.Activity.Current?.Context,
            cancellationToken: applicationLifetime.ApplicationStopping);
    }

    internal static async Task<TResult> ExecuteAsync<TResult>(
        IHostApplicationLifetime applicationLifetime,
        InteractionAuthorityScope authorityScope,
        IInteractionAuthorityOperationContextProjector contextProjector,
        Func<OperationContext, ValueTask<TResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        ArgumentNullException.ThrowIfNull(authorityScope);
        ArgumentNullException.ThrowIfNull(contextProjector);
        ArgumentNullException.ThrowIfNull(execute);
        var operationContext = Project(Create(applicationLifetime), authorityScope, contextProjector);
        try
        {
            operationContext.ThrowIfCancellationRequested();
            return await execute(operationContext).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
            when (applicationLifetime.ApplicationStopping.IsCancellationRequested)
        {
            throw new DurableTaskWorkerStoppingException(exception);
        }
    }

    internal static OperationContext Project(
        OperationContext context,
        InteractionAuthorityScope authorityScope,
        IInteractionAuthorityOperationContextProjector contextProjector)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorityScope);
        ArgumentNullException.ThrowIfNull(contextProjector);
        var projected = contextProjector.Project(context, authorityScope)
            ?? throw new InvalidOperationException("The interaction-authority context projector returned null.");
        if (!ReferenceEquals(projected.TimeProvider, context.TimeProvider)
            || projected.StartedUtc != context.StartedUtc
            || projected.TraceContext != context.TraceContext
            || projected.CancellationToken != context.CancellationToken)
        {
            throw new InvalidOperationException(
                "The interaction-authority context projector changed physical time, trace, or cancellation evidence.");
        }
        return projected;
    }

    internal static bool IsWorkerStoppingFailure(TaskFailureDetails failure) =>
        string.Equals(
            failure.ErrorType,
            typeof(DurableTaskWorkerStoppingException).FullName,
            StringComparison.Ordinal);
}

/// <summary>Activity boundary for target-deduplicated canonical domain-event publication.</summary>
[DurableTask(DurableTaskSequentialProcessNames.DomainEventPublicationActivity)]
public sealed class DurableTaskDomainEventPublicationActivity
    : TaskActivity<DomainEventPublicationInvocation, DurableTaskDomainEventPublication>
{
    readonly DurableTaskSequentialProcessPlanCatalog catalog;
    readonly IInteractionAuthorityOperationContextProjector contextProjector;

    /// <summary>Creates a publication activity over exact worker deployment policy.</summary>
    /// <param name="catalog">Worker catalog containing deterministic publisher resolution.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    public DurableTaskDomainEventPublicationActivity(DurableTaskSequentialProcessPlanCatalog catalog) : this(
        catalog,
        PassthroughInteractionAuthorityOperationContextProjector.Instance)
    {
    }

    /// <summary>Creates a publication activity with explicit canonical-authority context projection.</summary>
    /// <param name="catalog">Worker catalog containing deterministic publisher resolution.</param>
    /// <param name="contextProjector">Host interpretation of canonical interaction authority.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public DurableTaskDomainEventPublicationActivity(
        DurableTaskSequentialProcessPlanCatalog catalog,
        IInteractionAuthorityOperationContextProjector contextProjector)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.contextProjector = contextProjector ?? throw new ArgumentNullException(nameof(contextProjector));
    }

    /// <inheritdoc />
    public override async Task<DurableTaskDomainEventPublication> RunAsync(
        TaskActivityContext context,
        DomainEventPublicationInvocation input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var publisher = catalog.ResolveDomainEventPublisher(input.DomainEvent.Contract);
        var operationContext = DurableTaskActivityOperationContext.Project(
            OperationContext.Create(),
            input.DomainEvent.Context.AuthorityScope,
            contextProjector);
        var acknowledgement = await publisher.PublishAsync(operationContext, input).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The domain-event publisher returned null acknowledgement evidence.");
        return DurableTaskDomainEventPublication.From(input, operationContext.UtcNow, acknowledgement);
    }
}

/// <summary>Physical orchestration failure corresponding to a canonical authored Process failure.</summary>
public sealed class DurableTaskProcessFailedException : Exception
{
    /// <summary>Creates a failure with canonical diagnostic detail.</summary>
    /// <param name="message">Non-empty canonical failure detail.</param>
    public DurableTaskProcessFailedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Physical activity failure emitted when a Durable Task worker stops while canonical Process activity work is in flight.
/// </summary>
/// <remarks>
/// The Process orchestrator retries only this adapter-owned failure on an equivalent worker. It is physical Scheduler
/// evidence and does not represent authored Process failure, cancellation, or attempt restart.
/// </remarks>
public sealed class DurableTaskWorkerStoppingException : Exception
{
    internal DurableTaskWorkerStoppingException(OperationCanceledException innerException)
        : base(
            "The Durable Task worker stopped while canonical Process activity work was in flight; "
            + "an equivalent worker must retry the exact activity.",
            innerException)
    {
    }
}

/// <summary>Physical fail-closed signal that a durable Request requires authored recovery evidence.</summary>
public sealed class DurableTaskDurableOperationRecoveryRequiredException : Exception
{
    /// <summary>Creates a failure retaining exact logical operation and recovery status evidence.</summary>
    /// <param name="operationId">Canonical Request emission identity.</param>
    /// <param name="disposition">Target reason automatic execution stopped.</param>
    /// <param name="status">Canonical durable-operation status requiring recovery.</param>
    /// <param name="recoveryIntent">Exact reconciliation or escalation intent when the status declares one.</param>
    /// <exception cref="ArgumentException"><paramref name="operationId"/> is default.</exception>
    public DurableTaskDurableOperationRecoveryRequiredException(
        EmissionId operationId,
        DurableTaskDurableOperationDisposition disposition,
        DurableOperationStatus status,
        DurableOperationRecoveryIntent? recoveryIntent)
        : base($"Durable Request '{operationId.Value}' requires authored recovery: {disposition}/{status}.")
    {
        if (string.IsNullOrWhiteSpace(operationId.Value))
        {
            throw new ArgumentException("A recovery failure requires its operation identity.", nameof(operationId));
        }
        OperationId = operationId;
        Disposition = disposition;
        Status = status;
        RecoveryIntent = recoveryIntent;
    }

    /// <summary>Canonical Request emission identity.</summary>
    public EmissionId OperationId { get; }

    /// <summary>Target reason automatic execution stopped.</summary>
    public DurableTaskDurableOperationDisposition Disposition { get; }

    /// <summary>Canonical durable-operation status requiring authored evidence.</summary>
    public DurableOperationStatus Status { get; }

    /// <summary>Exact reconciliation or escalation intent when declared by the operation state.</summary>
    public DurableOperationRecoveryIntent? RecoveryIntent { get; }
}

/// <summary>Idempotent client operations for the generic bounded Process orchestration.</summary>
public static class DurableTaskSequentialProcessClientExtensions
{
    /// <summary>Schedules one exact Process start or reuses an identical existing physical instance.</summary>
    /// <param name="client">Standalone Durable Task client.</param>
    /// <param name="start">Exact canonical Process start evidence.</param>
    /// <param name="cancellationToken">Cancels transport admission only.</param>
    /// <returns>The stable physical instance identity and whether existing equal admission was replayed.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="client"/> or <paramref name="start"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The stable physical identity already exists with different canonical start evidence.
    /// </exception>
    public static async Task<DurableTaskProcessScheduleResult> ScheduleCohesiveProcessAsync(
        this DurableTaskClient client,
        DurableTaskSequentialProcessStart start,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(start);
        if (start.Resume is not null)
        {
            throw new ArgumentException(
                "Client admission accepts only initial Process starts; resume state is target-owned.",
                nameof(start));
        }
        var instanceId = DurableTaskProcessExecutionIdentity.GetPhysicalInstanceId(
            start.Receipt.Request.Context.Authorization.AuthorityScope,
            start.Receipt.Request.InitialContinuation.ProcessInstanceId);
        var options = CreateStartOptions(start);
        try
        {
            _ = await client.ScheduleNewOrchestrationInstanceAsync(
                DurableTaskSequentialProcessNames.Orchestration,
                start,
                options,
                cancellationToken).ConfigureAwait(false);
            return new(instanceId, Replayed: false);
        }
        catch (OrchestrationAlreadyExistsException)
        {
            var existing = await client.GetInstanceAsync(
                instanceId,
                getInputsAndOutputs: true,
                cancellationToken).ConfigureAwait(false);
            var retained = existing?.ReadInputAs<DurableTaskSequentialProcessStart>();
            var converter = DurableTaskProcessDataConverter.Create();
            if (retained is null
                || !string.Equals(
                    converter.Serialize(retained.Receipt),
                    converter.Serialize(start.Receipt),
                    StringComparison.Ordinal)
                || !string.Equals(
                    converter.Serialize(retained.ActivationContext),
                    converter.Serialize(start.ActivationContext),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The stable Durable Task Process instance identity is already bound to different start evidence.");
            }
            return new(instanceId, Replayed: true);
        }
    }

    internal static StartOrchestrationOptions CreateStartOptions(DurableTaskSequentialProcessStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        var instanceId = DurableTaskProcessExecutionIdentity.GetPhysicalInstanceId(
            start.Receipt.Request.Context.Authorization.AuthorityScope,
            start.Receipt.Request.InitialContinuation.ProcessInstanceId);
        return new StartOrchestrationOptions(instanceId)
        {
            Tags = DurableTaskProcessTags.Create(start.Receipt)
        }
            .WithDedupeStatuses([.. StartOrchestrationOptionsExtensions.ValidDedupeStatuses]);
    }

    /// <summary>Raises one canonical interaction to the exact physical instance selected by a Process start.</summary>
    /// <param name="client">Standalone Durable Task client.</param>
    /// <param name="start">Original canonical start used to derive the physical instance identity.</param>
    /// <param name="input">Canonical token-addressed interaction evidence.</param>
    /// <param name="cancellationToken">Cancels transport delivery only.</param>
    /// <returns>A task that completes when the provider admits the external event.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="client"/>, <paramref name="start"/>, or <paramref name="input"/> is
    /// <see langword="null"/>.
    /// </exception>
    public static Task RaiseCohesiveProcessInteractionAsync(
        this DurableTaskClient client,
        DurableTaskSequentialProcessStart start,
        ProcessActivationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(input);
        return client.RaiseEventAsync(
            DurableTaskSequentialProcessIdentities.OrchestrationInstance(start),
            DurableTaskSequentialProcessNames.InteractionEvent,
            input,
            cancellationToken);
    }

    /// <summary>Raises one canonical lifecycle command to the exact physical instance selected by a Process start.</summary>
    /// <remarks>
    /// This is a lower-level transport operation retained for callers that already hold exact start evidence.
    /// Completion confirms provider admission of the external event only. Use
    /// <see cref="DurableTaskProcessControlAdmissionClientExtensions.AdmitCohesiveProcessControlAsync"/> for the
    /// durable request/reply binding that returns the exact safe canonical result. Custom status is not a command
    /// receipt channel.
    /// </remarks>
    /// <param name="client">Standalone Durable Task client.</param>
    /// <param name="start">Original canonical start used to derive the physical instance identity.</param>
    /// <param name="command">Canonical lifecycle command to evaluate inside the orchestration.</param>
    /// <param name="cancellationToken">Cancels transport delivery only; it never requests semantic cancellation.</param>
    /// <returns>A task that completes when the provider admits the external event.</returns>
    /// <exception cref="ArgumentNullException">Any required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The command targets another Process instance or carries authorization from another authority scope.
    /// </exception>
    public static Task RaiseCohesiveProcessControlAsync(
        this DurableTaskClient client,
        DurableTaskSequentialProcessStart start,
        ProcessControlCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(command);
        if (command.Context.ProcessInstanceId
            != start.Receipt.Request.InitialContinuation.ProcessInstanceId)
        {
            throw new ArgumentException(
                "A Durable Task lifecycle command must target the started Process instance.",
                nameof(command));
        }
        if (command.Context.Authorization.AuthorityScope != start.ActivationContext.AuthorityScope)
        {
            throw new ArgumentException(
                "A Durable Task lifecycle command must carry the started Process authority scope.",
                nameof(command));
        }

        var action = DurableTaskProcessControlProtocol.GetAction(command);
        var observedAtUtc = DateTimeOffset.UtcNow;
        if (observedAtUtc < command.Context.IssuedAtUtc)
            observedAtUtc = command.Context.IssuedAtUtc;
        var invocation = new ExecutionApiInvocationContext(
            command.Context.Authorization,
            command.Context.Provenance,
            command.Context.IssuedAtUtc,
            observedAtUtc,
            [ExecutionControlApiWireNames.AuthorizationRequirement(action)]);
        var admission = new DurableTaskProcessControlAdmission(command, invocation);
        var response = DurableTaskProcessControlProtocol.Response(
            start.ActivationContext.AuthorityScope,
            command);
        return client.RaiseEventAsync(
            DurableTaskSequentialProcessIdentities.OrchestrationInstance(start),
            DurableTaskSequentialProcessNames.ControlEvent,
            new DurableTaskProcessControlRequest(admission, response.Key),
            cancellationToken);
    }
}
