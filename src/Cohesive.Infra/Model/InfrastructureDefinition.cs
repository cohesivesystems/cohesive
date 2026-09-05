using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra;

/// <summary>Closed semantic family of a node in a canonical infrastructure definition.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureNodeKind
{
    /// <summary>An executable application or system workload.</summary>
    Workload = 0,

    /// <summary>A required logical infrastructure resource.</summary>
    Resource = 1
}

/// <summary>Desired ownership and replacement lifecycle of an infrastructure resource.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureResourceLifecycle
{
    /// <summary>The resource and its durable contents normally survive workload replacement.</summary>
    Persistent = 0,

    /// <summary>The resource may be recreated with the deployment that owns it.</summary>
    Ephemeral = 1,

    /// <summary>The resource already exists outside this definition's provisioning ownership.</summary>
    External = 2
}

/// <summary>One provider-neutral capability required by an infrastructure node.</summary>
public sealed record InfrastructureCapabilityRequirement
{
    /// <summary>Creates an infrastructure capability requirement.</summary>
    /// <param name="id">Stable definition-local requirement identity.</param>
    /// <param name="capability">Provider-neutral capability demanded by the owning node.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or <paramref name="capability"/> is a default uninitialized value.
    /// </exception>
    [JsonConstructor]
    public InfrastructureCapabilityRequirement(
        InfrastructureRequirementId id,
        InfrastructureCapabilityId capability)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure capability requirement requires a stable identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(capability.Value))
            throw new ArgumentException("An infrastructure capability requirement requires a capability identity.", nameof(capability));

        Id = id;
        Capability = capability;
    }

    /// <summary>Stable definition-local requirement identity.</summary>
    public InfrastructureRequirementId Id { get; }

    /// <summary>Provider-neutral capability demanded by the owning node.</summary>
    public InfrastructureCapabilityId Capability { get; }

    /// <summary>Creates a requirement with a deterministic identity derived from its node and capability.</summary>
    /// <param name="node">Owning infrastructure node.</param>
    /// <param name="capability">Provider-neutral capability demanded by the node.</param>
    /// <returns>A capability requirement with a stable collision-free textual identity.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="node"/> or <paramref name="capability"/> is a default uninitialized value.
    /// </exception>
    public static InfrastructureCapabilityRequirement ForNode(
        InfrastructureNodeId node,
        InfrastructureCapabilityId capability)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A derived infrastructure requirement requires a node identity.", nameof(node));
        if (string.IsNullOrWhiteSpace(capability.Value))
            throw new ArgumentException("A derived infrastructure requirement requires a capability identity.", nameof(capability));

        return new(
            new($"node/{Uri.EscapeDataString(node.Value)}/requires/{Uri.EscapeDataString(capability.Value)}"),
            capability);
    }
}

/// <summary>Canonical provider-neutral definition of one executable infrastructure workload.</summary>
public sealed record InfrastructureWorkloadDefinition
{
    /// <summary>Creates an infrastructure workload definition.</summary>
    /// <param name="id">Stable definition-local workload identity.</param>
    /// <param name="requirements">Capabilities required by the workload.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default, or <paramref name="requirements"/> contains a null, duplicate identity,
    /// or duplicate capability.
    /// </exception>
    [JsonConstructor]
    public InfrastructureWorkloadDefinition(
        InfrastructureNodeId id,
        ImmutableArray<InfrastructureCapabilityRequirement> requirements = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure workload requires a stable node identity.", nameof(id));

        Id = id;
        Requirements = InfrastructureModelCollections.NormalizeRequirements(requirements, nameof(requirements));
    }

    /// <summary>Stable definition-local workload identity.</summary>
    public InfrastructureNodeId Id { get; }

    /// <summary>Semantic node family.</summary>
    [JsonIgnore]
    public InfrastructureNodeKind Kind => InfrastructureNodeKind.Workload;

    /// <summary>Required capabilities in deterministic requirement-identity order.</summary>
    public ImmutableArray<InfrastructureCapabilityRequirement> Requirements { get; }

    /// <summary>Compares normalized workload definitions structurally.</summary>
    /// <param name="other">Other workload definition.</param>
    /// <returns><see langword="true"/> when identity and every capability requirement are equal.</returns>
    public bool Equals(InfrastructureWorkloadDefinition? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Requirements.SequenceEqual(other.Requirements);

    /// <summary>Returns a structural hash code for the normalized workload definition.</summary>
    /// <returns>A hash derived from the identity and every capability requirement.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        foreach (var requirement in Requirements)
            hash.Add(requirement);
        return hash.ToHashCode();
    }
}

/// <summary>Canonical provider-neutral definition of one logical infrastructure resource.</summary>
public sealed record InfrastructureResourceDefinition
{
    /// <summary>Creates an infrastructure resource definition.</summary>
    /// <param name="id">Stable definition-local resource identity.</param>
    /// <param name="lifecycle">Desired ownership and replacement lifecycle.</param>
    /// <param name="requirements">Capabilities required from the realized resource.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default, or <paramref name="requirements"/> contains a null, duplicate identity,
    /// or duplicate capability.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifecycle"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureResourceDefinition(
        InfrastructureNodeId id,
        InfrastructureResourceLifecycle lifecycle,
        ImmutableArray<InfrastructureCapabilityRequirement> requirements = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure resource requires a stable node identity.", nameof(id));
        if (!Enum.IsDefined(lifecycle))
            throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "Unsupported infrastructure resource lifecycle.");

        Id = id;
        Lifecycle = lifecycle;
        Requirements = InfrastructureModelCollections.NormalizeRequirements(requirements, nameof(requirements));
    }

    /// <summary>Stable definition-local resource identity.</summary>
    public InfrastructureNodeId Id { get; }

    /// <summary>Semantic node family.</summary>
    [JsonIgnore]
    public InfrastructureNodeKind Kind => InfrastructureNodeKind.Resource;

    /// <summary>Desired ownership and replacement lifecycle.</summary>
    public InfrastructureResourceLifecycle Lifecycle { get; }

    /// <summary>Required capabilities in deterministic requirement-identity order.</summary>
    public ImmutableArray<InfrastructureCapabilityRequirement> Requirements { get; }

    /// <summary>Compares normalized resource definitions structurally.</summary>
    /// <param name="other">Other resource definition.</param>
    /// <returns><see langword="true"/> when lifecycle, identity, and every capability requirement are equal.</returns>
    public bool Equals(InfrastructureResourceDefinition? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Lifecycle == other.Lifecycle
        && Requirements.SequenceEqual(other.Requirements);

    /// <summary>Returns a structural hash code for the normalized resource definition.</summary>
    /// <returns>A hash derived from lifecycle, identity, and every capability requirement.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Lifecycle);
        foreach (var requirement in Requirements)
            hash.Add(requirement);
        return hash.ToHashCode();
    }
}

/// <summary>One directed semantic contract binding between two infrastructure nodes.</summary>
public sealed record InfrastructureBindingDefinition
{
    /// <summary>Creates an infrastructure binding definition.</summary>
    /// <param name="id">Stable definition-local binding identity.</param>
    /// <param name="source">Node that consumes or initiates the binding contract.</param>
    /// <param name="target">Node that supplies or receives the binding contract.</param>
    /// <param name="contract">Provider-neutral contract carried by the binding.</param>
    /// <exception cref="ArgumentException">Any identity is a default uninitialized value.</exception>
    [JsonConstructor]
    public InfrastructureBindingDefinition(
        InfrastructureBindingId id,
        InfrastructureNodeId source,
        InfrastructureNodeId target,
        InfrastructureBindingContractId contract)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure binding requires a stable identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(source.Value))
            throw new ArgumentException("An infrastructure binding requires a source node.", nameof(source));
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("An infrastructure binding requires a target node.", nameof(target));
        if (string.IsNullOrWhiteSpace(contract.Value))
            throw new ArgumentException("An infrastructure binding requires a contract identity.", nameof(contract));

        Id = id;
        Source = source;
        Target = target;
        Contract = contract;
    }

    /// <summary>Stable definition-local binding identity.</summary>
    public InfrastructureBindingId Id { get; }

    /// <summary>Node that consumes or initiates the binding contract.</summary>
    public InfrastructureNodeId Source { get; }

    /// <summary>Node that supplies or receives the binding contract.</summary>
    public InfrastructureNodeId Target { get; }

    /// <summary>Provider-neutral contract carried by the binding.</summary>
    public InfrastructureBindingContractId Contract { get; }

    /// <summary>Derives the conventional stable identity for one exact directed contract binding.</summary>
    /// <param name="source">Node that consumes or initiates the binding contract.</param>
    /// <param name="target">Node that supplies or receives the binding contract.</param>
    /// <param name="contract">Provider-neutral contract carried by the binding.</param>
    /// <returns>An identity derived only from the exact source, target, and contract semantic slot.</returns>
    /// <exception cref="ArgumentException">Any identity is a default uninitialized value.</exception>
    public static InfrastructureBindingId DeriveId(
        InfrastructureNodeId source,
        InfrastructureNodeId target,
        InfrastructureBindingContractId contract)
    {
        if (string.IsNullOrWhiteSpace(source.Value))
            throw new ArgumentException("A conventional infrastructure binding requires a source node.", nameof(source));
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("A conventional infrastructure binding requires a target node.", nameof(target));
        if (string.IsNullOrWhiteSpace(contract.Value))
            throw new ArgumentException("A conventional infrastructure binding requires a contract identity.", nameof(contract));

        return new(
            $"bindings/{Uri.EscapeDataString(source.Value)}/to/{Uri.EscapeDataString(target.Value)}/as/{Uri.EscapeDataString(contract.Value)}");
    }
}

/// <summary>One provider-neutral prerequisite for admitting an infrastructure node as ready.</summary>
public sealed record InfrastructureReadinessDependency
{
    /// <summary>Creates an infrastructure readiness dependency.</summary>
    /// <param name="id">Stable definition-local dependency identity.</param>
    /// <param name="subject">Node whose readiness is gated.</param>
    /// <param name="dependency">Node that must be ready before the subject is ready.</param>
    /// <exception cref="ArgumentException">An identity is default or the dependency is self-referential.</exception>
    [JsonConstructor]
    public InfrastructureReadinessDependency(
        InfrastructureReadinessDependencyId id,
        InfrastructureNodeId subject,
        InfrastructureNodeId dependency)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure readiness dependency requires a stable identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(subject.Value))
            throw new ArgumentException("An infrastructure readiness dependency requires a subject node.", nameof(subject));
        if (string.IsNullOrWhiteSpace(dependency.Value))
            throw new ArgumentException("An infrastructure readiness dependency requires a dependency node.", nameof(dependency));
        if (subject == dependency)
            throw new ArgumentException("An infrastructure node cannot require itself to be ready.", nameof(dependency));

        Id = id;
        Subject = subject;
        Dependency = dependency;
    }

    /// <summary>Stable definition-local dependency identity.</summary>
    public InfrastructureReadinessDependencyId Id { get; }

    /// <summary>Node whose readiness is gated.</summary>
    public InfrastructureNodeId Subject { get; }

    /// <summary>Node that must be ready before the subject is ready.</summary>
    public InfrastructureNodeId Dependency { get; }

    /// <summary>Derives the conventional stable identity for one exact directed readiness dependency.</summary>
    /// <param name="subject">Node whose readiness is gated.</param>
    /// <param name="dependency">Node that must be ready before the subject is ready.</param>
    /// <returns>An identity derived only from the exact subject and dependency semantic slot.</returns>
    /// <exception cref="ArgumentException">An identity is default or the dependency is self-referential.</exception>
    public static InfrastructureReadinessDependencyId DeriveId(
        InfrastructureNodeId subject,
        InfrastructureNodeId dependency)
    {
        if (string.IsNullOrWhiteSpace(subject.Value))
            throw new ArgumentException("A conventional readiness dependency requires a subject node.", nameof(subject));
        if (string.IsNullOrWhiteSpace(dependency.Value))
            throw new ArgumentException("A conventional readiness dependency requires a dependency node.", nameof(dependency));
        if (subject == dependency)
            throw new ArgumentException("An infrastructure node cannot require itself to be ready.", nameof(dependency));

        return new(
            $"readiness/{Uri.EscapeDataString(subject.Value)}/requires/{Uri.EscapeDataString(dependency.Value)}");
    }
}

/// <summary>Canonical provider-neutral desired infrastructure topology.</summary>
/// <remarks>
/// Workload, resource, binding, readiness-dependency, and requirement collection order is non-semantic and normalized
/// by stable identity.
/// Concrete provider resources, deployment handles, generated programs, and observed state are interpretations of this
/// definition and do not belong in this IR.
/// </remarks>
public sealed record InfrastructureDefinition
{
    /// <summary>Creates a normalized infrastructure definition.</summary>
    /// <param name="id">Stable identity shared by semantic revisions.</param>
    /// <param name="revision">Stable identity of this exact semantic revision.</param>
    /// <param name="workloads">Executable workload nodes.</param>
    /// <param name="resources">Logical resource nodes.</param>
    /// <param name="bindings">Directed contracts between declared nodes.</param>
    /// <param name="readinessDependencies">Directed prerequisites that gate node readiness.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default; no node is declared; a collection contains nulls or duplicate identities; a node identity
    /// is reused across node families; a requirement identity is reused across nodes; a binding repeats the same
    /// source, target, and contract; a binding or readiness dependency references an undeclared node; a readiness slot
    /// repeats; or the readiness graph contains a cycle.
    /// </exception>
    [JsonConstructor]
    public InfrastructureDefinition(
        InfrastructureDefinitionId id,
        InfrastructureRevisionId revision,
        ImmutableArray<InfrastructureWorkloadDefinition> workloads = default,
        ImmutableArray<InfrastructureResourceDefinition> resources = default,
        ImmutableArray<InfrastructureBindingDefinition> bindings = default,
        ImmutableArray<InfrastructureReadinessDependency> readinessDependencies = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure definition requires a stable identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(revision.Value))
            throw new ArgumentException("An infrastructure definition requires a stable revision identity.", nameof(revision));

        Id = id;
        Revision = revision;
        Workloads = InfrastructureModelCollections.NormalizeReferenceValues(
            workloads,
            static workload => workload.Id.Value,
            "Infrastructure workload identities cannot repeat.",
            nameof(workloads));
        Resources = InfrastructureModelCollections.NormalizeReferenceValues(
            resources,
            static resource => resource.Id.Value,
            "Infrastructure resource identities cannot repeat.",
            nameof(resources));
        Bindings = InfrastructureModelCollections.NormalizeReferenceValues(
            bindings,
            static binding => binding.Id.Value,
            "Infrastructure binding identities cannot repeat.",
            nameof(bindings));
        ReadinessDependencies = InfrastructureModelCollections.NormalizeReferenceValues(
            readinessDependencies,
            static dependency => dependency.Id.Value,
            "Infrastructure readiness-dependency identities cannot repeat.",
            nameof(readinessDependencies));

        if (Workloads.IsDefaultOrEmpty && Resources.IsDefaultOrEmpty)
            throw new ArgumentException("An infrastructure definition requires at least one workload or resource node.");

        HashSet<InfrastructureNodeId> nodes = [];
        foreach (var workload in Workloads)
        {
            if (!nodes.Add(workload.Id))
                throw new ArgumentException($"Infrastructure node identity '{workload.Id.Value}' is duplicated.");
        }
        foreach (var resource in Resources)
        {
            if (!nodes.Add(resource.Id))
                throw new ArgumentException($"Infrastructure node identity '{resource.Id.Value}' is duplicated across node kinds.");
        }

        HashSet<InfrastructureRequirementId> requirementIds = [];
        foreach (var requirement in Workloads.SelectMany(static workload => workload.Requirements)
                     .Concat(Resources.SelectMany(static resource => resource.Requirements)))
        {
            if (!requirementIds.Add(requirement.Id))
            {
                throw new ArgumentException(
                    $"Infrastructure requirement identity '{requirement.Id.Value}' is duplicated across nodes.");
            }
        }

        HashSet<(InfrastructureNodeId Source, InfrastructureNodeId Target, InfrastructureBindingContractId Contract)>
            bindingSlots = [];
        foreach (var binding in Bindings)
        {
            if (!nodes.Contains(binding.Source) || !nodes.Contains(binding.Target))
            {
                throw new ArgumentException(
                    $"Infrastructure binding '{binding.Id.Value}' references an undeclared source or target node.",
                    nameof(bindings));
            }
            if (!bindingSlots.Add((binding.Source, binding.Target, binding.Contract)))
            {
                throw new ArgumentException(
                    $"Infrastructure binding '{binding.Id.Value}' duplicates an existing source, target, and contract.",
                    nameof(bindings));
            }
        }

        HashSet<(InfrastructureNodeId Subject, InfrastructureNodeId Dependency)> readinessSlots = [];
        foreach (var dependency in ReadinessDependencies)
        {
            if (!nodes.Contains(dependency.Subject) || !nodes.Contains(dependency.Dependency))
            {
                throw new ArgumentException(
                    $"Infrastructure readiness dependency '{dependency.Id.Value}' references an undeclared subject or dependency node.",
                    nameof(readinessDependencies));
            }
            if (!readinessSlots.Add((dependency.Subject, dependency.Dependency)))
            {
                throw new ArgumentException(
                    $"Infrastructure readiness dependency '{dependency.Id.Value}' duplicates an existing subject and dependency.",
                    nameof(readinessDependencies));
            }
        }

        ValidateAcyclicReadiness(nodes, nameof(readinessDependencies));
    }

    /// <summary>Stable identity shared by semantic revisions.</summary>
    public InfrastructureDefinitionId Id { get; }

    /// <summary>Stable identity of this exact semantic revision.</summary>
    public InfrastructureRevisionId Revision { get; }

    /// <summary>Workload nodes in deterministic identity order.</summary>
    public ImmutableArray<InfrastructureWorkloadDefinition> Workloads { get; }

    /// <summary>Resource nodes in deterministic identity order.</summary>
    public ImmutableArray<InfrastructureResourceDefinition> Resources { get; }

    /// <summary>Bindings in deterministic identity order.</summary>
    public ImmutableArray<InfrastructureBindingDefinition> Bindings { get; }

    /// <summary>Readiness prerequisites in deterministic dependency-identity order.</summary>
    public ImmutableArray<InfrastructureReadinessDependency> ReadinessDependencies { get; }

    /// <summary>Gets the declared semantic kind of one infrastructure node.</summary>
    /// <param name="node">Stable node identity to inspect.</param>
    /// <returns><see cref="InfrastructureNodeKind.Workload"/> or <see cref="InfrastructureNodeKind.Resource"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="node"/> is a default uninitialized value.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="node"/> is absent from this definition.</exception>
    public InfrastructureNodeKind GetNodeKind(InfrastructureNodeId node)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("Infrastructure node lookup requires an identity.", nameof(node));
        if (Workloads.Any(workload => workload.Id == node))
            return InfrastructureNodeKind.Workload;
        if (Resources.Any(resource => resource.Id == node))
            return InfrastructureNodeKind.Resource;
        throw new KeyNotFoundException($"Infrastructure node '{node.Value}' is not declared.");
    }

    /// <summary>Compares normalized infrastructure definitions structurally.</summary>
    /// <param name="other">Other infrastructure definition.</param>
    /// <returns><see langword="true"/> when identity, revision, nodes, requirements, bindings, and readiness dependencies are equal.</returns>
    public bool Equals(InfrastructureDefinition? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Revision == other.Revision
        && Workloads.SequenceEqual(other.Workloads)
        && Resources.SequenceEqual(other.Resources)
        && Bindings.SequenceEqual(other.Bindings)
        && ReadinessDependencies.SequenceEqual(other.ReadinessDependencies);

    /// <summary>Returns a structural hash code for the normalized infrastructure definition.</summary>
    /// <returns>A hash derived from every canonical definition field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Revision);
        foreach (var workload in Workloads)
            hash.Add(workload);
        foreach (var resource in Resources)
            hash.Add(resource);
        foreach (var binding in Bindings)
            hash.Add(binding);
        foreach (var dependency in ReadinessDependencies)
            hash.Add(dependency);
        return hash.ToHashCode();
    }

    void ValidateAcyclicReadiness(IEnumerable<InfrastructureNodeId> nodes, string parameterName)
    {
        var dependencies = ReadinessDependencies
            .GroupBy(static dependency => dependency.Subject)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static dependency => dependency.Dependency).ToImmutableArray());
        HashSet<InfrastructureNodeId> complete = [];
        HashSet<InfrastructureNodeId> active = [];
        foreach (var node in nodes)
            Visit(node);

        void Visit(InfrastructureNodeId node)
        {
            if (complete.Contains(node) || !dependencies.TryGetValue(node, out var required))
                return;
            if (!active.Add(node))
            {
                throw new ArgumentException(
                    $"Infrastructure readiness dependency graph contains a cycle through node '{node.Value}'.",
                    parameterName);
            }
            foreach (var dependency in required)
                Visit(dependency);
            active.Remove(node);
            complete.Add(node);
        }
    }
}

static class InfrastructureModelCollections
{
    internal static ImmutableArray<InfrastructureCapabilityRequirement> NormalizeRequirements(
        ImmutableArray<InfrastructureCapabilityRequirement> requirements,
        string parameterName)
    {
        var normalized = requirements.IsDefault ? [] : requirements;
        if (normalized.Any(static requirement => requirement is null))
            throw new ArgumentException("Infrastructure requirements cannot contain null entries.", parameterName);
        if (normalized.GroupBy(static requirement => requirement.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Infrastructure requirement identities cannot repeat on one node.", parameterName);
        if (normalized.GroupBy(static requirement => requirement.Capability).Any(static group => group.Count() > 1))
            throw new ArgumentException("An infrastructure node cannot demand the same capability more than once.", parameterName);

        return [.. normalized.OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)];
    }

    internal static ImmutableArray<T> NormalizeReferenceValues<T>(
        ImmutableArray<T> values,
        Func<T, string> identity,
        string duplicateMessage,
        string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static value => value is null))
            throw new ArgumentException("Canonical infrastructure collections cannot contain null entries.", parameterName);
        if (normalized.GroupBy(identity, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException(duplicateMessage, parameterName);

        return [.. normalized.OrderBy(identity, StringComparer.Ordinal)];
    }
}
