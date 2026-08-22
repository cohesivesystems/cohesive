using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;

namespace Cohesive.Adapters.DurableTask;

/// <summary>Stable diagnostics emitted while admitting Process plans to a Durable Task worker.</summary>
public static class DurableTaskProcessPlanAdmissionDiagnosticCodes
{
    /// <summary>An executable external Request node has no exact concrete durable Request binding.</summary>
    public const string ExternalRequestBindingMissing = "processes.durableTask.admission.externalRequestBinding.missing";

    /// <summary>An executable external Request node has a binding incompatible with its exact interaction catalog.</summary>
    public const string ExternalRequestBindingInvalid = "processes.durableTask.admission.externalRequestBinding.invalid";

    /// <summary>An executable external Request node has no exact deployed adapter capability evidence.</summary>
    public const string ExternalRequestCapabilityMissing = "processes.durableTask.admission.externalRequestCapability.missing";

    /// <summary>Deployed adapter capability evidence cannot satisfy an executable external Request binding.</summary>
    public const string ExternalRequestCapabilityInvalid = "processes.durableTask.admission.externalRequestCapability.invalid";

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
    /// Concrete durable Request bindings deployed to this worker. Every external operation and child Process Request
    /// in <paramref name="plans"/> requires one exact compatible binding.
    /// </param>
    /// <param name="domainEventPublisherResolver">
    /// Deterministic exact domain-event publisher resolver. Plans that directly emit an event require a publisher
    /// whose capabilities declare target deduplication for that exact contract.
    /// </param>
    /// <param name="operationAdapterCapabilities">
    /// Exact external-operation capability evidence published by the same deployed adapter authority used at runtime.
    /// Every ordinary external Request in <paramref name="plans"/> requires matching evidence.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="plans"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An entry is null, repeats an exact reference, has a conflicting fingerprint, or was not compiled against the
    /// exact executable profile; a Request lacks one exact compatible binding or external adapter capability; or a
    /// directly emitted event lacks an exact target-deduplicating publisher.
    /// </exception>
    public DurableTaskSequentialProcessPlanCatalog(
        IEnumerable<DurableTaskProcessRealizationPlan> plans,
        IEnumerable<DurableRequestBinding>? requestBindings = null,
        IDomainEventPublisherResolver? domainEventPublisherResolver = null,
        IDurableOperationAdapterCapabilityResolver? operationAdapterCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var bindingCatalog = new DurableRequestBindingCatalog(requestBindings ?? []);
        this.domainEventPublisherResolver = domainEventPublisherResolver
            ?? EmptyDomainEventPublisherResolver.Instance;
        var adapterCapabilities = operationAdapterCapabilities
            ?? EmptyDurableOperationAdapterCapabilityResolver.Instance;
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
            RequireRequestCapabilities(
                plan,
                bindingCatalog,
                adapterCapabilities,
                nameof(requestBindings),
                nameof(operationAdapterCapabilities));
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

    static void RequireRequestCapabilities(
        DurableTaskProcessRealizationPlan plan,
        DurableRequestBindingCatalog bindings,
        IDurableOperationAdapterCapabilityResolver adapterCapabilities,
        string bindingParameterName,
        string capabilityParameterName)
    {
        var contracts = plan.CanonicalPlan.ValidationContext.InteractionContracts;
        DurableOperationReferenceExecutor? validator = contracts is null
            ? null
            : new(contracts);
        foreach (var requirement in ProcessRequestRequirementCollector.Collect(plan.CanonicalPlan).Requirements)
        {
            var isExternal = requirement.Kind == ProcessRequestRequirementKind.ExternalOperation;
            var bindingMissingCode = isExternal
                ? DurableTaskProcessPlanAdmissionDiagnosticCodes.ExternalRequestBindingMissing
                : DurableTaskProcessPlanAdmissionDiagnosticCodes.ChildRequestBindingMissing;
            var bindingInvalidCode = isExternal
                ? DurableTaskProcessPlanAdmissionDiagnosticCodes.ExternalRequestBindingInvalid
                : DurableTaskProcessPlanAdmissionDiagnosticCodes.ChildRequestBindingInvalid;
            var source = $"Process '{Describe(plan.Definition)}' node '{requirement.Node.Value}'";
            if (!bindings.TryResolve(requirement.Request, out var binding) || binding is null)
            {
                throw new ArgumentException(
                    $"{bindingMissingCode}: {source} requires exact Request '{Describe(requirement.Request)}', "
                    + "but no concrete durable binding was deployed.",
                    bindingParameterName);
            }
            if (validator is null)
            {
                throw new ArgumentException(
                    $"{bindingInvalidCode}: {source} cannot validate its Request binding because the compiled "
                    + "interaction catalog is absent.",
                    bindingParameterName);
            }

            var validation = validator.ValidateBinding(binding);
            if (!validation.IsValid)
            {
                throw new ArgumentException(
                    $"{bindingInvalidCode}: {source} has an incompatible binding for exact Request "
                    + $"'{Describe(requirement.Request)}': "
                    + Format(validation),
                    bindingParameterName);
            }

            if (!isExternal)
            {
                continue;
            }

            if (!adapterCapabilities.TryResolve(requirement.Request, out var capabilities)
                || capabilities is null)
            {
                throw new ArgumentException(
                    $"{DurableTaskProcessPlanAdmissionDiagnosticCodes.ExternalRequestCapabilityMissing}: {source} "
                    + $"requires exact Request '{Describe(requirement.Request)}', but no deployed durable-operation "
                    + "adapter publishes capability evidence for it.",
                    capabilityParameterName);
            }

            var capabilityValidation = binding.ReconciliationTarget is null
                ? DurableOperationReferenceExecutor.AssessAdapterCapabilities(binding, capabilities)
                : DurableOperationReferenceExecutor.AssessReconciliationAdapterCapabilities(binding, capabilities);
            if (!capabilityValidation.IsValid)
            {
                throw new ArgumentException(
                    $"{DurableTaskProcessPlanAdmissionDiagnosticCodes.ExternalRequestCapabilityInvalid}: {source} "
                    + $"has incompatible adapter capabilities for exact Request "
                    + $"'{Describe(requirement.Request)}': {Format(capabilityValidation)}",
                    capabilityParameterName);
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
