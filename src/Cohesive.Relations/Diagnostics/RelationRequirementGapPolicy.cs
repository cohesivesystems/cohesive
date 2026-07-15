using System.Collections.Immutable;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Diagnostics;

/// <summary>Stable identity of a relation requirement gap policy.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Cohesive.Model.Serialization.SingleValueWrapperJsonConverter))]
public readonly record struct RelationRequirementGapPolicyId
{
    /// <summary>Creates a policy identifier.</summary>
    /// <param name="value">Stable non-empty policy identity, including version when behavior may evolve.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationRequirementGapPolicyId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable policy identity.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Origin and precedence category of a relation requirement gap policy decision.</summary>
public enum RelationRequirementGapPolicySource
{
    /// <summary>The framework convention supplied the decision.</summary>
    Convention = 0,

    /// <summary>An application or caller supplied the decision explicitly.</summary>
    Explicit = 1
}

/// <summary>Permitted execution disposition for one affected output.</summary>
public enum RelationRequirementGapDispositionKind
{
    /// <summary>Leave the affected output unresolved.</summary>
    Unresolved = 0,

    /// <summary>Suppress the explicitly identified affected output scope.</summary>
    SuppressOutput = 1,

    /// <summary>Substitute an explicit null when the output contract permits null.</summary>
    SubstituteNull = 2,

    /// <summary>Substitute an explicitly supplied concrete non-null, non-missing semantic default value.</summary>
    SubstituteDefault = 3
}

/// <summary>Whether a gap impact is projected as a diagnostic.</summary>
public enum RelationRequirementGapReportingKind
{
    /// <summary>Project a diagnostic with the selected severity.</summary>
    Report = 0,

    /// <summary>Do not project a diagnostic for this impact.</summary>
    Suppress = 1
}

/// <summary>Execution disposition selected for one gap impact.</summary>
public sealed record RelationRequirementGapDisposition
{
    /// <summary>Shared unresolved disposition.</summary>
    public static RelationRequirementGapDisposition Unresolved { get; } = new(RelationRequirementGapDispositionKind.Unresolved);

    /// <summary>Shared output-suppression disposition.</summary>
    public static RelationRequirementGapDisposition SuppressOutput { get; } = new(RelationRequirementGapDispositionKind.SuppressOutput);

    /// <summary>Shared explicit-null substitution disposition.</summary>
    public static RelationRequirementGapDisposition SubstituteNull { get; } = new(RelationRequirementGapDispositionKind.SubstituteNull);

    /// <summary>Creates a disposition.</summary>
    /// <param name="kind">Disposition kind.</param>
    /// <param name="substitution">
    /// Concrete non-null, non-missing value for <see cref="RelationRequirementGapDispositionKind.SubstituteDefault"/>.
    /// Use <see cref="SubstituteNull"/> for explicit null substitution.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A default substitution omits <paramref name="substitution"/> or supplies null or undefined, or another
    /// disposition supplies a value.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    public RelationRequirementGapDisposition(
        RelationRequirementGapDispositionKind kind,
        ObservationValue? substitution = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported relation requirement gap disposition.");
        }

        if (kind == RelationRequirementGapDispositionKind.SubstituteDefault && substitution is null)
        {
            throw new ArgumentException("Default substitution requires an explicit semantic value.", nameof(substitution));
        }

        if (kind == RelationRequirementGapDispositionKind.SubstituteDefault
            && substitution is { Kind: ObservationValueKind.Null or ObservationValueKind.Undefined })
        {
            throw new ArgumentException(
                "Default substitution requires a concrete non-null, non-missing value; use SubstituteNull for explicit null.",
                nameof(substitution));
        }

        if (kind != RelationRequirementGapDispositionKind.SubstituteDefault && substitution is not null)
        {
            throw new ArgumentException("Only default substitution can carry a semantic value.", nameof(substitution));
        }

        Kind = kind;
        Substitution = substitution;
    }

    /// <summary>Disposition kind.</summary>
    public RelationRequirementGapDispositionKind Kind { get; }

    /// <summary>Concrete non-null, non-missing default value, or <see langword="null"/> for another disposition.</summary>
    public ObservationValue? Substitution { get; }

    /// <summary>Creates an explicit semantic-default substitution.</summary>
    /// <param name="value">
    /// Concrete non-null, non-missing semantic default. Use <see cref="SubstituteNull"/> for explicit null.
    /// </param>
    /// <returns>A default-substitution disposition carrying <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is <see cref="ObservationValue.Null"/> or
    /// <see cref="ObservationValue.Undefined"/>.
    /// </exception>
    public static RelationRequirementGapDisposition UseDefault(ObservationValue value) =>
        new(RelationRequirementGapDispositionKind.SubstituteDefault, value);
}

/// <summary>Policy choice returned for one gap impact.</summary>
public sealed record RelationRequirementGapPolicyChoice
{
    /// <summary>Creates a policy choice.</summary>
    /// <param name="disposition">Selected output disposition.</param>
    /// <param name="reporting">Whether to project a diagnostic.</param>
    /// <param name="severity">Severity used when <paramref name="reporting"/> is report.</param>
    /// <exception cref="ArgumentNullException"><paramref name="disposition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="reporting"/> or <paramref name="severity"/> is unsupported.
    /// </exception>
    public RelationRequirementGapPolicyChoice(
        RelationRequirementGapDisposition disposition,
        RelationRequirementGapReportingKind reporting,
        DiagnosticSeverity severity = DiagnosticSeverity.Error)
    {
        Disposition = Guard.RequireNotNull(disposition);
        if (!Enum.IsDefined(reporting))
        {
            throw new ArgumentOutOfRangeException(nameof(reporting), reporting, "Unsupported relation requirement gap reporting choice.");
        }

        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        }

        Reporting = reporting;
        Severity = severity;
    }

    /// <summary>Selected output disposition.</summary>
    public RelationRequirementGapDisposition Disposition { get; }

    /// <summary>Whether to project a diagnostic.</summary>
    public RelationRequirementGapReportingKind Reporting { get; }

    /// <summary>Severity used when <see cref="Reporting"/> is report.</summary>
    public DiagnosticSeverity Severity { get; }
}

/// <summary>Selects an explicit disposition and reporting choice for each relation requirement gap impact.</summary>
public interface IRelationRequirementGapPolicy
{
    /// <summary>Stable policy identity.</summary>
    RelationRequirementGapPolicyId Id { get; }

    /// <summary>Whether decisions are convention-derived or explicitly configured.</summary>
    RelationRequirementGapPolicySource Source { get; }

    /// <summary>Selects behavior for one demanded-output impact of a gap.</summary>
    /// <param name="gap">Structured causal gap.</param>
    /// <param name="impact">Demanded-output impact being decided.</param>
    /// <returns>Explicit disposition and reporting choice.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="gap"/> or <paramref name="impact"/> is <see langword="null"/>.
    /// </exception>
    RelationRequirementGapPolicyChoice Decide(RelationRequirementGap gap, RelationQueryDependencyImpact impact);
}

/// <summary>Delegate-backed relation requirement gap policy with explicit provenance.</summary>
public sealed class RelationRequirementGapPolicy : IRelationRequirementGapPolicy
{
    readonly Func<RelationRequirementGap, RelationQueryDependencyImpact, RelationRequirementGapPolicyChoice> decide;

    /// <summary>
    /// Framework convention that reports unresolved required impacts and retains optional impacts without reporting.
    /// </summary>
    public static RelationRequirementGapPolicy Conventional { get; } = new(
        new("cohesive.relations.requirement-gaps/conventional-v1"),
        RelationRequirementGapPolicySource.Convention,
        static (_, impact) => new(
            RelationRequirementGapDisposition.Unresolved,
            impact.Requirement == IR.QueryInputRequirement.Required
                ? RelationRequirementGapReportingKind.Report
                : RelationRequirementGapReportingKind.Suppress));

    /// <summary>Creates a delegate-backed policy.</summary>
    /// <param name="id">Stable policy identity including version.</param>
    /// <param name="source">Policy origin and precedence category.</param>
    /// <param name="decide">Decision function invoked once for every gap impact.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is default.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="decide"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is unsupported.</exception>
    public RelationRequirementGapPolicy(
        RelationRequirementGapPolicyId id,
        RelationRequirementGapPolicySource source,
        Func<RelationRequirementGap, RelationQueryDependencyImpact, RelationRequirementGapPolicyChoice> decide)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("A relation requirement gap policy requires an identity.", nameof(id));
        }

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unsupported policy source.");
        }

        Id = id;
        Source = source;
        this.decide = Guard.RequireNotNull(decide);
    }

    /// <inheritdoc />
    public RelationRequirementGapPolicyId Id { get; }

    /// <inheritdoc />
    public RelationRequirementGapPolicySource Source { get; }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">
    /// <paramref name="gap"/> or <paramref name="impact"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">The configured delegate returns <see langword="null"/>.</exception>
    public RelationRequirementGapPolicyChoice Decide(RelationRequirementGap gap, RelationQueryDependencyImpact impact)
    {
        ArgumentNullException.ThrowIfNull(gap);
        ArgumentNullException.ThrowIfNull(impact);
        return decide(gap, impact)
            ?? throw new InvalidOperationException($"Relation requirement gap policy '{Id.Value}' returned no choice.");
    }
}

/// <summary>Normalized decision for one demanded-output impact of a relation requirement gap.</summary>
public sealed record RelationRequirementGapDecision
{
    internal RelationRequirementGapDecision(
        RelationRequirementGapId gap,
        RelationQueryDependencyImpact impact,
        RelationRequirementGapDisposition disposition,
        RelationRequirementGapReportingKind reporting,
        DiagnosticSeverity severity,
        RelationRequirementGapPolicyId policy,
        RelationRequirementGapPolicySource source)
    {
        Gap = gap;
        Impact = Guard.RequireNotNull(impact);
        Disposition = Guard.RequireNotNull(disposition);
        Reporting = reporting;
        Severity = severity;
        Policy = policy;
        Source = source;
    }

    /// <summary>Gap receiving the decision.</summary>
    public RelationRequirementGapId Gap { get; }

    /// <summary>Demanded-output impact receiving the decision.</summary>
    public RelationQueryDependencyImpact Impact { get; }

    /// <summary>Selected execution disposition.</summary>
    public RelationRequirementGapDisposition Disposition { get; }

    /// <summary>Selected diagnostic reporting behavior.</summary>
    public RelationRequirementGapReportingKind Reporting { get; }

    /// <summary>Selected diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>Policy that selected the decision.</summary>
    public RelationRequirementGapPolicyId Policy { get; }

    /// <summary>Whether the decision was conventional or explicit.</summary>
    public RelationRequirementGapPolicySource Source { get; }
}

/// <summary>Immutable result of runtime evidence validation and relation requirement gap analysis.</summary>
public sealed class RelationRequirementGapAnalysisResult
{
    internal RelationRequirementGapAnalysisResult(
        bool isEvidenceValid,
        bool isConclusive,
        ImmutableArray<RelationRequirementGap> gaps,
        ImmutableArray<RelationRequirementGapDecision> decisions,
        ImmutableArray<RelationRuntimeDiagnostic> diagnostics)
    {
        IsEvidenceValid = isEvidenceValid;
        IsConclusive = isConclusive;
        Gaps = gaps;
        Decisions = decisions;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Whether the evidence is consistently attributed to the compiled input contract and can be interpreted.
    /// </summary>
    /// <remarks>
    /// Valid partial evidence may still have <see cref="IsConclusive"/> set to <see langword="false"/>.
    /// Invalid evidence must not be executed because its input identities or occurrence topology are untrustworthy.
    /// </remarks>
    public bool IsEvidenceValid { get; }

    /// <summary>
    /// Whether the evidence snapshot was complete and valid enough for absence to be interpreted conclusively.
    /// </summary>
    public bool IsConclusive { get; }

    /// <summary>Structured causal gaps in deterministic order.</summary>
    public ImmutableArray<RelationRequirementGap> Gaps { get; }

    /// <summary>Per-impact policy decisions in deterministic gap/output/effect order.</summary>
    public ImmutableArray<RelationRequirementGapDecision> Decisions { get; }

    /// <summary>Evidence, policy, and reported requirement-gap diagnostics in deterministic order.</summary>
    public ImmutableArray<RelationRuntimeDiagnostic> Diagnostics { get; }

    /// <summary>Whether any diagnostic has error severity.</summary>
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
}
