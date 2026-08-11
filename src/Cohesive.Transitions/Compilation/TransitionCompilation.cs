using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.IR;

namespace Cohesive.Transitions.Compilation;

/// <summary>Whether a statically derived requirement applies on some or every feasible activation path.</summary>
public enum TransitionRequirementStrength
{
    /// <summary>The requirement applies on at least one feasible activation path.</summary>
    May = 0,

    /// <summary>The requirement applies on every feasible activation path in the queried domain.</summary>
    Must = 1
}

/// <summary>Proof state retained by target-independent Transition compilation.</summary>
public enum TransitionProofStatus
{
    /// <summary>The proposition was established by the supported restricted proof model.</summary>
    Proven = 0,

    /// <summary>The proposition was refuted and compilation can provide a witness.</summary>
    Disproven = 1,

    /// <summary>The proposition is not decidable by the supported restricted proof model.</summary>
    Unknown = 2,

    /// <summary>The control-flow alternative cannot be selected.</summary>
    Impossible = 3
}

/// <summary>Semantic role of an expression inside canonical Transition IR.</summary>
public enum TransitionExpressionSiteKind
{
    /// <summary>An ordered admission predicate.</summary>
    AdmissionPredicate = 0,

    /// <summary>An outcome returned when admission is rejected.</summary>
    AdmissionRejection = 1,

    /// <summary>A lexical Let value.</summary>
    LetValue = 2,

    /// <summary>An ordered Choice predicate.</summary>
    ChoicePredicate = 3,

    /// <summary>The value inspected by a Match node.</summary>
    MatchValue = 4,

    /// <summary>A sparse patch operand.</summary>
    PatchOperand = 5,

    /// <summary>An emission-intent payload.</summary>
    EmissionPayload = 6,

    /// <summary>A terminal outcome value.</summary>
    OutcomeValue = 7,

    /// <summary>A post-update invariant predicate.</summary>
    InvariantPredicate = 8,

    /// <summary>A shape-owned computed-field expression.</summary>
    ComputedField = 9,

    /// <summary>A linked Cohesive.Machines source-configuration predicate.</summary>
    MachineSourceConfiguration = 10,

    /// <summary>A typed outcome returned when a linked Machine edge is illegal.</summary>
    MachineRejection = 11,

    /// <summary>A linked Cohesive.Machines target-configuration predicate.</summary>
    MachineTargetConfiguration = 12,

    /// <summary>A pure input-derived complete observation for an absent subject.</summary>
    SubjectInitializer = 13
}

/// <summary>Why an aggregate observation can influence Transition semantics.</summary>
[Flags]
public enum TransitionObservationInfluence
{
    /// <summary>No influence role was retained.</summary>
    None = 0,

    /// <summary>The observation influences admission.</summary>
    Admission = 1 << 0,

    /// <summary>The observation influences branch selection.</summary>
    Branch = 1 << 1,

    /// <summary>The observation influences a value or sparse patch calculation.</summary>
    Calculation = 1 << 2,

    /// <summary>The observation influences invariant validation.</summary>
    Invariant = 1 << 3,

    /// <summary>The observation influences a terminal outcome.</summary>
    Outcome = 1 << 4,

    /// <summary>The observation influences an emission intent.</summary>
    Emission = 1 << 5,

    /// <summary>The observation is a transitive dependency of a computed field.</summary>
    DerivedField = 1 << 6,

    /// <summary>The patch algebra requires the prior target value.</summary>
    PatchTarget = 1 << 7
}

/// <summary>A complete coherent observation or one aggregate-relative sparse field access.</summary>
public sealed record TransitionObservationAccess
{
    static readonly TransitionObservationAccess WholeValue = new(path: null);

    TransitionObservationAccess(FieldPath? path) => Path = path;

    /// <summary>The complete coherent aggregate observation.</summary>
    public static TransitionObservationAccess Whole => WholeValue;

    /// <summary>Creates a sparse aggregate field access.</summary>
    /// <param name="path">Non-empty aggregate-relative semantic field path.</param>
    /// <returns>An observation access selecting exactly <paramref name="path"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is default or empty.</exception>
    public static TransitionObservationAccess At(FieldPath path)
    {
        if (path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A sparse observation access requires a non-empty field path.", nameof(path));
        }

        return new(path);
    }

    /// <summary>
    /// Aggregate-relative field path, or <see langword="null"/> when the complete observation is selected.
    /// </summary>
    public FieldPath? Path { get; }

    /// <summary>Whether the complete coherent aggregate observation is selected.</summary>
    public bool IsWhole => Path is null;

    internal string SortKey => Path is { } path ? $"1:{path}" : "0";

    /// <summary>Formats the access as <c>$observation</c> or its aggregate-relative path.</summary>
    /// <returns>A stable human-readable observation selector.</returns>
    public override string ToString() => Path?.ToString() ?? "$observation";
}

/// <summary>One analyzed canonical Transition expression site and its owning IR construct.</summary>
public sealed class TransitionExpressionSiteAnalysis
{
    /// <summary>Creates expression-site analysis attribution.</summary>
    /// <param name="node">Stable identity of the owning Transition node or rule.</param>
    /// <param name="kind">Semantic role of the expression.</param>
    /// <param name="analysis">Shared expression analysis result.</param>
    /// <exception cref="ArgumentNullException"><paramref name="analysis"/> is <see langword="null"/>.</exception>
    internal TransitionExpressionSiteAnalysis(
        ExecutionNodeId node,
        TransitionExpressionSiteKind kind,
        ExprAnalysisResult analysis)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
        {
            throw new ArgumentException("An expression site requires an owning node identity.", nameof(node));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Transition expression-site kind.");
        }

        Node = node;
        Kind = kind;
        Analysis = Guard.RequireNotNull(analysis);
    }

    /// <summary>Stable identity of the owning Transition construct.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Semantic role of the expression.</summary>
    public TransitionExpressionSiteKind Kind { get; }

    /// <summary>Shared type, scope, capability, constant, and requirement analysis.</summary>
    public ExprAnalysisResult Analysis { get; }
}

/// <summary>Path-sensitive provenance for one occurrence of a semantic requirement.</summary>
public sealed record TransitionRequirementOccurrence
{
    /// <summary>Creates one requirement occurrence.</summary>
    /// <param name="node">Stable owning node identity.</param>
    /// <param name="condition">Definition-owned condition under which the occurrence is evaluated.</param>
    /// <param name="location">Canonical persisted-document location.</param>
    /// <param name="site">Optional shared expression-site identity.</param>
    /// <param name="schemaLocation">Optional expression-tree location within <paramref name="site"/>.</param>
    /// <param name="influence">Observation influence role, or <see cref="TransitionObservationInfluence.None"/>.</param>
    /// <param name="sourceReferences">Producer source references resolved from the execution source map.</param>
    /// <exception cref="ArgumentException">An identity, condition, location, or source reference is invalid.</exception>
    internal TransitionRequirementOccurrence(
        ExecutionNodeId node,
        TransitionConditionRef condition,
        string location,
        ExprSiteId? site = null,
        string? schemaLocation = null,
        TransitionObservationInfluence influence = TransitionObservationInfluence.None,
        ImmutableArray<string> sourceReferences = default)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
        {
            throw new ArgumentException("A requirement occurrence requires an owning node identity.", nameof(node));
        }

        if (site is { } siteId && string.IsNullOrWhiteSpace(siteId.Value))
        {
            throw new ArgumentException("A requirement occurrence site identity cannot be empty.", nameof(site));
        }

        if ((influence & ~AllInfluences) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(influence), influence, "Unsupported observation influence.");
        }

        Node = node;
        Condition = condition;
        Location = Guard.RequireNotNullOrWhiteSpace(location);
        Site = site;
        SchemaLocation = schemaLocation.TrimmedEmptyOrWhiteSpaceAs();
        Influence = influence;
        SourceReferences = NormalizeStrings(sourceReferences, nameof(sourceReferences));
    }

    static readonly TransitionObservationInfluence AllInfluences =
        TransitionObservationInfluence.Admission
        | TransitionObservationInfluence.Branch
        | TransitionObservationInfluence.Calculation
        | TransitionObservationInfluence.Invariant
        | TransitionObservationInfluence.Outcome
        | TransitionObservationInfluence.Emission
        | TransitionObservationInfluence.DerivedField
        | TransitionObservationInfluence.PatchTarget;

    /// <summary>Stable owning node identity.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Definition-owned condition under which this occurrence is evaluated.</summary>
    public TransitionConditionRef Condition { get; }

    /// <summary>Canonical persisted-document location.</summary>
    public string Location { get; }

    /// <summary>Optional shared expression-site identity.</summary>
    public ExprSiteId? Site { get; }

    /// <summary>Optional expression-tree location within <see cref="Site"/>.</summary>
    public string? SchemaLocation { get; }

    /// <summary>Observation influence role.</summary>
    public TransitionObservationInfluence Influence { get; }

    /// <summary>Resolved producer source references in ordinal order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    static ImmutableArray<string> NormalizeStrings(ImmutableArray<string> values, string parameterName)
    {
        if (values.IsDefaultOrEmpty)
        {
            return [];
        }

        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Source references cannot be empty or white space.", parameterName);
        }

        return [.. values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}

/// <summary>Base class for one requirement projected from the conditional semantic fact model.</summary>
public abstract class TransitionSemanticRequirement
{
    private protected TransitionSemanticRequirement(
        TransitionConditionRef condition,
        TransitionRequirementStrength invocationStrength,
        ImmutableArray<TransitionRequirementOccurrence> occurrences)
    {
        if (!Enum.IsDefined(invocationStrength))
        {
            throw new ArgumentOutOfRangeException(
                nameof(invocationStrength),
                invocationStrength,
                "Unsupported requirement strength.");
        }
        if (occurrences.IsDefaultOrEmpty || occurrences.Any(static occurrence => occurrence is null))
        {
            throw new ArgumentException("A semantic requirement needs at least one occurrence.", nameof(occurrences));
        }

        Condition = condition;
        InvocationStrength = invocationStrength;
        Occurrences = occurrences;
    }

    /// <summary>Combined definition-owned condition for every occurrence of this requirement.</summary>
    public TransitionConditionRef Condition { get; }

    /// <summary>Whether the requirement applies on some or every feasible invocation path.</summary>
    public TransitionRequirementStrength InvocationStrength { get; }

    /// <summary>Path-sensitive occurrences retaining node, expression, condition, and source provenance.</summary>
    public ImmutableArray<TransitionRequirementOccurrence> Occurrences { get; }
}

/// <summary>The Transition requires atomic initialization of an authoritatively absent subject.</summary>
public sealed class TransitionSubjectCreationRequirement : TransitionSemanticRequirement
{
    /// <summary>Creates an unconditional absent-subject creation requirement.</summary>
    /// <param name="condition">Definition-owned creation condition.</param>
    /// <param name="invocationStrength">Requirement strength relative to the invocation domain.</param>
    /// <param name="occurrences">Initialization expression provenance.</param>
    internal TransitionSubjectCreationRequirement(
        TransitionConditionRef condition,
        TransitionRequirementStrength invocationStrength,
        ImmutableArray<TransitionRequirementOccurrence> occurrences)
        : base(condition, invocationStrength, occurrences)
    {
    }
}

/// <summary>A sparse aggregate observation required by Transition evaluation.</summary>
public sealed class TransitionObservationRequirement : TransitionSemanticRequirement
{
    /// <summary>Creates an observation requirement.</summary>
    /// <param name="access">Complete or aggregate-relative sparse observation access.</param>
    /// <param name="condition">Combined condition for all observation occurrences.</param>
    /// <param name="invocationStrength">Requirement strength relative to the complete invocation domain.</param>
    /// <param name="influences">Combined semantic influence roles.</param>
    /// <param name="commitValidationCondition">
    /// Exact paths on which this observation must remain coherent through an authoritative commit.
    /// </param>
    /// <param name="commitValidationInvocationStrength">
    /// Freshness-demand strength relative to the invocation domain, or <see langword="null"/> when no commit path uses it.
    /// </param>
    /// <param name="commitValidationOccurrences">Conditional provenance for commit freshness demands.</param>
    /// <param name="occurrences">Conditional provenance for every use.</param>
    internal TransitionObservationRequirement(
        TransitionObservationAccess access,
        TransitionConditionRef condition,
        TransitionRequirementStrength invocationStrength,
        TransitionObservationInfluence influences,
        TransitionConditionRef commitValidationCondition,
        TransitionRequirementStrength? commitValidationInvocationStrength,
        ImmutableArray<TransitionRequirementOccurrence> commitValidationOccurrences,
        ImmutableArray<TransitionRequirementOccurrence> occurrences)
        : base(condition, invocationStrength, occurrences)
    {
        Access = Guard.RequireNotNull(access);
        if (commitValidationInvocationStrength is { } commitStrength && !Enum.IsDefined(commitStrength))
        {
            throw new ArgumentOutOfRangeException(
                nameof(commitValidationInvocationStrength),
                commitValidationInvocationStrength,
                "Unsupported commit-validation requirement strength.");
        }
        if (commitValidationOccurrences.IsDefault)
        {
            commitValidationOccurrences = [];
        }

        if (commitValidationOccurrences.Any(static occurrence => occurrence is null))
        {
            throw new ArgumentException(
                "Commit-validation occurrences cannot contain null entries.",
                nameof(commitValidationOccurrences));
        }
        if (commitValidationOccurrences.IsDefaultOrEmpty != (commitValidationInvocationStrength is null))
        {
            throw new ArgumentException(
                "Commit-validation strength must be supplied exactly when commit-validation occurrences exist.",
                nameof(commitValidationInvocationStrength));
        }

        Influences = influences;
        CommitValidationCondition = commitValidationCondition;
        CommitValidationInvocationStrength = commitValidationInvocationStrength;
        CommitValidationOccurrences = commitValidationOccurrences;
    }

    /// <summary>Complete or aggregate-relative sparse observation access.</summary>
    public TransitionObservationAccess Access { get; }

    /// <summary>Combined reasons the observation can influence semantics.</summary>
    public TransitionObservationInfluence Influences { get; }

    /// <summary>Exact paths on which this observation must remain coherent through an authoritative commit.</summary>
    public TransitionConditionRef CommitValidationCondition { get; }

    /// <summary>
    /// Freshness-demand strength relative to the invocation domain, or <see langword="null"/> when no commit path uses it.
    /// </summary>
    public TransitionRequirementStrength? CommitValidationInvocationStrength { get; }

    /// <summary>Conditional provenance for commit freshness and conflict-detection demands.</summary>
    public ImmutableArray<TransitionRequirementOccurrence> CommitValidationOccurrences { get; }

    /// <summary>Whether at least one feasible path requires commit-time freshness validation.</summary>
    public bool RequiresCommitValidation => !CommitValidationOccurrences.IsDefaultOrEmpty;
}

/// <summary>
/// An authored or compiler-derived candidate-state write executed during semantic evaluation.
/// </summary>
/// <remarks>
/// The requirement condition describes patch execution. Whether the candidate mutation is retained
/// is determined by the accepted and commit domains after outcome and invariant evaluation.
/// </remarks>
public sealed class TransitionWriteRequirement : TransitionSemanticRequirement
{
    /// <summary>Creates a write requirement.</summary>
    /// <param name="path">Aggregate-relative semantic field path.</param>
    /// <param name="isDerived">Whether the compiler introduced the write to recompute a computed field.</param>
    /// <param name="condition">Combined condition for all write occurrences.</param>
    /// <param name="invocationStrength">Requirement strength relative to the complete invocation domain.</param>
    /// <param name="occurrences">Conditional provenance for every write.</param>
    internal TransitionWriteRequirement(
        FieldPath path,
        bool isDerived,
        TransitionConditionRef condition,
        TransitionRequirementStrength invocationStrength,
        ImmutableArray<TransitionRequirementOccurrence> occurrences)
        : base(condition, invocationStrength, occurrences)
    {
        if (path.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A write requirement needs a non-empty field path.", nameof(path));
        }

        Path = path;
        IsDerived = isDerived;
    }

    /// <summary>Aggregate-relative semantic field path.</summary>
    public FieldPath Path { get; }

    /// <summary>Whether this is compiler-derived recomputation rather than an authored patch.</summary>
    public bool IsDerived { get; }
}

/// <summary>A pure emission intent that may be produced by the Transition.</summary>
public sealed class TransitionEmissionRequirement : TransitionSemanticRequirement
{
    /// <summary>Creates an emission requirement.</summary>
    /// <param name="contract">Exact referenced interaction contract.</param>
    /// <param name="condition">Combined condition for all emission occurrences.</param>
    /// <param name="invocationStrength">Requirement strength relative to the complete invocation domain.</param>
    /// <param name="occurrences">Conditional provenance for every emission.</param>
    internal TransitionEmissionRequirement(
        ExecutionDefinitionReference contract,
        TransitionConditionRef condition,
        TransitionRequirementStrength invocationStrength,
        ImmutableArray<TransitionRequirementOccurrence> occurrences)
        : base(condition, invocationStrength, occurrences) => Contract = Guard.RequireNotNull(contract);

    /// <summary>Exact referenced interaction contract.</summary>
    public ExecutionDefinitionReference Contract { get; }
}

/// <summary>A fingerprint-bound Cohesive.Machines edge movement that may execute on a Transition path.</summary>
public sealed class TransitionMachineMovementRequirement : TransitionSemanticRequirement
{
    /// <summary>Creates a conditional Machine movement requirement.</summary>
    /// <param name="machine">Exact Machine definition reference.</param>
    /// <param name="edge">Stable edge identity within <paramref name="machine"/>.</param>
    /// <param name="condition">Combined condition for all movement occurrences.</param>
    /// <param name="invocationStrength">Requirement strength relative to the complete invocation domain.</param>
    /// <param name="occurrences">Conditional provenance for every movement.</param>
    internal TransitionMachineMovementRequirement(
        ExecutionDefinitionReference machine,
        ExecutionNodeId edge,
        TransitionConditionRef condition,
        TransitionRequirementStrength invocationStrength,
        ImmutableArray<TransitionRequirementOccurrence> occurrences)
        : base(condition, invocationStrength, occurrences)
    {
        Machine = Guard.RequireNotNull(machine);
        if (string.IsNullOrWhiteSpace(edge.Value))
            throw new ArgumentException("A Machine movement requires a stable edge identity.", nameof(edge));
        Edge = edge;
    }

    /// <summary>Exact authoritative Machine definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Machine { get; }

    /// <summary>Stable edge identity within <see cref="Machine"/>.</summary>
    public ExecutionNodeId Edge { get; }
}

/// <summary>An expression operation or ambient semantic capability required by the Transition.</summary>
public sealed class TransitionCapabilityRequirement : TransitionSemanticRequirement
{
    /// <summary>Creates a capability requirement.</summary>
    /// <param name="capability">Shared expression capability requirement.</param>
    /// <param name="condition">Combined condition for all capability occurrences.</param>
    /// <param name="invocationStrength">Requirement strength relative to the complete invocation domain.</param>
    /// <param name="occurrences">Conditional provenance for every capability use.</param>
    internal TransitionCapabilityRequirement(
        ExprCapabilityRequirement capability,
        TransitionConditionRef condition,
        TransitionRequirementStrength invocationStrength,
        ImmutableArray<TransitionRequirementOccurrence> occurrences)
        : base(condition, invocationStrength, occurrences)
    {
        if (string.IsNullOrWhiteSpace(capability.Capability.Value) || !Enum.IsDefined(capability.Kind))
        {
            throw new ArgumentException("A capability requirement must be initialized.", nameof(capability));
        }

        Capability = capability;
    }

    /// <summary>Shared expression capability requirement.</summary>
    public ExprCapabilityRequirement Capability { get; }
}

/// <summary>A typed terminal outcome reachable from admission or the Transition body.</summary>
public sealed class TransitionOutcomeRequirement : TransitionSemanticRequirement
{
    /// <summary>Creates an outcome requirement.</summary>
    /// <param name="decisionKind">Terminal decision kind.</param>
    /// <param name="condition">Combined condition for all outcome occurrences.</param>
    /// <param name="invocationStrength">Requirement strength relative to the complete invocation domain.</param>
    /// <param name="occurrences">Conditional provenance for every outcome.</param>
    internal TransitionOutcomeRequirement(
        TransitionDecisionKind decisionKind,
        TransitionConditionRef condition,
        TransitionRequirementStrength invocationStrength,
        ImmutableArray<TransitionRequirementOccurrence> occurrences)
        : base(condition, invocationStrength, occurrences)
    {
        if (!Enum.IsDefined(decisionKind) || decisionKind == TransitionDecisionKind.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(decisionKind), decisionKind, "Unsupported terminal decision kind.");
        }

        DecisionKind = decisionKind;
    }

    /// <summary>Terminal decision kind, including admission rejection.</summary>
    public TransitionDecisionKind DecisionKind { get; }
}

/// <summary>Reachability proof for one authored Choice or Match alternative.</summary>
public sealed record TransitionAlternativeAnalysis
{
    internal TransitionAlternativeAnalysis(
        ExecutionNodeId node,
        TransitionProofStatus status,
        TransitionConditionRef condition,
        string reason)
    {
        Node = node;
        Status = status;
        Condition = condition;
        Reason = reason;
    }

    /// <summary>Stable alternative or fallback identity.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Static reachability proof status.</summary>
    public TransitionProofStatus Status { get; }

    /// <summary>Exact definition-owned condition selecting this alternative.</summary>
    public TransitionConditionRef Condition { get; }

    /// <summary>Stable human-readable proof explanation.</summary>
    public string Reason { get; }
}

/// <summary>Coverage and alternative reachability analysis for one Choice or Match node.</summary>
public sealed class TransitionBranchAnalysis
{
    /// <summary>Creates branch analysis.</summary>
    /// <param name="node">Stable Choice or Match node identity.</param>
    /// <param name="domain">Condition reaching the Choice or Match node.</param>
    /// <param name="coverage">Proof of declared branch completeness.</param>
    /// <param name="reason">Stable proof explanation.</param>
    /// <param name="alternatives">Authored alternatives in semantic order.</param>
    /// <param name="uncoveredValues">Finite uncovered witnesses, when known.</param>
    internal TransitionBranchAnalysis(
        ExecutionNodeId node,
        TransitionConditionRef domain,
        TransitionProofStatus coverage,
        string reason,
        ImmutableArray<TransitionAlternativeAnalysis> alternatives,
        ImmutableArray<string> uncoveredValues = default)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
        {
            throw new ArgumentException("Branch analysis requires a node identity.", nameof(node));
        }

        if (!Enum.IsDefined(coverage))
        {
            throw new ArgumentOutOfRangeException(nameof(coverage), coverage, "Unsupported proof status.");
        }

        if (alternatives.IsDefault || alternatives.Any(static alternative => alternative is null))
        {
            throw new ArgumentException("Branch alternatives must be initialized and non-null.", nameof(alternatives));
        }

        Node = node;
        Domain = domain;
        Coverage = coverage;
        Reason = Guard.RequireNotNullOrWhiteSpace(reason);
        Alternatives = alternatives;
        UncoveredValues = uncoveredValues.IsDefault
            ? []
            : [.. uncoveredValues.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>Stable Choice or Match node identity.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Definition-owned condition reaching this branch construct.</summary>
    public TransitionConditionRef Domain { get; }

    /// <summary>Proof of declared branch completeness.</summary>
    public TransitionProofStatus Coverage { get; }

    /// <summary>Stable proof explanation.</summary>
    public string Reason { get; }

    /// <summary>Authored alternatives in semantic order.</summary>
    public ImmutableArray<TransitionAlternativeAnalysis> Alternatives { get; }

    /// <summary>Finite uncovered witnesses in canonical order.</summary>
    public ImmutableArray<string> UncoveredValues { get; }
}

/// <summary>One computed field and its direct and transitive aggregate dependencies.</summary>
public sealed class TransitionDerivedFieldAnalysis
{
    /// <summary>Creates computed-field dependency analysis.</summary>
    /// <param name="field">Computed aggregate field.</param>
    /// <param name="directDependencies">Direct aggregate field dependencies.</param>
    /// <param name="baseDependencies">Transitive non-computed leaf dependencies.</param>
    /// <param name="affectedByWrites">Whether at least one authored write can require recomputation.</param>
    internal TransitionDerivedFieldAnalysis(
        FieldPath field,
        ImmutableArray<FieldPath> directDependencies,
        ImmutableArray<FieldPath> baseDependencies,
        bool affectedByWrites)
    {
        if (field.Segments.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A derived field requires a non-empty path.", nameof(field));
        }

        Field = field;
        DirectDependencies = directDependencies.IsDefault ? [] : directDependencies;
        BaseDependencies = baseDependencies.IsDefault ? [] : baseDependencies;
        AffectedByWrites = affectedByWrites;
    }

    /// <summary>Computed aggregate field.</summary>
    public FieldPath Field { get; }

    /// <summary>Direct aggregate field dependencies in canonical order.</summary>
    public ImmutableArray<FieldPath> DirectDependencies { get; }

    /// <summary>Transitive non-computed leaf dependencies in canonical order.</summary>
    public ImmutableArray<FieldPath> BaseDependencies { get; }

    /// <summary>Whether an authored write can require recomputation.</summary>
    public bool AffectedByWrites { get; }
}

/// <summary>
/// Executable computed-field slice retained by a compiled Transition plan.
/// </summary>
/// <remarks>
/// This derived artifact preserves the exact Shape-owned expression and topological order used during compilation;
/// it does not become an independent persisted semantic authority.
/// </remarks>
public sealed class CompiledTransitionDerivedField
{
    /// <summary>Creates one executable computed-field slice.</summary>
    /// <param name="node">Stable compiler-derived node identity used by execution evidence.</param>
    /// <param name="path">Aggregate-relative computed-field path.</param>
    /// <param name="contract">Exact computed-field value contract.</param>
    /// <param name="expression">Shape-owned canonical compute expression.</param>
    /// <param name="directDependencies">Direct candidate-state dependencies.</param>
    internal CompiledTransitionDerivedField(
        ExecutionNodeId node,
        FieldPath path,
        ValueContract contract,
        Expr expression,
        ImmutableArray<FieldPath> directDependencies)
    {
        Node = node;
        Path = path;
        Contract = Guard.RequireNotNull(contract);
        Expression = Guard.RequireNotNull(expression);
        DirectDependencies = directDependencies.IsDefault ? [] : directDependencies;
    }

    /// <summary>Stable compiler-derived node identity used by execution evidence.</summary>
    public ExecutionNodeId Node { get; }

    /// <summary>Aggregate-relative computed-field path.</summary>
    public FieldPath Path { get; }

    /// <summary>Exact computed-field value contract.</summary>
    public ValueContract Contract { get; }

    /// <summary>Shape-owned canonical compute expression.</summary>
    public Expr Expression { get; }

    /// <summary>Direct candidate-state dependencies in deterministic path order.</summary>
    public ImmutableArray<FieldPath> DirectDependencies { get; }
}

/// <summary>Target-independent static semantic analysis, including partial evidence retained on failure.</summary>
public sealed class TransitionSemanticAnalysis
{
    internal TransitionSemanticAnalysis(
        TransitionConditionModel conditions,
        TransitionConditionRef invocationDomain,
        TransitionConditionRef admittedDomain,
        TransitionConditionRef acceptedDomain,
        TransitionConditionRef commitDomain,
        ImmutableArray<TransitionExpressionSiteAnalysis> expressionSites,
        ImmutableArray<TransitionSemanticRequirement> requirements,
        ImmutableArray<TransitionBranchAnalysis> branches,
        ImmutableArray<TransitionDerivedFieldAnalysis> derivedFields)
    {
        Conditions = Guard.RequireNotNull(conditions);
        InvocationDomain = invocationDomain;
        AdmittedDomain = admittedDomain;
        AcceptedDomain = acceptedDomain;
        CommitDomain = commitDomain;
        ExpressionSites = expressionSites;
        Requirements = requirements;
        Branches = branches;
        DerivedFields = derivedFields;
    }

    /// <summary>Authoritative condition and proof model for every conditional compiler projection.</summary>
    public TransitionConditionModel Conditions { get; }

    /// <summary>Complete Transition invocation domain.</summary>
    public TransitionConditionRef InvocationDomain { get; }

    /// <summary>Domain admitted to the Transition body after ordered preconditions.</summary>
    public TransitionConditionRef AdmittedDomain { get; }

    /// <summary>Domain producing Applied or NoChange outcomes whose resulting state satisfies every invariant.</summary>
    public TransitionConditionRef AcceptedDomain { get; }

    /// <summary>
    /// Domain requiring an authoritative commit: invariant-valid Applied paths, plus terminal
    /// Applied, NoChange, or DomainRejected paths that retain durable emission intent.
    /// </summary>
    public TransitionConditionRef CommitDomain { get; }

    /// <summary>Expression sites in stable semantic-site order.</summary>
    public ImmutableArray<TransitionExpressionSiteAnalysis> ExpressionSites { get; }

    /// <summary>
    /// Single closed collection of observation, write, emission, capability, and outcome projections.
    /// </summary>
    public ImmutableArray<TransitionSemanticRequirement> Requirements { get; }

    /// <summary>Choice and Match proof results in node-identity order.</summary>
    public ImmutableArray<TransitionBranchAnalysis> Branches { get; }

    /// <summary>Computed-field dependency closure in field-path order.</summary>
    public ImmutableArray<TransitionDerivedFieldAnalysis> DerivedFields { get; }

    /// <summary>Returns requirements of one semantic variant without introducing another authority.</summary>
    /// <typeparam name="TRequirement">Required closed requirement subtype.</typeparam>
    /// <returns>Requirements assignable to <typeparamref name="TRequirement"/> in canonical order.</returns>
    public ImmutableArray<TRequirement> GetRequirements<TRequirement>()
        where TRequirement : TransitionSemanticRequirement =>
        [.. Requirements.OfType<TRequirement>()];
}

/// <summary>Successful target-independent compilation plan for one exact Transition definition document.</summary>
public sealed class CompiledTransitionPlan
{
    internal CompiledTransitionPlan(
        ExecutionDefinitionDocument document,
        TransitionDefinition definition,
        TransitionSemanticAnalysis analysis,
        ShapeGraph? shapeGraph,
        ImmutableArray<CompiledTransitionDerivedField> derivedFields,
        ImmutableArray<TransitionMachineEdgeLink> machineEdges)
    {
        Document = document;
        Definition = definition;
        Analysis = analysis;
        ShapeGraph = shapeGraph;
        DerivedFields = derivedFields.IsDefault ? [] : derivedFields;
        MachineEdges = machineEdges.IsDefault ? [] : machineEdges;
    }

    /// <summary>Exact fingerprinted Transition definition document.</summary>
    public ExecutionDefinitionDocument Document { get; }

    /// <summary>Exact fingerprint-pinned identity of <see cref="Document"/>.</summary>
    public ExecutionDefinitionReference DefinitionReference => new(
        Document.Metadata.DefinitionId,
        Document.Metadata.RevisionId,
        Document.Metadata.Fingerprint);

    /// <summary>Canonical typed Transition definition.</summary>
    public TransitionDefinition Definition { get; }

    /// <summary>Target-independent conditional semantic analysis.</summary>
    public TransitionSemanticAnalysis Analysis { get; }

    /// <summary>Exact Shape graph used to resolve contracts and computed fields, when one was required.</summary>
    public ShapeGraph? ShapeGraph { get; }

    /// <summary>Executable computed fields in dependency-first topological order.</summary>
    public ImmutableArray<CompiledTransitionDerivedField> DerivedFields { get; }

    /// <summary>Machine-derived linked edge slices used by this plan in exact reference order.</summary>
    public ImmutableArray<TransitionMachineEdgeLink> MachineEdges { get; }
}

/// <summary>Result of attempting target-independent Transition compilation.</summary>
public sealed class TransitionCompilationResult
{
    internal TransitionCompilationResult(
        ExecutionDefinitionDocument document,
        TransitionDefinition? definition,
        TransitionSemanticAnalysis? analysis,
        CompiledTransitionPlan? plan,
        DocumentValidationResult validation)
    {
        Document = document;
        Definition = definition;
        Analysis = analysis;
        Plan = plan;
        Validation = validation;
    }

    /// <summary>Exact supplied execution-definition document.</summary>
    public ExecutionDefinitionDocument Document { get; }

    /// <summary>Typed Transition payload when strict projection succeeded.</summary>
    public TransitionDefinition? Definition { get; }

    /// <summary>Partial or complete semantic analysis when structural validation permitted analysis.</summary>
    public TransitionSemanticAnalysis? Analysis { get; }

    /// <summary>Executable static plan only when all compiler diagnostics are non-errors.</summary>
    public CompiledTransitionPlan? Plan { get; }

    /// <summary>Deterministically ordered document, expression, proof, flow, and dependency diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether compilation produced a complete plan with no error diagnostics.</summary>
    public bool IsSuccessful => Plan is not null && Validation.IsValid;
}
