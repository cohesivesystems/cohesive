using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Stable identity of an infrastructure interpretation target.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureTargetId
{
    /// <summary>Creates an interpretation-target identity.</summary>
    /// <param name="value">Stable provider, platform, or backend target identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureTargetId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable target identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw target identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of an infrastructure target-facility manifest.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureTargetFacilityManifestId
{
    /// <summary>Creates a target-facility manifest identity.</summary>
    /// <param name="value">Stable identity that changes when facility-selection authority changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureTargetFacilityManifestId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable target-facility manifest identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw target-facility manifest identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of an infrastructure target-deployment manifest.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureTargetDeploymentManifestId
{
    /// <summary>Creates a target-deployment manifest identity.</summary>
    /// <param name="value">Stable identity that changes when physical deployment declarations change.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureTargetDeploymentManifestId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable target-deployment manifest identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw target-deployment manifest identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable target-local identity of one selectable infrastructure facility.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureTargetFacilityId
{
    /// <summary>Creates a target-facility identity.</summary>
    /// <param name="value">Stable target-local facility identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureTargetFacilityId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable target-facility identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw target-facility identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of an infrastructure capability profile.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureCapabilityProfileId
{
    /// <summary>Creates a capability-profile identity.</summary>
    /// <param name="value">Stable identity that changes when the supplied capability set changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityProfileId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable capability-profile identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw profile identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one coherent configured target variant.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureCapabilityVariantId
{
    /// <summary>Creates a coherent-variant identity.</summary>
    /// <param name="value">Stable profile-local variant identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityVariantId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable coherent-variant identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw variant identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one attributable infrastructure capability assertion.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureCapabilityEvidenceId
{
    /// <summary>Creates a capability-evidence identity.</summary>
    /// <param name="value">Stable variant-local evidence identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityEvidenceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable evidence identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw evidence identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one versioned infrastructure capability-composition rule.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureCapabilityRuleId
{
    /// <summary>Creates a capability-rule identity.</summary>
    /// <param name="value">Stable variant-local rule identity including its semantic version.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityRuleId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable rule identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw rule identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one explicit infrastructure operating boundary.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureOperatingBoundaryId
{
    /// <summary>Creates an operating-boundary identity.</summary>
    /// <param name="value">Stable variant-local boundary identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureOperatingBoundaryId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable boundary identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw boundary identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of an infrastructure boundary-acceptance policy.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct InfrastructureBoundaryAcceptancePolicyId
{
    /// <summary>Creates a boundary-acceptance policy identity.</summary>
    /// <param name="value">Stable identity that changes when the governed acceptance intent changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureBoundaryAcceptancePolicyId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable policy identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw policy identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}
