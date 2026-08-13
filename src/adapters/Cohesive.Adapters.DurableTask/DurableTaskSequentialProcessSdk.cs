using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using DurableTask.Core.Exceptions;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Worker;
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
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(catalog);
        var converter = DurableTaskProcessDataConverter.Create();
        builder.Services.TryAddSingleton(catalog);
        builder.Services.TryAddSingleton(converter);
        builder.Services.TryAddSingleton<IDurableOperationAdapterResolver>(
            EmptyDurableOperationAdapterResolver.Instance);
        builder.Services.TryAddSingleton<IDurableOperationExceptionClassifier>(
            ConservativeDurableOperationExceptionClassifier.Instance);
        builder.Configure(options => options.DataConverter = converter);
        return builder.AddTasks(tasks =>
        {
            tasks.AddOrchestrator(new DurableTaskSequentialProcessOrchestrator(catalog));
            tasks.AddActivity<DurableTaskProcessHostOperationActivity>();
            tasks.AddActivity<DurableTaskProcessSignalTargetActivity>();
            tasks.AddActivity<DurableTaskDomainEventPublicationActivity>();
            tasks.AddActivity<DurableTaskDurableOperationActivity>();
            tasks.AddActivity<DurableTaskDurableOperationReconciliationActivity>();
        });
    }
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
        Func<Task<ProcessChildCancellationIntent>>? waitForChildCancellation = context.Parent is null
            ? null
            : () => context.WaitForExternalEvent<ProcessChildCancellationIntent>(
                DurableTaskSequentialProcessNames.ChildCancellationEvent);
        var result = await DurableTaskSequentialProcessInterpreter.RunAsync(
            physical.CanonicalPlan,
            input,
            catalog.BindingResolver,
            operation => context.CallActivityAsync<ProcessOperationResult>(
                DurableTaskSequentialProcessNames.HostOperationActivity,
                operation),
            invocation => context.CallActivityAsync<DurableTaskDurableOperationAttemptResult>(
                DurableTaskSequentialProcessNames.DurableOperationActivity,
                invocation),
            invocation => ExecuteChildProcessAsync(context, catalog, invocation),
            state => context.CallActivityAsync<DurableTaskDurableOperationReconciliationResult>(
                DurableTaskSequentialProcessNames.DurableOperationReconciliationActivity,
                state),
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
                resolution),
            signal => DeliverSignal(context, signal),
            () => context.WaitForExternalEvent<ProcessControlCommand>(
                DurableTaskSequentialProcessNames.ControlEvent),
            domainEvent => context.CallActivityAsync<DurableTaskDomainEventPublication>(
                DurableTaskSequentialProcessNames.DomainEventPublicationActivity,
                domainEvent)).ConfigureAwait(true);

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
        if (result.Disposition == ProcessActivationDisposition.Failed && context.Parent is null)
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
                ordering: request.Context.Ordering));
        var result = await context.CallSubOrchestratorAsync<DurableTaskSequentialProcessResult>(
            DurableTaskSequentialProcessNames.Orchestration,
            start,
            new TaskOptions().WithInstanceId(
                DurableTaskSequentialProcessIdentities.OrchestrationInstance(start))).ConfigureAwait(true);
        return ProjectChildTerminal(child, target, result);
    }

    static DurableTaskDurableOperationAttemptResult ProjectChildTerminal(
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
        var value = terminal.Detail?.Value ?? PortableValue.Missing(child.Definition.Result);
        if (value.Contract != child.Definition.Result
            || value.State is PortableValueState.Unknown or PortableValueState.Failed)
        {
            throw new InvalidOperationException(
                "A child sub-orchestration returned terminal detail outside its exact result contract.");
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
        RequestTerminalOutcome outcome = terminal.Kind == ExecutionTerminalOutcomeKind.Completed
            ? new RequestResultOutcome(outcomeId, value)
            : new RequestFailureOutcome(outcomeId, value);
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

    /// <summary>Creates an activity over exact adapter resolution and explicit exception classification.</summary>
    /// <param name="resolver">Exact canonical Request adapter resolver.</param>
    /// <param name="exceptionClassifier">Provider-aware adapter exception classifier.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public DurableTaskDurableOperationActivity(
        IDurableOperationAdapterResolver resolver,
        IDurableOperationExceptionClassifier exceptionClassifier)
    {
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.exceptionClassifier = exceptionClassifier ?? throw new ArgumentNullException(nameof(exceptionClassifier));
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
            var observation = await DurableOperationReferenceExecutor.ExecuteAsync(
                    OperationContext.Create(),
                    input,
                    adapter)
                .ConfigureAwait(false);
            return new(observation, deadlineElapsed: false);
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

    /// <summary>Creates an activity over exact adapter resolution.</summary>
    /// <param name="resolver">Exact canonical Request adapter resolver.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is <see langword="null"/>.</exception>
    public DurableTaskDurableOperationReconciliationActivity(IDurableOperationAdapterResolver resolver) =>
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

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
            var observation = await DurableOperationReferenceExecutor.ReconcileAsync(
                    OperationContext.Create(),
                    input,
                    adapter)
                .ConfigureAwait(false);
            return new(observation, deadlineElapsed: false);
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

    /// <summary>Creates an activity over the application's canonical Process host.</summary>
    /// <param name="host">Asynchronous host that resolves exact Transition and Relation/Query operations.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public DurableTaskProcessHostOperationActivity(
        IAsyncProcessReferenceHost host,
        IHostApplicationLifetime applicationLifetime)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
    }

    /// <inheritdoc />
    public override async Task<ProcessOperationResult> RunAsync(
        TaskActivityContext context,
        DurableTaskProcessHostOperation input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var operationContext = DurableTaskActivityOperationContext.Create(applicationLifetime);
        operationContext.ThrowIfCancellationRequested();
        var result = input.Kind switch
        {
            DurableTaskProcessHostOperationKind.Transition =>
                await host.InvokeTransitionAsync(operationContext, input.Transition!).ConfigureAwait(false),
            DurableTaskProcessHostOperationKind.RelationQuery =>
                await host.EvaluateRelationAsync(operationContext, input.RelationQuery!).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "Unsupported host operation kind.")
        };
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

    /// <summary>Creates a Signal-target activity over the application's canonical Process host.</summary>
    /// <param name="host">Asynchronous host that resolves portable values into the closed canonical interaction-target union.</param>
    /// <param name="applicationLifetime">Worker lifetime supplying physical shutdown cancellation.</param>
    /// <exception cref="ArgumentNullException">Either dependency is <see langword="null"/>.</exception>
    public DurableTaskProcessSignalTargetActivity(
        IAsyncProcessReferenceHost host,
        IHostApplicationLifetime applicationLifetime)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.applicationLifetime = applicationLifetime
            ?? throw new ArgumentNullException(nameof(applicationLifetime));
    }

    /// <inheritdoc />
    public override async Task<ProcessSignalTargetResult> RunAsync(
        TaskActivityContext context,
        ProcessSignalTargetResolution input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var operationContext = DurableTaskActivityOperationContext.Create(applicationLifetime);
        operationContext.ThrowIfCancellationRequested();
        return await host.ResolveSignalTargetAsync(operationContext, input).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "The asynchronous Process host returned null Signal-target evidence.");
    }
}

static class DurableTaskActivityOperationContext
{
    internal static OperationContext Create(IHostApplicationLifetime applicationLifetime)
    {
        ArgumentNullException.ThrowIfNull(applicationLifetime);
        return OperationContext.Create(
            traceContext: System.Diagnostics.Activity.Current?.Context,
            cancellationToken: applicationLifetime.ApplicationStopping);
    }
}

/// <summary>Activity boundary for target-deduplicated canonical domain-event publication.</summary>
[DurableTask(DurableTaskSequentialProcessNames.DomainEventPublicationActivity)]
public sealed class DurableTaskDomainEventPublicationActivity
    : TaskActivity<DomainEventPublicationInvocation, DurableTaskDomainEventPublication>
{
    readonly DurableTaskSequentialProcessPlanCatalog catalog;

    /// <summary>Creates a publication activity over exact worker deployment policy.</summary>
    /// <param name="catalog">Worker catalog containing deterministic publisher resolution.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.</exception>
    public DurableTaskDomainEventPublicationActivity(DurableTaskSequentialProcessPlanCatalog catalog) =>
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    /// <inheritdoc />
    public override async Task<DurableTaskDomainEventPublication> RunAsync(
        TaskActivityContext context,
        DomainEventPublicationInvocation input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var publisher = catalog.ResolveDomainEventPublisher(input.DomainEvent.Contract);
        var operationContext = OperationContext.Create();
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
        var options = new StartOrchestrationOptions(instanceId)
        {
            Tags = DurableTaskProcessTags.Create(start.Receipt)
        }
            .WithDedupeStatuses([.. StartOrchestrationOptionsExtensions.ValidDedupeStatuses]);
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
    /// Completion confirms provider admission of the external event only. The command's canonical disposition,
    /// receipt, diagnostics, and any realization intent are exposed through the orchestration result or custom status.
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

        return client.RaiseEventAsync(
            DurableTaskSequentialProcessIdentities.OrchestrationInstance(start),
            DurableTaskSequentialProcessNames.ControlEvent,
            command,
            cancellationToken);
    }
}
