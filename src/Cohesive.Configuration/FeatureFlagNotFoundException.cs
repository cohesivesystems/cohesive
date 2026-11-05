namespace Cohesive.Configuration;

/// <summary>A declared flag is absent from its provider; absence never means disabled.</summary>
public sealed class FeatureFlagNotFoundException : InvalidOperationException
{
    /// <summary>Identifies the missing flag without retaining targeting attributes.</summary>
    /// <param name="flag">Missing provider key.</param>
    public FeatureFlagNotFoundException(string flag) : base($"Feature flag '{flag}' does not exist. Define it before admitting this selection.")
    { ArgumentException.ThrowIfNullOrWhiteSpace(flag); Flag = flag; }
    /// <summary>Missing provider key.</summary>
    public string Flag { get; }
}
