using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Realization;

/// <summary>Stable identity of one demand-scoped realization requirement.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryRealizationRequirementId
{
    /// <summary>Creates a realization-requirement identifier.</summary>
    /// <param name="value">Stable non-empty requirement identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryRealizationRequirementId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable requirement identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of an interpretation target.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryTargetId
{
    /// <summary>Creates a target identifier.</summary>
    /// <param name="value">Stable non-empty target identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryTargetId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable target identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of a target capability profile.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryTargetProfileId
{
    /// <summary>Creates a target-profile identifier.</summary>
    /// <param name="value">Stable non-empty profile identity, including a version when behavior can evolve.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryTargetProfileId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable target-profile identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of one target capability assertion.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryTargetCapabilityEvidenceId
{
    /// <summary>Creates a target-capability-evidence identifier.</summary>
    /// <param name="value">Stable non-empty evidence identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryTargetCapabilityEvidenceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable capability-evidence identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of an exact capability-composition rule.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryCompositionRuleId
{
    /// <summary>Creates a composition-rule identifier.</summary>
    /// <param name="value">Stable non-empty rule identity, including a version when behavior can evolve.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryCompositionRuleId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable composition-rule identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of a declared operating boundary.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryOperatingBoundaryId
{
    /// <summary>Creates an operating-boundary identifier.</summary>
    /// <param name="value">Stable non-empty boundary identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryOperatingBoundaryId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable operating-boundary identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of realization compiler policy.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryRealizationPolicyId
{
    /// <summary>Creates a realization-policy identifier.</summary>
    /// <param name="value">Stable non-empty policy identity, including a version when behavior can evolve.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryRealizationPolicyId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable policy identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of one explicit local realization override.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryRealizationOverrideId
{
    /// <summary>Creates a realization-override identifier.</summary>
    /// <param name="value">Stable non-empty override identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryRealizationOverrideId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable override identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Versioned deterministic content identity of a derived realization report.</summary>
public sealed record RelationQueryRealizationFingerprint
{
    /// <summary>Creates a realization fingerprint.</summary>
    /// <param name="algorithm">Stable fingerprint algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Fingerprint value encoded according to <paramref name="algorithm"/>.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="algorithm"/>, <paramref name="canonicalization"/>, or <paramref name="value"/> is empty
    /// or white space.
    /// </exception>
    [JsonConstructor]
    public RelationQueryRealizationFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Stable fingerprint algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Stable canonicalization-profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Raw deterministic fingerprint value.</summary>
    public string Value { get; }
}
