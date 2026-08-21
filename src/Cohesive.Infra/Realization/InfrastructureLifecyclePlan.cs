using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Stable target-native identity of one selected physical resource.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructurePhysicalResourceId
{
    /// <summary>Creates a physical-resource identity.</summary>
    /// <param name="value">Stable provider or imported resource identity retained by the realization.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructurePhysicalResourceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable physical-resource identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw physical-resource identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of the backend state scope that owns one physical resource lifecycle.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureLifecycleAuthorityId
{
    /// <summary>Creates a lifecycle-authority identity.</summary>
    /// <param name="value">Stable backend stack, workspace, state, environment, or external-owner identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLifecycleAuthorityId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable lifecycle-authority identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw lifecycle-authority identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Whether a lifecycle interpreter manages or only references one physical resource.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum InfrastructureLifecycleDisposition
{
    /// <summary>The interpreter is the unique lifecycle manager for this physical resource.</summary>
    Managed = 0,

    /// <summary>The interpreter consumes the resource without managing its lifecycle.</summary>
    Referenced = 1
}

/// <summary>One lifecycle interpreter's relationship to a selected physical resource.</summary>
public sealed record InfrastructureResourceLifecycleBinding
{
    /// <summary>Creates a resource lifecycle binding.</summary>
    /// <param name="resource">Canonical logical resource node being realized.</param>
    /// <param name="physicalResource">Exact selected physical resource identity.</param>
    /// <param name="interpreter">Lifecycle interpreter participating in this binding.</param>
    /// <param name="authority">Backend state scope that owns the physical resource lifecycle.</param>
    /// <param name="disposition">Whether the interpreter manages or references the resource.</param>
    /// <exception cref="ArgumentException">Any identity is default or empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureResourceLifecycleBinding(
        InfrastructureNodeId resource,
        InfrastructurePhysicalResourceId physicalResource,
        InfrastructureTargetId interpreter,
        InfrastructureLifecycleAuthorityId authority,
        InfrastructureLifecycleDisposition disposition)
    {
        if (string.IsNullOrWhiteSpace(resource.Value))
            throw new ArgumentException("A lifecycle binding requires a logical resource identity.", nameof(resource));
        if (string.IsNullOrWhiteSpace(physicalResource.Value))
            throw new ArgumentException("A lifecycle binding requires a physical resource identity.", nameof(physicalResource));
        if (string.IsNullOrWhiteSpace(interpreter.Value))
            throw new ArgumentException("A lifecycle binding requires an interpreter identity.", nameof(interpreter));
        if (string.IsNullOrWhiteSpace(authority.Value))
            throw new ArgumentException("A lifecycle binding requires an authority identity.", nameof(authority));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported lifecycle disposition.");

        Resource = resource;
        PhysicalResource = physicalResource;
        Interpreter = interpreter;
        Authority = authority;
        Disposition = disposition;
    }

    /// <summary>Canonical logical resource node being realized.</summary>
    public InfrastructureNodeId Resource { get; }

    /// <summary>Exact selected physical resource identity.</summary>
    public InfrastructurePhysicalResourceId PhysicalResource { get; }

    /// <summary>Lifecycle interpreter participating in this binding.</summary>
    public InfrastructureTargetId Interpreter { get; }

    /// <summary>Backend state scope that owns the physical resource lifecycle.</summary>
    public InfrastructureLifecycleAuthorityId Authority { get; }

    /// <summary>Whether the interpreter manages or references the resource.</summary>
    public InfrastructureLifecycleDisposition Disposition { get; }
}

/// <summary>Validated lifecycle ownership partition for one exact infrastructure definition.</summary>
/// <remarks>
/// Every non-external logical resource has exactly one managing interpreter. Other interpreters can reference the
/// same physical resource and authority, but cannot establish a second management authority.
/// </remarks>
public sealed record InfrastructureLifecyclePlan
{
    /// <summary>Creates and validates a lifecycle plan.</summary>
    /// <param name="definition">Exact fingerprinted infrastructure definition.</param>
    /// <param name="bindings">Lifecycle bindings for every logical resource.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A binding is null or duplicated; a binding references a workload or unknown node; a logical resource maps to
    /// inconsistent physical identities or authorities; a non-external resource lacks exactly one manager; or an
    /// external resource is managed by this realization.
    /// </exception>
    [JsonConstructor]
    public InfrastructureLifecyclePlan(
        InfrastructureDefinitionDocument definition,
        ImmutableArray<InfrastructureResourceLifecycleBinding> bindings = default)
    {
        Definition = Guard.RequireNotNull(definition);
        Bindings = NormalizeBindings(bindings);
        ValidateOwnership();
    }

    /// <summary>Exact fingerprinted infrastructure definition.</summary>
    public InfrastructureDefinitionDocument Definition { get; }

    /// <summary>Lifecycle bindings in logical-resource and interpreter order.</summary>
    public ImmutableArray<InfrastructureResourceLifecycleBinding> Bindings { get; }

    /// <summary>Compares lifecycle plans structurally.</summary>
    /// <param name="other">Other lifecycle plan.</param>
    /// <returns><see langword="true"/> when the definition and every lifecycle binding are equal.</returns>
    public bool Equals(InfrastructureLifecyclePlan? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Definition == other.Definition
        && Bindings.SequenceEqual(other.Bindings);

    /// <summary>Returns a structural hash code for this lifecycle plan.</summary>
    /// <returns>A hash code derived from the definition and normalized bindings.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Definition);
        foreach (var binding in Bindings)
            hash.Add(binding);
        return hash.ToHashCode();
    }

    void ValidateOwnership()
    {
        var resources = Definition.Definition.Resources.ToDictionary(static resource => resource.Id);
        foreach (var binding in Bindings)
        {
            if (!resources.ContainsKey(binding.Resource))
            {
                throw new ArgumentException(
                    $"Lifecycle binding references unknown or non-resource node '{binding.Resource.Value}'.",
                    nameof(Bindings));
            }
        }

        foreach (var resource in Definition.Definition.Resources)
        {
            var matches = Bindings.Where(binding => binding.Resource == resource.Id).ToArray();
            if (matches.Length == 0)
                throw new ArgumentException($"Resource '{resource.Id.Value}' has no lifecycle binding.", nameof(Bindings));

            var physical = matches[0].PhysicalResource;
            var authority = matches[0].Authority;
            if (matches.Any(binding => binding.PhysicalResource != physical || binding.Authority != authority))
            {
                throw new ArgumentException(
                    $"Resource '{resource.Id.Value}' maps to inconsistent physical identities or lifecycle authorities.",
                    nameof(Bindings));
            }

            var managers = matches.Count(static binding => binding.Disposition == InfrastructureLifecycleDisposition.Managed);
            if (resource.Lifecycle == InfrastructureResourceLifecycle.External && managers != 0)
            {
                throw new ArgumentException(
                    $"External resource '{resource.Id.Value}' cannot be managed by this realization.",
                    nameof(Bindings));
            }
            if (resource.Lifecycle != InfrastructureResourceLifecycle.External && managers != 1)
            {
                throw new ArgumentException(
                    $"Resource '{resource.Id.Value}' requires exactly one lifecycle manager; found {managers}.",
                    nameof(Bindings));
            }
        }

        foreach (var physicalGroup in Bindings.GroupBy(static binding => binding.PhysicalResource))
        {
            var authorities = physicalGroup
                .Select(static binding => binding.Authority)
                .Distinct()
                .ToArray();
            if (authorities.Length > 1)
            {
                throw new ArgumentException(
                    $"Physical resource '{physicalGroup.Key.Value}' maps to several lifecycle authorities.",
                    nameof(Bindings));
            }

            var hasExternalAlias = physicalGroup.Any(binding =>
                resources[binding.Resource].Lifecycle == InfrastructureResourceLifecycle.External);
            var hasManager = physicalGroup.Any(static binding =>
                binding.Disposition == InfrastructureLifecycleDisposition.Managed);
            if (hasExternalAlias && hasManager)
            {
                throw new ArgumentException(
                    $"Physical resource '{physicalGroup.Key.Value}' cannot be external through one logical resource and managed through another.",
                    nameof(Bindings));
            }

            var managementAuthorities = physicalGroup
                .Where(static binding => binding.Disposition == InfrastructureLifecycleDisposition.Managed)
                .Select(static binding => (binding.Interpreter, binding.Authority))
                .Distinct()
                .ToArray();
            if (managementAuthorities.Length > 1)
            {
                throw new ArgumentException(
                    $"Physical resource '{physicalGroup.Key.Value}' has several lifecycle management authorities.",
                    nameof(Bindings));
            }
        }
    }

    static ImmutableArray<InfrastructureResourceLifecycleBinding> NormalizeBindings(
        ImmutableArray<InfrastructureResourceLifecycleBinding> bindings)
    {
        if (bindings.IsDefaultOrEmpty)
            return [];
        if (bindings.Any(static binding => binding is null))
            throw new ArgumentException("Infrastructure lifecycle bindings cannot contain null.", nameof(bindings));

        var ordered = bindings.Sort(static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.Resource.Value, right.Resource.Value);
            if (comparison != 0)
                return comparison;
            comparison = StringComparer.Ordinal.Compare(left.Interpreter.Value, right.Interpreter.Value);
            if (comparison != 0)
                return comparison;
            return left.Disposition.CompareTo(right.Disposition);
        });
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Resource == ordered[index].Resource
                && ordered[index - 1].Interpreter == ordered[index].Interpreter)
            {
                throw new ArgumentException(
                    $"Interpreter '{ordered[index].Interpreter.Value}' participates more than once in resource '{ordered[index].Resource.Value}'.",
                    nameof(bindings));
            }
        }
        return ordered;
    }
}
