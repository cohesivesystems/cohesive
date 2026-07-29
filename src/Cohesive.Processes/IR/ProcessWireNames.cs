namespace Cohesive.Processes.IR;

/// <summary>Stable wire names used by canonical Process IR v1.</summary>
/// <remarks>
/// These constants are the single authority for persisted discriminators. CLR type names are not part of the
/// portable Process contract and may change without changing its durable representation.
/// </remarks>
public static class ProcessWireNames
{
    /// <summary>Shared execution-definition kind for Process documents.</summary>
    public const string DefinitionKind = "process";

    /// <summary>JSON property carrying a Process-node discriminator.</summary>
    public const string NodeDiscriminator = "$node";

    /// <summary>Transition-invocation node discriminator.</summary>
    public const string InvokeTransitionNode = "invokeTransition";

    /// <summary>Relation-evaluation node discriminator.</summary>
    public const string EvaluateRelationNode = "evaluateRelation";

    /// <summary>Request-emission node discriminator.</summary>
    public const string RequestNode = "request";

    /// <summary>Domain-event emission node discriminator.</summary>
    public const string EmitEventNode = "emitEvent";

    /// <summary>Signal-send node discriminator.</summary>
    public const string SendSignalNode = "sendSignal";

    /// <summary>Predicate-choice node discriminator.</summary>
    public const string ChoiceNode = "choice";

    /// <summary>Exact-pattern match node discriminator.</summary>
    public const string MatchNode = "match";

    /// <summary>Parallel-token fork node discriminator.</summary>
    public const string ForkNode = "fork";

    /// <summary>Parallel-token join node discriminator.</summary>
    public const string JoinNode = "join";

    /// <summary>Durable exclusive input-arbitration node discriminator.</summary>
    public const string AwaitMatchNode = "awaitMatch";

    /// <summary>Absolute-time timer node discriminator.</summary>
    public const string TimerNode = "timer";

    /// <summary>Reply-emission node discriminator.</summary>
    public const string ReplyNode = "reply";

    /// <summary>Explicit durable-cut node discriminator.</summary>
    public const string DurableCutNode = "durableCut";

    /// <summary>Successful terminal Process node discriminator.</summary>
    public const string ReturnNode = "return";

    /// <summary>Failed terminal Process node discriminator.</summary>
    public const string FailNode = "fail";

    /// <summary>JSON property carrying an AwaitMatch-clause discriminator.</summary>
    public const string AwaitClauseDiscriminator = "$clause";

    /// <summary>Typed interaction AwaitMatch-clause discriminator.</summary>
    public const string InteractionAwaitClause = "interaction";

    /// <summary>Absolute-time AwaitMatch timer-clause discriminator.</summary>
    public const string TimerAwaitClause = "timer";
}
