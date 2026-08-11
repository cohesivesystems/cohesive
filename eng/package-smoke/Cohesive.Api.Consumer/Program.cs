using Cohesive.Api;

var definition = Api.Define("PackageSmoke")
    .Query("Health")
        .Route("GET", "/health")
        .Returns<string>()
        .Done()
    .Build();

return definition.Endpoints.Count == 1 ? 0 : 1;
