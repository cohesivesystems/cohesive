using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Storage;

internal static class LoadWebHost
{
    internal static async Task Run(LoadApplication model, string[] args)
    {
        var root = LoadApplication.ExampleDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = Path.Combine(root, "wwwroot") });
        // This explicit local demo host accepts selectable identities only in Development.
        if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Run the demo with ASPNETCORE_ENVIRONMENT=Development.");
        builder.Services.AddRequestOperationContext();
        builder.Services.AddSingleton<IHttpOperationContextEnricher>(new DemoIdentity(model));
        builder.Services.AddCohesiveExceptionHandling();
        builder.Services.AddAuthorization(options =>
        {
            foreach (var requirement in model.Commands.Operations.SelectMany(op => op.AuthorizationRequirements).Distinct())
                options.AddPolicy(requirement.Id, policy => policy.RequireAssertion(_ => true));
        });
        foreach (var load in LoadApplication.Loads) await model.Seed(load.Id, load.DocumentsComplete);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.UseRequestOperationContext();
        app.UseAuthorization();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapApiEndpoint(model.Catalog, () => Results.Json(new LoadCatalog(LoadApplication.Roles, LoadApplication.Loads.Select(load => load.Id).ToArray(), LoadApplication.RoleHeader), LoadClientGeneration.Json));
        app.MapApiEndpoint(model.Details, async (OperationContext context, string id) =>
        {
            var snapshot = await model.Repository.TryGet(context, id, EntityReadOptions.Full.WithPartitionKey(LoadApplication.Organization));
            if (snapshot is null) return Results.NotFound(new ApiProblem("load.missing", "Load not found."));
            var fields = snapshot.Entity.Observation;
            return Results.Json(new LoadDetails(id, Enum.Parse<LoadStatus>(fields.GetField(nameof(Load.Status)).GetString()!),
                fields.GetField(nameof(Load.DocumentsComplete)).GetBoolean(), fields.GetField(nameof(Load.LastReason)).GetString()!), LoadClientGeneration.Json);
        });
        app.MapServiceTransitionInspection<Command>(model.Runtime, model.Entity.StateShape.QualifiedId, model.InspectionRoute);
        foreach (var operation in model.Commands.Operations)
            app.MapServiceTransition<Command, string>(model.Runtime, operation.Name, operation.Http!.Route,
                authorizationPolicyResolver: (_, requirement) => requirement.Id);
        await app.RunAsync();
    }

    sealed class DemoIdentity(LoadApplication model) : IHttpOperationContextEnricher
    {
        public ValueTask<OperationContext> EnrichAsync(HttpContext http, OperationContext context)
        {
            var roles = http.Request.Headers[LoadApplication.RoleHeader];
            var role = roles.Count == 0 ? "viewer" : roles.Count == 1 ? roles[0]! : "";
            if (!LoadApplication.Roles.Contains(role)) throw new BadHttpRequestException("Unknown demo role.");
            return ValueTask.FromResult(context.WithIdentityContext(model.Identity(role)));
        }
    }
}
