# Cohesive.Configuration

Configuration profiles, configuration projection, dependency selection, and runtime profile helpers for Cohesive applications.

## Install

```bash
dotnet add package Cohesive.Configuration
```

## Use When

- You need layered runtime profiles over `Microsoft.Extensions.Configuration`.
- You want to project typed command or option objects into configuration overrides.
- You need a declarative catalog for selecting infrastructure dependencies by profile, environment, or backend mode.

## Example

```csharp
using Cohesive.Configuration;

var projection = new ConfigurationProjection<TrainCommand, RuntimeSettings>("Training");
projection.Map(x => x.ConnectionString, x => x.Storage.Default.ConnectionString);
projection.Set(false, x => x.EnableDemoDefinitions);

var overrides = projection.Build(new TrainCommand(
    ConnectionString: "UseDevelopmentStorage=true"));
```

## Related Packages

- `Cohesive.Host` for CLI binding that can flow into configuration projection.
- `Cohesive.Adapters.AzureAppConfiguration` for Azure App Configuration integration.
