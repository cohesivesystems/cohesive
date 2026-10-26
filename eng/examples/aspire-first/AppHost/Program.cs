using Microsoft.Extensions.DependencyInjection;
using Cohesive.Application;
using Cohesive.Cli;
using Cohesive.Adapters.Aspire;
using AspireFirst;

var cli = new CliApplication("Fulfillment application").WithoutEnvironmentVariables()
    .AddApplicationExplanation((hostArgs, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (_, model, association) = Configure(hostArgs);
        return model.Explain(association);
    });
return await cli.RunOrFallbackAsync(args, async (hostArgs, cancellationToken) =>
{
    var (builder, _, _) = Configure(hostArgs);
    await builder.Build().RunAsync(cancellationToken);
    return 0;
});

static (IDistributedApplicationBuilder Builder, FulfillmentInfrastructure Model, AspireInfrastructureAssociation Association)
    Configure(IReadOnlyList<string> hostArgs)
{
    var builder = DistributedApplication.CreateBuilder([.. hostArgs]);
    var policy = AspireFirst.Orders.FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
    var model = ModelActivator.Create<FulfillmentInfrastructure>(services => services.AddSingleton(policy));
    var association = model.Configure(builder);
    if (!association.Deployment.IsComplete)
        throw new InvalidOperationException(string.Join(Environment.NewLine,
            association.Deployment.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    return (builder, model, association);
}
