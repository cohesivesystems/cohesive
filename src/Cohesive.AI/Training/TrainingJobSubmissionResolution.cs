namespace Cohesive.AI.Training;

/// <summary>
/// Closed result of reconciling one exact logical training submission with a provider.
/// </summary>
public abstract record TrainingJobSubmissionResolution
{
    private TrainingJobSubmissionResolution()
    {
    }

    /// <summary>The provider has accepted the exact logical submission.</summary>
    /// <param name="TrainingJob">Provider job bound to the submission identity and request fingerprint.</param>
    public sealed record Accepted(TrainingJobReference TrainingJob) : TrainingJobSubmissionResolution;

    /// <summary>The provider authoritatively reports no job for the logical submission identity.</summary>
    public sealed record ConfirmedAbsent : TrainingJobSubmissionResolution;

    /// <summary>The provider could not establish whether the logical submission was accepted.</summary>
    /// <param name="ErrorType">Stable provider or adapter error classification.</param>
    /// <param name="ErrorMessage">Human-readable reconciliation failure description.</param>
    /// <param name="IsTransient">Whether another reconciliation attempt may succeed without configuration changes.</param>
    public sealed record Unresolved(
        string ErrorType,
        string ErrorMessage,
        bool IsTransient) : TrainingJobSubmissionResolution;
}
