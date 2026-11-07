using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Adapters.TypeScript;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Model;

internal static class LoadClientGeneration
{
    internal static readonly JsonSerializerOptions Json = CreateJson();
    static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    internal static void Write(LoadApplication model, string directory, bool check = false)
    {
        Directory.CreateDirectory(directory);
        var graph = new ClrShapeGraphBuilder().UsePublicJsonContracts(Json)
            .AddShape<Command>().AddShape<LoadDetails>().AddShape<LoadCatalog>()
            .AddShape<ServiceTransitionInspection<Command>>().AddShape<ServiceTransitionAction>()
            .AddShape<ApiProblem>().AddShape<ApiValidationProblem>().Build(new("logistics"));
        var shapes = new TypeScriptShapeEmitter(new() { FileName = "contracts.generated.ts" }).Emit(graph);
        foreach (var document in shapes.Documents) WriteDocument(document.FileName, document.Text);
        Emit(model.Commands, "commands.generated.ts");
        Emit(new ApiDefinition([model.Catalog.Operation, model.Details.Operation, model.Inspection.Operation]), "queries.generated.ts");

        void WriteDocument(string fileName, string text)
        {
            var path = Path.Combine(directory, fileName);
            if (check)
            {
                if (!File.Exists(path) || File.ReadAllText(path) != text)
                    throw new InvalidOperationException($"Generated client is stale: {fileName}. Run --generate.");
            }
            else File.WriteAllText(path, text);
        }

        void Emit(ApiDefinition definition, string fileName)
        {
            var emission = new TypeScriptApiClientEmitter(new()
            {
                FileName = fileName, ShapesImportPath = "./contracts.generated.js", JsonSerializerOptions = Json
            }).Emit(definition);
            foreach (var document in emission.Documents) WriteDocument(document.FileName, document.Text);
        }
    }
}
