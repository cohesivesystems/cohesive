using System.Reflection;

namespace Cohesive.Model.Authoring;

/// <summary>
/// Maps CLR types to semantic <see cref="TypeRef"/> definitions.
/// </summary>
public interface IClrTypeRefMapper
{
    /// <summary>
    /// Maps a CLR type into a semantic type reference using available nullability metadata.
    /// </summary>
    /// <param name="clrType">CLR type to project into a portable semantic type reference.</param>
    /// <param name="nullability">Optional reflection nullability metadata for the mapped occurrence.</param>
    /// <returns>
    /// A portable semantic type reference. Implementations may return a diagnostic-bearing
    /// <see cref="OpaqueRuntimeTypeRef"/> when the CLR shape cannot be represented safely.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is <see langword="null"/>.</exception>
    TypeRef Map(Type clrType, NullabilityInfo? nullability);
}
