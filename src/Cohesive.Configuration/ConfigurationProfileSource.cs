using Microsoft.Extensions.Configuration;

namespace Cohesive.Configuration;

sealed class ConfigurationProfileSource(ConfigurationProfileResolution resolution) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new ConfigurationProfileProvider(resolution);
    }
    
    sealed class ConfigurationProfileProvider(ConfigurationProfileResolution resolution) : ConfigurationProvider
    {
        public override void Load() => 
            Data = new Dictionary<string, string?>(resolution.Values, StringComparer.OrdinalIgnoreCase);
    }
}