using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Local;

/// <summary>Stable repository-project identity within a local infrastructure interpretation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureLocalProjectId
{
    /// <summary>Creates a local project identity.</summary>
    /// <param name="value">Stable non-empty project identity containing no white-space.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or contains white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalProjectId(string value)
    {
        value = Guard.RequireNotNullOrWhiteSpace(value);
        if (value.Any(char.IsWhiteSpace))
            throw new ArgumentException("A local project identity cannot contain white-space.", nameof(value));
        Value = value;
    }

    /// <summary>Stable project identity.</summary>
    public string Value { get; }

    /// <summary>Returns the stable project identity.</summary>
    /// <returns>The project identity.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of one local environment policy.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureLocalEnvironmentProfileId
{
    /// <summary>Creates a local environment-profile identity.</summary>
    /// <param name="value">Stable identity that changes when environment policy changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalEnvironmentProfileId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw profile identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw profile identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one endpoint within a local service.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureLocalEndpointId
{
    /// <summary>Creates an endpoint identity.</summary>
    /// <param name="value">Stable service-local endpoint identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalEndpointId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw endpoint identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw endpoint identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one local persistent or ephemeral volume.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureLocalVolumeId
{
    /// <summary>Creates a volume identity.</summary>
    /// <param name="value">Stable topology-local volume identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalVolumeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw volume identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw volume identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one generated local configuration file.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureLocalFileId
{
    /// <summary>Creates a generated-file identity.</summary>
    /// <param name="value">Stable topology-local file identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalFileId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw file identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw file identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable application-owned identity of one local harness operation.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureLocalOperationId
{
    /// <summary>Creates an operation identity.</summary>
    /// <param name="value">Stable topology-local operation intent identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureLocalOperationId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw operation identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw operation identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}
