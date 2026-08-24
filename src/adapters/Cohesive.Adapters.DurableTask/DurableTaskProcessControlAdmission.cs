using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Exact trusted input to standalone Durable Task Process lifecycle-control admission.</summary>
public sealed record DurableTaskProcessControlAdmission
{
    /// <summary>Creates one trusted lifecycle-control admission.</summary>
    /// <param name="request">Canonical caller request whose authority evidence will be rebound.</param>
    /// <param name="invocation">Trusted server-side authorization, timing, provenance, and API grants.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="request"/> is not a supported lifecycle mutation.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="invocation"/> does not grant the command's canonical API authorization requirement.
    /// </exception>
    [JsonConstructor]
    public DurableTaskProcessControlAdmission(
        ProcessControlCommand request,
        ExecutionApiInvocationContext invocation)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        var action = DurableTaskProcessControlProtocol.GetAction(request);
        var requirement = ExecutionControlApiWireNames.AuthorizationRequirement(action);
        if (!invocation.GrantsRequirement(requirement))
        {
            throw new UnauthorizedAccessException(
                $"Durable Task Process-control admission requires authorization grant '{requirement}'.");
        }
    }

    /// <summary>Canonical caller request whose authority evidence will be rebound.</summary>
    public ProcessControlCommand Request { get; }

    /// <summary>Trusted server-side authorization, timing, provenance, and API grants.</summary>
    public ExecutionApiInvocationContext Invocation { get; }
}

/// <summary>Client operations for canonical lifecycle-control admission through standalone Durable Task.</summary>
public static class DurableTaskProcessControlAdmissionClientExtensions
{
    /// <summary>Creates the authoritative lifecycle-control binding used by the canonical execution API dispatcher.</summary>
    /// <param name="client">Standalone Durable Task client for the admitted worker task hub.</param>
    /// <returns>A reusable asynchronous dispatcher for Pause, Continue, RestartAttempt, Cancel, and Terminate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is <see langword="null"/>.</exception>
    public static ExecutionProcessControlDispatcher CreateCohesiveProcessControlDispatcher(
        this DurableTaskClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return DispatchAsync;

        async ValueTask<ExecutionControlResult> DispatchAsync(
            OperationContext context,
            ProcessControlCommand request,
            ExecutionApiInvocationContext invocation)
        {
            ArgumentNullException.ThrowIfNull(context);
            context.ThrowIfCancellationRequested();
            return await client.AdmitCohesiveProcessControlAsync(
                    new(request, invocation),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Durably admits one logical Process lifecycle command and waits for its exact safe canonical result.</summary>
    /// <remarks>
    /// Cancellation stops only the caller's wait. The content-addressed response remains durable, and an exact retry
    /// returns that original result. Scheduler external-event acknowledgement is never returned as command success.
    /// </remarks>
    /// <param name="client">Standalone Durable Task client for the same task hub as the Process worker.</param>
    /// <param name="admission">Caller request plus trusted invocation evidence.</param>
    /// <param name="cancellationToken">Cancels waiting and transport calls, never semantic Process execution.</param>
    /// <returns>The exact safe canonical decision result.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">Canonical authorization rejects the command.</exception>
    /// <exception cref="KeyNotFoundException">No Process is visible at the trusted logical address.</exception>
    /// <exception cref="InvalidOperationException">
    /// The physical Process terminates without transferring canonical terminal-control state.
    /// </exception>
    /// <exception cref="OperationCanceledException">Waiting is cancelled.</exception>
    public static async Task<ExecutionControlResult> AdmitCohesiveProcessControlAsync(
        this DurableTaskClient client,
        DurableTaskProcessControlAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(admission);
        var admissionInstanceId = "cohesive-control-admission:v1:" + Guid.NewGuid().ToString("N");
        _ = await client.ScheduleNewOrchestrationInstanceAsync(
            DurableTaskSequentialProcessNames.ControlAdmissionOrchestration,
            admission,
            new StartOrchestrationOptions(admissionInstanceId),
            cancellationToken).ConfigureAwait(false);
        var completed = await client.WaitForInstanceCompletionAsync(
            admissionInstanceId,
            getInputsAndOutputs: true,
            cancellationToken).ConfigureAwait(false);
        if (completed.RuntimeStatus != OrchestrationRuntimeStatus.Completed)
        {
            throw new InvalidOperationException(
                $"Durable Task Process-control admission '{admissionInstanceId}' completed with provider status "
                + $"'{completed.RuntimeStatus}': {completed.FailureDetails?.ErrorMessage ?? "no failure details"}.");
        }
        return (completed.ReadOutputAs<DurableTaskProcessControlResponse>()
                ?? throw new InvalidOperationException(
                    $"Durable Task Process-control admission '{admissionInstanceId}' retained no canonical response."))
            .RequireResult();
    }
}

sealed class DurableTaskProcessControlAdmissionOrchestrator
    : TaskOrchestrator<DurableTaskProcessControlAdmission, DurableTaskProcessControlResponse>
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public override async Task<DurableTaskProcessControlResponse> RunAsync(
        TaskOrchestrationContext context,
        DurableTaskProcessControlAdmission input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var scope = input.Invocation.Authorization.AuthorityScope;
        var processInstanceId = input.Request.Context.ProcessInstanceId;
        var start = await context.Entities.CallEntityAsync<DurableTaskSequentialProcessStart?>(
            DurableTaskProcessControlProtocol.StartIndex(scope, processInstanceId),
            nameof(DurableTaskProcessStartIndexEntity.Read),
            new CallEntityOptions()).ConfigureAwait(true);
        if (start is null)
            return new(DurableTaskProcessControlResponseKind.NotFound);
        if (start.Receipt.Request.InitialContinuation.ProcessInstanceId != processInstanceId
            || start.ActivationContext.AuthorityScope != scope)
        {
            throw new InvalidOperationException(
                "The retained Process-start index conflicts with its trusted logical address.");
        }

        var responseId = DurableTaskProcessControlProtocol.Response(scope, input.Request);
        if (await ReadResponseAsync(context, responseId).ConfigureAwait(true) is { } retained)
            return retained;

        context.SendEvent(
            DurableTaskSequentialProcessIdentities.OrchestrationInstance(start),
            DurableTaskSequentialProcessNames.ControlEvent,
            new DurableTaskProcessControlRequest(input, responseId.Key));
        var terminalId = DurableTaskProcessControlProtocol.Terminal(scope, processInstanceId);
        var terminalAdmissionApplied = false;
        while (true)
        {
            if (await ReadResponseAsync(context, responseId).ConfigureAwait(true) is { } response)
                return response;

            var terminal = await context.Entities.CallEntityAsync<DurableTaskTerminalProcessControlState>(
                terminalId,
                nameof(DurableTaskTerminalProcessControlEntity.Read),
                new CallEntityOptions()).ConfigureAwait(true);
            if (terminal.Terminal is not null && !terminalAdmissionApplied)
            {
                var terminalResponse = await context.Entities.CallEntityAsync<DurableTaskProcessControlResponse>(
                    terminalId,
                    nameof(DurableTaskTerminalProcessControlEntity.Apply),
                    new DurableTaskTerminalProcessControlAdmission(input),
                    new CallEntityOptions()).ConfigureAwait(true);
                _ = await context.Entities.CallEntityAsync<DurableTaskProcessControlResponse>(
                    responseId,
                    nameof(DurableTaskProcessControlResponseEntity.Claim),
                    terminalResponse,
                    new CallEntityOptions()).ConfigureAwait(true);
                terminalAdmissionApplied = true;
                continue;
            }

            await context.CreateTimer(PollInterval, CancellationToken.None).ConfigureAwait(true);
        }
    }

    static Task<DurableTaskProcessControlResponse?> ReadResponseAsync(
        TaskOrchestrationContext context,
        EntityInstanceId responseId) =>
        context.Entities.CallEntityAsync<DurableTaskProcessControlResponse?>(
            responseId,
            nameof(DurableTaskProcessControlResponseEntity.Read),
            new CallEntityOptions());
}

sealed record DurableTaskProcessControlRequest(
    DurableTaskProcessControlAdmission Admission,
    string ResponseIdentity);

enum DurableTaskProcessControlResponseKind
{
    Unspecified = 0,
    Result = 1,
    Forbidden = 2,
    NotFound = 3
}

sealed record DurableTaskProcessControlResponse
{
    [JsonConstructor]
    public DurableTaskProcessControlResponse(
        DurableTaskProcessControlResponseKind kind,
        ExecutionControlResult? result = null)
    {
        if (kind is DurableTaskProcessControlResponseKind.Unspecified
            || !Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A control response kind must be explicit.");
        }
        if ((kind == DurableTaskProcessControlResponseKind.Result) != (result is not null))
            throw new ArgumentException("Only a successful control response carries a safe result.", nameof(result));
        Kind = kind;
        Result = result;
    }

    public DurableTaskProcessControlResponseKind Kind { get; }

    public ExecutionControlResult? Result { get; }

    internal static DurableTaskProcessControlResponse FromDecision(
        ProcessControlDecision decision,
        ExecutionStatus status) => decision.Disposition switch
        {
            ProcessControlDecisionDisposition.Unauthorized => new(DurableTaskProcessControlResponseKind.Forbidden),
            ProcessControlDecisionDisposition.TargetMismatch => new(DurableTaskProcessControlResponseKind.NotFound),
            _ => new(
                DurableTaskProcessControlResponseKind.Result,
                ExecutionControlResult.FromDecision(decision, status))
        };

    internal ExecutionControlResult RequireResult() => Kind switch
    {
        DurableTaskProcessControlResponseKind.Result => Result!,
        DurableTaskProcessControlResponseKind.Forbidden => throw new UnauthorizedAccessException(
            "Canonical Process-control authorization rejected the command."),
        DurableTaskProcessControlResponseKind.NotFound => throw new KeyNotFoundException(
            "No Process is visible at the trusted logical address."),
        _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, "Unsupported control response kind.")
    };
}

sealed record DurableTaskProcessControlResponseState(DurableTaskProcessControlResponse? Response);

sealed class DurableTaskProcessControlResponseEntity : TaskEntity<DurableTaskProcessControlResponseState>
{
    protected override DurableTaskProcessControlResponseState InitializeState(TaskEntityOperation entityOperation) =>
        new(Response: null);

    public DurableTaskProcessControlResponse? Read() => State.Response;

    public DurableTaskProcessControlResponse Claim(DurableTaskProcessControlResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (State.Response is { } retained)
            return retained;
        State = new(response);
        return response;
    }
}

sealed record DurableTaskTerminalProcessControlState(
    DurableTaskSequentialProcessResult? Terminal,
    ProcessControlState? Control);

sealed record DurableTaskTerminalProcessControlAdmission(
    DurableTaskProcessControlAdmission Admission);

sealed class DurableTaskTerminalProcessControlEntity : TaskEntity<DurableTaskTerminalProcessControlState>
{
    static readonly ProcessControlReferenceExecutor Executor = new(CreateEmptyContracts());

    protected override DurableTaskTerminalProcessControlState InitializeState(TaskEntityOperation entityOperation) =>
        new(Terminal: null, Control: null);

    public DurableTaskTerminalProcessControlState Read() => State;

    public DurableTaskTerminalProcessControlState Handoff(DurableTaskSequentialProcessResult terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!terminal.Control.IsTerminal
            && terminal.State.Terminal.Kind == ExecutionTerminalOutcomeKind.None)
        {
            throw new ArgumentException("Terminal-control handoff requires a terminal canonical Process.", nameof(terminal));
        }
        if (State.Terminal is { } retained)
        {
            if (retained != terminal)
                throw new InvalidOperationException("Terminal-control authority is already bound to different evidence.");
            return State;
        }
        State = new(terminal, terminal.Control);
        return State;
    }

    public DurableTaskProcessControlResponse Apply(DurableTaskTerminalProcessControlAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var terminal = State.Terminal
            ?? throw new InvalidOperationException("Terminal-control authority has not been handed off.");
        var control = State.Control
            ?? throw new InvalidOperationException("Terminal-control authority retained no canonical state.");
        var canonical = ExecutionProcessControlCommandAdmission.Rebind(
            admission.Admission.Request,
            admission.Admission.Invocation,
            control);
        var decision = Executor.Apply(
            control,
            canonical,
            admission.Admission.Invocation.ObservedAtUtc);
        State = new(terminal, decision.State);
        var status = ProcessExecutionStatusProjector.Project(
            terminal.State,
            decision.State,
            [.. terminal.DurableOperations.Select(static operation => operation.State)]);
        var response = DurableTaskProcessControlResponse.FromDecision(decision, status);
        return response;
    }

    static InteractionContractCatalog CreateEmptyContracts()
    {
        var validation = InteractionContractCatalog.TryCreate([], out var catalog);
        if (!validation.IsValid || catalog is null)
            throw new InvalidOperationException("The empty canonical interaction catalog could not be constructed.");
        return catalog;
    }
}

static class DurableTaskProcessControlProtocol
{
    static readonly ExecutionApiInvocationContext IdentityInvocation = new(
        new(
            "cohesive.adapters.durable-task.control-response-identity",
            new("cohesive.adapters.durable-task.identity"),
            "physical-identity-only"),
        new(
            new("cohesive.adapters.durable-task.control-response-identity"),
            new("physical-identity-only"),
            DocumentOrigin.Unknown),
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        []);

    internal static string GetAction(ProcessControlCommand request) => request switch
    {
        InspectProcessCommand => ExecutionControlWireNames.Inspect,
        PauseProcessCommand => ExecutionControlWireNames.Pause,
        ContinueProcessCommand => ExecutionControlWireNames.Continue,
        RestartProcessAttemptCommand => ExecutionControlWireNames.RestartAttempt,
        CancelProcessCommand => ExecutionControlWireNames.Cancel,
        TerminateProcessCommand => ExecutionControlWireNames.Terminate,
        _ => throw new ArgumentException(
            "Durable Task control admission supports Inspect, Pause, Continue, RestartAttempt, Cancel, and Terminate.",
            nameof(request))
    };

    internal static EntityInstanceId StartIndex(
        InteractionAuthorityScope scope,
        ProcessInstanceId processInstanceId) => new(
        DurableTaskSequentialProcessNames.StartAdmissionIndexEntity,
        DurableTaskSequentialProcessIdentities.StartAdmissionIndex(
            scope,
            "instance",
            processInstanceId.Value));

    internal static EntityInstanceId Response(
        InteractionAuthorityScope scope,
        ProcessControlCommand request)
    {
        _ = GetAction(request);
        var normalized = ExecutionProcessControlCommandAdmission.Rebind(request, IdentityInvocation);
        var fingerprint = Convert.ToHexStringLower(
            SHA256.HashData(ProcessControlJsonSerializer.GetCanonicalBytes(normalized)));
        return new(
            DurableTaskSequentialProcessNames.ControlResponseEntity,
            DurableTaskSequentialProcessIdentities.ControlResponse(scope, fingerprint));
    }

    internal static EntityInstanceId Terminal(
        InteractionAuthorityScope scope,
        ProcessInstanceId processInstanceId) => new(
        DurableTaskSequentialProcessNames.TerminalControlEntity,
        DurableTaskSequentialProcessIdentities.TerminalControl(scope, processInstanceId));
}
