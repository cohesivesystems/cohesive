namespace Cohesive.AI.Inference;

/// <summary>
/// Describes a batch request for graph scoring.
/// </summary>
/// <param name="Graphs">Graphs to score.</param>
public readonly record struct GraphBatchRequest(
    IReadOnlyList<GraphInstance> Graphs
    );