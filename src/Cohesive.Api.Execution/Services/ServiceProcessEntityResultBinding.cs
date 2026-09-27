using Cohesive.Api.Services;
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
        IProcessExecutionValueRepository values) : base(operationId)
    {
        Process = process ?? throw new ArgumentNullException(nameof(process));
        Transition = transition ?? throw new ArgumentNullException(nameof(transition));
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        ArgumentException.ThrowIfNullOrWhiteSpace(authority);
        Authority = authority;
        Values = values ?? throw new ArgumentNullException(nameof(values));
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

    internal override void Validate(ServiceOperation operation)
    {
        if (operation is not ServiceProcessEntityResultOperation result || result.Process != Process.DefinitionReference
            || result.Entity != Entity.Entity.StateShape.QualifiedId
            || Process.Definition.Nodes.SingleOrDefault(node => node.Id == result.CommitNode) is not InvokeTransitionProcessNode invocation
            || invocation.Transition != Transition.DefinitionReference
            || Transition.Definition.Observation != ValueContract.FromShape(Entity.Entity.Shape))
            throw ServiceBindingValidationException.Error("services.binding.resultSourceMismatch",
                "The result source must identify an exact Transition invocation and its entity authority.", "/bindings/processEntityResult");
    }
}

public sealed partial class ServiceRuntime
{
    /// <summary>Reads the original committed entity for a declared terminal Process result source.</summary>
    /// <remarks>Admission precedes reads; logical resource authorization uses the exact retained snapshot.
    /// Missing, ambiguous or incompatible receipt evidence never falls back to the current entity. This method
    /// does not start, retry or wait for execution. Medium adapters project the authorized snapshot into their DTO.</remarks>
    /// <exception cref="ArgumentException">The operation or instance identity is invalid.</exception>
    /// <exception cref="InvalidOperationException">A physical binding returns contradictory evidence.</exception>
    /// <exception cref="OperationCanceledException">Invocation cancellation is requested.</exception>
    public async ValueTask<ServiceOperationResult<EntitySnapshot>> ReadCommittedEntityAsync(
        OperationContext context, string operationId, ProcessInstanceId instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.Value);
        if (!operations.TryGetValue(operationId, out var linked)
            || linked.Operation is not ServiceProcessEntityResultOperation operation
            || linked.Binding is not ServiceProcessEntityResultBinding binding)
            throw new ArgumentException("The operation is not a declared committed-entity result read.", nameof(operationId));
        using var evidence = new ServiceInvocationEvidence(definitionReference, operationId, operation.Process,
            new(instance.Value));
        try
        {
            context.ThrowIfCancellationRequested();
            var scope = await authorization.AdmitAsync(context, operation).ConfigureAwait(false);
            if (scope is null) return Reject(ApiResultKind.Forbidden, "services.authorization.denied", "Result access is not authorized.");
            evidence.Record("authorityAdmitted");
            var trusted = context.WithSingleEffectiveScope(scope.Kind, scope.Id, partitionKey: scope.ResolvePartitionKey());
            var authority = new InteractionAuthorityScope(binding.Authority, scope.Id);
            var read = await binding.Values.GetValuesAsync(trusted, authority, instance).ConfigureAwait(false);
            if (read.State == ProcessExecutionValueReadState.NotFound)
                return Reject(ApiResultKind.NotFound, "services.process.notFound", "No execution is visible at this target.");
            var values = read.Values!;
            if (values.Definition != operation.Process || values.ProcessInstanceId != instance)
                return Reject(ApiResultKind.NotFound, "services.process.notFound", "No execution is visible at the declared exact definition.");
            if (read.State == ProcessExecutionValueReadState.InProgress)
                return Reject(ApiResultKind.Accepted, "services.process.inProgress", "The execution has not completed.");
            if (read.State != ProcessExecutionValueReadState.Available || values.TerminalContinuation is null || values.Evidence.IsDefault)
                return Reject(ApiResultKind.InfrastructureError, "services.process.evidenceUnavailable", "Exact terminal execution evidence is unavailable.");
            evidence.Record("terminalValuesRead");
            if (values.TerminalOutcome!.Kind != ExecutionTerminalOutcomeKind.Completed)
                return Reject(ApiResultKind.DomainError, "services.process.notCompleted", "The Process did not complete successfully.");
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
            var resolved = await repository.ResolveTransitionOperation(trusted, reference).ConfigureAwait(false);
            if (resolved.Receipt is not { } receipt)
                return Reject(ApiResultKind.InfrastructureError, "services.process.receiptUnavailable", "The exact committed receipt could not be resolved.");
            if (receipt.Request.Reference != reference || receipt.Request.Transition != binding.Transition.DefinitionReference
                || receipt.Entity.PartitionKey != scope.ResolvePartitionKey()
                || receipt.Entity.Entity.EntityId.Value != reference.Subject.EntityId.Value
                || (receipt.Entity.LoadedFields is not null && binding.Entity.Entity.Shape.Fields.Any(
                    field => !receipt.Entity.LoadedFields.Contains(field.Name.Value))))
                throw new InvalidOperationException("The resolved receipt contradicts the declared Transition or trusted placement.");
            evidence.Record("commitReceiptResolved");
            if (!await authorization.AuthorizeResourceAsync(trusted, operation, receipt.Entity).ConfigureAwait(false))
                return Reject(ApiResultKind.Forbidden, "services.authorization.resourceDenied", "Result access is not authorized for this resource.");
            evidence.Record("resourceAuthorized");
            if (receipt.Commit.DecisionKind is not (TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange))
                return Reject(ApiResultKind.DomainError, "services.process.commitRejected", "The selected Transition did not accept the entity change.");
            return new(ApiResultKind.Success, receipt.Entity, [], evidence.Complete(ApiResultKind.Success));
        }
        catch (Exception exception) { evidence.Fail(exception); throw; }

        ServiceOperationResult<EntitySnapshot> Reject(ApiResultKind kind, string code, string message)
        {
            evidence.Record("resultUnavailable", code);
            return new(kind, null, [new(code, kind == ApiResultKind.Accepted ? DiagnosticSeverity.Info : DiagnosticSeverity.Error, message, "/result")], evidence.Complete(kind));
        }
    }
}
