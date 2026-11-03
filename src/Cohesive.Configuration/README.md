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

- `Cohesive.Cli` for typed command binding that can flow into configuration projection.
- `Cohesive.Host` for optional generic-host lifecycle and dependency-injection integration.
- `Cohesive.Adapters.AzureAppConfiguration` for Azure App Configuration integration.

## Live feature decisions

`IFeatureFlagEvaluator<TContext>` is a narrow, invocation-scoped decision contract;
native clients are concurrent host-lifetime resources. The explicit context travels
into the provider rather than being inferred by reflection or cached in a singleton.
Adapters are available in `Cohesive.Adapters.FeatureManagement` (including Azure App
Configuration) and `Cohesive.Adapters.LaunchDarkly`. The SDK owns refresh and cached
value policy. Both implement the same cancellation and no-context-retention contract.

This is separate from `DependencySelectionCatalog`: the catalog selects from fixed
registered rules, while live flags obtain changing decisions from a native provider.
