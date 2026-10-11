using Cohesive.Adapters.AspNet;
using Cohesive.Identity;
using Cohesive.Prelude;

namespace AspireFirst.Orders;

/// <summary>Explicit synthetic identity for the local example. This is not authentication.</summary>
/// <remarks>Do not deploy this enricher as an authorization policy. Replace it with authenticated grants.</remarks>
public sealed class FulfillmentDemoIdentity : IHttpOperationContextEnricher
{
    /// <summary>Attaches the demo actor and its sole local fulfillment grant.</summary>
    public static OperationContext Attach(OperationContext context)
    {
        var actor = new PrincipalRef("local-demo", PrincipalKind.User);
        var scope = new ScopeRef(FulfillmentDemo.LocalPartition, "demo", PartitionKey: FulfillmentDemo.LocalPartition);
        return context.WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new(actor, scope, ["fulfillment.write"], "local-example")]));
    }

    /// <inheritdoc />
    public ValueTask<OperationContext> EnrichAsync(HttpContext httpContext, OperationContext context) =>
        ValueTask.FromResult(Attach(context));
}
