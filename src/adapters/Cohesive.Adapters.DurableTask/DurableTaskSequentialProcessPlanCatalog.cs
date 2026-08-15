using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.IR;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Stable diagnostics emitted while admitting Process plans to a Durable Task worker.</summary>
public static class DurableTaskProcessPlanAdmissionDiagnosticCodes
{
    /// <summary>An executable child Process node has no exact concrete durable Request binding.</summary>
    public const string ChildRequestBindingMissing = "processes.durableTask.admission.childRequestBinding.missing";

    /// <summary>An executable child Process node has a binding incompatible with its exact interaction catalog.</summary>
    public const string ChildRequestBindingInvalid = "processes.durableTask.admission.childRequestBinding.invalid";
}

/// <summary>Immutable exact-reference catalog of precompiled Process plans admitted for bounded execution.</summary>
/// <remarks>
/// This catalog is a worker deployment projection, not definition authority. Every entry retains its canonical
/// document and compiled plan, and lookup requires the complete definition identity, revision, and fingerprint.
/// A worker restart must rebuild an equivalent catalog before it can replay an in-flight orchestration.
/// </remarks>
public sealed class DurableTaskSequentialProcessPlanCatalog
{
    readonly ImmutableDictionary<ExecutionDefinitionReference, DurableTaskProcessRealizationPlan> plans;
    readonly IDomainEventPublisherResolver domainEventPublisherResolver;

    /// <summary>Creates an immutable catalog from executable-qualified canonical Processes.</summary>
    /// <param name="plans">Exact Durable Task realization plans deployed to this worker.</param>
    /// <param name="requestBindings">
    /// Concrete durable Request bindings deployed to this worker. Bindings remain optional for ordinary external
    /// Requests, but every child Process Request in <paramref name="plans"/> requires one exact compatible binding.
    /// </param>
    /// <param name="domainEventPublisherResolver">
    /// Deterministic exact domain-event publisher resolver. Plans that directly emit an event require a publisher
    /// whose capabilities declare target deduplication for that exact contract.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="plans"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is null, repeats an exact reference, has a conflicting fingerprint, or was not compiled against the
    /// exact executable profile; a child Process Request lacks one exact compatible binding; or a directly emitted
    /// event lacks an exact target-deduplicating publisher.
    /// </exception>
    public DurableTaskSequentialProcessPlanCatalog(
        IEnumerable<DurableTaskProcessRealizationPlan> plans,
        IEnumerable<DurableRequestBinding>? requestBindings = null,
        IDomainEventPublisherResolver? domainEventPublisherResolver = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var bindingCatalog = new DurableRequestBindingCatalog(requestBindings ?? []);
        this.domainEventPublisherResolver = domainEventPublisherResolver
            ?? EmptyDomainEventPublisherResolver.Instance;
        var builder = ImmutableDictionary.CreateBuilder<ExecutionDefinitionReference, DurableTaskProcessRealizationPlan>();
        Dictionary<(ExecutionDefinitionId Definition, ExecutionRevisionId Revision), ExecutionDefinitionReference>
            revisions = [];
        foreach (var plan in plans)
        {
            if (plan is null)
            {
                throw new ArgumentException("A Process plan catalog cannot contain null entries.", nameof(plans));
            }

            if (plan.Realization.TargetProfile.Id != DurableTaskProcessTargetProfile.ExecutableProfileId)
            {
                throw new ArgumentException(
                    $"Process definition '{plan.Definition.DefinitionId.Value}' was planned with profile "
                    + $"'{plan.Realization.TargetProfile.Id.Value}'. Worker admission requires executable profile "
                    + $"'{DurableTaskProcessTargetProfile.ExecutableProfileId.Value}'; compile it with "
                    + $"{nameof(DurableTaskProcessRealizationCompiler)}.{nameof(DurableTaskProcessRealizationCompiler.CompileExecutable)}.",
                    nameof(plans));
            }
            RequireChildProcessBindings(plan, bindingCatalog, nameof(requestBindings));
            foreach (var domainEvent in plan.CanonicalPlan.Definition.Nodes.OfType<EmitEventProcessNode>())
            {
                _ = ResolveDomainEventPublisher(domainEvent.Contract, nameof(plans));
            }
            var revisionKey = (plan.Definition.DefinitionId, plan.Definition.RevisionId);
            if (revisions.TryGetValue(revisionKey, out var retained)
                && retained.Fingerprint != plan.Definition.Fingerprint)
            {
                throw new ArgumentException(
                    $"Process definition '{plan.Definition.DefinitionId.Value}' revision "
                    + $"'{plan.Definition.RevisionId.Value}' is deployed with conflicting fingerprints.",
                    nameof(plans));
            }
            revisions[revisionKey] = plan.Definition;
            if (!builder.TryAdd(plan.Definition, plan))
            {
                throw new ArgumentException(
                    $"Process definition '{plan.Definition.DefinitionId.Value}' revision "
                    + $"'{plan.Definition.RevisionId.Value}' is deployed more than once with the same fingerprint.",
                    nameof(plans));
            }
        }

        this.plans = builder.ToImmutable();
        BindingResolver = bindingCatalog;
    }

    /// <summary>Number of exact Process plans deployed to the worker.</summary>
    public int Count => plans.Count;

    internal IDurableRequestBindingResolver BindingResolver { get; }

    static void RequireChildProcessBindings(
        DurableTaskProcessRealizationPlan plan,
        DurableRequestBindingCatalog bindings,
        string parameterName)
    {
        var contracts = plan.CanonicalPlan.ValidationContext.InteractionContracts;
        DurableOperationReferenceExecutor? validator = contracts is null
            ? null
            : new(contracts);
        foreach (var node in plan.CanonicalPlan.Definition.Nodes)
        {
            var request = node switch
            {
                InvokeProcessProcessNode invoke => invoke.Contract,
                ForEachPartitionProcessNode partition => partition.Contract,
                _ => null
            };
            if (request is null)
            {
                continue;
            }

            var source = $"Process '{Describe(plan.Definition)}' node '{node.Id.Value}'";
            if (!bindings.TryResolve(request, out var binding) || binding is null)
            {
                throw new ArgumentException(
                    $"{DurableTaskProcessPlanAdmissionDiagnosticCodes.ChildRequestBindingMissing}: {source} "
                    + $"invokes a child through exact Request '{Describe(request)}', but no concrete durable "
                    + "binding was deployed. Register the binding derived from the child invocation protocol.",
                    parameterName);
            }
            if (validator is null)
            {
                throw new ArgumentException(
                    $"{DurableTaskProcessPlanAdmissionDiagnosticCodes.ChildRequestBindingInvalid}: {source} "
                    + "cannot validate its child Request binding because the compiled interaction catalog is absent.",
                    parameterName);
            }

            var validation = validator.ValidateBinding(binding);
            if (!validation.IsValid)
            {
                throw new ArgumentException(
                    $"{DurableTaskProcessPlanAdmissionDiagnosticCodes.ChildRequestBindingInvalid}: {source} "
                    + $"has an incompatible binding for exact Request '{Describe(request)}': "
                    + Format(validation),
                    parameterName);
            }
        }

        static string Format(DocumentValidationResult validation) => string.Join(
            "; ",
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
    }

    internal IDomainEventPublisher ResolveDomainEventPublisher(
        DomainEventContractReference contract,
        string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (!domainEventPublisherResolver.TryResolve(contract, out var publisher) || publisher is null)
        {
            var message = $"No target-deduplicating domain-event publisher is registered for exact contract "
                + $"'{Describe(contract)}'.";
            if (parameterName is not null)
            {
                throw new ArgumentException(message, parameterName);
            }
            throw new InvalidOperationException(message);
        }

        var capabilities = publisher.Capabilities;
        if (capabilities is null)
        {
            var message =
                $"Domain-event publisher for exact contract '{Describe(contract)}' returned null capabilities.";
            if (parameterName is not null)
            {
                throw new ArgumentException(message, parameterName);
            }
            throw new InvalidOperationException(message);
        }
        if (!capabilities.Supports(contract))
        {
            var message = $"Domain-event publisher for exact contract '{Describe(contract)}' does not declare "
                + "target deduplication for that contract.";
            if (parameterName is not null)
            {
                throw new ArgumentException(message, parameterName);
            }
            throw new InvalidOperationException(message);
        }

        return publisher;

        static string Describe(DomainEventContractReference candidate) =>
            $"{candidate.Definition.DefinitionId.Value}@{candidate.Definition.RevisionId.Value}#"
            + candidate.Definition.Fingerprint.Value;
    }

    /// <summary>Resolves the precompiled plan matching one complete canonical definition reference.</summary>
    /// <param name="definition">Exact definition identity, revision, and fingerprint.</param>
    /// <returns>The matching realization plan.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">No exact deployed plan matches <paramref name="definition"/>.</exception>
    public DurableTaskProcessRealizationPlan GetExact(ExecutionDefinitionReference definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return plans.TryGetValue(definition, out var plan)
            ? plan
            : throw new KeyNotFoundException(
                $"No Durable Task Process plan is deployed for exact definition "
                + $"'{definition.DefinitionId.Value}' revision '{definition.RevisionId.Value}' fingerprint "
                + $"'{definition.Fingerprint.Value}'.");
    }

    static string Describe(ExecutionDefinitionReference definition) =>
        $"{definition.DefinitionId.Value}@{definition.RevisionId.Value}#{definition.Fingerprint.Value}";

    static string Describe(RequestContractReference request) => Describe(request.Definition);
}
