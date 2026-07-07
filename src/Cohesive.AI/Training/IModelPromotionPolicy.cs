using Cohesive.AI.Registry;

namespace Cohesive.AI.Training;

/// <summary>
/// Decides whether a newly trained model should become production.
/// </summary>
public interface IModelPromotionPolicy
{
    /// <summary>
    /// Evaluates whether a candidate training result should be promoted.
    /// </summary>
    /// <param name="candidate">Candidate training result.</param>
    /// <param name="currentProduction">Current production model metadata, if any.</param>
    /// <returns><see langword="true"/> when the candidate should be promoted; otherwise <see langword="false"/>.</returns>
    bool ShouldPromote(TrainingResult candidate, ModelMetadata? currentProduction);
}
