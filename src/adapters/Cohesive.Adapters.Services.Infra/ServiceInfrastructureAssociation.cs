using System.Collections.Immutable;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Infra;

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
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(infrastructure);
        ArgumentNullException.ThrowIfNull(operations);
        var validation = ServiceDefinitionDocuments.ValidateAndProject(service, out var definition);
        if (!validation.IsValid || !service.Extensions.IsEmpty)
            throw new ArgumentException("A valid service declaration without semantic extensions is required.", nameof(service));
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
