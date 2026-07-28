using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Transitions.IR;

/// <summary>Closed persisted union of finite canonical Transition control-flow nodes.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = TransitionWireNames.NodeDiscriminator)]
[JsonDerivedType(typeof(SequenceTransitionNode), TransitionWireNames.SequenceNode)]
[JsonDerivedType(typeof(LetTransitionNode), TransitionWireNames.LetNode)]
[JsonDerivedType(typeof(ChoiceTransitionNode), TransitionWireNames.ChoiceNode)]
[JsonDerivedType(typeof(MatchTransitionNode), TransitionWireNames.MatchNode)]
[JsonDerivedType(typeof(UpdateTransitionNode), TransitionWireNames.UpdateNode)]
[JsonDerivedType(typeof(EmitTransitionNode), TransitionWireNames.EmitNode)]
[JsonDerivedType(typeof(MoveMachineTransitionNode), TransitionWireNames.MoveMachineNode)]
[JsonDerivedType(typeof(OutcomeTransitionNode), TransitionWireNames.OutcomeNode)]
public abstract record TransitionNode
{
    /// <summary>Creates a Transition node.</summary>
    /// <param name="id">Stable node identity used by diagnostics, source maps, and traces.</param>
    private protected TransitionNode(ExecutionNodeId id) => Id = id;

    /// <summary>Stable node identity used by diagnostics, source maps, and traces.</summary>
    public ExecutionNodeId Id { get; }
}

/// <summary>An ordered finite sequence of Transition nodes.</summary>
public sealed record SequenceTransitionNode : TransitionNode
{
    /// <summary>Creates a sequence node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="steps">Ordered child nodes.</param>
    [JsonConstructor]
    public SequenceTransitionNode(ExecutionNodeId id, ImmutableArray<TransitionNode> steps)
        : base(id) => Steps = steps.IsDefault ? [] : steps;

    /// <summary>Ordered child nodes.</summary>
    public ImmutableArray<TransitionNode> Steps { get; }

    /// <summary>Compares sequence nodes by stable identity and ordered child semantics.</summary>
    /// <param name="other">Sequence to compare with this value.</param>
    /// <returns><see langword="true"/> when identity and every ordered child are equal.</returns>
    public bool Equals(SequenceTransitionNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Steps.SequenceEqual(other.Steps);

    /// <summary>Returns a structural hash code for the stable identity and ordered children.</summary>
    /// <returns>A hash code derived from the node identity and every ordered child.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        foreach (var step in Steps)
        {
            hash.Add(step);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Binds one pure expression result for later nodes in the enclosing lexical sequence.</summary>
/// <remarks>
/// The complete bound value is referenced by a <see cref="BindingExpr"/> carrying <see cref="Binding"/>.
/// Fields of an object-valued binding are referenced by a <see cref="FieldExpr"/> carrying that same identity.
/// Lexical visibility and type checking are compiler responsibilities.
/// </remarks>
public sealed record LetTransitionNode : TransitionNode
{
    /// <summary>Creates a lexical binding node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="binding">Stable value-binding identity.</param>
    /// <param name="contract">Typed contract of the bound value.</param>
    /// <param name="value">Pure value expression.</param>
    [JsonConstructor]
    public LetTransitionNode(
        ExecutionNodeId id,
        ValueBindingId binding,
        ValueContract contract,
        Expr value)
        : base(id)
    {
        Binding = binding;
        Contract = contract;
        Value = value;
    }

    /// <summary>Stable value-binding identity.</summary>
    public ValueBindingId Binding { get; }

    /// <summary>Typed contract of the bound value.</summary>
    public ValueContract Contract { get; }

    /// <summary>Pure value expression.</summary>
    public Expr Value { get; }
}

/// <summary>Selection semantics for ordered Choice and Match cases.</summary>
public enum TransitionCaseSelection
{
    /// <summary>No case-selection rule was supplied; this value is invalid in canonical IR.</summary>
    Unspecified = 0,

    /// <summary>Evaluate cases in order and select the first predicate or exact pattern that matches.</summary>
    OrderedFirstMatch = 1
}

/// <summary>How a Choice or Match construct declares that all inputs are covered.</summary>
public enum TransitionBranchCompleteness
{
    /// <summary>No completeness contract was supplied; this value is invalid in canonical IR.</summary>
    Unspecified = 0,

    /// <summary>The declared cases are intended to be exhaustive and must be proven by compilation.</summary>
    Exhaustive = 1,

    /// <summary>An explicit fallback provides coverage when no declared case matches.</summary>
    Fallback = 2
}

/// <summary>One stable predicate branch in an ordered Choice node.</summary>
public sealed record TransitionChoiceCase
{
    /// <summary>Creates a predicate branch.</summary>
    /// <param name="id">Stable branch identity.</param>
    /// <param name="predicate">Pure branch predicate.</param>
    /// <param name="body">Finite branch body.</param>
    [JsonConstructor]
    public TransitionChoiceCase(ExecutionNodeId id, Expr predicate, SequenceTransitionNode body)
    {
        Id = id;
        Predicate = predicate;
        Body = body;
    }

    /// <summary>Stable branch identity.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Pure branch predicate.</summary>
    public Expr Predicate { get; }

    /// <summary>Finite branch body.</summary>
    public SequenceTransitionNode Body { get; }
}

/// <summary>An explicit stable fallback for a Choice or Match node.</summary>
public sealed record TransitionFallback
{
    /// <summary>Creates a fallback branch.</summary>
    /// <param name="id">Stable fallback identity.</param>
    /// <param name="body">Finite fallback body.</param>
    [JsonConstructor]
    public TransitionFallback(ExecutionNodeId id, SequenceTransitionNode body)
    {
        Id = id;
        Body = body;
    }

    /// <summary>Stable fallback identity.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Finite fallback body.</summary>
    public SequenceTransitionNode Body { get; }
}

/// <summary>An explicitly ordered predicate choice.</summary>
public sealed record ChoiceTransitionNode : TransitionNode
{
    /// <summary>Creates a predicate choice node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="selection">Explicit branch-selection semantics.</param>
    /// <param name="completeness">Declared coverage mode.</param>
    /// <param name="cases">Ordered predicate cases.</param>
    /// <param name="fallback">Explicit fallback when <paramref name="completeness"/> requires one.</param>
    [JsonConstructor]
    public ChoiceTransitionNode(
        ExecutionNodeId id,
        TransitionCaseSelection selection,
        TransitionBranchCompleteness completeness,
        ImmutableArray<TransitionChoiceCase> cases,
        TransitionFallback? fallback = null)
        : base(id)
    {
        Selection = selection;
        Completeness = completeness;
        Cases = cases.IsDefault ? [] : cases;
        Fallback = fallback;
    }

    /// <summary>Explicit branch-selection semantics.</summary>
    public TransitionCaseSelection Selection { get; }

    /// <summary>Declared coverage mode.</summary>
    public TransitionBranchCompleteness Completeness { get; }

    /// <summary>Ordered predicate cases.</summary>
    public ImmutableArray<TransitionChoiceCase> Cases { get; }

    /// <summary>Optional explicit fallback.</summary>
    public TransitionFallback? Fallback { get; }

    /// <summary>Compares Choice nodes by complete ordered persisted semantics.</summary>
    /// <param name="other">Choice to compare with this value.</param>
    /// <returns><see langword="true"/> when identity, policies, cases, and fallback are equal.</returns>
    public bool Equals(ChoiceTransitionNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Selection == other.Selection
        && Completeness == other.Completeness
        && Cases.SequenceEqual(other.Cases)
        && Fallback == other.Fallback;

    /// <summary>Returns a structural hash code for complete ordered Choice semantics.</summary>
    /// <returns>A hash code derived from identity, policies, cases, and fallback.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Selection);
        hash.Add(Completeness);
        foreach (var choiceCase in Cases)
        {
            hash.Add(choiceCase);
        }

        hash.Add(Fallback);
        return hash.ToHashCode();
    }
}

/// <summary>One stable exact-value case in a Match node.</summary>
public sealed record TransitionMatchCase
{
    /// <summary>Creates an exact-value match case.</summary>
    /// <param name="id">Stable case identity.</param>
    /// <param name="pattern">Typed exact portable pattern.</param>
    /// <param name="body">Finite case body.</param>
    [JsonConstructor]
    public TransitionMatchCase(ExecutionNodeId id, PortableValue pattern, SequenceTransitionNode body)
    {
        Id = id;
        Pattern = pattern;
        Body = body;
    }

    /// <summary>Stable case identity.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Typed exact portable pattern.</summary>
    public PortableValue Pattern { get; }

    /// <summary>Finite case body.</summary>
    public SequenceTransitionNode Body { get; }
}

/// <summary>An explicitly typed exact-pattern match.</summary>
public sealed record MatchTransitionNode : TransitionNode
{
    /// <summary>Creates an exact-pattern match node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="selection">Explicit case-selection semantics.</param>
    /// <param name="completeness">Declared coverage mode.</param>
    /// <param name="value">Pure value expression being matched.</param>
    /// <param name="contract">Typed contract of <paramref name="value"/> and every case pattern.</param>
    /// <param name="cases">Ordered exact-value cases.</param>
    /// <param name="fallback">Explicit fallback when <paramref name="completeness"/> requires one.</param>
    [JsonConstructor]
    public MatchTransitionNode(
        ExecutionNodeId id,
        TransitionCaseSelection selection,
        TransitionBranchCompleteness completeness,
        Expr value,
        ValueContract contract,
        ImmutableArray<TransitionMatchCase> cases,
        TransitionFallback? fallback = null)
        : base(id)
    {
        Selection = selection;
        Completeness = completeness;
        Value = value;
        Contract = contract;
        Cases = cases.IsDefault ? [] : cases;
        Fallback = fallback;
    }

    /// <summary>Explicit case-selection semantics.</summary>
    public TransitionCaseSelection Selection { get; }

    /// <summary>Declared coverage mode.</summary>
    public TransitionBranchCompleteness Completeness { get; }

    /// <summary>Pure value expression being matched.</summary>
    public Expr Value { get; }

    /// <summary>Typed contract of <see cref="Value"/> and every case pattern.</summary>
    public ValueContract Contract { get; }

    /// <summary>Ordered exact-value cases.</summary>
    public ImmutableArray<TransitionMatchCase> Cases { get; }

    /// <summary>Optional explicit fallback.</summary>
    public TransitionFallback? Fallback { get; }

    /// <summary>Compares Match nodes by complete ordered persisted semantics.</summary>
    /// <param name="other">Match to compare with this value.</param>
    /// <returns><see langword="true"/> when identity, policies, value, contract, cases, and fallback are equal.</returns>
    public bool Equals(MatchTransitionNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Selection == other.Selection
        && Completeness == other.Completeness
        && Value == other.Value
        && Contract == other.Contract
        && Cases.SequenceEqual(other.Cases)
        && Fallback == other.Fallback;

    /// <summary>Returns a structural hash code for complete ordered Match semantics.</summary>
    /// <returns>A hash code derived from identity, policies, value, contract, cases, and fallback.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Selection);
        hash.Add(Completeness);
        hash.Add(Value);
        hash.Add(Contract);
        foreach (var matchCase in Cases)
        {
            hash.Add(matchCase);
        }

        hash.Add(Fallback);
        return hash.ToHashCode();
    }
}

/// <summary>Applies one algebraic sparse patch to an aggregate-relative semantic field path.</summary>
public sealed record UpdateTransitionNode : TransitionNode
{
    /// <summary>Creates a sparse update node.</summary>
    /// <param name="id">Stable node and patch identity.</param>
    /// <param name="path">Aggregate-relative semantic field path.</param>
    /// <param name="operation">Algebraic patch operation.</param>
    [JsonConstructor]
    public UpdateTransitionNode(
        ExecutionNodeId id,
        FieldPath path,
        TransitionPatchOperation operation)
        : base(id)
    {
        Path = path;
        Operation = operation;
    }

    /// <summary>Aggregate-relative semantic field path.</summary>
    public FieldPath Path { get; }

    /// <summary>Algebraic patch operation.</summary>
    public TransitionPatchOperation Operation { get; }
}

/// <summary>Produces a pure typed emission intent referencing an exact external interaction contract.</summary>
/// <remarks>
/// The exact referenced interaction contract is the sole authority for whether the intent is a domain event
/// or request and for its payload and response obligations. A Transition v1 Emit reference must resolve to
/// one of those interaction families; link resolution and payload typing are compiler responsibilities.
/// </remarks>
public sealed record EmitTransitionNode : TransitionNode
{
    /// <summary>Creates an emission-intent node.</summary>
    /// <param name="id">Stable emission node identity.</param>
    /// <param name="contract">Exact versioned and fingerprinted interaction contract reference.</param>
    /// <param name="payload">Pure typed payload expression.</param>
    [JsonConstructor]
    public EmitTransitionNode(
        ExecutionNodeId id,
        ExecutionDefinitionReference contract,
        Expr payload)
        : base(id)
    {
        Contract = contract;
        Payload = payload;
    }

    /// <summary>
    /// Exact versioned and fingerprinted interaction contract reference whose definition owns the interaction kind.
    /// </summary>
    public ExecutionDefinitionReference Contract { get; }

    /// <summary>Pure typed payload expression.</summary>
    public Expr Payload { get; }
}

/// <summary>
/// Applies one exact edge from a fingerprint-bound Cohesive.Machines definition.
/// </summary>
/// <remarks>
/// Source and target configurations, their observation dependencies, and the state patch remain owned by the
/// referenced Machine definition. A Transition compiler links this node to immutable Machine-derived evidence;
/// the Transition IR deliberately does not duplicate the lifecycle graph or physical status-field conventions.
/// </remarks>
public sealed record MoveMachineTransitionNode : TransitionNode
{
    /// <summary>Creates a Machine edge movement.</summary>
    /// <param name="id">Stable movement-node identity.</param>
    /// <param name="machine">Exact Machine definition revision and fingerprint.</param>
    /// <param name="edge">Stable edge identity owned by the referenced Machine.</param>
    /// <param name="rejection">Typed Transition outcome returned when the source configuration is illegal.</param>
    [JsonConstructor]
    public MoveMachineTransitionNode(
        ExecutionNodeId id,
        ExecutionDefinitionReference machine,
        ExecutionNodeId edge,
        Expr rejection)
        : base(id)
    {
        Machine = machine;
        Edge = edge;
        Rejection = rejection;
    }

    /// <summary>Exact Machine definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Machine { get; }

    /// <summary>Stable edge identity owned by the referenced Machine.</summary>
    public ExecutionNodeId Edge { get; }

    /// <summary>Typed Transition outcome returned when the source configuration is illegal.</summary>
    public Expr Rejection { get; }
}

/// <summary>Authorable terminal outcome dispositions.</summary>
public enum TransitionOutcomeDisposition
{
    /// <summary>No terminal disposition was supplied; this value is invalid in canonical IR.</summary>
    Unspecified = 0,

    /// <summary>The transition produces an applied candidate state.</summary>
    Applied = 1,

    /// <summary>The transition succeeds without changing aggregate state.</summary>
    NoChange = 2,

    /// <summary>The transition returns an explicit alternate domain rejection.</summary>
    DomainRejected = 3
}

/// <summary>Returns one typed terminal outcome from a Transition body.</summary>
public sealed record OutcomeTransitionNode : TransitionNode
{
    /// <summary>Creates a terminal outcome node.</summary>
    /// <param name="id">Stable outcome-node identity.</param>
    /// <param name="disposition">Authorable terminal disposition.</param>
    /// <param name="value">Pure expression yielding the typed outcome value.</param>
    [JsonConstructor]
    public OutcomeTransitionNode(
        ExecutionNodeId id,
        TransitionOutcomeDisposition disposition,
        Expr value)
        : base(id)
    {
        Disposition = disposition;
        Value = value;
    }

    /// <summary>Authorable terminal disposition.</summary>
    public TransitionOutcomeDisposition Disposition { get; }

    /// <summary>Pure expression yielding the typed outcome value.</summary>
    public Expr Value { get; }
}
