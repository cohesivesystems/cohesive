using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra;

/// <summary>Stable identity of an infrastructure definition across its semantic revisions.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureDefinitionId
{
    /// <summary>Creates an infrastructure-definition identity.</summary>
    /// <param name="value">Stable producer-assigned identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureDefinitionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw infrastructure-definition identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw infrastructure-definition identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one semantic revision of an infrastructure definition.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureRevisionId
{
    /// <summary>Creates an infrastructure-revision identity.</summary>
    /// <param name="value">Stable producer-assigned revision identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureRevisionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw infrastructure-revision identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw infrastructure-revision identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one workload or resource node in an infrastructure definition.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureNodeId
{
    /// <summary>Creates an infrastructure-node identity.</summary>
    /// <param name="value">Stable definition-local node identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureNodeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw infrastructure-node identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw infrastructure-node identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one directed infrastructure binding.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureBindingId
{
    /// <summary>Creates an infrastructure-binding identity.</summary>
    /// <param name="value">Stable definition-local binding identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureBindingId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw infrastructure-binding identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw infrastructure-binding identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one node-owned infrastructure capability requirement.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureRequirementId
{
    /// <summary>Creates an infrastructure-requirement identity.</summary>
    /// <param name="value">Stable definition-local requirement identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureRequirementId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw infrastructure-requirement identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw infrastructure-requirement identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable provider-neutral identity of an infrastructure capability.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureCapabilityId
{
    /// <summary>Creates an infrastructure-capability identity.</summary>
    /// <param name="value">Stable provider-neutral capability identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw infrastructure-capability identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw infrastructure-capability identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable provider-neutral identity of a contract carried by an infrastructure binding.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureBindingContractId
{
    /// <summary>Creates an infrastructure binding-contract identity.</summary>
    /// <param name="value">Stable provider-neutral binding-contract identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureBindingContractId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw infrastructure binding-contract identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw infrastructure binding-contract identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}
