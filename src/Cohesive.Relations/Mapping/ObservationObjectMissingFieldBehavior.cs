namespace Cohesive.Relations.Mapping;

/// <summary>
/// Controls how observation-to-object mappers handle fields that are absent from the layout
/// or missing from a specific observation instance.
/// </summary>
public enum ObservationObjectMissingFieldBehavior
{
    /// <summary>
    /// Missing fields always cause mapping to fail.
    /// </summary>
    Throw = 0,

    /// <summary>
    /// Missing fields use the target member's default value when the member is optional,
    /// such as nullable/reference types or constructor parameters with default values.
    /// </summary>
    UseDefaultForOptionalMembers = 1,

    /// <summary>
    /// Missing fields always use the target member's default value.
    /// </summary>
    UseDefaultForAllMembers = 2
}
