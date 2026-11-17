using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage;

// The single authority for read/decide/project/atomic-receipt execution. Hosts supply emission provenance,
// authorization and result presentation; neither host duplicates optimistic concurrency or replay rules.
internal static class EntityTransitionExecutor
{
    internal static EntityReadOptions ReadOptions(CompiledTransitionPlan plan, IEntityRepository repository, string? partitionKey)
    {
        Cohesive.Transitions.Machines.EntityMachineBinding.RequireSame(repository.EntityDefinition, plan.Definition.MachineBinding);
        if (partitionKey is not null) ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        var trustedPartition = repository.TransitionOperationCapabilities.PartitionKey;
        if (partitionKey is not null && trustedPartition is not null && partitionKey != trustedPartition)
            throw new ArgumentException("Subject-read partition differs from the repository receipt partition.", nameof(partitionKey));
        return new(partitionKey: trustedPartition ?? partitionKey);
    }

    internal static DocumentValidationResult ValidateBinding(CompiledTransitionPlan plan, IEntityRepository repository)
    {
        var diagnostics = new List<DocumentValidationDiagnostic>();
        if (!repository.TransitionOperationCapabilities.SupportsAtomicStateAndReceipt)
            diagnostics.Add(new(ProcessTransitionBindingDiagnosticCodes.ReceiptCapabilityMissing, DiagnosticSeverity.Error,
                "The repository must support atomic state and receipt commits.", "/binding/repository"));
        if (plan.Definition.Observation != ValueContract.FromShape(repository.EntityDefinition.Shape))
            diagnostics.Add(new(ProcessTransitionBindingDiagnosticCodes.ObservationMismatch, DiagnosticSeverity.Error,
                "The plan observation contract must match the bound entity state contract.", "/binding/entity"));
        return new([.. diagnostics]);
    }

    internal static async ValueTask<EntityTransitionExecutionResult> ExecuteAsync(
        OperationContext context, IEntityRepository repository, CompiledTransitionPlan plan,
        EntityReadOptions readOptions, EntityTransitionOperationRequest request,
        EntityConcurrencyToken? expectedConcurrencyToken,
        Func<TransitionDecision, (DocumentValidationResult Validation, ImmutableArray<InteractionEnvelope> Envelopes)> lower)
    {
        var subject = request.Subject;
        var lookup = await repository.TryGetTransitionOperation(context, request).ConfigureAwait(false);
        if (lookup.Disposition != EntityTransitionOperationDisposition.NotFound)
        {
            return Retained(lookup);
        }

        EntitySnapshot? snapshot;
        try
        {
            snapshot = await repository.TryGet(context, subject.EntityId.Value,
                expectedConcurrencyToken is null ? readOptions
                    : new EntityReadOptions(partitionKey: readOptions.PartitionKey, expectedConcurrencyToken: expectedConcurrencyToken)).ConfigureAwait(false);
        }
        catch (ObservationConcurrencyConflictException) when (expectedConcurrencyToken is not null)
        {
            return await ChangedSubjectAsync().ConfigureAwait(false);
        }
        // Preserve the binding's guarantee even when a provider does not enforce read preconditions.
        if (snapshot is not null && expectedConcurrencyToken is { } expected && snapshot.ConcurrencyToken != expected)
            return await ChangedSubjectAsync().ConfigureAwait(false);

        async ValueTask<EntityTransitionExecutionResult> ChangedSubjectAsync()
        {
            var raced = await repository.TryGetTransitionOperation(context, request).ConfigureAwait(false);
            return raced.Disposition != EntityTransitionOperationDisposition.NotFound ? Retained(raced)
                : Failure(ProcessTransitionOperationAdapterDiagnosticCodes.SubjectChanged,
                    "The authoritative subject changed after its concurrency token was captured.", "/invocation/input");
        }
        var createsSubject = plan.Definition.SubjectCreation is not null;
        if (snapshot is null && !createsSubject)
        {
            // A matching entity commit may have raced the first lookup and a later delete. Prefer its durable
            // handoff evidence over reporting the now-absent subject.
            lookup = await repository.TryGetTransitionOperation(context, request).ConfigureAwait(false);
            if (lookup.Disposition != EntityTransitionOperationDisposition.NotFound)
            {
                return Retained(lookup);
            }
            return Failure(
                ProcessTransitionOperationAdapterDiagnosticCodes.SubjectMissing,
                $"No authoritative entity exists for subject '{subject.EntityId.Value}'.",
                "/invocation/subject/entityId");
        }
        if (snapshot is not null && createsSubject)
        {
            // Prefer exact handoff evidence if the same operation won a race after the first lookup.
            lookup = await repository.TryGetTransitionOperation(context, request).ConfigureAwait(false);
            if (lookup.Disposition != EntityTransitionOperationDisposition.NotFound)
            {
                return Retained(lookup);
            }

            // A replacement Process attempt has a fresh exact occurrence, but unique-subject creation is naturally
            // idempotent when the retained authority, Transition, subject, and input are identical. Replaying the
            // original receipt also preserves its canonical envelopes and target-deduplication identities.
            lookup = await repository.TryGetCreationTransitionOperation(context, request).ConfigureAwait(false);
            if (lookup.Disposition != EntityTransitionOperationDisposition.NotFound)
            {
                return Retained(lookup);
            }
            return Failure(
                ProcessTransitionOperationAdapterDiagnosticCodes.SubjectPresent,
                $"Authoritative entity '{subject.EntityId.Value}' already exists for a creation Transition.",
                "/invocation/subject/entityId");
        }

        var decision = createsSubject
            ? TransitionReferenceInterpreter.DecideCreation(
                plan,
                request.Operation.Activation,
                request.Input)
            : TransitionReferenceInterpreter.DecideFullState(
                plan,
                request.Operation.Activation,
                request.Input,
                PortableValue.Concrete(
                    plan.Definition.Observation,
                    ObservationValue.FromObject(snapshot!.Entity.Observation.Fields)));
        if (decision.Kind is not (TransitionDecisionKind.Applied
            or TransitionDecisionKind.NoChange
            or TransitionDecisionKind.AdmissionRejected
            or TransitionDecisionKind.DomainRejected)
            || decision.Outcome is null)
        {
            return decision.Diagnostics.IsEmpty
                ? Failure(
                    ProcessTransitionOperationAdapterDiagnosticCodes.DecisionNotCommittable,
                    $"Transition '{request.Transition.DefinitionId.Value}' produced non-committable decision '{decision.Kind}'.",
                    "/decision/kind")
                : Failed(decision.Diagnostics[0]);
        }

        var (lowering, envelopes) = lower(decision);
        if (!lowering.IsValid)
        {
            return Failed(lowering.Diagnostics[0]);
        }

        var result = ProcessOperationResult.Completed(decision.Outcome, envelopes);
        if (createsSubject && decision.Kind != TransitionDecisionKind.Applied)
        {
            // A rejected creation is pure: no subject exists to mutate, and the enclosing Process commit retains
            // the deterministic result and any Process-owned envelope handoff.
            return new(result, null);
        }

        EntityState candidateState;
        try
        {
            candidateState = TransitionStateProjector.ApplyToEntity(repository.EntityDefinition,
                subject.EntityId.Value, decision, createsSubject ? null : snapshot!.Entity);
        }
        catch (TransitionStatePreparationException exception)
        {
            return Failure(exception.Code, exception.Message, exception.Location);
        }
        var candidate = candidateState.Snapshot;

        var commit = new EntityTransitionOperationCommit(
            request,
            new(candidate, createsSubject ? null : snapshot!.ConcurrencyToken),
            decision.Kind,
            result,
            decision.GuaranteeDemands,
            decision.Evidence,
            createsSubject
                ? EntityTransitionSubjectCondition.MustBeAbsent
                : EntityTransitionSubjectCondition.MustExist);
        var committed = await repository.CommitTransitionOperation(context, commit).ConfigureAwait(false);
        return Retained(committed);
    }

    static EntityTransitionExecutionResult Retained(EntityTransitionOperationResult operation) => new(
        operation.Receipt is { } receipt
            ? receipt.Result.WithReceiptReference(EntityTransitionReceiptReferences.Project(receipt.Request.Reference))
            : ProcessOperationResult.Failed(operation.Diagnostics.FirstOrDefault() is { } diagnostic
                ? new(diagnostic.Code, diagnostic.Severity, "The entity operation could not be committed.", diagnostic.Location)
                : new(ProcessTransitionOperationAdapterDiagnosticCodes.DecisionNotCommittable, DiagnosticSeverity.Error,
                    "The entity operation produced no receipt.", "/entityOperation")), operation);

    static EntityTransitionExecutionResult Failed(DocumentValidationDiagnostic diagnostic) => new(ProcessOperationResult.Failed(diagnostic), null);
    static EntityTransitionExecutionResult Failure(string code, string message, string location) =>
        Failed(new(code, DiagnosticSeverity.Error, message, location));
}
