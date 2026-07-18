using System.Text.Json.Serialization;

namespace Cohesive.Relations.Physical;

/// <summary>Precedence tier that supplied one effective physical configuration value.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryConfigurationValueOrigin
{
    /// <summary>An explicit local declaration supplied the effective value.</summary>
    Explicit = 0,

    /// <summary>A scoped application or subsystem profile supplied the effective value.</summary>
    ScopedProfile = 1,

    /// <summary>A target-adapter convention supplied the effective value.</summary>
    AdapterConvention = 2,

    /// <summary>A framework-wide convention supplied the effective value.</summary>
    FrameworkDefault = 3
}

/// <summary>
/// Portable attribution for one effective physical configuration setting.
/// </summary>
/// <remarks>
/// The configured value remains in its owning artifact property. This decision records only the stable
/// setting identity and the authority responsible for the effective value, avoiding a second source of truth.
/// </remarks>
public sealed record RelationQueryConfigurationDecision
{
    /// <summary>Creates configuration attribution for one effective setting.</summary>
    /// <param name="setting">Stable artifact-scoped setting identity.</param>
    /// <param name="origin">Precedence tier that supplied the effective value.</param>
    /// <param name="authority">Stable identity and version of the declaration, profile, or convention.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="setting"/> or <paramref name="authority"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="setting"/> or <paramref name="authority"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryConfigurationDecision(
        string setting,
        RelationQueryConfigurationValueOrigin origin,
        string authority)
    {
        Setting = Guard.RequireNotNullOrWhiteSpace(setting);
        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported configuration-value origin.");
        }

        Origin = origin;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
    }

    /// <summary>Stable artifact-scoped setting identity.</summary>
    public string Setting { get; }

    /// <summary>Precedence tier that supplied the effective value.</summary>
    public RelationQueryConfigurationValueOrigin Origin { get; }

    /// <summary>Stable identity and version of the declaration, profile, or convention.</summary>
    public string Authority { get; }
}
