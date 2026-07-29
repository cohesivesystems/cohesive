namespace Cohesive.Execution;

/// <summary>Stable wire names for canonical interaction contracts and envelopes.</summary>
/// <remarks>
/// These constants are the single authority for persisted discriminators. CLR type names are not part of the
/// portable contract and may change without changing the wire representation.
/// </remarks>
public static class InteractionWireNames
{
    /// <summary>Shared execution-definition kind for interaction-contract documents.</summary>
    public const string DefinitionKind = "interaction";

    /// <summary>JSON discriminator for interaction contracts and runtime envelopes.</summary>
    public const string InteractionDiscriminator = "$interaction";

    /// <summary>Domain-event interaction discriminator.</summary>
    public const string DomainEvent = "domainEvent";

    /// <summary>Request interaction discriminator.</summary>
    public const string Request = "request";

    /// <summary>Signal interaction discriminator.</summary>
    public const string Signal = "signal";

    /// <summary>Reply interaction discriminator.</summary>
    public const string Reply = "reply";

    /// <summary>JSON discriminator for exact typed interaction-contract references.</summary>
    public const string ContractDiscriminator = "$contract";

    /// <summary>JSON discriminator for request terminal outcomes.</summary>
    public const string OutcomeDiscriminator = "$outcome";

    /// <summary>Successful typed result outcome discriminator.</summary>
    public const string ResultOutcome = "result";

    /// <summary>Typed terminal failure outcome discriminator.</summary>
    public const string FailureOutcome = "failure";

    /// <summary>Timeout outcome discriminator.</summary>
    public const string TimeoutOutcome = "timeout";

    /// <summary>Cancellation outcome discriminator.</summary>
    public const string CancellationOutcome = "cancellation";

    /// <summary>JSON discriminator for semantic interaction origins.</summary>
    public const string OriginDiscriminator = "$origin";

    /// <summary>Transition-node origin discriminator.</summary>
    public const string TransitionOrigin = "transition";

    /// <summary>Process-token origin discriminator.</summary>
    public const string ProcessOrigin = "process";

    /// <summary>JSON discriminator for addressed interaction targets.</summary>
    public const string TargetDiscriminator = "$target";

    /// <summary>Process-token target discriminator.</summary>
    public const string ProcessTokenTarget = "processToken";

    /// <summary>Declared Transition-continuation target discriminator.</summary>
    public const string TransitionTarget = "transition";
}
