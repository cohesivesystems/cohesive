using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Diagnostics;

/// <summary>Deterministic identity of one relation requirement gap within a runtime evaluation.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Cohesive.Model.Serialization.SingleValueWrapperJsonConverter))]
public readonly record struct RelationRequirementGapId
{
    /// <summary>Creates a relation requirement gap identifier.</summary>
    /// <param name="value">Stable non-empty identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public RelationRequirementGapId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Portable causal classification of unavailable runtime relation input.</summary>
public enum RelationRequirementGapCause
{
    /// <summary>A required source or invocation input was not provided.</summary>
    InputNotProvided = 0,

    /// <summary>Acquiring a required source, field, or invocation input failed.</summary>
    InputAcquisitionFailed = 1,

    /// <summary>A binding occurrence lacks required stable observation identity.</summary>
    ObservationIdentityMissing = 2,

    /// <summary>The reference-bearing field needed for traversal was not loaded.</summary>
    ReferenceFieldNotLoaded = 3,

    /// <summary>The reference-bearing field is semantically absent.</summary>
    ReferenceValueMissing = 4,

    /// <summary>The reference-bearing field is explicitly null.</summary>
    ReferenceValueNull = 5,

    /// <summary>Required relationship resolution was not attempted.</summary>
    ResolutionNotAttempted = 6,

    /// <summary>Relationship resolution was attempted but failed.</summary>
    ResolutionFailed = 7,

    /// <summary>An authoritative relationship lookup completed without a related observation.</summary>
    RelatedObservationNotFound = 8,

    /// <summary>Candidate related observations were rejected.</summary>
    RelatedObservationRejected = 9,

    /// <summary>A required field was not selected or loaded.</summary>
    RequiredFieldNotLoaded = 10,

    /// <summary>A field whose value contract requires presence is semantically absent.</summary>
    RequiredValueMissing = 11,

    /// <summary>A field whose value contract is non-nullable is explicitly null.</summary>
    RequiredValueNull = 12,

    /// <summary>A required expression capability is unavailable.</summary>
    CapabilityUnavailable = 13,

    /// <summary>Observed related values violate the traversal cardinality contract.</summary>
    CardinalityViolation = 14,

    /// <summary>A later adapter or evaluator reported conversion failure.</summary>
    ConversionFailure = 15
}

/// <summary>Portable action that may resolve or deliberately disposition a relation requirement gap.</summary>
public enum RelationRequirementGapResolutionKind
{
    /// <summary>Provide a missing source, parameter, or other invocation input.</summary>
    ProvideInput = 0,

    /// <summary>Load a field that was omitted from the available observation.</summary>
    LoadField = 1,

    /// <summary>Provide or correct a relationship reference value.</summary>
    ProvideReferenceValue = 2,

    /// <summary>Attempt semantic relationship resolution.</summary>
    ResolveRelationship = 3,

    /// <summary>Retry a failed relationship or input acquisition.</summary>
    RetryAcquisition = 4,

    /// <summary>Provide the related observation addressed by a reference.</summary>
    ProvideRelatedObservation = 5,

    /// <summary>Correct data or resolver behavior that violates cardinality.</summary>
    CorrectCardinality = 6,

    /// <summary>Provide a required non-null value.</summary>
    ProvideValue = 7,

    /// <summary>Provide a required evaluator capability.</summary>
    ProvideCapability = 8,

    /// <summary>Correct or configure the failed conversion.</summary>
    CorrectConversion = 9
}

/// <summary>Typed field evidence attached to a value-oriented relation requirement gap.</summary>
public sealed record RelationRequirementGapValueContext
{
    internal RelationRequirementGapValueContext(
        RelationQueryFieldReference field,
        ExprValueContract? expected,
        RelationQueryFieldEvidenceState observedState,
        ObservationValue? observedValue,
        string? evidenceReference)
    {
        Field = field;
        Expected = expected;
        ObservedState = observedState;
        ObservedValue = observedValue;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Graph-qualified field whose value is unavailable or invalid.</summary>
    public RelationQueryFieldReference Field { get; }

    /// <summary>Compiled value contract, or <see langword="null"/> when unresolved statically.</summary>
    public ExprValueContract? Expected { get; }

    /// <summary>Observed runtime field state.</summary>
    public RelationQueryFieldEvidenceState ObservedState { get; }

    /// <summary>Observed non-null value, or <see langword="null"/> for another field state.</summary>
    public ObservationValue? ObservedValue { get; }

    /// <summary>Opaque evidence or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Typed relationship evidence attached to a traversal-oriented relation requirement gap.</summary>
public sealed record RelationRequirementGapRelationshipContext
{
    internal RelationRequirementGapRelationshipContext(
        RelationshipDefinition definition,
        RelationshipTraversalDirection direction,
        ValueBindingId from,
        ValueBindingId result,
        JoinKind joinKind,
        RelationshipTraversalCardinality expectedCardinality,
        RelationQueryTraversalEvidenceState observedState,
        RelationQueryEvidenceCompleteness completeness,
        int observedCount,
        ObservationValue? referenceValue,
        string? evidenceReference)
    {
        Definition = Guard.RequireNotNull(definition);
        Direction = direction;
        From = from;
        Result = result;
        JoinKind = joinKind;
        ExpectedCardinality = expectedCardinality;
        ObservedState = observedState;
        Completeness = completeness;
        ObservedCount = observedCount;
        ReferenceValue = referenceValue;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Exact canonical relationship definition consumed by static compilation.</summary>
    public RelationshipDefinition Definition { get; }

    /// <summary>Direction in which the relationship is traversed.</summary>
    public RelationshipTraversalDirection Direction { get; }

    /// <summary>Binding from which traversal begins.</summary>
    public ValueBindingId From { get; }

    /// <summary>Binding introduced for related values.</summary>
    public ValueBindingId Result { get; }

    /// <summary>Join semantics applied when related values are absent.</summary>
    public JoinKind JoinKind { get; }

    /// <summary>Compiled maximum number of results per source occurrence.</summary>
    public RelationshipTraversalCardinality ExpectedCardinality { get; }

    /// <summary>Observed traversal state.</summary>
    public RelationQueryTraversalEvidenceState ObservedState { get; }

    /// <summary>Whether completed traversal evidence is authoritative and complete.</summary>
    public RelationQueryEvidenceCompleteness Completeness { get; }

    /// <summary>Number of observed related occurrences.</summary>
    public int ObservedCount { get; }

    /// <summary>Observed relationship reference value, or <see langword="null"/> when unavailable or inapplicable.</summary>
    public ObservationValue? ReferenceValue { get; }

    /// <summary>Opaque attempt, rejection, or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>
/// Structured runtime requirement gap between a compiled semantic input contract and available evidence.
/// </summary>
public sealed record RelationRequirementGap
{
    internal RelationRequirementGap(
        RelationRequirementGapId id,
        RelationQueryEvaluationId evaluation,
        RelationQueryObservationOccurrence? occurrence,
        RelationRequirementGapCause cause,
        RelationQueryRequirementInput input,
        ImmutableArray<RelationQueryDependencyImpact> impacts,
        ImmutableArray<RelationQueryInputId> blockedInputs,
        ImmutableArray<RelationQueryFieldReference> requiredFields,
        RelationRequirementGapValueContext? valueContext,
        RelationRequirementGapRelationshipContext? relationshipContext,
        string? evidenceReference,
        ImmutableArray<RelationRequirementGapResolutionKind> suggestedResolutions,
        RelationQueryCompilationProvenance provenance,
        RelationQueryCompilationDemand demand)
    {
        Id = id;
        Evaluation = evaluation;
        Occurrence = occurrence;
        Cause = cause;
        Input = Guard.RequireNotNull(input);
        Impacts = impacts;
        BlockedInputs = blockedInputs;
        RequiredFields = requiredFields;
        ValueContext = valueContext;
        RelationshipContext = relationshipContext;
        EvidenceReference = evidenceReference;
        SuggestedResolutions = suggestedResolutions;
        Provenance = Guard.RequireNotNull(provenance);
        Demand = Guard.RequireNotNull(demand);
    }

    /// <summary>Deterministic identity within the evaluation.</summary>
    public RelationRequirementGapId Id { get; }

    /// <summary>Evaluation in which the gap was observed.</summary>
    public RelationQueryEvaluationId Evaluation { get; }

    /// <summary>Affected binding occurrence, or <see langword="null"/> for evaluation-wide input.</summary>
    public RelationQueryObservationOccurrence? Occurrence { get; }

    /// <summary>Portable causal classification.</summary>
    public RelationRequirementGapCause Cause { get; }

    /// <summary>Canonical compiled input at the causal boundary.</summary>
    public RelationQueryRequirementInput Input { get; }

    /// <summary>Demanded-output impacts copied from the canonical dependency manifest.</summary>
    public ImmutableArray<RelationQueryDependencyImpact> Impacts { get; }

    /// <summary>Downstream compiled inputs suppressed because this causal input is unavailable.</summary>
    public ImmutableArray<RelationQueryInputId> BlockedInputs { get; }

    /// <summary>Related fields required after the causal input is resolved.</summary>
    public ImmutableArray<RelationQueryFieldReference> RequiredFields { get; }

    /// <summary>Typed field/value context, or <see langword="null"/> for a non-field gap.</summary>
    public RelationRequirementGapValueContext? ValueContext { get; }

    /// <summary>Typed relationship context, or <see langword="null"/> for a non-traversal gap.</summary>
    public RelationRequirementGapRelationshipContext? RelationshipContext { get; }

    /// <summary>Opaque evidence, attempt, or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }

    /// <summary>Portable resolution suggestions in deterministic enum order.</summary>
    public ImmutableArray<RelationRequirementGapResolutionKind> SuggestedResolutions { get; }

    /// <summary>Exact semantic snapshots and compiler profile that produced the input contract.</summary>
    public RelationQueryCompilationProvenance Provenance { get; }

    /// <summary>Effective output demand whose requirements produced the gap.</summary>
    public RelationQueryCompilationDemand Demand { get; }
}

/// <summary>Structured runtime diagnostic projected from invalid evidence, policy, or an unresolved requirement gap.</summary>
public sealed record RelationRuntimeDiagnostic
{
    internal RelationRuntimeDiagnostic(
        string code,
        DiagnosticSeverity severity,
        string message,
        RelationQueryEvaluationId evaluation,
        RelationQueryInputId? input = null,
        RelationQueryOccurrenceId? occurrence = null,
        RelationRequirementGapId? gap = null,
        RelationQueryOutputReference? output = null,
        string? evidenceReference = null)
    {
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        Severity = severity;
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        Evaluation = evaluation;
        Input = input;
        Occurrence = occurrence;
        Gap = gap;
        Output = output;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Stable machine-readable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Diagnostic severity selected by validation or reporting policy.</summary>
    public DiagnosticSeverity Severity { get; }

    /// <summary>
    /// Human-readable message. Provider payloads remain behind <see cref="EvidenceReference"/>, and observed
    /// field or relationship-reference values are not interpolated into the message.
    /// </summary>
    public string Message { get; }

    /// <summary>Evaluation to which the diagnostic belongs.</summary>
    public RelationQueryEvaluationId Evaluation { get; }

    /// <summary>Affected compiled input, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Affected binding occurrence, or <see langword="null"/>.</summary>
    public RelationQueryOccurrenceId? Occurrence { get; }

    /// <summary>Source requirement gap, or <see langword="null"/> for evidence or policy validation.</summary>
    public RelationRequirementGapId? Gap { get; }

    /// <summary>Affected demanded output, or <see langword="null"/>.</summary>
    public RelationQueryOutputReference? Output { get; }

    /// <summary>Opaque evidence or failure reference, or <see langword="null"/>.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Stable codes emitted by relation runtime evidence and requirement-gap analysis.</summary>
public static class RelationRuntimeDiagnosticCodes
{
    /// <summary>Evidence was produced for a different compiled input contract.</summary>
    public const string PlanMismatch = "REL3001";

    /// <summary>Evidence references an input absent from the compiled input contract.</summary>
    public const string InputUnknown = "REL3002";

    /// <summary>Evidence kind does not match the compiled input kind.</summary>
    public const string InputKindMismatch = "REL3003";

    /// <summary>More than one evidence record describes the same compiled input occurrence.</summary>
    public const string EvidenceDuplicate = "REL3004";

    /// <summary>Evidence contains a contradictory occurrence, binding, or shape association.</summary>
    public const string EvidenceConflict = "REL3005";

    /// <summary>A supplied value contradicts the compiled portable value contract.</summary>
    public const string ValueContractMismatch = "REL3006";

    /// <summary>Policy selected null substitution for an output that does not permit null.</summary>
    public const string NullSubstitutionInvalid = "REL3007";

    /// <summary>Policy selected a default value that does not satisfy the output contract.</summary>
    public const string DefaultSubstitutionInvalid = "REL3008";

    /// <summary>A missing invocation or source input was reported.</summary>
    public const string RequirementGapInputNotProvided = "REL3101";

    /// <summary>An input acquisition failure was reported.</summary>
    public const string RequirementGapInputAcquisitionFailed = "REL3102";

    /// <summary>Missing observation identity was reported.</summary>
    public const string RequirementGapObservationIdentityMissing = "REL3103";

    /// <summary>An unloaded relationship reference field was reported.</summary>
    public const string RequirementGapReferenceFieldNotLoaded = "REL3104";

    /// <summary>A missing relationship reference value was reported.</summary>
    public const string RequirementGapReferenceValueMissing = "REL3105";

    /// <summary>A null relationship reference value was reported.</summary>
    public const string RequirementGapReferenceValueNull = "REL3106";

    /// <summary>An unattempted relationship resolution was reported.</summary>
    public const string RequirementGapResolutionNotAttempted = "REL3107";

    /// <summary>A failed relationship resolution was reported.</summary>
    public const string RequirementGapResolutionFailed = "REL3108";

    /// <summary>An authoritative related-observation miss was reported.</summary>
    public const string RequirementGapRelatedObservationNotFound = "REL3109";

    /// <summary>A rejected related observation was reported.</summary>
    public const string RequirementGapRelatedObservationRejected = "REL3110";

    /// <summary>An unloaded required field was reported.</summary>
    public const string RequirementGapRequiredFieldNotLoaded = "REL3111";

    /// <summary>A semantically missing required value was reported.</summary>
    public const string RequirementGapRequiredValueMissing = "REL3112";

    /// <summary>An invalid null required value was reported.</summary>
    public const string RequirementGapRequiredValueNull = "REL3113";

    /// <summary>An unavailable expression capability was reported.</summary>
    public const string RequirementGapCapabilityUnavailable = "REL3114";

    /// <summary>A traversal cardinality violation was reported.</summary>
    public const string RequirementGapCardinalityViolation = "REL3115";

    /// <summary>A conversion failure was reported.</summary>
    public const string RequirementGapConversionFailure = "REL3116";
}
