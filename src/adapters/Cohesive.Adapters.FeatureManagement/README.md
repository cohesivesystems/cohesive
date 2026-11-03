# Microsoft feature management

`FeatureManagementEvaluator<TContext>` adapts a host-owned `IVariantFeatureManager`
to `IFeatureFlagEvaluator<TContext>`. Use `AddFeatureManagement`, not the snapshot
manager, for decisions that observe subsequent native configuration refreshes.

```csharp
services.AddFeatureManagement();
services.AddSingleton<IFeatureFlagEvaluator<string>, FeatureManagementEvaluator<string>>();
```

The same registration works with reloadable JSON configuration or Azure App
Configuration's feature-flag provider. The host configures Azure credentials,
`UseFeatureFlags`, refresh interval and the refresh trigger (middleware or a
background refresher). Cohesive does not poll Azure per query or own that client.
An Azure refresh interval is not a fleet-wide cutover or freshness guarantee.

The query input is supplied explicitly to contextual filters. For tenant rollout,
use a host filter that consumes trusted request identity; do not treat a URL key
as proof of tenant membership. The evaluator does not cache or retain targeting
context. Missing flags follow Microsoft's disabled behavior. Evaluation errors
and cancellation propagate; selected-backend errors never cause another read.

Tests exercise configuration reload and concurrent contextual filtering through
the real manager without an Azure account. Azure transport refresh remains a
host/provider integration responsibility.
