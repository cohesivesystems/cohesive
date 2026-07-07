using Cohesive.CodeGen;

namespace Cohesive.Api.CodeGen;

/// <summary>
/// Emits source code from an API definition.
/// </summary>
public interface IApiCodeEmitter
{
    /// <summary>
    /// Output language identifier.
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Emits code for the supplied definition.
    /// </summary>
    CodeEmission Emit(in ApiCodeGenerationRequest request);
}
