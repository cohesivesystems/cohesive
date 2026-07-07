namespace Cohesive.Api.CodeGen;

/// <summary>
/// Input for API code generation.
/// </summary>
public readonly record struct ApiCodeGenerationRequest
{
    /// <summary>
    /// Creates an API code generation request.
    /// </summary>
    public ApiCodeGenerationRequest(ApiDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    /// <summary>
    /// API definition to emit.
    /// </summary>
    public ApiDefinition Definition { get; }
}
