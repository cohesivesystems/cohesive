using Cohesive.Api.Services;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Prelude;
using Cohesive.Relations.Execution;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Processes.Runtime;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>Reusable association of an entity authority and its invocation-scoped physical repository.</summary>
public sealed class ServiceEntityBinding
{
    /// <summary>Captures the association without resolving the repository.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ServiceEntityBinding(EntityDefinition entity, Func<OperationContext, IEntityRepository> repository)
    {
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }
    /// <summary>Canonical entity definition.</summary>
    public EntityDefinition Entity { get; }
    internal Func<OperationContext, IEntityRepository> Repository { get; }
}

/// <summary>Binds a declared committed-entity response to native Process values and entity receipts.</summary>
public sealed class ServiceProcessEntityResultBinding : ServiceBinding
{
    /// <summary>Links exact immutable plans and physical readers without resolving repositories.</summary>
    /// <exception cref="ArgumentNullException">A dependency is null.</exception>
    /// <exception cref="ArgumentException">The authority is empty.</exception>
    public ServiceProcessEntityResultBinding(string operationId, CompiledProcessPlan process,
        CompiledTransitionPlan transition, ServiceEntityBinding entity, string authority,
        IProcessExecutionValueRepository values, DeterministicHostedQueryBinding? resultClassifier = null) : base(operationId)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        Transition = transition ?? throw new ArgumentNullException(nameof(transition));
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        Authority = authority;
        Values = values ?? throw new ArgumentNullException(nameof(values));
        ResultClassifier = resultClassifier;
    }
    /// <summary>Exact Process plan owning the selected commit node.</summary>
    public CompiledProcessPlan Process { get; }
    /// <summary>Exact Transition invoked at that node.</summary>
    public CompiledTransitionPlan Transition { get; }
    /// <summary>Entity and repository association shared with other service bindings.</summary>
    public ServiceEntityBinding Entity { get; }
    /// <summary>Native logical execution authority.</summary>
    public string Authority { get; }
    internal IProcessExecutionValueRepository Values { get; }
    /// <summary>Exact deterministic classifier, invoked only after result-read admission.</summary>
    public DeterministicHostedQueryBinding? ResultClassifier { get; }
    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceProcessEntityResultOperation result || result.Process != Process.DefinitionReference
            || result.Entity != Entity.Entity.StateShape.QualifiedId
            || Process.Definition.Nodes.SingleOrDefault(node => node.Id == result.CommitNode) is not InvokeTransitionProcessNode invocation
            || invocation.Transition != Transition.DefinitionReference
            || Transition.Definition.Observation != ValueContract.FromShape(Entity.Entity.Shape))
            throw ServiceBindingValidationException.Error("services.binding.resultSourceMismatch",
                "The result source must identify an exact Transition invocation and its entity authority.", "/bindings/processEntityResult");
        ServiceProcessResultBinding.ValidateClassifier(result.ResultClassifier, ResultClassifier, Process.Definition.Result);
    }
}

public sealed partial class ServiceRuntime
{
    /// <summary>Projects a declared result read into the existing API model with an explicit medium-owned response view.</summary>
    /// <typeparam name="TResponse">Public response type produced from the authorized retained snapshot.</typeparam>
    /// <param name="operationId">Declared result-read identity.</param>
    /// <param name="http">Optional HTTP projection; result reads cannot require a request body.</param>
    /// <returns>A typed API endpoint with declaration-derived identity, requirements and standard outcomes.</returns>
    /// <exception cref="ArgumentException">The operation is not a result read or the projection requires a body.</exception>
    public ApiEndpoint ProjectCommittedEntityResult<TResponse>(string operationId, HttpBinding? http = null)
    {
        if (!operations.TryGetValue(operationId, out var linked)
            || linked.Operation is not ServiceProcessEntityResultOperation operation
            || linked.Binding is not ServiceProcessEntityResultBinding binding)
            throw new ArgumentException("The operation is not a declared committed-entity result read.", nameof(operationId));
        return ServiceApiProjection.CreateCommittedEntityResult<TResponse>(definitionReference, operation, http,
            binding.Entity.Entity.Name, binding.Transition.DefinitionReference);
    }

    /// <summary>Reads the original committed entity for a declared terminal Process result source.</summary>
    /// <remarks>Admission precedes reads; logical resource authorization uses the exact retained snapshot.
    /// Missing, ambiguous or incompatible receipt evidence never falls back to the current entity. This method
    /// does not start or retry execution; an optional bound requests provider-native completion waiting. Medium adapters project the authorized snapshot into their DTO.</remarks>
    /// <exception cref="ArgumentException">The operation or instance identity is invalid.</exception>
    /// <exception cref="InvalidOperationException">A physical binding returns contradictory evidence.</exception>
    /// <exception cref="OperationCanceledException">Invocation cancellation is requested.</exception>
    public async ValueTask<ServiceOperationResult<EntitySnapshot>> ReadCommittedEntityAsync(
        OperationContext context, string operationId, ProcessInstanceId instance, TimeSpan? maximumWait = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.Value);
        if (maximumWait is { } duration && (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(maximumWait), "A positive finite timer duration is required.");
        if (!operations.TryGetValue(operationId, out var linked)
            || linked.Operation is not ServiceProcessEntityResultOperation operation
            || linked.Binding is not ServiceProcessEntityResultBinding binding)
            throw new ArgumentException("The operation is not a declared committed-entity result read.", nameof(operationId));
        return await ReadProcessResultCoreAsync(context, operation, binding.Process, binding.Authority,
            binding.Values, instance, maximumWait, Project).ConfigureAwait(false);

        async ValueTask<ServiceOperationResult<EntitySnapshot>> Project(AdmittedProcessResult admitted,
            ServiceInvocationEvidence evidence)
        {
            var (trusted, scope, authority, values) = admitted;
            if (binding.ResultClassifier is { } classifier)
            {
                if (values.TerminalOutcome!.Detail?.Value is not PortableValue terminal)
                    return Reject(ApiResultKind.InfrastructureError, "services.process.resultUnavailable", "The canonical terminal result is unavailable.");
                var rejection = ClassifyProcessResult<EntitySnapshot>(classifier, terminal, trusted, evidence);
                if (rejection is not null) return rejection;
            }
            var candidates = values.Evidence.SelectMany(item => item.Trace).Where(item =>
                item.Continuation == values.TerminalContinuation && item.Node == operation.CommitNode
                && item.Kind == ProcessTraceEventKind.OperationCompleted && item.Detail == "completed").Take(2).ToArray();
            if (candidates.Length != 1 || candidates[0].ReceiptReference is null)
                return Reject(ApiResultKind.InfrastructureError, "services.process.receiptUnavailable", "The declared result requires one exact retained commit receipt.");
            var trace = candidates[0];
            var reference = EntityTransitionReceiptReferences.Read(trace.ReceiptReference!);
            if (reference.AuthorityScope != authority || reference.Operation.Continuation != values.TerminalContinuation
                || reference.Operation.Activation != trace.Activation || reference.Operation.Token != trace.Token
                || reference.Operation.Node != trace.Node || reference.Operation.Occurrence != trace.OperationOccurrence)
                throw new InvalidOperationException("The receipt locator contradicts the admitted execution occurrence.");
            var repository = binding.Entity.Repository(trusted)
                ?? throw new InvalidOperationException("The repository binding returned null.");
            if (repository.EntityDefinition.StateShape.QualifiedId != operation.Entity
                || reference.Subject.EntityType.Value != repository.EntityType)
                throw new InvalidOperationException("The repository or receipt subject contradicts the declared entity authority.");
            var resolved = await EntityTransitionReceiptReferences.ResolveSnapshotAsync(repository, trusted, reference,
                binding.Transition.DefinitionReference, authority, values.TerminalContinuation, scope.ResolvePartitionKey(),
                (context, snapshot) => authorization.AuthorizeResourceAsync(context, operation, snapshot)).ConfigureAwait(false);
            if (resolved.Type == ResultType.Failure)
            {
                var diagnostic = resolved.Failure!;
                if (diagnostic.Code == EntityTransitionOperationDiagnosticCodes.ReceiptMismatch)
                    throw new InvalidOperationException("The resolved receipt contradicts the declared Transition or trusted placement.");
                if (diagnostic.Code == EntityTransitionOperationDiagnosticCodes.ReceiptResourceDenied)
                {
                    evidence.Record("commitReceiptResolved");
                    return Reject(ApiResultKind.Forbidden, "services.authorization.resourceDenied", "Result access is not authorized for this resource.");
                }
                if (diagnostic.Code == EntityTransitionOperationDiagnosticCodes.SubjectStateConflict)
                {
                    evidence.Record("commitReceiptResolved");
                    evidence.Record("resourceAuthorized");
                    return Reject(ApiResultKind.DomainError, "services.process.commitRejected", "The selected Transition did not accept the entity change.");
                }
                return Reject(ApiResultKind.InfrastructureError, "services.process.receiptUnavailable", "The exact committed receipt could not be resolved.");
            }
            evidence.Record("commitReceiptResolved");
            evidence.Record("resourceAuthorized");
            return new(ApiResultKind.Success, resolved.Success, [], evidence.Complete(ApiResultKind.Success));

            ServiceOperationResult<EntitySnapshot> Reject(ApiResultKind kind, string code, string message) =>
                RejectProcessResult<EntitySnapshot>(evidence, kind, code, message);
        }
    }
}
