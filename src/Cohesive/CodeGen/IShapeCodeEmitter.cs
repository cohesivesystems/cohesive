namespace Cohesive.CodeGen;

/// <summary>
/// Emits source artifacts from a shape graph.
/// </summary>
public interface IShapeCodeEmitter
{
    /// <summary>
    /// Output language identifier.
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Emits code from the supplied request.
    /// </summary>
    CodeEmission Emit(in ShapeCodeGenerationRequest request);
}
