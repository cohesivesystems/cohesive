using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Prelude;

namespace Cohesive.CodeGen;

/// <summary>
/// Shape-graph input for code generation.
/// </summary>
public readonly record struct ShapeCodeGenerationRequest
{
    /// <summary>
    /// Creates a shape-generation request.
    /// </summary>
    public ShapeCodeGenerationRequest(ShapeGraph graph)
    {
        Graph = Guard.RequireNotNull(graph);
    }

    /// <summary>
    /// Shape graph to emit.
    /// </summary>
    public ShapeGraph Graph { get; }

    /// <summary>
    /// Creates a request from a single shape and optional named types.
    /// </summary>
    public static ShapeCodeGenerationRequest FromShape(
        Shape shape,
        ImmutableArray<TypeDefinition> namedTypes = default,
        GraphId? graphId = null)
    {
        ArgumentNullException.ThrowIfNull(shape);

        var graph = new ShapeGraph(
            id: graphId ?? GraphId.New(),
            shapes: [shape],
            namedTypes: namedTypes.IsDefault ? [] : namedTypes);

        return new ShapeCodeGenerationRequest(graph);
    }
}
