namespace Cohesive.Configuration.Tests;

public sealed class ConfigurationParameterOverridesTests
{
    [Fact]
    public void Add_UsesPrefixWithoutRequiringTrailingSeparator()
    {
        var b = new ConfigurationParameterOverrides<RuntimeSettings>("TrainingRuntime");

        b.Add(x => x.Modules.ProcessEngine.Mode, BackendMode.Remote);
        b.Add(x => x.Infrastructure.AzureML!["Default"].WorkspaceName, "workspace");

        Assert.Equal("Remote", b.Overrides["TrainingRuntime:Modules:ProcessEngine:Mode"]);
        Assert.Equal("workspace", b.Overrides["TrainingRuntime:Infrastructure:AzureML:Default:WorkspaceName"]);
    }

    [Fact]
    public void Add_SkipsNullAndWhitespaceValues()
    {
        var b = new ConfigurationParameterOverrides<RuntimeSettings>("TrainingRuntime");

        b.Add(x => x.Modules.ProcessEngine.Mode, null);
        b.Add("Infrastructure:AzureML:Default:WorkspaceName", " ");

        Assert.Empty(b.Overrides);
    }

    sealed class RuntimeSettings
    {
        public ModuleSettings Modules { get; init; } = new();

        public InfrastructureSettings Infrastructure { get; init; } = new();
    }

    sealed class ModuleSettings
    {
        public ProcessEngineSettings ProcessEngine { get; init; } = new();
    }

    sealed class ProcessEngineSettings
    {
        public BackendMode Mode { get; init; }
    }

    sealed class InfrastructureSettings
    {
        public IReadOnlyDictionary<string, AzureMLSettings>? AzureML { get; init; }
    }

    sealed class AzureMLSettings
    {
        public string? WorkspaceName { get; init; }
    }

    enum BackendMode
    {
        Remote
    }
}
