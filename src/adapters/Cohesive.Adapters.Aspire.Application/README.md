# Cohesive.Adapters.Aspire.Application

Optional application/CLI integration for native Aspire AppHosts. Base Aspire associations
and offline Application models do not depend on this package.

```csharp
return await AspireApplication.RunAsync(args, builder =>
{
    var model = ModelActivator.Create<OrdersInfrastructure>();
    return model.Explain(model.Configure(builder));
});
```

The single callback configures resources and produces an admitted explanation for both
paths. `ApplicationExplanation.Create` checks deployment completeness and identity before
storage admission. Native execution only builds/starts after this succeeds. `explain`
serializes the same report without building or starting the application. `explain --help`
does not activate the model. Configuration runs once per invocation; there is no second
native factory or example-owned diagnostic formatting.

The helper owns the built application's disposal. Pre-cancelled dispatch throws
`OperationCanceledException` before configuration. After dispatch, explain uses CLI exit
codes and native execution preserves Aspire cancellation. This is offline admission, not
live deployment/readiness qualification.
