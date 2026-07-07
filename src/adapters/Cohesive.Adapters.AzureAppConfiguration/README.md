# Cohesive.Adapters.AzureAppConfiguration

Azure App Configuration integration for Cohesive configuration profiles and runtime settings.

## Install

```bash
dotnet add package Cohesive.Adapters.AzureAppConfiguration
```

## Use When

- You want Cohesive configuration profiles backed by Azure App Configuration.
- You need Azure identity-based access to centralized application settings.
- You want cloud configuration to participate in the same profile/projection model used locally.

## Example

```csharp
using Cohesive.Adapters.AzureAppConfiguration;

builder.Configuration.AddConfiguredAzureAppConfiguration(
    builder.Configuration,
    builder.Environment,
    new AzureAppConfigurationBootstrapOptions
    {
        SectionPath = "AzureAppConfiguration",
        SkipLocalEnvironmentWhenUnspecified = true
    });
```

## Related Packages

- `Cohesive.Configuration` for profile and projection primitives.
