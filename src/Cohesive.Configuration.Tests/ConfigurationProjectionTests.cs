namespace Cohesive.Configuration.Tests;

public sealed class ConfigurationProjectionTests
{
    [Fact]
    public void Build_MapsSourceValuesAcrossTypedTargets()
    {
        var projection = new ConfigurationProjection<CommandOptions, RuntimeSettings>("TrainingRuntime");
        projection.Set(false, x => x.EnableDemoDefinitions);
        projection.Map(x => x.ProcessEngineMode, x => x.Modules.ProcessEngine.Mode);
        projection.Map(x => x.ConnectionString, x => x.Infrastructure.AzureBlobStorage!["Default"].ConnectionString);
        projection.Map(
            x => x.DatabaseName,
            x => x.Modules.EntityRepositories.TrainingPolicies.DatabaseName,
            x => x.Modules.EntityRepositories.TrainingRuns.DatabaseName);

        var overrides = projection.Build(new()
        {
            ProcessEngineMode = BackendMode.Remote,
            ConnectionString = "UseDevelopmentStorage=true",
            DatabaseName = "training"
        });

        Assert.Equal(bool.FalseString, overrides["TrainingRuntime:EnableDemoDefinitions"]);
        Assert.Equal("Remote", overrides["TrainingRuntime:Modules:ProcessEngine:Mode"]);
        Assert.Equal("UseDevelopmentStorage=true", overrides["TrainingRuntime:Infrastructure:AzureBlobStorage:Default:ConnectionString"]);
        Assert.Equal("training", overrides["TrainingRuntime:Modules:EntityRepositories:TrainingPolicies:DatabaseName"]);
        Assert.Equal("training", overrides["TrainingRuntime:Modules:EntityRepositories:TrainingRuns:DatabaseName"]);
    }

    [Fact]
    public void Build_SupportsConditionalBranchesAndRawPaths()
    {
        var projection = new ConfigurationProjection<CommandOptions, RuntimeSettings>("TrainingRuntime");
        projection.When(
            x => x.UseRemoteArtifacts,
            then =>
            {
                then.Set("DatasetArtifacts", x => x.Modules.DatasetArtifacts.AzureBlobStorageName);
                then.Map(x => x.ConnectionString, "Infrastructure:AzureBlobStorage:DatasetArtifacts:ConnectionString");
            });

        var remote = projection.Build(new()
        {
            UseRemoteArtifacts = true,
            ConnectionString = "conn"
        });
        var local = projection.Build(new()
        {
            UseRemoteArtifacts = false,
            ConnectionString = "conn"
        });

        Assert.Equal("DatasetArtifacts", remote["TrainingRuntime:Modules:DatasetArtifacts:AzureBlobStorageName"]);
        Assert.Equal("conn", remote["TrainingRuntime:Infrastructure:AzureBlobStorage:DatasetArtifacts:ConnectionString"]);
        Assert.DoesNotContain("TrainingRuntime:Modules:DatasetArtifacts:AzureBlobStorageName", local.Keys);
        Assert.DoesNotContain("TrainingRuntime:Infrastructure:AzureBlobStorage:DatasetArtifacts:ConnectionString", local.Keys);
    }

    sealed class CommandOptions
    {
        public BackendMode? ProcessEngineMode { get; init; }

        public string? ConnectionString { get; init; }

        public string? DatabaseName { get; init; }

        public bool UseRemoteArtifacts { get; init; }
    }

    sealed class RuntimeSettings
    {
        public bool EnableDemoDefinitions { get; init; }

        public ModuleSettings Modules { get; init; } = new();

        public InfrastructureSettings Infrastructure { get; init; } = new();
    }

    sealed class ModuleSettings
    {
        public ProcessEngineSettings ProcessEngine { get; init; } = new();

        public DatasetArtifactsSettings DatasetArtifacts { get; init; } = new();

        public EntityRepositoriesSettings EntityRepositories { get; init; } = new();
    }

    sealed class ProcessEngineSettings
    {
        public BackendMode Mode { get; init; }
    }

    sealed class DatasetArtifactsSettings
    {
        public string? AzureBlobStorageName { get; init; }
    }

    sealed class EntityRepositoriesSettings
    {
        public RepositorySettings TrainingPolicies { get; init; } = new();

        public RepositorySettings TrainingRuns { get; init; } = new();
    }

    sealed class RepositorySettings
    {
        public string? DatabaseName { get; init; }
    }

    sealed class InfrastructureSettings
    {
        public IReadOnlyDictionary<string, AzureBlobStorageSettings>? AzureBlobStorage { get; init; }
    }

    sealed class AzureBlobStorageSettings
    {
        public string? ConnectionString { get; init; }
    }

    enum BackendMode
    {
        Remote
    }
}
