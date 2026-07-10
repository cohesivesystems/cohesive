using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Stable identifier for a shape scoped by the graph that contains it.
/// </summary>
public readonly record struct QualifiedShapeId
{
    /// <summary>
    /// Creates a qualified shape identifier.
    /// </summary>
    /// <param name="graphId">Identifier for the graph that contains the shape.</param>
    /// <param name="shapeId">Identifier for the shape within the graph.</param>
    [JsonConstructor]
    public QualifiedShapeId(GraphId graphId, ShapeId shapeId)
    {
        GraphId = RequireGraphId(graphId);
        ShapeId = RequireShapeId(shapeId);
    }

    /// <summary>
    /// Identifier for the graph that contains the shape.
    /// </summary>
    public GraphId GraphId { get; }

    /// <summary>
    /// Identifier for the shape within the graph.
    /// </summary>
    public ShapeId ShapeId { get; }

    /// <inheritdoc />
    public override string ToString() => $"{GraphId.Value}:{ShapeId.Value}";

    static GraphId RequireGraphId(GraphId graphId) =>
        string.IsNullOrWhiteSpace(graphId.Value)
            ? throw new ArgumentException("Graph id is required.", nameof(graphId))
            : graphId;

    static ShapeId RequireShapeId(ShapeId shapeId) =>
        string.IsNullOrWhiteSpace(shapeId.Value)
            ? throw new ArgumentException("Shape id is required.", nameof(shapeId))
            : shapeId;
}

/// <summary>
/// Runtime handle for a shape scoped by the graph object that contains it.
/// </summary>
public readonly record struct GraphShapeId
{
    /// <summary>
    /// Creates a graph-scoped shape identifier.
    /// </summary>
    /// <param name="graph">Graph that contains the shape.</param>
    /// <param name="shapeId">Identifier for the shape within the graph.</param>
    [JsonConstructor]
    public GraphShapeId(ShapeGraph graph, ShapeId shapeId)
    {
        Graph = Guard.RequireNotNull(graph);
        ShapeId = RequireShapeId(shapeId);

        if (!Graph.TryGetShape(ShapeId, out _))
            throw new ArgumentException(
                $"Shape graph '{Graph.Id.Value}' does not contain shape '{ShapeId.Value}'.",
                nameof(shapeId));
    }

    /// <summary>
    /// Graph that contains the shape.
    /// </summary>
    public ShapeGraph Graph { get; }

    /// <summary>
    /// Identifier for the shape within the graph.
    /// </summary>
    public ShapeId ShapeId { get; }

    /// <summary>
    /// Qualified identifier formed from the graph id and shape id.
    /// </summary>
    [JsonIgnore]
    public QualifiedShapeId QualifiedId => new(Graph.Id, ShapeId);

    /// <summary>Extracts the qualified shape identifier from a graph shape identifier.</summary>
    public static implicit operator QualifiedShapeId(GraphShapeId value) => value.QualifiedId;

    /// <inheritdoc />
    public override string ToString() => QualifiedId.ToString();

    static ShapeId RequireShapeId(ShapeId shapeId) =>
        string.IsNullOrWhiteSpace(shapeId.Value)
            ? throw new ArgumentException("Shape id is required.", nameof(shapeId))
            : shapeId;
}
