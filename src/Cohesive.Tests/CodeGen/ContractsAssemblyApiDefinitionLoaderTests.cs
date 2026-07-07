using Cohesive.Api;
using Cohesive.CodeGen.Cli;

namespace Cohesive.Tests.CodeGen;

public sealed class ContractsAssemblyApiDefinitionLoaderTests
{
    [Fact]
    public void Load_DiscoversPublicStaticApiDefinitions_FromAssemblyLoadContext()
    {
        var assemblyPath = typeof(ContractsAssemblyApiDefinitionLoaderTests).Assembly.Location;

        var definition = ContractsAssemblyApiDefinitionLoader.Load(assemblyPath);

        Assert.Contains(definition.Operations, operation => operation.Name == "LoaderHealth");
    }

    [Fact]
    public void LoadConstants_DiscoversPublicStaticConstFields()
    {
        var assemblyPath = typeof(ContractsAssemblyApiDefinitionLoaderTests).Assembly.Location;

        var constants = ContractsAssemblyConstantsLoader.Load(assemblyPath);

        var group = Assert.Single(constants.Groups, group => group.Name == nameof(LoaderTestContractIds));
        Assert.Contains(group.Constants, constant => constant.Name == nameof(LoaderTestContractIds.Home) && Equals(constant.Value, "home"));
        Assert.Contains(group.Constants, constant => constant.Name == nameof(LoaderTestContractIds.MaxAttempts) && Equals(constant.Value, 3));
    }
}

public static class LoaderTestApiDefinition
{
    [Cohesive.Api.ApiDefinition]
    public static ApiDefinition Definition { get; } = Cohesive.Api.Api.Define()
        .Action("LoaderHealth")
            .Route("GET", "/health")
            .Returns<LoaderHealthResponse>()
            .Done()
        .Build();
}

public sealed record LoaderHealthResponse(string Status);

public static class LoaderTestContractIds
{
    public const string Home = "home";
    public const int MaxAttempts = 3;
}
