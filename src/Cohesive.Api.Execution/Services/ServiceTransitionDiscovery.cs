using Cohesive.Identity;
using System.Collections.Immutable;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Api.Execution.Services;

/// <summary>Actor-specific availability of a discoverable entity operation.</summary>
public enum ServiceTransitionAvailabilityKind
{
    /// <summary>The supplied command is authorized and accepted against the reported snapshot.</summary>
    Permitted,
    /// <summary>A complete command is needed to evaluate eligibility.</summary>
    RequiresInput,
    /// <summary>The transition rejected the supplied command or state.</summary>
    Unavailable,
    /// <summary>The operation may be discovered but invocation is not authorized.</summary>
    Forbidden,
    /// <summary>Evaluation reported invalid input, definition, ambiguity or invariant failure.</summary>
    Invalid,
    /// <summary>Evaluation could not establish availability.</summary>
    Indeterminate
}

/// <summary>Advisory availability with exact operation authority and the entity version examined.</summary>
/// <param name="OperationId">Declared service operation identity.</param>
/// <param name="Transition">Exact transition revision and fingerprint.</param>
/// <param name="Input">Required command contract.</param>
/// <param name="ConcurrencyToken">Entity snapshot token; execution still checks this and authorizes anew.</param>
/// <param name="Kind">Availability classification.</param>
/// <param name="Decision">Native transition decision kind when evaluation ran.</param>
/// <param name="Diagnostics">Structured, payload-free reasons; no candidate state or patches are exposed.</param>
public sealed record ServiceTransitionAvailability(string OperationId, ExecutionDefinitionReference Transition,
    ValueContract Input, EntityConcurrencyToken ConcurrencyToken, ServiceTransitionAvailabilityKind Kind,
    TransitionDecisionKind? Decision, ImmutableArray<DocumentValidationDiagnostic> Diagnostics);

public sealed partial class ServiceRuntime
{
    /// <summary>Inspects registered entity transitions using current identity, resource and command evidence without committing.</summary>
    /// <param name="context">Explicit actor, scope, time and cancellation context.</param>
    /// <param name="entity">Exact entity authority to inspect.</param>
    /// <param name="subject">Repository entity identity; caller-provided snapshots are not accepted.</param>
    /// <param name="inputs">Optional complete portable commands keyed by operation identity. Missing commands yield RequiresInput.</param>
    /// <returns>Discoverable operations in ordinal identity order, each carrying the token of its authorized snapshot.</returns>
    /// <exception cref="ArgumentNullException">Context is null.</exception>
    /// <exception cref="ArgumentException">Subject identity is empty.</exception>
    /// <exception cref="InvalidOperationException">A repository supplies an inexact snapshot or entity authority.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    /// <remarks>Visibility uses the host's discovery policy, defaulting to invocation authorization. Hidden, inaccessible
    /// and absent resources produce no entries. A separate visibility policy may expose Forbidden operations. Availability
    /// is advisory: InvokeAsync always reauthorizes and enforces the supplied concurrency token. Entries may come from
    /// different snapshot versions and do not imply an atomic catalog read. Within one call, the same repository
    /// instance and equal authorized scope share a snapshot; policies still run for every operation. Omitted nonempty commands are conservatively
    /// RequiresInput; this API does not invent values or claim partial-input symbolic evaluation. It evaluates the entire
    /// canonical Transition with supplied input, including machine admission, Requires and final invariants.</remarks>
    public async Task<ImmutableArray<ServiceTransitionAvailability>> InspectTransitionsAsync(OperationContext context,
        QualifiedShapeId entity, string subject, IReadOnlyDictionary<string, PortableValue>? inputs = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var results = ImmutableArray.CreateBuilder<ServiceTransitionAvailability>();
        var snapshots = new Dictionary<IEntityRepository, Dictionary<ScopeRef, (EntitySnapshot? Snapshot, ObservationValue? Observation)>>(ReferenceEqualityComparer.Instance);
        var sharedPolicy = ReferenceEquals(discoveryAuthorization, authorization);
        foreach (var linked in operations.Values.OrderBy(item => item.Operation.Id, StringComparer.Ordinal))
        {
            context.ThrowIfCancellationRequested();
            if (linked.Operation is not ServiceTransitionOperation operation || operation.Entity != entity) continue;
            var visibleScope = await discoveryAuthorization.AdmitAsync(context, operation).ConfigureAwait(false);
            if (visibleScope is null) continue;
            var binding = (ServiceTransitionBinding)linked.Binding;
            var repository = binding.Repository(context) ?? throw new InvalidOperationException("The repository binding returned null.");
            if (repository.EntityDefinition.StateShape.QualifiedId != entity)
                throw new InvalidOperationException("The repository does not realize the declared entity authority.");
            var options = EntityReadOptions.Full.WithPartitionKey(visibleScope.ResolvePartitionKey());
            if (!snapshots.TryGetValue(repository, out var scopedSnapshots))
                snapshots.Add(repository, scopedSnapshots = []);
            if (!scopedSnapshots.TryGetValue(visibleScope, out var cached))
            {
                var loaded = await repository.TryGet(context, subject, options).ConfigureAwait(false);
                if (loaded is not null) ValidateTransitionSnapshot(repository, loaded, subject, options.PartitionKey);
                cached = (loaded, null);
                scopedSnapshots.Add(visibleScope, cached);
            }
            var snapshot = cached.Snapshot;
            if (snapshot is null) continue;
            if (!await discoveryAuthorization.AuthorizeResourceAsync(context, operation, snapshot).ConfigureAwait(false)) continue;

            if (cached.Observation is null)
            {
                var state = repository.EntityDefinition.CreateState(snapshot.Entity);
                cached.Observation = ObservationValue.FromObject(state.Fields);
                scopedSnapshots[visibleScope] = cached;
            }
            var kind = ServiceTransitionAvailabilityKind.RequiresInput;
            TransitionDecisionKind? decisionKind = null;
            ImmutableArray<DocumentValidationDiagnostic> diagnostics = [];
            var invokeScope = sharedPolicy ? visibleScope : await authorization.AdmitAsync(context, operation).ConfigureAwait(false);
            if (invokeScope is null || invokeScope.Kind != visibleScope.Kind || invokeScope.Id != visibleScope.Id
                || invokeScope.ResolvePartitionKey() != visibleScope.ResolvePartitionKey()
                || (!sharedPolicy && !await authorization.AuthorizeResourceAsync(context, operation, snapshot).ConfigureAwait(false)))
                kind = ServiceTransitionAvailabilityKind.Forbidden;
            else
            {
                PortableValue? input = null;
                if (inputs is not null) inputs.TryGetValue(operation.Id, out input);
                if (input is null && binding.Plan.Definition.Input.Type is ObjectTypeRef { Fields.IsEmpty: true })
                    input = PortableValue.Concrete(binding.Plan.Definition.Input, ObservationValue.EmptyObject);
                if (input is not null)
                {
                    context.ThrowIfCancellationRequested();
                    var decision = TransitionReferenceInterpreter.DecideFullState(binding.Plan, new("services.inspect/" + operation.Id),
                        input, PortableValue.Concrete(binding.Plan.Definition.Observation, cached.Observation.Value));
                    decisionKind = decision.Kind;
                    kind = decision.Kind switch
                    {
                        TransitionDecisionKind.Applied or TransitionDecisionKind.NoChange => ServiceTransitionAvailabilityKind.Permitted,
                        TransitionDecisionKind.AdmissionRejected or TransitionDecisionKind.DomainRejected or TransitionDecisionKind.Conflict => ServiceTransitionAvailabilityKind.Unavailable,
                        TransitionDecisionKind.InvalidDefinition => ServiceTransitionAvailabilityKind.Invalid,
                        _ => ServiceTransitionAvailabilityKind.Indeterminate
                    };
                    diagnostics = [.. decision.Diagnostics.Select(d => new DocumentValidationDiagnostic(d.Code, d.Severity,
                        "Transition inspection could not establish permission to apply this command.", d.Location))];
                }
            }
            context.ThrowIfCancellationRequested();
            results.Add(new(operation.Id, operation.Transition, binding.Plan.Definition.Input, snapshot.ConcurrencyToken,
                kind, decisionKind, diagnostics));
        }
        return results.ToImmutable();
    }
    static void ValidateTransitionSnapshot(IEntityRepository repository, EntitySnapshot snapshot, string subject, string? partition)
    {
        if (snapshot.Entity.EntityId.Value != subject
            || (partition is not null && snapshot.PartitionKey != partition)
            || (snapshot.LoadedFields is not null && repository.EntityDefinition.Shape.Fields.Any(f => !snapshot.LoadedFields.Contains(f.Name.Value))))
            throw new InvalidOperationException("The repository returned an inexact or partial subject snapshot.");
    }
}
