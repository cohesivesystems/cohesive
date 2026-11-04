# Microsoft feature management

`FeatureManagementEvaluator<TContext>` adapts a host-owned `IVariantFeatureManager`
and its `IFeatureDefinitionProvider` to Cohesive. Register them once:

```csharp
services.AddCohesiveFeatureManagement<TenantContext>();
```

The helper uses `AddFeatureManagement` (not the snapshot manager), retains existing
evaluator registrations, and does not allocate native clients itself. Configure
contextual filters with the normal Microsoft feature-management builder.

The same registration works with reloadable JSON or Azure App Configuration. The
host configures credentials, `UseFeatureFlags`, refresh interval and a refresh
trigger (middleware or background refresher). Cohesive does not poll Azure per query.
The adapter uses native definition metadata for startup existence admission and
rechecks it during evaluation. Missing or removed flags throw
`FeatureFlagNotFoundException`, matching LaunchDarkly; they never mean disabled.
A declared false flag is valid. Provider errors and cancellation propagate.

Targeting comes from the individual selection's typed projection, which can read
trusted request identity independently of query input. Context is never retained
by Cohesive. A flag is not authorization. Native refresh is not an atomic snapshot
across independent evaluations or hosts; configuration changes remain eventually visible.

Tests cover true/false reload, actual flag removal, contextual filtering under
concurrency, startup rejection and idempotent registration. They use the native
manager and configuration providers without an Azure account.

Flag deletion intentionally fails affected reads; it is not a rollback operation. Set an
existing flag to false to select live reads. The per-evaluation definition lookup retains
that distinction after configuration reload. With the real in-memory configuration provider
and native manager, warmed evaluation measured 144 bytes/read enabled and 72 bytes/read
disabled, both directly and through the adapter (1,000 reads, Release net10.0); the adapter
added zero bytes. The
regression checks enabled and disabled decisions. This does not establish latency or
allocation bounds for custom definition providers, contextual filters, or refresh work.
