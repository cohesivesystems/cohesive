namespace Cohesive.Processes.Model;

/// <summary>
/// Process execution capability.
/// </summary>
public enum ProcessCapability
{
    /// <summary>Represents the pure evaluation option.</summary>
    PureEvaluation = 0,
    /// <summary>Represents the state read option.</summary>
    StateRead = 1,
    /// <summary>Represents the state mutation option.</summary>
    StateMutation = 2,
    /// <summary>Represents the transactions option.</summary>
    Transactions = 3,
    /// <summary>Represents the external io option.</summary>
    ExternalIO = 4,
    /// <summary>Represents the outbox option.</summary>
    Outbox = 5
}
