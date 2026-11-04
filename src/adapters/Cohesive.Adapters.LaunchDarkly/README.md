# LaunchDarkly live flags

Register one host-owned `ILdClient`, then map the application's targeting contract
into LaunchDarkly's native context:

```csharp
services.AddCohesiveLaunchDarkly<TenantContext>(tenant => Context.New(tenant.Id));
```

The query's selection maps invocation input/request identity into `TenantContext`;
it is not constrained to the query's input type. The helper retains an existing
evaluator registration. The host owns native client lifetime, credentials, streaming,
polling and last-known-value policy. Flag targeting never grants authorization.

At startup `RequireFlagAsync` uses the SDK's event-free `AllFlagsState` snapshot with
a synthetic admission context, then checks the requested key. No request context projection or SDK evaluation events are produced. This catalog snapshot is a startup
cost per admitted selection, not a per-read scan. Unready SDK state fails admission.
Missing flags throw the shared `FeatureFlagNotFoundException`, including during
runtime evaluation. A declared false flag is valid.

Per invocation, `BoolVariationDetail` runs once on the explicitly projected context.
Error reasons propagate instead of accepting the SDK's fallback argument. Successful
cached values retain the SDK's freshness policy; they are not guaranteed current
across a fleet. No targeting context is cached by Cohesive.

Tests use the SDK's TestData source with event transport disabled to prove updates,
targeting, admission, missing-key errors and cancellation without external accounts.
