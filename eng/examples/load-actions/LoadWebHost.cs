using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.AspNet.Entities;
using Cohesive.Api;
using Cohesive.Identity;
using Cohesive.Model;

internal static class LoadWebHost
{
    internal static async Task Run(LoadApplication model, string[] args)
    {
        var root = LoadApplication.ExampleDirectory;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = Path.Combine(root, "wwwroot") });
        // This explicit local demo host accepts selectable identities only in Development.
        if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Run the demo with ASPNETCORE_ENVIRONMENT=Development.");
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = LoadClientGeneration.Json.PropertyNamingPolicy);
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
        app.MapEntityApi<Load>(model.Entity, model.Repository, LoadApplication.Organization, bindings =>
            bindings.Get(model.Details, (id, load) => TypedResults.Ok(
                new LoadDetails(id, load.Status, load.DocumentsComplete, load.LastReason))));
        app.MapServiceTransitionInspection<Command>(model.Runtime, model.Entity.StateShape.QualifiedId, model.InspectionRoute);
        app.MapServiceTransitions<Command, string>(model.Runtime, model.Commands,
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
