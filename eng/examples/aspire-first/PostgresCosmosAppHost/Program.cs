using AspireFirst.PostgresCosmos;
using Cohesive.Application;
using Cohesive.Cli;
using Cohesive.Adapters.Aspire;

var cli = new CliApplication("PostgreSQL and Cosmos fulfillment application").WithoutEnvironmentVariables()
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

static (IDistributedApplicationBuilder Builder, PostgresCosmosFulfillmentInfrastructure Model, AspireInfrastructureAssociation Association)
    Configure(IReadOnlyList<string> hostArgs)
{
    var builder = DistributedApplication.CreateBuilder([.. hostArgs]);
    var model = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
    var association = model.Configure(builder);
    if (!association.Deployment.IsComplete)
        throw new InvalidOperationException(string.Join(Environment.NewLine,
            association.Deployment.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    return (builder, model, association);
}
