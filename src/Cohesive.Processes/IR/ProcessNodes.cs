using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Processes.IR;

/// <summary>Closed persisted union of finite canonical Process graph nodes.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = ProcessWireNames.NodeDiscriminator)]
[JsonDerivedType(typeof(InvokeTransitionProcessNode), ProcessWireNames.InvokeTransitionNode)]
[JsonDerivedType(typeof(EvaluateRelationProcessNode), ProcessWireNames.EvaluateRelationNode)]
[JsonDerivedType(typeof(RequestProcessNode), ProcessWireNames.RequestNode)]
[JsonDerivedType(typeof(EmitEventProcessNode), ProcessWireNames.EmitEventNode)]
[JsonDerivedType(typeof(SendSignalProcessNode), ProcessWireNames.SendSignalNode)]
[JsonDerivedType(typeof(ChoiceProcessNode), ProcessWireNames.ChoiceNode)]
[JsonDerivedType(typeof(MatchProcessNode), ProcessWireNames.MatchNode)]
[JsonDerivedType(typeof(ForkProcessNode), ProcessWireNames.ForkNode)]
[JsonDerivedType(typeof(JoinProcessNode), ProcessWireNames.JoinNode)]
[JsonDerivedType(typeof(AwaitMatchProcessNode), ProcessWireNames.AwaitMatchNode)]
[JsonDerivedType(typeof(TimerProcessNode), ProcessWireNames.TimerNode)]
[JsonDerivedType(typeof(ReplyProcessNode), ProcessWireNames.ReplyNode)]
[JsonDerivedType(typeof(DurableCutProcessNode), ProcessWireNames.DurableCutNode)]
[JsonDerivedType(typeof(ReturnProcessNode), ProcessWireNames.ReturnNode)]
[JsonDerivedType(typeof(FailProcessNode), ProcessWireNames.FailNode)]
public abstract record ProcessNode
{
    /// <summary>Creates a Process node.</summary>
    /// <param name="id">Stable node identity used by graph links, diagnostics, source maps, and traces.</param>
    private protected ProcessNode(ExecutionNodeId id) => Id = id;

    /// <summary>Stable node identity used by graph links, diagnostics, source maps, and traces.</summary>
    public ExecutionNodeId Id { get; }
}

/// <summary>Invokes one exact aggregate Transition without embedding its definition or interpreter.</summary>
public sealed record InvokeTransitionProcessNode : ProcessNode
{
    /// <summary>Creates a Transition invocation node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="transition">Exact Transition definition revision and fingerprint.</param>
    /// <param name="subject">Portable expression identifying the authoritative aggregate subject.</param>
    /// <param name="input">Portable typed Transition input expression.</param>
    /// <param name="continuation">Typed continuation receiving the Transition outcome when requested.</param>
    [JsonConstructor]
    public InvokeTransitionProcessNode(
        ExecutionNodeId id,
        ExecutionDefinitionReference transition,
        Expr subject,
        Expr input,
        ProcessContinuation continuation)
        : base(id)
    {
        Transition = transition;
        Subject = subject;
        Input = input;
        Continuation = continuation;
    }

    /// <summary>Exact Transition definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Transition { get; }

    /// <summary>Portable expression identifying the authoritative aggregate subject.</summary>
    public Expr Subject { get; }

    /// <summary>Portable typed Transition input expression.</summary>
    public Expr Input { get; }

    /// <summary>Typed continuation receiving the Transition outcome when requested.</summary>
    public ProcessContinuation Continuation { get; }
}

/// <summary>Evaluates one exact canonical Relation or Query definition.</summary>
public sealed record EvaluateRelationProcessNode : ProcessNode
{
    /// <summary>Creates a Relation or Query evaluation node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="relation">Exact Relation or Query definition revision and fingerprint.</param>
    /// <param name="input">Portable typed query input expression.</param>
    /// <param name="continuation">Typed continuation receiving the query result when requested.</param>
    [JsonConstructor]
    public EvaluateRelationProcessNode(
        ExecutionNodeId id,
        ExecutionDefinitionReference relation,
        Expr input,
        ProcessContinuation continuation)
        : base(id)
    {
        Relation = relation;
        Input = input;
        Continuation = continuation;
    }

    /// <summary>Exact Relation or Query definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Relation { get; }

    /// <summary>Portable typed query input expression.</summary>
    public Expr Input { get; }

    /// <summary>Typed continuation receiving the query result when requested.</summary>
    public ProcessContinuation Continuation { get; }
}

/// <summary>One stable terminal-outcome branch of a canonical Request.</summary>
public sealed record ProcessRequestOutcomeBranch
{
    /// <summary>Creates a Request terminal-outcome branch.</summary>
    /// <param name="id">Stable branch identity.</param>
    /// <param name="outcome">Stable outcome identity declared by the exact Request contract.</param>
    /// <param name="continuation">Typed continuation selected when this terminal outcome is accepted.</param>
    [JsonConstructor]
    public ProcessRequestOutcomeBranch(
        ExecutionNodeId id,
        RequestTerminalOutcomeId outcome,
        ProcessContinuation continuation)
    {
        Id = id;
        Outcome = outcome;
        Continuation = continuation;
    }

    /// <summary>Stable branch identity.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Stable outcome identity declared by the exact Request contract.</summary>
    public RequestTerminalOutcomeId Outcome { get; }

    /// <summary>Typed continuation selected when this terminal outcome is accepted.</summary>
    public ProcessContinuation Continuation { get; }
}

/// <summary>Creates one durable typed Request obligation with explicit terminal continuations.</summary>
public sealed record RequestProcessNode : ProcessNode
{
    /// <summary>Creates a Request node.</summary>
    /// <param name="id">Stable node and logical emission identity basis.</param>
    /// <param name="contract">Exact typed Request contract.</param>
    /// <param name="payload">Portable typed Request payload expression.</param>
    /// <param name="outcomes">Set-like terminal outcome branches keyed by Request-owned outcome identity.</param>
    [JsonConstructor]
    public RequestProcessNode(
        ExecutionNodeId id,
        RequestContractReference contract,
        Expr payload,
        ImmutableArray<ProcessRequestOutcomeBranch> outcomes)
        : base(id)
    {
        Contract = contract;
        Payload = payload;
        Outcomes = ProcessIrCollections.NormalizeSet(outcomes, CompareOutcomeBranches);
    }

    /// <summary>Exact typed Request contract.</summary>
    public RequestContractReference Contract { get; }

    /// <summary>Portable typed Request payload expression.</summary>
    public Expr Payload { get; }

    /// <summary>Terminal outcome branches in deterministic Request outcome-identity order.</summary>
    public ImmutableArray<ProcessRequestOutcomeBranch> Outcomes { get; }

    /// <summary>Compares Request nodes by complete normalized semantic value.</summary>
    /// <param name="other">Request node to compare with this value.</param>
    /// <returns><see langword="true"/> when identity, contract, payload, and every outcome branch are equal.</returns>
    public bool Equals(RequestProcessNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Contract == other.Contract
        && Payload == other.Payload
        && Outcomes.SequenceEqual(other.Outcomes);

    /// <summary>Returns a structural hash code for complete Request semantics.</summary>
    /// <returns>A hash code derived from identity, contract, payload, and normalized outcome branches.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Contract);
        hash.Add(Payload);
        foreach (var outcome in Outcomes)
            hash.Add(outcome);
        return hash.ToHashCode();
    }

    static int CompareOutcomeBranches(ProcessRequestOutcomeBranch? left, ProcessRequestOutcomeBranch? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var comparison = StringComparer.Ordinal.Compare(left.Outcome.Value, right.Outcome.Value);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
    }
}

/// <summary>Emits one typed domain event and continues without creating a response obligation.</summary>
public sealed record EmitEventProcessNode : ProcessNode
{
    /// <summary>Creates a domain-event emission node.</summary>
    /// <param name="id">Stable node and logical emission identity basis.</param>
    /// <param name="contract">Exact typed domain-event contract.</param>
    /// <param name="payload">Portable typed event payload expression.</param>
    /// <param name="next">Stable edge selected after the emission intent is accepted.</param>
    [JsonConstructor]
    public EmitEventProcessNode(
        ExecutionNodeId id,
        DomainEventContractReference contract,
        Expr payload,
        ProcessEdge next)
        : base(id)
    {
        Contract = contract;
        Payload = payload;
        Next = next;
    }

    /// <summary>Exact typed domain-event contract.</summary>
    public DomainEventContractReference Contract { get; }

    /// <summary>Portable typed event payload expression.</summary>
    public Expr Payload { get; }

    /// <summary>Stable edge selected after the emission intent is accepted.</summary>
    public ProcessEdge Next { get; }
}

/// <summary>Sends one typed Signal to an explicitly computed semantic target.</summary>
public sealed record SendSignalProcessNode : ProcessNode
{
    /// <summary>Creates a Signal-send node.</summary>
    /// <param name="id">Stable node and logical emission identity basis.</param>
    /// <param name="contract">Exact typed Signal contract.</param>
    /// <param name="target">Portable expression identifying the Signal target.</param>
    /// <param name="payload">Portable typed Signal payload expression.</param>
    /// <param name="next">Stable edge selected after the send intent is accepted.</param>
    [JsonConstructor]
    public SendSignalProcessNode(
        ExecutionNodeId id,
        SignalContractReference contract,
        Expr target,
        Expr payload,
        ProcessEdge next)
        : base(id)
    {
        Contract = contract;
        Target = target;
        Payload = payload;
        Next = next;
    }

    /// <summary>Exact typed Signal contract.</summary>
    public SignalContractReference Contract { get; }

    /// <summary>Portable expression identifying the Signal target.</summary>
    public Expr Target { get; }

    /// <summary>Portable typed Signal payload expression.</summary>
    public Expr Payload { get; }

    /// <summary>Stable edge selected after the send intent is accepted.</summary>
    public ProcessEdge Next { get; }
}

/// <summary>One stable predicate case in an ordered Process Choice.</summary>
public sealed record ProcessChoiceCase
{
    /// <summary>Creates an ordered predicate case.</summary>
    /// <param name="id">Stable case identity.</param>
    /// <param name="predicate">Portable Boolean branch predicate.</param>
    /// <param name="next">Stable edge selected when the predicate matches.</param>
    [JsonConstructor]
    public ProcessChoiceCase(ExecutionNodeId id, Expr predicate, ProcessEdge next)
    {
        Id = id;
        Predicate = predicate;
        Next = next;
    }

    /// <summary>Stable case identity.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Portable Boolean branch predicate.</summary>
    public Expr Predicate { get; }

    /// <summary>Stable edge selected when the predicate matches.</summary>
    public ProcessEdge Next { get; }
}

/// <summary>Explicit stable fallback shared by Process Choice and Match nodes.</summary>
public sealed record ProcessFallback
{
    /// <summary>Creates an explicit fallback branch.</summary>
    /// <param name="id">Stable fallback identity.</param>
    /// <param name="next">Stable fallback edge.</param>
    [JsonConstructor]
    public ProcessFallback(ExecutionNodeId id, ProcessEdge next)
    {
        Id = id;
        Next = next;
    }

    /// <summary>Stable fallback identity.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Stable fallback edge.</summary>
    public ProcessEdge Next { get; }
}

/// <summary>An explicitly ordered portable predicate choice.</summary>
public sealed record ChoiceProcessNode : ProcessNode
{
    /// <summary>Creates a predicate Choice node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="selection">Explicit ordered case-selection semantics.</param>
    /// <param name="completeness">Declared branch coverage mode.</param>
    /// <param name="cases">Semantically ordered predicate cases.</param>
    /// <param name="fallback">Optional explicit fallback branch.</param>
    [JsonConstructor]
    public ChoiceProcessNode(
        ExecutionNodeId id,
        CaseSelection selection,
        BranchCompleteness completeness,
        ImmutableArray<ProcessChoiceCase> cases,
        ProcessFallback? fallback = null)
        : base(id)
    {
        Selection = selection;
        Completeness = completeness;
        Cases = cases.IsDefault ? [] : cases;
        Fallback = fallback;
    }

    /// <summary>Explicit ordered case-selection semantics.</summary>
    public CaseSelection Selection { get; }

    /// <summary>Declared branch coverage mode.</summary>
    public BranchCompleteness Completeness { get; }

    /// <summary>Semantically ordered predicate cases.</summary>
    public ImmutableArray<ProcessChoiceCase> Cases { get; }

    /// <summary>Optional explicit fallback branch.</summary>
    public ProcessFallback? Fallback { get; }

    /// <summary>Compares Choice nodes by complete ordered persisted semantics.</summary>
    /// <param name="other">Choice node to compare with this value.</param>
    /// <returns><see langword="true"/> when identity, policies, ordered cases, and fallback are equal.</returns>
    public bool Equals(ChoiceProcessNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Selection == other.Selection
        && Completeness == other.Completeness
        && Cases.SequenceEqual(other.Cases)
        && Fallback == other.Fallback;

    /// <summary>Returns a structural hash code for complete ordered Choice semantics.</summary>
    /// <returns>A hash code derived from identity, policies, ordered cases, and fallback.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Selection);
        hash.Add(Completeness);
        foreach (var choiceCase in Cases)
            hash.Add(choiceCase);
        hash.Add(Fallback);
        return hash.ToHashCode();
    }
}

/// <summary>One stable exact-value case in an ordered Process Match.</summary>
public sealed record ProcessMatchCase
{
    /// <summary>Creates an ordered exact-value case.</summary>
    /// <param name="id">Stable case identity.</param>
    /// <param name="pattern">Typed exact portable pattern.</param>
    /// <param name="next">Stable edge selected when the pattern matches.</param>
    [JsonConstructor]
    public ProcessMatchCase(ExecutionNodeId id, PortableValue pattern, ProcessEdge next)
    {
        Id = id;
        Pattern = pattern;
        Next = next;
    }

    /// <summary>Stable case identity.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Typed exact portable pattern.</summary>
    public PortableValue Pattern { get; }

    /// <summary>Stable edge selected when the pattern matches.</summary>
    public ProcessEdge Next { get; }
}

/// <summary>An explicitly typed and ordered exact-pattern Match.</summary>
public sealed record MatchProcessNode : ProcessNode
{
    /// <summary>Creates an exact-pattern Match node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="selection">Explicit ordered case-selection semantics.</param>
    /// <param name="completeness">Declared branch coverage mode.</param>
    /// <param name="value">Portable value expression being matched.</param>
    /// <param name="contract">Typed contract of <paramref name="value"/> and every case pattern.</param>
    /// <param name="cases">Semantically ordered exact-value cases.</param>
    /// <param name="fallback">Optional explicit fallback branch.</param>
    [JsonConstructor]
    public MatchProcessNode(
        ExecutionNodeId id,
        CaseSelection selection,
        BranchCompleteness completeness,
        Expr value,
        ValueContract contract,
        ImmutableArray<ProcessMatchCase> cases,
        ProcessFallback? fallback = null)
        : base(id)
    {
        Selection = selection;
        Completeness = completeness;
        Value = value;
        Contract = contract;
        Cases = cases.IsDefault ? [] : cases;
        Fallback = fallback;
    }

    /// <summary>Explicit ordered case-selection semantics.</summary>
    public CaseSelection Selection { get; }

    /// <summary>Declared branch coverage mode.</summary>
    public BranchCompleteness Completeness { get; }

    /// <summary>Portable value expression being matched.</summary>
    public Expr Value { get; }

    /// <summary>Typed contract of <see cref="Value"/> and every case pattern.</summary>
    public ValueContract Contract { get; }

    /// <summary>Semantically ordered exact-value cases.</summary>
    public ImmutableArray<ProcessMatchCase> Cases { get; }

    /// <summary>Optional explicit fallback branch.</summary>
    public ProcessFallback? Fallback { get; }

    /// <summary>Compares Match nodes by complete ordered persisted semantics.</summary>
    /// <param name="other">Match node to compare with this value.</param>
    /// <returns><see langword="true"/> when identity, policies, value, contract, ordered cases, and fallback are equal.</returns>
    public bool Equals(MatchProcessNode? other) =>
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
    /// <returns>A hash code derived from identity, policies, value, contract, ordered cases, and fallback.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Selection);
        hash.Add(Completeness);
        hash.Add(Value);
        hash.Add(Contract);
        foreach (var matchCase in Cases)
            hash.Add(matchCase);
        hash.Add(Fallback);
        return hash.ToHashCode();
    }
}

/// <summary>One stable token branch created by a Process Fork.</summary>
public sealed record ProcessForkBranch
{
    /// <summary>Creates a Fork branch.</summary>
    /// <param name="id">Stable branch identity retained through its owning Join.</param>
    /// <param name="start">Stable edge that starts the branch token.</param>
    [JsonConstructor]
    public ProcessForkBranch(ExecutionNodeId id, ProcessEdge start)
    {
        Id = id;
        Start = start;
    }

    /// <summary>Stable branch identity retained through its owning Join.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Stable edge that starts the branch token.</summary>
    public ProcessEdge Start { get; }
}

/// <summary>Creates a normalized finite set of parallel branch tokens owned by one reciprocal Join.</summary>
/// <remarks>
/// In Process IR v1, every finite branch exit must reach the reciprocal Join without passing through another Join.
/// Recurrence inside a branch is valid when every cycle crosses a durable boundary and every recurrent region retains
/// a structural exit to the reciprocal Join. Free-activation cycles and closed recurrent branches are invalid.
/// </remarks>
public sealed record ForkProcessNode : ProcessNode
{
    /// <summary>Creates a parallel Fork node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="branches">Set-like stable branch declarations.</param>
    /// <param name="join">Stable identity of the Join that owns convergence of these branches.</param>
    [JsonConstructor]
    public ForkProcessNode(
        ExecutionNodeId id,
        ImmutableArray<ProcessForkBranch> branches,
        ExecutionNodeId join)
        : base(id)
    {
        Branches = ProcessIrCollections.NormalizeSet(branches, CompareForkBranches);
        Join = join;
    }

    /// <summary>Branch declarations in deterministic stable-identity order.</summary>
    public ImmutableArray<ProcessForkBranch> Branches { get; }

    /// <summary>Stable identity of the Join that owns convergence of these branches.</summary>
    public ExecutionNodeId Join { get; }

    /// <summary>Compares Fork nodes by complete normalized persisted semantics.</summary>
    /// <param name="other">Fork node to compare with this value.</param>
    /// <returns><see langword="true"/> when identity, reciprocal Join, and every normalized branch are equal.</returns>
    public bool Equals(ForkProcessNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Join == other.Join
        && Branches.SequenceEqual(other.Branches);

    /// <summary>Returns a structural hash code for complete normalized Fork semantics.</summary>
    /// <returns>A hash code derived from identity, reciprocal Join, and normalized branches.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Join);
        foreach (var branch in Branches)
            hash.Add(branch);
        return hash.ToHashCode();
    }

    static int CompareForkBranches(ProcessForkBranch? left, ProcessForkBranch? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;
        return StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
    }
}

/// <summary>Converges tokens from one reciprocal Fork under an explicit deterministic policy.</summary>
public sealed record JoinProcessNode : ProcessNode
{
    /// <summary>Creates a parallel Join node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="fork">Stable identity of the reciprocal Fork.</param>
    /// <param name="policy">Explicit completion, failure, cancellation, ordering, and tie-break policy.</param>
    /// <param name="next">Stable edge selected after the Join is satisfied.</param>
    [JsonConstructor]
    public JoinProcessNode(
        ExecutionNodeId id,
        ExecutionNodeId fork,
        ProcessJoinPolicy policy,
        ProcessEdge next)
        : base(id)
    {
        Fork = fork;
        Policy = policy;
        Next = next;
    }

    /// <summary>Stable identity of the reciprocal Fork.</summary>
    public ExecutionNodeId Fork { get; }

    /// <summary>Explicit completion, failure, cancellation, ordering, and tie-break policy.</summary>
    public ProcessJoinPolicy Policy { get; }

    /// <summary>Stable edge selected after the Join is satisfied.</summary>
    public ProcessEdge Next { get; }
}

/// <summary>Durably registers a closed set of clauses and selects exactly one deterministic winner.</summary>
public sealed record AwaitMatchProcessNode : ProcessNode
{
    /// <summary>Creates a durable AwaitMatch node.</summary>
    /// <param name="id">Stable node and durable wait identity basis.</param>
    /// <param name="arbitration">Explicit exclusive winner-selection semantics.</param>
    /// <param name="clauses">Set-like typed interaction and timer clauses.</param>
    /// <param name="lateInput">Disposition for input arriving after a winner completed the wait.</param>
    /// <param name="staleInput">Disposition for input targeting incompatible continuation state.</param>
    /// <param name="duplicateInput">Disposition for repeated logical input.</param>
    /// <param name="missingTarget">Disposition when no compatible durable wait target can be resolved.</param>
    /// <param name="retentionHorizon">Minimum duration for which the wait remains addressable.</param>
    [JsonConstructor]
    public AwaitMatchProcessNode(
        ExecutionNodeId id,
        ProcessAwaitArbitration arbitration,
        ImmutableArray<ProcessAwaitClause> clauses,
        ProcessAwaitInputDisposition lateInput,
        ProcessAwaitInputDisposition staleInput,
        ProcessAwaitInputDisposition duplicateInput,
        ProcessAwaitMissingTargetDisposition missingTarget,
        TimeSpan retentionHorizon)
        : base(id)
    {
        Arbitration = arbitration;
        Clauses = ProcessIrCollections.NormalizeSet(clauses, CompareAwaitClauses);
        LateInput = lateInput;
        StaleInput = staleInput;
        DuplicateInput = duplicateInput;
        MissingTarget = missingTarget;
        RetentionHorizon = retentionHorizon;
    }

    /// <summary>Explicit exclusive winner-selection semantics.</summary>
    public ProcessAwaitArbitration Arbitration { get; }

    /// <summary>Typed clauses in descending priority and then ordinal stable-identity order.</summary>
    public ImmutableArray<ProcessAwaitClause> Clauses { get; }

    /// <summary>Disposition for input arriving after a winner completed the wait.</summary>
    public ProcessAwaitInputDisposition LateInput { get; }

    /// <summary>Disposition for input targeting incompatible continuation state.</summary>
    public ProcessAwaitInputDisposition StaleInput { get; }

    /// <summary>Disposition for repeated logical input.</summary>
    public ProcessAwaitInputDisposition DuplicateInput { get; }

    /// <summary>Disposition when no compatible durable wait target can be resolved.</summary>
    public ProcessAwaitMissingTargetDisposition MissingTarget { get; }

    /// <summary>Minimum positive duration for which the wait remains addressable.</summary>
    public TimeSpan RetentionHorizon { get; }

    /// <summary>Compares AwaitMatch nodes by complete normalized persisted semantics.</summary>
    /// <param name="other">AwaitMatch node to compare with this value.</param>
    /// <returns><see langword="true"/> when identity, policies, retention, and normalized clauses are equal.</returns>
    public bool Equals(AwaitMatchProcessNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Arbitration == other.Arbitration
        && LateInput == other.LateInput
        && StaleInput == other.StaleInput
        && DuplicateInput == other.DuplicateInput
        && MissingTarget == other.MissingTarget
        && RetentionHorizon == other.RetentionHorizon
        && Clauses.SequenceEqual(other.Clauses);

    /// <summary>Returns a structural hash code for complete normalized AwaitMatch semantics.</summary>
    /// <returns>A hash code derived from identity, policies, retention, and normalized clauses.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Arbitration);
        hash.Add(LateInput);
        hash.Add(StaleInput);
        hash.Add(DuplicateInput);
        hash.Add(MissingTarget);
        hash.Add(RetentionHorizon);
        foreach (var clause in Clauses)
            hash.Add(clause);
        return hash.ToHashCode();
    }

    static int CompareAwaitClauses(ProcessAwaitClause? left, ProcessAwaitClause? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var comparison = right.Priority.CompareTo(left.Priority);
        return comparison != 0
            ? comparison
            : StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
    }
}

/// <summary>Reaches a durable boundary until one explicitly computed absolute due instant.</summary>
public sealed record TimerProcessNode : ProcessNode
{
    /// <summary>Creates an absolute-time Timer node.</summary>
    /// <param name="id">Stable node and durable timer identity basis.</param>
    /// <param name="dueAt">Portable expression yielding the absolute due instant.</param>
    /// <param name="next">Stable edge selected after the timer is durably admitted as due.</param>
    [JsonConstructor]
    public TimerProcessNode(ExecutionNodeId id, Expr dueAt, ProcessEdge next)
        : base(id)
    {
        DueAt = dueAt;
        Next = next;
    }

    /// <summary>Portable expression yielding the absolute due instant.</summary>
    public Expr DueAt { get; }

    /// <summary>Stable edge selected after the timer is durably admitted as due.</summary>
    public ProcessEdge Next { get; }
}

/// <summary>Emits one typed Reply that discharges an exact Request terminal outcome.</summary>
public sealed record ReplyProcessNode : ProcessNode
{
    /// <summary>Creates a Reply node.</summary>
    /// <param name="id">Stable node and logical emission identity basis.</param>
    /// <param name="contract">Exact typed Reply contract.</param>
    /// <param name="request">Definitely visible inbound Request obligation being discharged.</param>
    /// <param name="payload">Portable typed Reply payload expression.</param>
    /// <param name="next">Stable edge selected after the Reply intent is accepted.</param>
    [JsonConstructor]
    public ReplyProcessNode(
        ExecutionNodeId id,
        ReplyContractReference contract,
        RequestObligationBindingId request,
        Expr payload,
        ProcessEdge next)
        : base(id)
    {
        Contract = contract;
        Request = request;
        Payload = payload;
        Next = next;
    }

    /// <summary>Exact typed Reply contract.</summary>
    public ReplyContractReference Contract { get; }

    /// <summary>Definitely visible inbound Request obligation being discharged.</summary>
    public RequestObligationBindingId Request { get; }

    /// <summary>Portable typed Reply payload expression.</summary>
    public Expr Payload { get; }

    /// <summary>Stable edge selected after the Reply intent is accepted.</summary>
    public ProcessEdge Next { get; }
}

/// <summary>Ends the current finite activation at an explicit durable boundary.</summary>
public sealed record DurableCutProcessNode : ProcessNode
{
    /// <summary>Creates an explicit durable-cut node.</summary>
    /// <param name="id">Stable durable-cut node identity.</param>
    /// <param name="resume">Stable edge at which a later activation resumes.</param>
    [JsonConstructor]
    public DurableCutProcessNode(ExecutionNodeId id, ProcessEdge resume)
        : base(id) => Resume = resume;

    /// <summary>Stable edge at which a later activation resumes.</summary>
    public ProcessEdge Resume { get; }
}

/// <summary>Terminates the Process successfully with a typed result expression.</summary>
public sealed record ReturnProcessNode : ProcessNode
{
    /// <summary>Creates a successful terminal Process node.</summary>
    /// <param name="id">Stable terminal node identity.</param>
    /// <param name="result">Portable expression producing the typed Process result.</param>
    [JsonConstructor]
    public ReturnProcessNode(ExecutionNodeId id, Expr result)
        : base(id) => Result = result;

    /// <summary>Portable expression producing the typed Process result.</summary>
    public Expr Result { get; }
}

/// <summary>Terminates the Process as failed with a typed result expression.</summary>
public sealed record FailProcessNode : ProcessNode
{
    /// <summary>Creates a failed terminal Process node.</summary>
    /// <param name="id">Stable terminal node identity.</param>
    /// <param name="result">Portable expression producing the typed Process failure result.</param>
    [JsonConstructor]
    public FailProcessNode(ExecutionNodeId id, Expr result)
        : base(id) => Result = result;

    /// <summary>Portable expression producing the typed Process failure result.</summary>
    public Expr Result { get; }
}
