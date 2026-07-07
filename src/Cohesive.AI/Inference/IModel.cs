namespace Cohesive.AI.Inference;

/// <summary>
/// Represents a named model artifact.
/// </summary>
public interface IModel
{
    /// <summary>
    /// Gets the logical model name.
    /// </summary>
    string ModelName { get; }

    /// <summary>
    /// Gets the model version identifier.
    /// </summary>
    string Version { get; }
}
