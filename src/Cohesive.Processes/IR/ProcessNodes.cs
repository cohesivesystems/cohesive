using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;

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
[JsonDerivedType(typeof(InvokeProcessProcessNode), ProcessWireNames.InvokeProcessNode)]
[JsonDerivedType(typeof(ForEachPartitionProcessNode), ProcessWireNames.ForEachPartitionNode)]
[JsonDerivedType(typeof(RepeatAcrossActivationProcessNode), ProcessWireNames.RepeatAcrossActivationNode)]
[JsonDerivedType(typeof(CancellationFinalizerProcessNode), ProcessWireNames.CancellationFinalizerNode)]
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

/// <summary>Declares one exact authored child Process that must acknowledge cooperative cancellation.</summary>
/// <remarks>
/// This is a lifecycle declaration in the closed Process construct union, not an ordinary graph node. It cannot be
/// the Process entry or the target of a control-flow edge. Its stable identity anchors child invocation, Request
/// capability, source attribution, and replay evidence when cancellation supersedes the normal graph.
/// </remarks>
public sealed record CancellationFinalizerProcessNode : ProcessNode
{
    /// <summary>Creates an exact cancellation-finalizer declaration.</summary>
    /// <param name="id">Stable lifecycle declaration and child-invocation identity basis.</param>
    /// <param name="process">Exact cancellation-finalizer Process definition revision and fingerprint.</param>
    /// <param name="contract">Exact Request contract used to durably start and join the finalizer.</param>
    /// <param name="outcomeMapping">Total mapping from finalizer child terminal status to Request outcomes.</param>
    [JsonConstructor]
    public CancellationFinalizerProcessNode(
        ExecutionNodeId id,
        ExecutionDefinitionReference process,
        RequestContractReference contract,
        ProcessChildOutcomeMapping outcomeMapping)
        : base(id)
    {
        Process = process;
        Contract = contract;
        OutcomeMapping = outcomeMapping;
    }

    /// <summary>Exact cancellation-finalizer Process definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Process { get; }

    /// <summary>Exact Request contract used to durably start and join the finalizer.</summary>
    public RequestContractReference Contract { get; }

    /// <summary>Total mapping from finalizer child terminal status to exact Request outcomes.</summary>
    public ProcessChildOutcomeMapping OutcomeMapping { get; }
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
        Outcomes = ProcessRequestSemantics.NormalizeOutcomes(outcomes);
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
    /// <param name="capacityDomain">Optional declared capacity domain consumed while the branch is active.</param>
    [JsonConstructor]
    public ProcessForkBranch(
        ExecutionNodeId id,
        ProcessEdge start,
        string? capacityDomain = null)
    {
        Id = id;
        Start = start;
        CapacityDomain = capacityDomain;
    }

    /// <summary>Stable branch identity retained through its owning Join.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Stable edge that starts the branch token.</summary>
    public ProcessEdge Start { get; }

    /// <summary>Optional declared capacity domain consumed while the branch is active.</summary>
    public string? CapacityDomain { get; }
}

/// <summary>Creates a normalized finite set of parallel branch tokens owned by one reciprocal Join.</summary>
/// <remarks>
/// In Process IR v2, every finite branch exit must reach the reciprocal Join without passing through another Join.
/// Recurrence inside a branch is valid when every cycle crosses a durable boundary and every recurrent region retains
/// a structural exit to the reciprocal Join. Free-activation cycles and closed recurrent branches are invalid.
/// </remarks>
public sealed record ForkProcessNode : ProcessNode
{
    /// <summary>Creates a parallel Fork node.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="branches">Set-like stable branch declarations.</param>
    /// <param name="join">Stable identity of the Join that owns convergence of these branches.</param>
    public ForkProcessNode(
        ExecutionNodeId id,
        ImmutableArray<ProcessForkBranch> branches,
        ExecutionNodeId join)
        : this(
            id,
            branches,
            join,
            ProcessWorkLimits.EagerFiniteSet(branches.IsDefault ? 0 : branches.Length),
            capacityDomains: [])
    {
    }

    /// <summary>Creates a parallel Fork node with explicit durable admission limits.</summary>
    /// <param name="id">Stable node identity.</param>
    /// <param name="branches">Set-like stable branch declarations.</param>
    /// <param name="join">Stable identity of the Join that owns convergence of these branches.</param>
    /// <param name="limits">Hard finite branch, per-activation start, and parallelism limits.</param>
    /// <param name="capacityDomains">Optional named capacity limits consumed by assigned branches.</param>
    [JsonConstructor]
    public ForkProcessNode(
        ExecutionNodeId id,
        ImmutableArray<ProcessForkBranch> branches,
        ExecutionNodeId join,
        ProcessWorkLimits limits,
        ImmutableArray<ProcessCapacityDomainLimit> capacityDomains)
        : base(id)
    {
        Branches = ProcessIrCollections.NormalizeSet(branches, CompareForkBranches);
        Join = join;
        Limits = limits;
        CapacityDomains = ProcessIrCollections.NormalizeCapacityDomains(capacityDomains);
    }

    /// <summary>Branch declarations in deterministic stable-identity order.</summary>
    public ImmutableArray<ProcessForkBranch> Branches { get; }

    /// <summary>Stable identity of the Join that owns convergence of these branches.</summary>
    public ExecutionNodeId Join { get; }

    /// <summary>Hard finite work and admission limits.</summary>
    public ProcessWorkLimits Limits { get; }

    /// <summary>Capacity-domain limits in deterministic ordinal identity order.</summary>
    public ImmutableArray<ProcessCapacityDomainLimit> CapacityDomains { get; }

    /// <summary>Compares Fork nodes by complete normalized persisted semantics.</summary>
    /// <param name="other">Fork node to compare with this value.</param>
    /// <returns><see langword="true"/> when identity, reciprocal Join, and every normalized branch are equal.</returns>
    public bool Equals(ForkProcessNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Join == other.Join
        && Limits == other.Limits
        && CapacityDomains.SequenceEqual(other.CapacityDomains)
        && Branches.SequenceEqual(other.Branches);

    /// <summary>Returns a structural hash code for complete normalized Fork semantics.</summary>
    /// <returns>A hash code derived from identity, reciprocal Join, and normalized branches.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Join);
        hash.Add(Limits);
        foreach (var domain in CapacityDomains)
            hash.Add(domain);
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

/// <summary>One portable result expression owned by a reciprocal Fork branch.</summary>
public sealed record ProcessJoinBranchResult
{
    /// <summary>Creates a selected-branch result projection.</summary>
    /// <param name="branch">Exact reciprocal Fork branch identity.</param>
    /// <param name="result">Portable result expression evaluated in the completed branch token scope.</param>
    [JsonConstructor]
    public ProcessJoinBranchResult(ExecutionNodeId branch, Expr result)
    {
        Branch = branch;
        Result = result;
    }

    /// <summary>Exact reciprocal Fork branch identity.</summary>
    public ExecutionNodeId Branch { get; }

    /// <summary>Portable result expression evaluated only for a branch selected by the Join policy.</summary>
    public Expr Result { get; }
}

/// <summary>
/// Typed projection of the branch results selected by an <c>Any</c> or <c>RequiredCount</c> Join.
/// </summary>
/// <remarks>
/// The projection does not publish arbitrary branch-local bindings. Each branch supplies one explicit portable
/// expression, and the interpreter evaluates only the deterministically selected branches before populating the
/// single output binding.
/// </remarks>
public sealed record ProcessJoinResultProjection
{
    /// <summary>Creates a partial-Join result projection.</summary>
    /// <param name="output">Typed binding populated when the Join resolves.</param>
    /// <param name="resultContract">Common portable contract of every selected branch result.</param>
    /// <param name="branches">Set-like result expressions keyed by reciprocal Fork branch identity.</param>
    [JsonConstructor]
    public ProcessJoinResultProjection(
        ProcessOutputBinding output,
        ValueContract resultContract,
        ImmutableArray<ProcessJoinBranchResult> branches)
    {
        Output = output;
        ResultContract = resultContract;
        Branches = ProcessIrCollections.NormalizeSet(branches, CompareBranches);
    }

    /// <summary>Typed binding populated with the selected winner or winner collection.</summary>
    public ProcessOutputBinding Output { get; }

    /// <summary>Common portable contract of every selected branch result expression.</summary>
    public ValueContract ResultContract { get; }

    /// <summary>Branch result expressions in deterministic branch-identity order.</summary>
    public ImmutableArray<ProcessJoinBranchResult> Branches { get; }

    /// <summary>Compares projections by output, result contract, and normalized branch expressions.</summary>
    /// <param name="other">Projection to compare with this value.</param>
    /// <returns><see langword="true"/> when the complete normalized projections are equal.</returns>
    public bool Equals(ProcessJoinResultProjection? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Output == other.Output
        && ResultContract == other.ResultContract
        && Branches.SequenceEqual(other.Branches);

    /// <summary>Returns a structural hash for the complete normalized projection.</summary>
    /// <returns>A hash derived from the output, result contract, and branch projections.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Output);
        hash.Add(ResultContract);
        foreach (var branch in Branches)
        {
            hash.Add(branch);
        }

        return hash.ToHashCode();
    }

    static int CompareBranches(ProcessJoinBranchResult? left, ProcessJoinBranchResult? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        return StringComparer.Ordinal.Compare(left.Branch.Value, right.Branch.Value);
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
    /// <param name="result">Optional typed projection of selected partial-Join branch results.</param>
    [JsonConstructor]
    public JoinProcessNode(
        ExecutionNodeId id,
        ExecutionNodeId fork,
        ProcessJoinPolicy policy,
        ProcessEdge next,
        ProcessJoinResultProjection? result = null)
        : base(id)
    {
        Fork = fork;
        Policy = policy;
        Next = next;
        Result = result;
    }

    /// <summary>Stable identity of the reciprocal Fork.</summary>
    public ExecutionNodeId Fork { get; }

    /// <summary>Explicit completion, failure, cancellation, ordering, and tie-break policy.</summary>
    public ProcessJoinPolicy Policy { get; }

    /// <summary>Stable edge selected after the Join is satisfied.</summary>
    public ProcessEdge Next { get; }

    /// <summary>
    /// Optional typed projection populated from exactly the branches selected by a partial Join.
    /// </summary>
    public ProcessJoinResultProjection? Result { get; }
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

/// <summary>
/// Starts and joins one exact child Process through the canonical durable Request and Reply protocol.
/// </summary>
/// <remarks>
/// Child Process identity is derived by an interpreter from the parent Process instance, attempt, token, node, and
/// occurrence. The exact Request contract defines the durable start/join protocol; this node does not
/// introduce another inbox, outbox, or external-operation model.
/// </remarks>
public sealed record InvokeProcessProcessNode : ProcessNode
{
    /// <summary>Creates a child Process invocation node.</summary>
    /// <param name="id">Stable node and child-invocation identity basis.</param>
    /// <param name="process">Exact child Process definition revision and fingerprint.</param>
    /// <param name="contract">Exact Request contract used to durably start and join the child.</param>
    /// <param name="outcomeMapping">Total mapping from child terminal status to exact Request outcomes.</param>
    /// <param name="input">Portable typed child Process input and Request payload expression.</param>
    /// <param name="purpose">Explicit ordinary-work, compensation, or reconciliation purpose.</param>
    /// <param name="cancellation">Explicit parent-to-child cancellation behavior.</param>
    /// <param name="outcomes">Set-like terminal Request outcome continuations.</param>
    [JsonConstructor]
    public InvokeProcessProcessNode(
        ExecutionNodeId id,
        ExecutionDefinitionReference process,
        RequestContractReference contract,
        ProcessChildOutcomeMapping outcomeMapping,
        Expr input,
        ProcessChildPurpose purpose,
        ProcessChildCancellationPolicy cancellation,
        ImmutableArray<ProcessRequestOutcomeBranch> outcomes)
        : base(id)
    {
        Process = process;
        Contract = contract;
        OutcomeMapping = outcomeMapping;
        Input = input;
        Purpose = purpose;
        Cancellation = cancellation;
        Outcomes = ProcessRequestSemantics.NormalizeOutcomes(outcomes);
    }

    /// <summary>Exact child Process definition revision and fingerprint.</summary>
    public ExecutionDefinitionReference Process { get; }

    /// <summary>Exact Request contract used to durably start and join the child.</summary>
    public RequestContractReference Contract { get; }

    /// <summary>Total mapping from child terminal status to exact Request outcomes.</summary>
    public ProcessChildOutcomeMapping OutcomeMapping { get; }

    /// <summary>Portable typed child Process input and Request payload expression.</summary>
    public Expr Input { get; }

    /// <summary>Explicit semantic purpose of the child invocation.</summary>
    public ProcessChildPurpose Purpose { get; }

    /// <summary>Explicit parent-to-child cancellation behavior.</summary>
    public ProcessChildCancellationPolicy Cancellation { get; }

    /// <summary>Terminal outcome branches in deterministic Request outcome-identity order.</summary>
    public ImmutableArray<ProcessRequestOutcomeBranch> Outcomes { get; }

    /// <summary>Compares child invocation nodes by complete normalized semantic value.</summary>
    /// <param name="other">Child invocation node to compare with this value.</param>
    /// <returns><see langword="true"/> when every child, Request, policy, and outcome semantic is equal.</returns>
    public bool Equals(InvokeProcessProcessNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Process == other.Process
        && Contract == other.Contract
        && OutcomeMapping == other.OutcomeMapping
        && Input == other.Input
        && Purpose == other.Purpose
        && Cancellation == other.Cancellation
        && Outcomes.SequenceEqual(other.Outcomes);

    /// <summary>Returns a structural hash code for complete child invocation semantics.</summary>
    /// <returns>A hash code derived from child identity, Request semantics, policies, and normalized outcomes.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Process);
        hash.Add(Contract);
        hash.Add(OutcomeMapping);
        hash.Add(Input);
        hash.Add(Purpose);
        hash.Add(Cancellation);
        foreach (var outcome in Outcomes)
            hash.Add(outcome);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Starts a finite, bounded set of partition-keyed child Processes and joins their terminal outcomes.
/// </summary>
/// <remarks>
/// This construct owns only coarse partition-to-child coordination. Page, cursor, shard-item, and record-level
/// progress remain owned by the semantic storage block supplying the partitions and child work.
/// </remarks>
public sealed record ForEachPartitionProcessNode : ProcessNode
{
    /// <summary>Creates bounded partition work.</summary>
    /// <param name="id">Stable node and bounded-work occurrence identity basis.</param>
    /// <param name="partitions">Portable expression producing the finite partition collection.</param>
    /// <param name="partition">Typed lexical binding for one partition value.</param>
    /// <param name="progressIdentity">Portable String expression producing a stable identity for each partition.</param>
    /// <param name="process">Exact child Process definition used for every partition.</param>
    /// <param name="contract">Exact Request contract used to durably start and join each child.</param>
    /// <param name="outcomeMapping">Total mapping from child terminal status to exact Request outcomes.</param>
    /// <param name="childInput">Portable child input expression evaluated with <paramref name="partition"/> visible.</param>
    /// <param name="limits">Explicit finite item, activation-start, and parallelism limits.</param>
    /// <param name="failure">Explicit sibling-admission behavior after one child fails.</param>
    /// <param name="capacityIdentity">
    /// Optional portable String expression assigning each partition to a declared capacity domain.
    /// </param>
    /// <param name="capacityDomains">
    /// Canonical identity-to-parallelism limits; empty exactly when <paramref name="capacityIdentity"/> is null.
    /// </param>
    /// <param name="cancellation">Explicit parent-to-child cancellation behavior.</param>
    /// <param name="completed">Edge selected after every partition child completes successfully.</param>
    /// <param name="failed">Edge selected when bounded child work reaches its declared failed outcome.</param>
    [JsonConstructor]
    public ForEachPartitionProcessNode(
        ExecutionNodeId id,
        Expr partitions,
        ProcessOutputBinding partition,
        Expr progressIdentity,
        ExecutionDefinitionReference process,
        RequestContractReference contract,
        ProcessChildOutcomeMapping outcomeMapping,
        Expr childInput,
        ProcessWorkLimits limits,
        ProcessPartitionFailurePolicy failure,
        Expr? capacityIdentity,
        ImmutableArray<ProcessCapacityDomainLimit> capacityDomains,
        ProcessChildCancellationPolicy cancellation,
        ProcessEdge completed,
        ProcessEdge failed)
        : base(id)
    {
        Partitions = partitions;
        Partition = partition;
        ProgressIdentity = progressIdentity;
        Process = process;
        Contract = contract;
        OutcomeMapping = outcomeMapping;
        ChildInput = childInput;
        Limits = limits;
        Failure = failure;
        CapacityIdentity = capacityIdentity;
        CapacityDomains = ProcessIrCollections.NormalizeCapacityDomains(capacityDomains);
        Cancellation = cancellation;
        Completed = completed;
        Failed = failed;
    }

    /// <summary>Portable expression producing the finite partition collection.</summary>
    public Expr Partitions { get; }

    /// <summary>Typed lexical binding for one partition value.</summary>
    public ProcessOutputBinding Partition { get; }

    /// <summary>Portable String expression producing a stable identity for each partition.</summary>
    public Expr ProgressIdentity { get; }

    /// <summary>Exact child Process definition used for every partition.</summary>
    public ExecutionDefinitionReference Process { get; }

    /// <summary>Exact Request contract used to durably start and join each child.</summary>
    public RequestContractReference Contract { get; }

    /// <summary>Total mapping from child terminal status to exact Request outcomes.</summary>
    public ProcessChildOutcomeMapping OutcomeMapping { get; }

    /// <summary>Portable typed child input expression evaluated with <see cref="Partition"/> visible.</summary>
    public Expr ChildInput { get; }

    /// <summary>Explicit finite work limits.</summary>
    public ProcessWorkLimits Limits { get; }

    /// <summary>Explicit sibling-admission behavior after one child fails.</summary>
    public ProcessPartitionFailurePolicy Failure { get; }

    /// <summary>Optional portable String expression assigning each partition to a capacity domain.</summary>
    public Expr? CapacityIdentity { get; }

    /// <summary>Capacity-domain limits in deterministic ordinal identity order.</summary>
    public ImmutableArray<ProcessCapacityDomainLimit> CapacityDomains { get; }

    /// <summary>Explicit parent-to-child cancellation behavior.</summary>
    public ProcessChildCancellationPolicy Cancellation { get; }

    /// <summary>Edge selected after every partition child completes successfully.</summary>
    public ProcessEdge Completed { get; }

    /// <summary>Edge selected when bounded child work reaches its declared failed outcome.</summary>
    public ProcessEdge Failed { get; }

    /// <summary>Compares bounded partition nodes by complete normalized persisted semantics.</summary>
    /// <param name="other">Partition node to compare with this value.</param>
    /// <returns><see langword="true"/> when every expression, child contract, policy, limit, and edge is equal.</returns>
    public bool Equals(ForEachPartitionProcessNode? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Partitions == other.Partitions
        && Partition == other.Partition
        && ProgressIdentity == other.ProgressIdentity
        && Process == other.Process
        && Contract == other.Contract
        && OutcomeMapping == other.OutcomeMapping
        && ChildInput == other.ChildInput
        && Limits == other.Limits
        && Failure == other.Failure
        && CapacityIdentity == other.CapacityIdentity
        && Cancellation == other.Cancellation
        && Completed == other.Completed
        && Failed == other.Failed
        && CapacityDomains.SequenceEqual(other.CapacityDomains);

    /// <summary>Returns a structural hash code for complete bounded partition semantics.</summary>
    /// <returns>A hash code aligned with <see cref="Equals(ForEachPartitionProcessNode?)"/>.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Partitions);
        hash.Add(Partition);
        hash.Add(ProgressIdentity);
        hash.Add(Process);
        hash.Add(Contract);
        hash.Add(OutcomeMapping);
        hash.Add(ChildInput);
        hash.Add(Limits);
        hash.Add(Failure);
        hash.Add(CapacityIdentity);
        foreach (var domain in CapacityDomains)
            hash.Add(domain);
        hash.Add(Cancellation);
        hash.Add(Completed);
        hash.Add(Failed);
        return hash.ToHashCode();
    }

}

/// <summary>
/// Makes one explicitly bounded recurrence decision and crosses a durable activation boundary before repeating.
/// </summary>
public sealed record RepeatAcrossActivationProcessNode : ProcessNode
{
    /// <summary>Creates durable recurrence semantics.</summary>
    /// <param name="id">Stable recurrence node and occurrence identity basis.</param>
    /// <param name="continueWhen">Portable Boolean expression deciding whether another occurrence is required.</param>
    /// <param name="progress">Portable typed value used to prove progress across occurrences.</param>
    /// <param name="progressContract">Exact portable contract of <paramref name="progress"/>.</param>
    /// <param name="policy">Explicit occurrence and unchanged-progress limits.</param>
    /// <param name="repeat">Edge selected at a durable cut when another occurrence is admitted.</param>
    /// <param name="completed">Edge selected when <paramref name="continueWhen"/> is false.</param>
    /// <param name="exhausted">Edge selected when the total occurrence limit is reached.</param>
    /// <param name="stalled">Edge selected when progress remains unchanged beyond its declared limit.</param>
    /// <param name="initialState">Optional portable state supplied to the first occurrence.</param>
    /// <param name="nextState">Optional portable state retained for and supplied to the next occurrence.</param>
    /// <param name="stateContract">Exact portable contract shared by recurrence state expressions.</param>
    /// <param name="stateOutput">Binding populated with the state consumed by each occurrence and returned at termination.</param>
    [JsonConstructor]
    public RepeatAcrossActivationProcessNode(
        ExecutionNodeId id,
        Expr continueWhen,
        Expr progress,
        ValueContract progressContract,
        ProcessRecurrencePolicy policy,
        ProcessEdge repeat,
        ProcessEdge completed,
        ProcessEdge exhausted,
        ProcessEdge stalled,
        Expr? initialState = null,
        Expr? nextState = null,
        ValueContract? stateContract = null,
        ProcessOutputBinding? stateOutput = null)
        : base(id)
    {
        ContinueWhen = continueWhen;
        Progress = progress;
        ProgressContract = progressContract;
        Policy = policy;
        Repeat = repeat;
        Completed = completed;
        Exhausted = exhausted;
        Stalled = stalled;
        InitialState = initialState;
        NextState = nextState;
        StateContract = stateContract;
        StateOutput = stateOutput;
    }

    /// <summary>Portable Boolean expression deciding whether another occurrence is required.</summary>
    public Expr ContinueWhen { get; }

    /// <summary>Portable typed value used to prove progress across occurrences.</summary>
    public Expr Progress { get; }

    /// <summary>Exact portable contract of <see cref="Progress"/>.</summary>
    public ValueContract ProgressContract { get; }

    /// <summary>Explicit occurrence and unchanged-progress limits.</summary>
    public ProcessRecurrencePolicy Policy { get; }

    /// <summary>Edge selected at a durable cut when another occurrence is admitted.</summary>
    public ProcessEdge Repeat { get; }

    /// <summary>Edge selected when <see cref="ContinueWhen"/> is false.</summary>
    public ProcessEdge Completed { get; }

    /// <summary>Edge selected when the total occurrence limit is reached.</summary>
    public ProcessEdge Exhausted { get; }

    /// <summary>Edge selected when progress remains unchanged beyond its declared limit.</summary>
    public ProcessEdge Stalled { get; }

    /// <summary>Optional portable state supplied to the first occurrence.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Expr? InitialState { get; }

    /// <summary>Optional portable state retained for and supplied to the next occurrence.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Expr? NextState { get; }

    /// <summary>Exact portable contract shared by <see cref="InitialState"/> and <see cref="NextState"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ValueContract? StateContract { get; }

    /// <summary>Binding populated with the state consumed by each occurrence and returned at recurrence termination.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProcessOutputBinding? StateOutput { get; }
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
