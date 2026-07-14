using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Drafts;

/// <summary>
/// Stable identifier for a portable relation draft across revisions of its content.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationDraftId
{
    /// <summary>Creates a relation draft identifier.</summary>
    /// <param name="value">Raw relation draft identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public RelationDraftId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw relation draft identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Content-derived semantic identifier for one candidate within a relation draft.
/// </summary>
/// <remarks>
/// The value must be produced by
/// <see cref="RelationDraftIdentityConvention.CreateCandidateId(Cohesive.Relations.IR.QueryAssignmentId, Expr)"/>
/// from the containing slot and candidate expression. Scores, rank, and producer telemetry are not
/// identity inputs.
/// </remarks>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationDraftCandidateId
{
    /// <summary>Creates a relation draft candidate identifier.</summary>
    /// <param name="value">Raw candidate identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public RelationDraftCandidateId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw candidate identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
