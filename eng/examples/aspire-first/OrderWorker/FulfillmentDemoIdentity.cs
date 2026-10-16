using Cohesive.Identity;
using Cohesive.Prelude;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;

namespace AspireFirst.Orders;

/// <summary>Explicit synthetic local identity and grants. This is not authentication.</summary>
public static class FulfillmentDemoIdentity
{
    /// <summary>Prepared once for this demo; production must supply authenticated identities and grants.</summary>
    public static IdentityContext Context { get; } = Create();

    /// <summary>Maps the declared demo requirements to the same service admission policy at the HTTP boundary.</summary>
    /// <param name="services">Worker services with request OperationContext support.</param>
    /// <param name="service">Canonical fulfillment service; requirements are not duplicated here.</param>
    public static void AddHttpPolicies(IServiceCollection services, ExecutionDefinitionDocument service)
    {
        var admission = new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField));
        services.AddAuthorization(options =>
        {
            foreach (var operation in service.GetDefinition<ServiceDefinition>().Operations)
                foreach (var requirement in operation.AuthorizationRequirements)
                    options.AddPolicy(requirement.Id, policy => policy.RequireAssertion(async context =>
                        context.Resource is HttpContext http && await admission.AdmitAsync(
                            http.RequestServices.GetRequiredService<OperationContext>(), operation) is not null));
        });
    }

    static IdentityContext Create()
    {
        var actor = new PrincipalRef("local-demo", PrincipalKind.User);
        var scope = new ScopeRef(FulfillmentDemo.LocalPartition, "demo", PartitionKey: FulfillmentDemo.LocalPartition);
        return new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new(actor, scope, ["fulfillment.write", "fulfillment.read"], "local-example")]);
    }
}
