using Cohesive.AI.Training;

namespace Cohesive.AI.Registry;

/// <summary>
/// Provides model metadata registration and promotion operations.
/// </summary>
public interface IModelRegistry
{
    /// <summary>
    /// Registers training output as a model version.
    /// </summary>
    /// <param name="result">Training result to register.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    ValueTask RegisterAsync(TrainingResult result, CancellationToken ct = default);

    /// <summary>
    /// Gets metadata for a specific model version.
    /// </summary>
    /// <param name="modelName">Logical model name.</param>
    /// <param name="version">Version identifier.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The matching metadata or <see langword="null"/> when not found.</returns>
    ValueTask<ModelMetadata?> GetAsync(string modelName, string version, CancellationToken ct = default);

    /// <summary>
    /// Gets metadata for the current production version of a model.
    /// </summary>
    /// <param name="modelName">Logical model name.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The production metadata or <see langword="null"/> when none is assigned.</returns>
    ValueTask<ModelMetadata?> GetProductionAsync(string modelName, CancellationToken ct = default);

    /// <summary>
    /// Promotes a model version to production.
    /// </summary>
    /// <param name="modelName">Logical model name.</param>
    /// <param name="version">Version identifier to promote.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    ValueTask PromoteAsync(string modelName, string version, CancellationToken ct = default);
}
