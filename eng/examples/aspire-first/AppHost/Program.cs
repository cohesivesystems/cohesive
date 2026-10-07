using AspireFirst;

var builder = DistributedApplication.CreateBuilder(args);
var association = OrdersApp.Configure(builder);
if (!association.Deployment.IsComplete)
    throw new InvalidOperationException(string.Join(Environment.NewLine,
        association.Deployment.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
builder.Build().Run();
