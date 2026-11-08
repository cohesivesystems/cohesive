using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;
using Cohesive.Processes.Machines;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;

namespace Cohesive.Storage.Processes;

/// <summary>Evidence-derived reaction progress; never an independently mutable execution state.</summary>
public enum MachineReactionStatus
{
    /// <summary>Durable start exists but no conclusive target evidence is retained.</summary>
    Pending,
    /// <summary>The exact target receipt proves successful completion.</summary>
    Completed,
    /// <summary>The exact target receipt proves admission or domain rejection.</summary>
    Rejected,
    /// <summary>Explicit terminal failure proves the target did not commit.</summary>
    Failed,
    /// <summary>Target commitment is uncertain and requires reconciliation.</summary>
    Indeterminate
}

/// <summary>Inspection projection retaining the evidence behind its classification.</summary>
/// <param name="Status">Derived progress classification.</param>
/// <param name="Operation">Exact target operation when known.</param>
/// <param name="Receipt">Resolved authoritative target evidence when available.</param>
/// <param name="Failure">Explicit attempt failure evidence when supplied by the execution host.</param>
/// <param name="Diagnostic">Explanation when no conclusive target receipt is available.</param>
public sealed record MachineReactionProgress(MachineReactionStatus Status, ProcessOperationOccurrence? Operation = null,
    EntityTransitionOperationReceipt? Receipt = null, DurableOperationFailure? Failure = null,
    DocumentValidationDiagnostic? Diagnostic = null);

/// <summary>Deterministic reaction start intent and evidence inspection for the atomic entity/Process interpretation.</summary>
/// <remarks>Preparing does not admit, persist, authorize or dispatch a start. Supply Start to the existing
/// Process start admission and durable runtime. Transport acceptance alone never proves completion.</remarks>
public sealed class MachineReactionInvocation
{
    static readonly Lazy<ValueContract> OriginContract = new(() =>
        new(new Cohesive.Model.Authoring.DefaultClrTypeRefMapper().Map(typeof(Identity), null)));
    MachineReactionInvocation(MachineReaction policy, EntityTransitionOperationReference source,
        ExecutionNodeId movementNode, ProcessStartRequest start)
    { Policy = policy; Source = source; MovementNode = movementNode; Start = start; }

    /// <summary>Exact immutable policy revision.</summary>
    public MachineReaction Policy { get; }
    /// <summary>Original source receipt identity, including authority and input fingerprint.</summary>
    public EntityTransitionOperationReference Source { get; }
    /// <summary>Movement node within the source receipt.</summary>
    public ExecutionNodeId MovementNode { get; }
    /// <summary>Canonical start intent with deterministic instance, attempt, command and idempotency identities.</summary>
    public ProcessStartRequest Start { get; }

    /// <summary>Projects one matching committed occurrence into a canonical Process start request.</summary>
    /// <param name="policy">Validated pure reaction declaration.</param>
    /// <param name="occurrence">Occurrence resolved through a trusted, authorized source repository.</param>
    /// <param name="authorization">Explicit host authorization evidence; must remain in the source authority scope.</param>
    /// <param name="issuedAtUtc">UTC physical start-request observation time.</param>
    /// <returns>A start intent, or null for another movement, entity type or machine revision.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">Authority, binding or source value contracts disagree.</exception>
    /// <exception cref="ArgumentException">The issuance time is not UTC.</exception>
    public static MachineReactionInvocation? Prepare(MachineReaction policy, EntityMachineOccurrence occurrence,
        ProcessControlAuthorizationContext authorization, DateTimeOffset issuedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(occurrence);
        ArgumentNullException.ThrowIfNull(authorization);
        var payload = policy.Payload;
        var binding = EntityMachineBinding.Read(payload.Source)!;
        if (occurrence.Movement.Contract != binding.Machine.Reference || occurrence.Movement.Edge != payload.Movement
            || occurrence.Receipt.Entity.Entity.Observation.ShapeId.ShapeId != payload.Source.Id) return null;
        if (occurrence.Source.AuthorityScope != authorization.AuthorityScope)
            throw new InvalidOperationException("Cross-authority reactions require an explicit supported realization.");
        if (!binding.Equals(occurrence.Receipt.Commit.Evidence.MachineBinding))
            throw new InvalidOperationException("The source receipt did not execute the declared entity-machine binding.");
        var definition = payload.Process.GetDefinition<ProcessDefinition>();
        var input = PortableValue.Concrete(definition.Input,
            ObservationValue.FromObject(occurrence.Receipt.Entity.Entity.Observation.Fields));
        if (!PortableExecutionValidator.Validate(input).IsValid)
            throw new InvalidOperationException("Committed source snapshot does not satisfy the reaction input contract.");
        var identity = new Identity(policy.Document.Reference, occurrence.Source, occurrence.Movement.Node);
        var key = "reaction/" + ProcessStorageContentFingerprints.Value(identity).Value;
        var instance = new ProcessInstanceId(key);
        var context = new ProcessControlCommandContext(new(key), new(key), instance, authorization,
            issuedAtUtc, policy.Document.Metadata.Provenance);
        var start = new ProcessStartRequest(ProcessStartRequest.CurrentSchemaVersion, payload.Process.Reference,
            context, new(instance, new(key + "/attempt/1")), input,
            PortableValue.Concrete(OriginContract.Value, ObservationValue.FromObject(identity)));
        return new(policy, occurrence.Source, occurrence.Movement.Node, start);
    }

    /// <summary>Restores inspection context from retained canonical start causation without rereading source state.</summary>
    /// <param name="policy">Exact registered reaction revision.</param>
    /// <param name="start">Trusted retained start request, including its original origin evidence.</param>
    /// <returns>The recovered reaction identity and start intent.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">Policy, source authority, input or deterministic identities disagree.</exception>
    public static MachineReactionInvocation Restore(MachineReaction policy, ProcessStartRequest start)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(start);
        if (start.Causation is not { State: PortableValueState.Concrete } cause || cause.Contract != OriginContract.Value
            || !PortableExecutionValidator.Validate(cause).IsValid)
            throw new InvalidOperationException("Reaction start has no valid origin evidence.");
        var identity = cause.Value!.Value.Deserialize<Identity>()
            ?? throw new InvalidOperationException("Reaction origin evidence is absent.");
        var key = "reaction/" + ProcessStorageContentFingerprints.Value(identity).Value;
        if (identity.Policy != policy.Document.Reference || start.Definition != policy.Payload.Process.Reference
            || identity.Source.AuthorityScope != start.Context.Authorization.AuthorityScope
            || start.InitialContinuation != new ProcessContinuationIdentity(new(key), new(key + "/attempt/1"))
            || start.Context.IdempotencyKey.Value != key
            || start.Input?.Contract != policy.Payload.Process.GetDefinition<ProcessDefinition>().Input
            || !PortableExecutionValidator.Validate(start.Input).IsValid)
            throw new InvalidOperationException("Reaction start does not match its exact policy, identity or input.");
        return new(policy, identity.Source, identity.MovementNode, start);
    }

    /// <summary>Inspects durable Process evidence and, optionally, its authorized resolved target receipt.</summary>
    /// <param name="checkpoint">Authoritative checkpoint for this exact start intent.</param>
    /// <param name="receipt">Exact target receipt resolved using the retained operation's locator; not current entity state.</param>
    /// <returns>Pending, indeterminate, or receipt-proven completed/rejected progress.</returns>
    /// <exception cref="ArgumentNullException">Checkpoint is null.</exception>
    /// <exception cref="InvalidOperationException">The checkpoint or receipt belongs to different work.</exception>
    public MachineReactionProgress Inspect(ProcessDurableCheckpoint checkpoint, EntityTransitionOperationReceipt? receipt = null)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!Start.HasSameIdempotentIntent(checkpoint.Start.Request))
            throw new InvalidOperationException("Checkpoint belongs to another reaction start intent.");
        var operation = checkpoint.Operations.SingleOrDefault(item => item.Key.Node == MachineReaction.InvocationNode
            && item.Key.Continuation == checkpoint.ContinuationIdentity);
        if (operation is null)
        {
            if (receipt is not null) throw new InvalidOperationException("No retained target operation identifies this receipt.");
            return new(MachineReactionStatus.Pending);
        }
        RequireOperation(operation.Key);
        if (operation.OperationDefinition != Policy.Payload.Target.Reference)
            throw new InvalidOperationException("Retained operation invokes another target revision.");
        if (receipt is null)
            return new(MachineReactionStatus.Indeterminate, operation.Key, Diagnostic: operation.Result.Failure
                ?? new("machine.reaction.receiptRequired", DiagnosticSeverity.Warning,
                    "Resolve the exact target receipt before declaring completion or rejection."));
        if (operation.Result.ReceiptReference is null
            || EntityTransitionReceiptReferences.Read(operation.Result.ReceiptReference) != receipt.Request.Reference
            || receipt.Request.Operation != operation.Key || receipt.Request.Transition != Policy.Payload.Target.Reference
            || receipt.Request.AuthorityScope != Source.AuthorityScope)
            throw new InvalidOperationException("Target receipt does not match the retained reaction operation.");
        var status = receipt.Commit.DecisionKind is TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange
            ? MachineReactionStatus.Completed : MachineReactionStatus.Rejected;
        return new(status, operation.Key, receipt);
    }

    /// <summary>Projects explicitly scoped failure evidence from a pluggable execution host.</summary>
    /// <param name="operation">Exact attempted target operation.</param>
    /// <param name="failure">Host-attested failure phase, effect evidence and retry disposition.</param>
    /// <returns>Indeterminate for possible commitment, Failed for terminal known non-commit, otherwise Pending retry.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The operation is outside this reaction's initial attempt.</exception>
    /// <remarks>This does not persist evidence or schedule retries. Hosts must retain attempt evidence through
    /// their existing durable execution facilities. Ambiguous evidence never authorizes blind resend.</remarks>
    public MachineReactionProgress InspectFailure(ProcessOperationOccurrence operation, DurableOperationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(failure);
        RequireOperation(operation);
        return new(failure.EffectEvidence == DurableOperationEffectEvidence.Ambiguous ? MachineReactionStatus.Indeterminate
            : failure.Disposition == DurableOperationFailureDisposition.Terminal ? MachineReactionStatus.Failed
            : MachineReactionStatus.Pending, operation, Failure: failure);
    }

    void RequireOperation(ProcessOperationOccurrence operation)
    {
        if (operation.Continuation != Start.InitialContinuation || operation.Node != MachineReaction.InvocationNode || operation.Occurrence != 0)
            throw new InvalidOperationException("Operation belongs to another reaction or unsupported restarted attempt.");
    }

    sealed record Identity(ExecutionDefinitionReference Policy, EntityTransitionOperationReference Source, ExecutionNodeId MovementNode);
}
