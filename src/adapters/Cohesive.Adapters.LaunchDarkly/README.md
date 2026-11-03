# LaunchDarkly live flags

`LaunchDarklyFeatureFlagEvaluator<TContext>` uses one host-owned `ILdClient` and an
explicit projection into LaunchDarkly's `Context`:

```csharp
services.AddSingleton<IFeatureFlagEvaluator<string>>(sp =>
    new LaunchDarklyFeatureFlagEvaluator<string>(
        sp.GetRequiredService<ILdClient>(),
        orderId => Context.New(orderId)));
```

The sample targets an order, not a tenant. For tenant rollout project trusted
identity through an injected operation-context accessor; do not infer tenant
identity from an arbitrary string input. Never use flag targeting as authorization.
The host registers/disposes the native singleton, owns SDK credentials and selects
streaming, polling and last-known-value behavior. Cohesive neither connects nor
creates native clients during model declaration.

The adapter calls `BoolVariationDetail` once per invocation and rejects SDK error
reasons (including unknown flags and invalid contexts) instead of silently accepting
the fallback argument. Successful SDK cached decisions retain the native freshness
policy; they are not guaranteed current across a fleet. No context is cached by
Cohesive. Cancellation is checked before synchronous native evaluation.

Tests use the SDK's TestData source and disabled event transport to prove updates,
targeting, missing-flag failure and cancellation with no network or credentials.
