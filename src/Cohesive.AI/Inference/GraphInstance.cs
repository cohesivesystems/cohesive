namespace Cohesive.AI.Inference;

/// <summary>
/// Represents a single graph instance used for graph scoring.
/// </summary>
public sealed class GraphInstance
{
    /// <summary>
    /// Gets node feature tensor values.
    /// </summary>
    public ReadOnlyMemory<float> NodeFeatures { get; init; }

    /// <summary>
    /// Gets edge indices in COO format.
    /// </summary>
    public ReadOnlyMemory<int> EdgeIndex { get; init; } // COO format

    /// <summary>
    /// Gets optional edge feature tensor values.
    /// </summary>
    public ReadOnlyMemory<float>? EdgeFeatures { get; init; }
}