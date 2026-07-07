using Cohesive.Transitions.Model;
using System.Reflection;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Maps CLR types to semantic <see cref="TypeRef"/> definitions.
/// </summary>
public interface IClrTypeRefMapper
{
    /// <summary>
    /// Maps a CLR type into a semantic type reference using available nullability metadata.
    /// </summary>
    TypeRef Map(Type clrType, NullabilityInfo? nullability);
}
