using System.Collections.Frozen;
using System.Collections.Immutable;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;

namespace Cohesive.Api.Execution.Services;

/// <summary>Explicit authority binding used by every invocation medium.</summary>
/// <remarks>
/// Admission must normalize identity, verify declared requirements and selected scope before returning trusted
/// scope evidence. Resource authorization must verify logical ownership independently of physical partition routing.
/// Policy is evaluated at invocation time; an entity token does not fence later permission revocation.
/// </remarks>
public interface IServiceInvocationAuthorization
{
    /// <summary>Returns the authorized logical scope and its trusted physical placement, or null when forbidden.</summary>
    /// <exception cref="OperationCanceledException">Invocation cancellation was observed.</exception>
    ValueTask<ScopeRef?> AdmitAsync(OperationContext context, ServiceOperation operation);

    /// <summary>Authorizes the exact runtime-loaded resource; false rejects without evaluating or committing.</summary>
    /// <exception cref="OperationCanceledException">Invocation cancellation was observed.</exception>
    ValueTask<bool> AuthorizeResourceAsync(OperationContext context, ServiceTransitionOperation operation, EntitySnapshot snapshot);
}

/// <summary>Structured declaration/binding diagnostics retained for human and agent inspection.</summary>
public sealed class ServiceBindingValidationException : ArgumentException
{
    /// <summary>Retains the existing document-validation result without flattening diagnostic evidence.</summary>
    public ServiceBindingValidationException(DocumentValidationResult validation)
        : base(string.Join("; ", validation.Diagnostics.Select(d => $"{d.Code}: {d.Message}"))) => Validation = validation;
    /// <summary>Canonical diagnostics identifying the invalid declaration or unsupported binding guarantee.</summary>
    public DocumentValidationResult Validation { get; }
    internal static ServiceBindingValidationException Error(string code, string message, string location) =>
        new(new([new(code, DiagnosticSeverity.Error, message, location)]));
}

/// <summary>Native realization of a declared operation; factories are never retained in portable IR.</summary>
public abstract class ServiceBinding
{
    /// <summary>Associates a native realization with one operation identity.</summary>
    /// <exception cref="ArgumentException">The identity is empty.</exception>
    private protected ServiceBinding(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        OperationId = operationId;
    }
    /// <summary>Identity from the portable service declaration.</summary>
    public string OperationId { get; }
    internal abstract void Validate(ServiceOperation operation);
}

/// <summary>Exact Transition plan, entity and invocation-scoped repository association.</summary>
public sealed class ServiceTransitionBinding : ServiceBinding
{
    /// <summary>Associates an operation with its prepared plan and invocation-scoped repository resolver.</summary>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    /// <exception cref="ArgumentException">The operation identity is empty.</exception>
    public ServiceTransitionBinding(string operationId, CompiledTransitionPlan plan, EntityDefinition entity,
        Func<OperationContext, IEntityRepository> repository) : base(operationId)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        if (plan.Definition.Observation != ValueContract.FromShape(entity.Shape))
            throw ServiceBindingValidationException.Error("services.binding.observationMismatch",
                "The plan observation contract must match the bound entity state contract.", "/binding/entity");
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }
    /// <summary>Reusable, immutable, exact compiled behavior.</summary>
    public CompiledTransitionPlan Plan { get; }
    /// <summary>Entity declaration against which plan and repository bindings are checked.</summary>
    public EntityDefinition Entity { get; }
    internal Func<OperationContext, IEntityRepository> Repository { get; }
    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceTransitionOperation transition)
            throw ServiceBindingValidationException.Error("services.binding.operationUnsupported",
                "A Transition binding requires a Transition operation.", "/bindings");
        if (Plan.DefinitionReference != transition.Transition || Entity.StateShape.QualifiedId != transition.Entity)
            throw ServiceBindingValidationException.Error("services.binding.inexact",
                "The binding must realize the exact declared Transition and entity.", "/bindings");
        if (Plan.Definition.SubjectCreation is not null
            || Plan.Analysis.Requirements.OfType<TransitionEmissionRequirement>().Any())
            throw ServiceBindingValidationException.Error("services.binding.capabilityUnsupported",
                "Subject creation and emissions require a qualified commit profile.", "/bindings");
    }

}

/// <summary>Service admission, native execution outcome and payload-free invocation evidence.</summary>
/// <typeparam name="TOutcome">Existing native outcome authority, retained without copying its result model.</typeparam>
public sealed record ServiceOperationResult<TOutcome>(ApiResultKind Kind, TOutcome? Outcome,
    ImmutableArray<DocumentValidationDiagnostic> Diagnostics, NormalizedExecutionTrace Trace)
    where TOutcome : class;

/// <summary>Declared portable outcome, storage fence and payload-free evidence of one service invocation.</summary>
/// <remarks>Internal subject snapshots and decision input/observation payloads are never part of this result.</remarks>
public sealed record ServiceInvocationResult(ApiResultKind Kind, PortableValue? Outcome,
    EntityConcurrencyToken? ConcurrencyToken, ImmutableArray<DocumentValidationDiagnostic> Diagnostics,
    NormalizedExecutionTrace Trace, NormalizedExecutionTrace? TransitionTrace);

/// <summary>Transport-independent invocation of exact service operations through their native execution authorities.</summary>
/// <remarks>
/// Transition operations require an existing entity, full-state loading and conditional writes, with no
/// emissions or durable replay. Query operations reuse canonical evaluation. Unsupported requirements fail binding.
/// Preparation is once per runtime; repositories,
/// authorization, inputs and results are invocation-scoped. The runtime neither retries nor accepts caller snapshots.
/// </remarks>
public sealed partial class ServiceRuntime
{
    readonly FrozenDictionary<string, (ServiceOperation Operation, ServiceBinding Binding)> operations;
    readonly IServiceInvocationAuthorization authorization;
    readonly ExecutionDefinitionReference definitionReference;

    /// <summary>Admits a canonical declaration and its exact physical bindings without resolving repositories.</summary>
    /// <exception cref="ArgumentException">The declaration or binding set is invalid or requires unsupported guarantees.</exception>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ServiceRuntime(ExecutionDefinitionDocument service, IEnumerable<ServiceBinding> bindings,
        IServiceInvocationAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(bindings);
        this.authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        var validation = ServiceDefinitionDocuments.ValidateAndProject(service, out var definition);
        if (!validation.IsValid)
            throw new ServiceBindingValidationException(validation);
        if (!service.Extensions.IsEmpty)
            throw ServiceBindingValidationException.Error("services.binding.extensionsUnsupported",
                "This service profile does not support semantic extensions.", "/extensions");
        Declaration = service;
        definitionReference = new(service.Metadata.DefinitionId, service.Metadata.RevisionId, service.Metadata.Fingerprint);
        var bound = new Dictionary<string, ServiceBinding>(StringComparer.Ordinal);
        foreach (var binding in bindings)
            if (binding is null || !bound.TryAdd(binding.OperationId, binding))
                throw ServiceBindingValidationException.Error("services.binding.duplicate", "Bindings must be non-null and uniquely identified.", "/bindings");
        if (bound.Count != definition!.Operations.Length)
            throw ServiceBindingValidationException.Error("services.binding.incomplete", "Each declared operation requires exactly one binding.", "/bindings");
        var linked = new Dictionary<string, (ServiceOperation, ServiceBinding)>(StringComparer.Ordinal);
        foreach (var operation in definition.Operations)
        {
            if (!bound.TryGetValue(operation.Id, out var binding))
                throw ServiceBindingValidationException.Error("services.binding.inexact",
                    "Every binding must identify a declared operation.", "/bindings");
            binding.Validate(operation);
            linked.Add(operation.Id, (operation, binding));
        }
        operations = linked.ToFrozenDictionary(StringComparer.Ordinal);
    }

    (ServiceTransitionOperation Operation, ServiceTransitionBinding Binding) Transition(string operationId)
    {
        if (!operations.TryGetValue(operationId, out var linked)
            || linked.Operation is not ServiceTransitionOperation operation
            || linked.Binding is not ServiceTransitionBinding binding)
            throw new ArgumentException("The operation is not a declared Transition.", nameof(operationId));
        return (operation, binding);
    }

    /// <summary>Validated portable authority available for human and agent inspection.</summary>
    public ExecutionDefinitionDocument Declaration { get; }

    /// <summary>Projects one declared operation into the existing API surface with checked CLR wire views.</summary>
    /// <remarks>CLR types are projections of the Transition contracts, not independent semantic authorities.</remarks>
    /// <exception cref="ArgumentException">The operation is undeclared or CLR views disagree with its contracts.</exception>
    public ApiEndpoint Project<TInput, TOutcome>(string operationId, HttpBinding? http = null)
    {
        var linked = Transition(operationId);
        var mapper = new DefaultClrTypeRefMapper();
        if (Contract(typeof(TInput)) != linked.Binding.Plan.Definition.Input
            || Contract(typeof(TOutcome)) != linked.Binding.Plan.Definition.Outcome)
            throw new ArgumentException("CLR API views must exactly project the Transition input and outcome contracts.");
        if (http?.Body is { } body && body.BodyType != typeof(TInput))
            throw new ArgumentException("The HTTP body must project the declared input type.", nameof(http));
        var operation = new ApiOperation(operationId, ApiOperationKind.Command, typeof(TInput), typeof(TOutcome),
            id: new($"service/{Uri.EscapeDataString(definitionReference.DefinitionId.Value)}/operation/{Uri.EscapeDataString(operationId)}"), entity: linked.Binding.Entity.Name,
            transitionReference: linked.Operation.Transition,
            authorizationRequirements: linked.Operation.AuthorizationRequirements,
            results: [new(ApiResultKind.Success, typeof(TOutcome), isPrimary: true),
                new(ApiResultKind.DomainError, typeof(TOutcome)),
                new(ApiResultKind.ValidationFailed, typeof(ApiValidationProblem)),
                new(ApiResultKind.Conflict, typeof(ApiProblem)),
                new(ApiResultKind.Forbidden, typeof(ApiProblem)),
                new(ApiResultKind.NotFound, typeof(ApiProblem)),
                new(ApiResultKind.InfrastructureError, typeof(ApiProblem))]);
        if (http is not null) operation = operation.WithHttp(http);
        return new ApiDefinition(new[] { operation }).Endpoints[0];

        ValueContract Contract(Type type) => new(mapper.Map(type, nullability: null),
            nullability: Nullable.GetUnderlyingType(type) is not null ? FieldNullability.Nullable : FieldNullability.NonNullable);
    }

    /// <summary>Returns the canonical Transition input contract without resolving execution dependencies.</summary>
    /// <exception cref="KeyNotFoundException">The operation is undeclared.</exception>
    public ValueContract InputContract(string operationId) => Transition(operationId).Binding.Plan.Definition.Input;

    /// <summary>Loads, authorizes, decides and conditionally commits one declared operation.</summary>
    /// <param name="context">Normalized identity, time, cancellation and correlation for this invocation.</param>
    /// <param name="operationId">Identity from the admitted service declaration.</param>
    /// <param name="subject">Repository entity identity, never a caller-supplied snapshot.</param>
    /// <param name="expectedToken">Required opaque token of the version reviewed by the caller.</param>
    /// <param name="activation">Caller/medium correlation identity; this is not a durable deduplication receipt.</param>
    /// <param name="input">Materialized typed input for the exact declared Transition.</param>
    /// <returns>A standard outcome and native evidence; rejected invocations never commit.</returns>
    /// <exception cref="ArgumentException">Required arguments are empty or the operation is undeclared.</exception>
    /// <exception cref="InvalidOperationException">A repository binding violates its declared entity authority.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; cancellation during commit can be ambiguous.</exception>
    /// <remarks>Repository/authority infrastructure failures propagate without exposing their messages in telemetry.</remarks>
    public async Task<ServiceInvocationResult> InvokeAsync(OperationContext context, string operationId, string subject,
        EntityConcurrencyToken expectedToken, ActivationId activation, PortableValue input)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedToken.Value);
        ArgumentException.ThrowIfNullOrWhiteSpace(activation.Value);
        var linked = Transition(operationId);
        using var evidence = new ServiceInvocationEvidence(definitionReference, operationId, linked.Operation.Transition, activation);
        try
        {
            context.ThrowIfCancellationRequested();
            var scope = await authorization.AdmitAsync(context, linked.Operation).ConfigureAwait(false);
            if (scope is null)
                return Reject(ApiResultKind.Forbidden, "services.authorization.denied", "/authorization", "The caller is not authorized to invoke this operation.");
            evidence.Record("authorityAdmitted");
            var readOptions = EntityReadOptions.Full.WithPartitionKey(scope.ResolvePartitionKey());
            var repository = linked.Binding.Repository(context)
                ?? throw new InvalidOperationException("The service repository binding returned null.");
            if (repository.EntityDefinition.StateShape.QualifiedId != linked.Operation.Entity)
                throw new InvalidOperationException("The repository does not realize the declared entity authority.");
            var snapshot = await repository.TryGet(context, subject, readOptions).ConfigureAwait(false);
            if (snapshot is null)
                return Reject(ApiResultKind.NotFound, "services.subject.missing", "/subject", "The subject was not found in the authorized scope.");
            evidence.Record("subjectLoaded");
            if (snapshot.Entity.EntityId.Value != subject
                || (readOptions.PartitionKey is not null && snapshot.PartitionKey != readOptions.PartitionKey)
                || (snapshot.LoadedFields is not null && repository.EntityDefinition.Shape.Fields.Any(f => !snapshot.LoadedFields.Contains(f.Name.Value))))
                throw new InvalidOperationException("The repository returned an inexact or partial subject snapshot.");
            if (!await authorization.AuthorizeResourceAsync(context, linked.Operation, snapshot).ConfigureAwait(false))
                return Reject(ApiResultKind.Forbidden, "services.authorization.resourceDenied", "/authorization/resource", "The caller is not authorized for this resource.");
            evidence.Record("resourceAuthorized");
            if (snapshot.ConcurrencyToken != expectedToken)
                return Reject(ApiResultKind.Conflict, "services.concurrency.stale", "/load", "The subject has changed since it was read.");
            context.ThrowIfCancellationRequested();
            var state = repository.EntityDefinition.CreateState(snapshot.Entity);
            var plan = linked.Binding.Plan;
            var decision = TransitionReferenceInterpreter.DecideFullState(plan, activation, input,
                PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(state.Fields)));
            evidence.Record("transitionDecided");
            if (decision.Kind is not (TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange))
            {

                return Complete(decision.Kind switch
                {
                    TransitionDecisionKind.AdmissionRejected or TransitionDecisionKind.DomainRejected => ApiResultKind.DomainError,
                    TransitionDecisionKind.Conflict => ApiResultKind.Conflict,
                    TransitionDecisionKind.InvalidDefinition => ApiResultKind.ValidationFailed,
                    _ => ApiResultKind.InfrastructureError
                }, null, decision, [.. decision.Diagnostics.Select(diagnostic => new DocumentValidationDiagnostic(
                    diagnostic.Code, diagnostic.Severity, "Transition invocation failed its declared contract.", diagnostic.Location))]);
            }
            if (!decision.Emissions.IsEmpty)
                throw new InvalidOperationException("An unsupported emission escaped binding admission.");
            if (!decision.GuaranteeDemands.CommitRequired)
            {

                return Complete(ApiResultKind.Success, snapshot, decision, []);
            }
            var candidate = TransitionStateProjector.Apply(ObservationValue.FromObject(state.Fields), decision);
            var next = repository.EntityDefinition.CreateState(subject, candidate.Fields!, checked(state.Version + 1));
            context.ThrowIfCancellationRequested();
            try
            {
                var committed = await repository.Upsert(context, new(next.Snapshot, snapshot.ConcurrencyToken)).ConfigureAwait(false);

                evidence.Record("commitCompleted");
                return Complete(ApiResultKind.Success, committed, decision, []);
            }
            catch (ObservationConcurrencyConflictException)
            {
                return Reject(ApiResultKind.Conflict, "services.concurrency.conflict", "/commit", "The subject changed before the conditional commit.");
            }
        }
        catch (Exception exception)
        {
            evidence.Fail(exception);
            throw;
        }

        ServiceInvocationResult Complete(ApiResultKind kind, EntitySnapshot? snapshot, TransitionDecision? decision,
            ImmutableArray<DocumentValidationDiagnostic> diagnostics)
        {
            var trace = evidence.Complete(kind);
            var transitionTrace = decision is null ? null
                : TransitionExecutionTraceProjector.Project(linked.Binding.Plan, decision).Trace;
            return new(kind, decision?.Outcome, snapshot?.ConcurrencyToken, diagnostics, trace, transitionTrace);
        }

        ServiceInvocationResult Reject(ApiResultKind kind, string code, string location, string message)
        {

            evidence.Record("invocationRejected", code);
            return Complete(kind, null, null, [new(code, DiagnosticSeverity.Error, message, location)]);
        }
    }
}
