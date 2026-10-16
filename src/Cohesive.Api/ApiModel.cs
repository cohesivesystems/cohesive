using Cohesive.Prelude;
namespace Cohesive.Api;

/// <summary>Optional instance authoring for a typed API using the existing endpoint builder.</summary>
/// <remarks>Configure endpoints during construction, then share the instance. Definition freezes the
/// builder once; member discovery uses no reflection. Separate contracts can still use ApiBuilder directly.</remarks>
public abstract class ApiModel : IAuthoringModel
{
    void IAuthoringModel.Complete() => _ = Definition;


    /// <summary>Starts an API declaration with an explicit optional semantic namespace.</summary>
    /// <param name="name">Semantic namespace; never inferred from the CLR class name.</param>
    protected ApiModel(string? name = null)
    {
        Endpoints = Api.Define(name);
    }

    /// <summary>Construction-only builder; captured operation builders also reject additions after freezing.</summary>
    protected ApiBuilder Endpoints { get; }
    /// <summary>Cached immutable public contract.</summary>
    public ApiDefinition Definition => Endpoints.Freeze();
}
