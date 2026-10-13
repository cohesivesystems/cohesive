using System.Collections.Immutable;

namespace Cohesive.Cli;

/// <summary>The source category of a consumed configuration value.</summary>
public enum CliConfigurationSourceKind
{
    /// <summary>An explicit command-line option or argument.</summary>
    CommandLine,
    /// <summary>An automatically bound or explicitly mapped environment variable.</summary>
    Environment,
    /// <summary>A provider registered on the application.</summary>
    ApplicationConfiguration,
    /// <summary>A provider registered on the command.</summary>
    CommandConfiguration,
    /// <summary>A captured explicit option default.</summary>
    DeclarationDefault,
    /// <summary>A CLR initializer, constructor, or type default after typed binding.</summary>
    ClrDefault,
    /// <summary>No semantic value was supplied for an optional explicit parameter.</summary>
    Absent
}

/// <summary>A winning source for a scalar key or collection element.</summary>
/// <param name="Key">Configuration key, including an element index when applicable.</param>
/// <param name="Kind">Source category.</param>
/// <param name="Source">Source identity: CLI name, environment variable, or provider type and registration index.</param>
public sealed record CliConfigurationOrigin(string Key, CliConfigurationSourceKind Kind, string Source);

/// <summary>A redaction-safe explanation of one effective bound parameter.</summary>
/// <param name="ConfigurationKey">Resolved configuration key.</param>
/// <param name="CliName">Resolved command-line name.</param>
/// <param name="Value">Effective display value; sensitive values are replaced with a redaction marker.</param>
/// <param name="Sensitive">Whether the parameter is sensitive.</param>
/// <param name="Origins">Winning sources; collections can have multiple origins.</param>
public sealed record CliParameterProvenance(
    string ConfigurationKey, string CliName, string? Value, bool Sensitive,
    ImmutableArray<CliConfigurationOrigin> Origins);
