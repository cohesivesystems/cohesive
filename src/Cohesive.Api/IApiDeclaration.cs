namespace Cohesive.Api;

/// <summary>A typed API catalog that owns its public endpoint definition.</summary>
/// <remarks>Reading Definition may finalize authoring. Consumers must reuse the returned immutable authority;
/// implementation registration never discovers endpoints by reflecting over catalog members.</remarks>
public interface IApiDeclaration
{
    /// <summary>The immutable endpoint contract owned by this catalog.</summary>
    ApiDefinition Definition { get; }
}
