using Cohesive.Identity;
using Cohesive.Prelude;

namespace AspireFirst.Orders;

/// <summary>Explicit synthetic local identity and grants. This is not authentication.</summary>
public static class FulfillmentDemoIdentity
{
    /// <summary>Prepared once for this demo; production must supply authenticated identities and grants.</summary>
    public static IdentityContext Context { get; } = Create();

    static IdentityContext Create()
    {
        var actor = new PrincipalRef("local-demo", PrincipalKind.User);
        var scope = new ScopeRef(FulfillmentStoragePolicy.LocalPartition, "demo", PartitionKey: FulfillmentStoragePolicy.LocalPartition);
        return new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new(actor, scope, ["fulfillment.write", "fulfillment.read"], "local-example")]);
    }
}
