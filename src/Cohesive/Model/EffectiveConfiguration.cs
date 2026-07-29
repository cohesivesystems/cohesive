using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>Precedence tier that supplied one effective compiled or operational configuration value.</summary>
/// <remarks>
/// Lower numeric values have higher authority. This is the shared deterministic precedence law for Cohesive
/// physical planning, target binding, and lifecycle control.
/// </remarks>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum EffectiveConfigurationOrigin
{
    /// <summary>An explicit local declaration supplied the effective value.</summary>
    Explicit = 0,

    /// <summary>A scoped application or subsystem profile supplied the effective value.</summary>
    ScopedProfile = 1,

    /// <summary>An adapter or compiler convention supplied the effective value.</summary>
    AdapterConvention = 2,

    /// <summary>A framework-wide deterministic convention supplied the effective value.</summary>
    FrameworkDefault = 3
}

/// <summary>Portable attribution for one effective physical or operational configuration setting.</summary>
/// <remarks>
/// The selected value remains in its owning artifact property. This record retains only the stable setting
/// identity and authority, keeping provenance inspectable without introducing a second value authority.
/// </remarks>
public sealed record EffectiveConfigurationDecision
{
    /// <summary>Creates attribution for one effective setting.</summary>
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
    public EffectiveConfigurationDecision(
        string setting,
        EffectiveConfigurationOrigin origin,
        string authority)
    {
        Setting = Guard.RequireNotNullOrWhiteSpace(setting);
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported effective-configuration origin.");
        Origin = origin;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
    }

    /// <summary>Stable artifact-scoped setting identity.</summary>
    public string Setting { get; }

    /// <summary>Precedence tier that supplied the effective value.</summary>
    public EffectiveConfigurationOrigin Origin { get; }

    /// <summary>Stable identity and version of the effective authority.</summary>
    public string Authority { get; }
}
