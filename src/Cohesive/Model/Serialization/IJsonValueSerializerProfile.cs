using System.Text.Json;

namespace Cohesive.Model.Serialization;

/// <summary>Exposes the serializer profile that a converter uses for its complete nested value.</summary>
/// <remarks>The converter must delegate that value unchanged to this profile. Return the same frozen
/// options instance on every access. Projection can then inspect the actual nested contract without
/// guessing converter behavior. The profile must not select the same converter recursively.</remarks>
public interface IJsonValueSerializerProfile
{
    /// <summary>Gets the immutable serializer options used to read and write the nested value.</summary>
    JsonSerializerOptions ValueSerializerOptions { get; }
}
