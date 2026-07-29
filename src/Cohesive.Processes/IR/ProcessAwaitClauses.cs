using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Processes.IR;

/// <summary>Closed persisted union of typed durable AwaitMatch clauses.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = ProcessWireNames.AwaitClauseDiscriminator)]
[JsonDerivedType(typeof(ProcessAwaitInteractionClause), ProcessWireNames.InteractionAwaitClause)]
[JsonDerivedType(typeof(ProcessAwaitTimerClause), ProcessWireNames.TimerAwaitClause)]
public abstract record ProcessAwaitClause
{
    /// <summary>Creates a durable AwaitMatch clause.</summary>
    /// <param name="id">Stable clause identity used for deterministic arbitration and continuation evidence.</param>
    /// <param name="priority">Explicit priority; greater values win before clause-identity arbitration.</param>
    /// <param name="continuation">Typed continuation selected when this clause wins.</param>
    private protected ProcessAwaitClause(
        ExecutionNodeId id,
        int priority,
        ProcessContinuation continuation)
    {
        Id = id;
        Priority = priority;
        Continuation = continuation;
    }

    /// <summary>Stable clause identity used for deterministic arbitration and continuation evidence.</summary>
    public ExecutionNodeId Id { get; }

    /// <summary>Explicit priority; greater values win before clause-identity arbitration.</summary>
    public int Priority { get; }

    /// <summary>Typed continuation selected when this clause wins.</summary>
    public ProcessContinuation Continuation { get; }
}

/// <summary>One typed interaction input eligible to win a durable AwaitMatch.</summary>
public sealed record ProcessAwaitInteractionClause : ProcessAwaitClause
{
    /// <summary>Creates a typed interaction AwaitMatch clause.</summary>
    /// <param name="id">Stable clause identity.</param>
    /// <param name="contract">Exact typed interaction contract admitted by the clause.</param>
    /// <param name="input">Typed binding made available to the optional guard and selected continuation.</param>
    /// <param name="requestObligation">
    /// Binding retaining the admitted Request identity when <paramref name="contract"/> is a Request; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <param name="guard">Optional portable Boolean eligibility guard evaluated against visible bindings.</param>
    /// <param name="priority">Explicit priority; greater values win before clause-identity arbitration.</param>
    /// <param name="continuation">Typed continuation selected when this clause wins.</param>
    [JsonConstructor]
    public ProcessAwaitInteractionClause(
        ExecutionNodeId id,
        InteractionContractReference contract,
        ProcessOutputBinding input,
        ProcessRequestObligationBinding? requestObligation,
        Expr? guard,
        int priority,
        ProcessContinuation continuation)
        : base(id, priority, continuation)
    {
        Contract = contract;
        Input = input;
        RequestObligation = requestObligation;
        Guard = guard;
    }

    /// <summary>Exact typed interaction contract admitted by the clause.</summary>
    public InteractionContractReference Contract { get; }

    /// <summary>Typed input binding made visible while evaluating and continuing this clause.</summary>
    public ProcessOutputBinding Input { get; }

    /// <summary>
    /// Binding retaining the admitted logical Request obligation, or <see langword="null"/> for non-Request input.
    /// </summary>
    public ProcessRequestObligationBinding? RequestObligation { get; }

    /// <summary>Optional portable Boolean eligibility guard.</summary>
    public Expr? Guard { get; }
}

/// <summary>One absolute-time timer eligible to win a durable AwaitMatch.</summary>
public sealed record ProcessAwaitTimerClause : ProcessAwaitClause
{
    /// <summary>Creates an absolute-time AwaitMatch timer clause.</summary>
    /// <param name="id">Stable clause identity.</param>
    /// <param name="dueAt">Portable expression yielding the absolute instant at which the clause becomes eligible.</param>
    /// <param name="priority">Explicit priority; greater values win before clause-identity arbitration.</param>
    /// <param name="continuation">Continuation selected when this timer clause wins.</param>
    [JsonConstructor]
    public ProcessAwaitTimerClause(
        ExecutionNodeId id,
        Expr dueAt,
        int priority,
        ProcessContinuation continuation)
        : base(id, priority, continuation) => DueAt = dueAt;

    /// <summary>Portable expression yielding the absolute due instant.</summary>
    public Expr DueAt { get; }
}
