using System.Text.Json.Serialization;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using CanonicalProcessStartResult = Cohesive.Execution.ProcessStartResult;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Exact trusted input to standalone Durable Task Process-start admission.</summary>
/// <remarks>
/// The caller supplies stable logical identities and typed Process input through <see cref="Request"/>. The
/// admission orchestration replaces its authority, issuance, and provenance with <see cref="Invocation"/> before
/// canonical evaluation. Authority and provenance in <see cref="ActivationContext"/> are replaced by the same
/// trusted evidence if the start wins admission.
/// </remarks>
public sealed record DurableTaskProcessStartAdmission
{
    /// <summary>Creates one trusted Process-start admission input.</summary>
    /// <param name="request">Canonical caller request whose authority evidence will be rebound.</param>
    /// <param name="activationContext">Top-level execution correlation, delivery, causation, and ordering policy.</param>
    /// <param name="invocation">Trusted server-side authorization, timing, provenance, and API grants.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="invocation"/> does not grant canonical Process-start admission.
    /// </exception>
    [JsonConstructor]
    public DurableTaskProcessStartAdmission(
        ProcessStartRequest request,
        ProcessActivationContext activationContext,
        ExecutionApiInvocationContext invocation)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ActivationContext = activationContext ?? throw new ArgumentNullException(nameof(activationContext));
        Invocation = invocation ?? throw new ArgumentNullException(nameof(invocation));
        var requirement = ExecutionControlApiWireNames.AuthorizationRequirement(ProcessStartWireNames.Start);
        if (!invocation.GrantsRequirement(requirement))
        {
            throw new UnauthorizedAccessException(
                $"Durable Task Process-start admission requires authorization grant '{requirement}'.");
        }
    }

    /// <summary>Canonical caller request whose authority evidence will be rebound.</summary>
    public ProcessStartRequest Request { get; }

    /// <summary>Top-level execution correlation, delivery, causation, and ordering policy.</summary>
    public ProcessActivationContext ActivationContext { get; }

    /// <summary>Trusted server-side authorization, timing, provenance, and API grants.</summary>
    public ExecutionApiInvocationContext Invocation { get; }
}

/// <summary>Projects one trusted API start invocation into its canonical Process activation context.</summary>
/// <param name="context">Explicit API operation context.</param>
/// <param name="request">Canonical caller start request.</param>
/// <param name="invocation">Trusted API authorization, timing, provenance, and grants.</param>
/// <returns>Correlation, delivery, causation, and ordering policy for the initial Process activation.</returns>
/// <remarks>
/// Durable Task admission always replaces the returned authority scope and provenance with
/// <paramref name="invocation"/>. The projection owns only product or transport correlation and delivery policy.
/// </remarks>
public delegate ProcessActivationContext DurableTaskProcessStartActivationContextFactory(
    OperationContext context,
    ProcessStartRequest request,
    ExecutionApiInvocationContext invocation);

/// <summary>Client operations for canonical Process-start admission through standalone Durable Task.</summary>
public static class DurableTaskProcessStartAdmissionClientExtensions
{
    /// <summary>Creates the authoritative Process-start binding used by the canonical execution API dispatcher.</summary>
    /// <param name="client">Standalone Durable Task client for the admitted worker task hub.</param>
    /// <param name="activationContextFactory">
    /// Application composition policy for initial correlation, delivery, causation, and ordering.
    /// </param>
    /// <returns>
    /// A reusable asynchronous start dispatcher suitable for
    /// <see cref="InMemoryExecutionControlApiAdapter"/> composition.
    /// </returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    public static ExecutionProcessStartDispatcher CreateCohesiveProcessStartDispatcher(
        this DurableTaskClient client,
        DurableTaskProcessStartActivationContextFactory activationContextFactory)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(activationContextFactory);
        return DispatchAsync;

        async ValueTask<CanonicalProcessStartResult> DispatchAsync(
            OperationContext context,
            ProcessStartRequest request,
            ExecutionApiInvocationContext invocation)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(invocation);
            context.ThrowIfCancellationRequested();
            var activationContext = activationContextFactory(context, request, invocation)
                ?? throw new InvalidOperationException(
                    "The Durable Task Process-start activation context factory returned no context.");
            return await client.AdmitCohesiveProcessAsync(
                    new(request, activationContext, invocation),
                    context.CancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Durably admits and schedules one exact canonical Process start.</summary>
    /// <param name="client">Standalone Durable Task client for the same task hub as the admitted worker catalog.</param>
    /// <param name="admission">Canonical request plus trusted invocation and activation evidence.</param>
    /// <param name="cancellationToken">
    /// Cancels waiting for the durable admission result; it never cancels an accepted Process.
    /// </param>
    /// <returns>The canonical accepted, replayed, or conflict result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="client"/> or <paramref name="admission"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The admission orchestration fails, is terminated, or completes without a canonical result.
    /// </exception>
    /// <exception cref="OperationCanceledException">Waiting is cancelled.</exception>
    public static async Task<CanonicalProcessStartResult> AdmitCohesiveProcessAsync(
        this DurableTaskClient client,
        DurableTaskProcessStartAdmission admission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(admission);
        var admissionInstanceId = "cohesive-process-start-admission:v1:" + Guid.NewGuid().ToString("N");
        _ = await client.ScheduleNewOrchestrationInstanceAsync(
            DurableTaskSequentialProcessNames.StartAdmissionOrchestration,
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
                $"Durable Task Process-start admission '{admissionInstanceId}' completed with provider status "
                + $"'{completed.RuntimeStatus}': {completed.FailureDetails?.ErrorMessage ?? "no failure details"}.");
        }

        return completed.ReadOutputAs<CanonicalProcessStartResult>()
            ?? throw new InvalidOperationException(
                $"Durable Task Process-start admission '{admissionInstanceId}' retained no canonical result.");
    }
}

sealed class DurableTaskProcessStartAdmissionOrchestrator(
    DurableTaskSequentialProcessPlanCatalog catalog)
    : TaskOrchestrator<DurableTaskProcessStartAdmission, CanonicalProcessStartResult>
{
    readonly DurableTaskSequentialProcessPlanCatalog catalog =
        catalog ?? throw new ArgumentNullException(nameof(catalog));

    public override async Task<CanonicalProcessStartResult> RunAsync(
        TaskOrchestrationContext context,
        DurableTaskProcessStartAdmission input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        var request = input.Request;
        var plan = catalog.GetExact(request.Definition);
        _ = ProcessReferenceInterpreter.Create(
            plan.CanonicalPlan,
            request.InitialContinuation,
            request.Input ?? PortableValue.Missing(plan.CanonicalPlan.Definition.Input));
        var scope = input.Invocation.Authorization.AuthorityScope;
        var commandIndex = Index(scope, "command", request.Context.CommandId.Value);
        var idempotencyIndex = Index(scope, "idempotency", request.Context.IdempotencyKey.Value);
        var instanceIndex = Index(scope, "instance", request.Context.ProcessInstanceId.Value);
        EntityInstanceId[] indices = [commandIndex, idempotencyIndex, instanceIndex];

        await using (await context.Entities.LockEntitiesAsync(indices))
        {
            var sameCommand = await ReadAsync(context, commandIndex).ConfigureAwait(true);
            var sameIdempotency = await ReadAsync(context, idempotencyIndex).ConfigureAwait(true);
            var existingInstance = await ReadAsync(context, instanceIndex).ConfigureAwait(true);
            var evaluated = DurableTaskProcessStartAdmissionEvaluator.Evaluate(
                input,
                sameCommand,
                sameIdempotency,
                existingInstance);
            var decision = evaluated.Decision;

            if (!decision.RequiresPersistence)
                return decision.Result;

            var start = evaluated.AcceptedStart!;
            await ClaimAsync(
                context,
                commandIndex,
                new(start, DurableTaskProcessStartIndexClaimKind.Retain)).ConfigureAwait(true);
            await ClaimAsync(
                context,
                idempotencyIndex,
                new(start, DurableTaskProcessStartIndexClaimKind.Retain)).ConfigureAwait(true);
            await ClaimAsync(
                context,
                instanceIndex,
                new(start, DurableTaskProcessStartIndexClaimKind.RetainAndSchedule)).ConfigureAwait(true);
            return decision.Result;
        }
    }

    static EntityInstanceId Index(InteractionAuthorityScope scope, string kind, string identity) => new(
        DurableTaskSequentialProcessNames.StartAdmissionIndexEntity,
        DurableTaskSequentialProcessIdentities.StartAdmissionIndex(scope, kind, identity));

    static Task<DurableTaskSequentialProcessStart?> ReadAsync(
        TaskOrchestrationContext context,
        EntityInstanceId index) =>
        context.Entities.CallEntityAsync<DurableTaskSequentialProcessStart?>(
            index,
            nameof(DurableTaskProcessStartIndexEntity.Read),
            new CallEntityOptions());

    static async Task ClaimAsync(
        TaskOrchestrationContext context,
        EntityInstanceId index,
        DurableTaskProcessStartIndexClaim claim) =>
        _ = await context.Entities.CallEntityAsync<DurableTaskSequentialProcessStart>(
            index,
            nameof(DurableTaskProcessStartIndexEntity.Claim),
            claim,
            new CallEntityOptions()).ConfigureAwait(true);

}

internal sealed record DurableTaskProcessStartAdmissionEvaluation(
    ProcessStartDecision Decision,
    DurableTaskSequentialProcessStart? AcceptedStart);

internal static class DurableTaskProcessStartAdmissionEvaluator
{
    static readonly ProcessStartReferenceEvaluator Evaluator = new();

    internal static DurableTaskProcessStartAdmissionEvaluation Evaluate(
        DurableTaskProcessStartAdmission input,
        DurableTaskSequentialProcessStart? sameCommand,
        DurableTaskSequentialProcessStart? sameIdempotency,
        DurableTaskSequentialProcessStart? existingInstance)
    {
        ArgumentNullException.ThrowIfNull(input);
        var canonical = Rebind(
            input.Request,
            input.Invocation,
            sameCommand?.Receipt.Request.Context);
        var decision = Evaluator.Evaluate(
            canonical,
            new(
                sameCommand?.Receipt,
                sameIdempotency?.Receipt,
                existingInstance?.Receipt,
                existingInstance?.Receipt.CreateInitialState()),
            input.Invocation.ObservedAtUtc);
        var accepted = decision.RequiresPersistence
            ? new DurableTaskSequentialProcessStart(
                decision.Receipt!,
                Rebind(input.ActivationContext, input.Invocation))
            : null;
        return new(decision, accepted);
    }

    static ProcessStartRequest Rebind(
        ProcessStartRequest request,
        ExecutionApiInvocationContext invocation,
        ProcessControlCommandContext? prior)
    {
        var context = request.Context;
        return new(
            request.SchemaVersion,
            request.Definition,
            new(
                context.CommandId,
                context.IdempotencyKey,
                context.ProcessInstanceId,
                prior?.Authorization ?? invocation.Authorization,
                prior?.IssuedAtUtc ?? invocation.IssuedAtUtc,
                prior?.Provenance ?? invocation.Provenance),
            request.InitialContinuation,
            request.Input);
    }

    static ProcessActivationContext Rebind(
        ProcessActivationContext activation,
        ExecutionApiInvocationContext invocation) => new(
        invocation.Authorization.AuthorityScope,
        activation.CorrelationId,
        activation.Delivery,
        invocation.Provenance,
        activation.CausationId,
        activation.Ordering);
}

sealed record DurableTaskProcessStartIndexState(DurableTaskSequentialProcessStart? Start);

enum DurableTaskProcessStartIndexClaimKind
{
    Unspecified = 0,
    Retain = 1,
    RetainAndSchedule = 2
}

sealed record DurableTaskProcessStartIndexClaim(
    DurableTaskSequentialProcessStart Start,
    DurableTaskProcessStartIndexClaimKind Kind);

sealed class DurableTaskProcessStartIndexEntity : TaskEntity<DurableTaskProcessStartIndexState>
{
    protected override DurableTaskProcessStartIndexState InitializeState(TaskEntityOperation entityOperation) =>
        new(Start: null);

    public DurableTaskSequentialProcessStart? Read() => State.Start;

    public DurableTaskSequentialProcessStart Claim(
        DurableTaskProcessStartIndexClaim claim,
        TaskEntityContext context)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(context);
        if (claim.Kind is not DurableTaskProcessStartIndexClaimKind.Retain
            and not DurableTaskProcessStartIndexClaimKind.RetainAndSchedule)
        {
            throw new ArgumentOutOfRangeException(
                nameof(claim),
                claim.Kind,
                "A Durable Task Process-start index claim kind must be explicit.");
        }
        if (State.Start is { } retained)
        {
            if (retained != claim.Start)
            {
                throw new InvalidOperationException(
                    "A Durable Task Process-start index is already bound to different canonical evidence.");
            }
            return retained;
        }

        State = new(claim.Start);
        if (claim.Kind == DurableTaskProcessStartIndexClaimKind.RetainAndSchedule)
        {
            _ = context.ScheduleNewOrchestration(
                new(DurableTaskSequentialProcessNames.Orchestration),
                claim.Start,
                DurableTaskSequentialProcessClientExtensions.CreateStartOptions(claim.Start));
        }
        return claim.Start;
    }
}
