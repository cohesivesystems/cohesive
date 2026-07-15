using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Physical;

/// <summary>Stable identity of one physical source instance.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQuerySourceInstanceId
{
    /// <summary>Creates a source-instance identity.</summary>
    /// <param name="value">Non-empty stable identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQuerySourceInstanceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw source-instance identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of one execution or consistency domain.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryExecutionDomainId
{
    /// <summary>Creates an execution-domain identity.</summary>
    /// <param name="value">Non-empty stable identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryExecutionDomainId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw execution-domain identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of one plan-scoped source-placement binding.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQuerySourcePlacementBindingId
{
    /// <summary>Creates a placement-binding identity.</summary>
    /// <param name="value">Non-empty stable identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQuerySourcePlacementBindingId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw placement-binding identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of one compiled physical stage.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryPhysicalStageId
{
    /// <summary>Creates a physical-stage identity.</summary>
    /// <param name="value">Non-empty stable identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryPhysicalStageId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw physical-stage identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of one physical lowering rule.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryPhysicalLoweringRuleId
{
    /// <summary>Creates a physical-lowering identity.</summary>
    /// <param name="value">Non-empty stable identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryPhysicalLoweringRuleId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw physical-lowering identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable, versioned identity of one physical-planning policy.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryPhysicalPlanningPolicyId
{
    /// <summary>Creates a physical-planning policy identity.</summary>
    /// <param name="value">Non-empty stable identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryPhysicalPlanningPolicyId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw physical-planning policy identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable identity of one attributable physical-planning decision.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryPhysicalPlanningDecisionId
{
    /// <summary>Creates a physical-planning decision identity.</summary>
    /// <param name="value">Non-empty stable identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationQueryPhysicalPlanningDecisionId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw physical-planning decision identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Versioned cryptographic identity of a source-placement artifact.</summary>
public sealed record RelationQuerySourcePlacementFingerprint
{
    /// <summary>Creates a source-placement fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public RelationQuerySourcePlacementFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Encoded hash value.</summary>
    public string Value { get; }
}

/// <summary>Versioned cryptographic identity of a compiled physical plan.</summary>
public sealed record RelationQueryPhysicalPlanFingerprint
{
    /// <summary>Creates a physical-plan fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identifier.</param>
    /// <param name="canonicalization">Canonicalization profile identifier.</param>
    /// <param name="value">Lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public RelationQueryPhysicalPlanFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identifier.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identifier.</summary>
    public string Canonicalization { get; }

    /// <summary>Encoded hash value.</summary>
    public string Value { get; }
}
