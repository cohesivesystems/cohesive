namespace Cohesive.Processes.Model;

/// <summary>
/// Process execution capability.
/// </summary>
public enum ProcessCapability
{
    PureEvaluation = 0,
    StateRead = 1,
    StateMutation = 2,
    Transactions = 3,
    ExternalIO = 4,
    Outbox = 5
}
