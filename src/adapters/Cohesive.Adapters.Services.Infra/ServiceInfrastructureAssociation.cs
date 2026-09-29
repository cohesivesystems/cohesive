using System.Collections.Immutable;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Adapters.Services.Infra;

/// <summary>Validated exact service placement over existing infrastructure consumer bindings.</summary>
/// <remarks>Placement is not a capability proof, authorization grant or readiness observation.
/// Target capability compilation and runtime evidence must independently qualify the selected bindings.</remarks>
public sealed class ServiceInfrastructureAssociation
{
    ServiceInfrastructureAssociation(ExecutionDefinitionReference service, InfrastructureDefinitionReference infrastructure,
        InfrastructureNodeId workload, ImmutableDictionary<string, ImmutableArray<InfrastructureBindingId>> operations)
    {
        Service = service; Infrastructure = infrastructure; Workload = workload; Operations = operations;
    }

    /// <summary>Exact service identity, revision and semantic fingerprint.</summary>
    public ExecutionDefinitionReference Service { get; }
    /// <summary>Exact infrastructure topology against which placement was checked.</summary>
    public InfrastructureDefinitionReference Infrastructure { get; }
    /// <summary>Existing workload consuming the selected bindings.</summary>
    public InfrastructureNodeId Workload { get; }
    /// <summary>Complete operation coverage with normalized, existing consumer binding identities.</summary>
    public ImmutableDictionary<string, ImmutableArray<InfrastructureBindingId>> Operations { get; }

    /// <summary>Validates target capability closure against this association's exact infrastructure authority.</summary>
    /// <param name="closure">Report produced by the existing infrastructure capability compiler.</param>
    /// <returns>Native closure diagnostics, or an exact-authority mismatch diagnostic.</returns>
    /// <remarks>This requires closure of the whole supplied deployment definition, not only this service's subset.
    /// It does not establish observed readiness, runtime registration, or dependency completeness.</remarks>
    /// <exception cref="ArgumentNullException">The report is null.</exception>
    public DocumentValidationResult ValidateCapabilityClosure(InfrastructureCapabilityClosureReport closure)
    {
        ArgumentNullException.ThrowIfNull(closure);
        if (closure.Definition.ToReference() != Infrastructure)
            return new([new("services.infra.definitionMismatch", DiagnosticSeverity.Error,
                "Capability evidence belongs to a different exact infrastructure definition.", "/infrastructure")]);
        if (closure.IsClosed) return new(closure.Diagnostics);
        return new([.. closure.Diagnostics, new("services.infra.capabilitiesUnclosed", DiagnosticSeverity.Error,
            "The associated deployment has unresolved capability requirements; inspect the native closure diagnostics.", "/capabilities")]);
    }

    /// <summary>Validates a native readiness assessment against independently selected realization authority.</summary>
    /// <remarks>Consume the assessment produced by the trusted native evaluator/provider pipeline once; this
    /// method does not recollect evidence or reevaluate observations. It is not a validator for untrusted persisted
    /// assessments. Provider scope, freshness and artifact integrity must be established at their owning boundary.
    /// Readiness covers the whole supplied realization and requires an explicit decision for this workload.
    /// Selected consumer bindings do not invent readiness obligations or prove dependency completeness.</remarks>
    /// <param name="expectedRealization">Independently selected exact physical realization, not inferred from the assessment.</param>
    /// <param name="assessment">Native assessment from the admitted observation pipeline.</param>
    /// <returns>Native diagnostics with exact-authority or non-readiness diagnostics when admission fails.</returns>
    /// <exception cref="ArgumentNullException">A reference argument is null.</exception>
    public DocumentValidationResult ValidateReadiness(InfrastructureRealizationReference expectedRealization,
        InfrastructureReadinessAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(expectedRealization);
        ArgumentNullException.ThrowIfNull(assessment);
        if (expectedRealization.Definition != Infrastructure || assessment.Realization != expectedRealization)
            return new([new("services.infra.realizationMismatch", DiagnosticSeverity.Error,
                "Readiness evidence must belong to the independently selected realization of the associated topology.", "/realization")]);
        var workload = assessment.FindDecision(Workload);
        if (assessment.IsReady && workload is { Kind: InfrastructureNodeKind.Workload, IsReady: true })
            return new(assessment.Diagnostics);
        return new([.. assessment.Diagnostics, new("services.infra.notReady", DiagnosticSeverity.Error,
            "The associated workload or deployment is not observed ready; inspect the native assessment diagnostics.", "/readiness")]);
    }

    /// <summary>Applies explicit service-wide prerequisites to every operation in the canonical declaration.</summary>
    /// <remarks>The caller owns placement policy; this method derives operation coverage, not dependencies.
    /// Adding an operation automatically includes the same prerequisites. Use per-operation Create when
    /// requirements differ. An explicit empty set is allowed and does not prove dependency completeness.</remarks>
    /// <param name="service">Canonical service document without unsupported semantic extensions.</param>
    /// <param name="infrastructure">Exact canonical infrastructure topology.</param>
    /// <param name="workload">Workload consuming every prerequisite.</param>
    /// <param name="prerequisites">Explicit unique existing consumer bindings, copied before projection.</param>
    /// <returns>Immutable association with coverage derived from the service declaration.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A declaration, workload or prerequisite is invalid.</exception>
    public static ServiceInfrastructureAssociation CreateWithSharedPrerequisites(ExecutionDefinitionDocument service,
        InfrastructureDefinition infrastructure, InfrastructureNodeId workload,
        IReadOnlyCollection<InfrastructureBindingId> prerequisites)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);
        var definition = RequireDefinition(service);
        ImmutableArray<InfrastructureBindingId> selected = [.. prerequisites];
        if (selected.Distinct().Count() != selected.Length)
            throw new ArgumentException("Shared prerequisites must be unique.", nameof(prerequisites));
        return CreateValidated(service, definition, infrastructure, workload,
            definition.Operations.ToDictionary(operation => operation.Id, _ => selected, StringComparer.Ordinal));
    }

    /// <summary>Checks complete operation placement without constructing resources or execution runtimes.</summary>
    /// <param name="service">Canonical service document; semantic extensions are unsupported by this profile.</param>
    /// <param name="infrastructure">Canonical infrastructure definition; its existing identities remain authoritative.</param>
    /// <param name="workload">Existing workload that consumes every selected infrastructure binding.</param>
    /// <param name="operations">Exactly one entry per service operation; an explicit empty set is permitted for operations without infrastructure dependencies.</param>
    /// <returns>An immutable association retaining both exact authorities.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A declaration, operation, workload or binding is invalid or unsupported.</exception>
    public static ServiceInfrastructureAssociation Create(ExecutionDefinitionDocument service,
        InfrastructureDefinition infrastructure, InfrastructureNodeId workload,
        IReadOnlyDictionary<string, ImmutableArray<InfrastructureBindingId>> operations)
    {
        return CreateValidated(service, RequireDefinition(service), infrastructure, workload, operations);
    }

    static ServiceDefinition RequireDefinition(ExecutionDefinitionDocument service)
    {
        ArgumentNullException.ThrowIfNull(service);
        var validation = ServiceDefinitionDocuments.ValidateAndProject(service, out var definition);
        if (!validation.IsValid || !service.Extensions.IsEmpty)
            throw new ArgumentException("A valid service declaration without semantic extensions is required.", nameof(service));
        return definition!;
    }

    static ServiceInfrastructureAssociation CreateValidated(ExecutionDefinitionDocument service, ServiceDefinition definition,
        InfrastructureDefinition infrastructure, InfrastructureNodeId workload,
        IReadOnlyDictionary<string, ImmutableArray<InfrastructureBindingId>> operations)
    {
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentNullException.ThrowIfNull(operations);
        if (!infrastructure.Workloads.Any(candidate => candidate.Id == workload))
            throw new ArgumentException($"Workload '{workload.Value}' is not declared.", nameof(workload));
        var names = definition!.Operations.Select(operation => operation.Id).ToHashSet(StringComparer.Ordinal);
        if (operations.Count != names.Count || operations.Keys.Any(key => !names.Contains(key)))
            throw new ArgumentException("Placement must cover every declared operation exactly, without unknown operations.", nameof(operations));
        var bindings = infrastructure.Bindings.ToDictionary(binding => binding.Id);
        var normalized = ImmutableDictionary.CreateBuilder<string, ImmutableArray<InfrastructureBindingId>>(StringComparer.Ordinal);
        foreach (var (operation, selected) in operations)
        {
            if (selected.IsDefault || selected.Distinct().Count() != selected.Length)
                throw new ArgumentException($"Operation '{operation}' requires an explicit set of unique bindings.", nameof(operations));
            foreach (var id in selected)
                if (!bindings.TryGetValue(id, out var binding) || binding.Source != workload)
                    throw new ArgumentException($"Operation '{operation}' binding '{id.Value}' is not consumed by workload '{workload.Value}'.", nameof(operations));
            normalized.Add(operation, [.. selected.OrderBy(id => id.Value, StringComparer.Ordinal)]);
        }
        var document = InfrastructureDefinitionDocument.FromDefinition(infrastructure);
        return new(new(service.Metadata.DefinitionId, service.Metadata.RevisionId, service.Metadata.Fingerprint),
            document.ToReference(), workload, normalized.ToImmutable());
    }
}
