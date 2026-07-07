using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Cohesive.Configuration.Tests;

public sealed class ConfigurationProfileConfigurationTests
{
    [Fact]
    public void ResolveConfigurationProfile_ResolvesInheritanceAndFlattensValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["App:ActiveProfile"] = "dev",
                ["App:Profiles:base:Feature:Mode"] = "base",
                ["App:Profiles:base:Feature:Enabled"] = "false",
                ["App:Profiles:shared:Extends"] = "base",
                ["App:Profiles:shared:Feature:Enabled"] = "true",
                ["App:Profiles:dev:Extends"] = "shared",
                ["App:Profiles:dev:Feature:Mode"] = "remote",
                ["App:Profiles:dev:Feature:Endpoint"] = "https://dev.example"
            })
            .Build();

        var resolution = configuration.ResolveConfigurationProfile(new()
        {
            ActiveProfileKey = "App:ActiveProfile",
            ProfilesSectionPath = "App:Profiles"
        });

        Assert.Equal("dev", resolution.ActiveProfile);
        Assert.Equal(["base", "shared", "dev"], resolution.AppliedProfiles);
        Assert.Equal("remote", resolution.Values["Feature:Mode"]);
        Assert.Equal("true", resolution.Values["Feature:Enabled"]);
        Assert.Equal("https://dev.example", resolution.Values["Feature:Endpoint"]);
    }

    [Fact]
    public void CreateProfiledConfiguration_OverlaysResolvedValuesOnBaseConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Feature:Mode"] = "unprofiled",
                ["Feature:RetryCount"] = "3",
                ["App:ActiveProfile"] = "dev",
                ["App:Profiles:base:Feature:Mode"] = "base",
                ["App:Profiles:dev:Extends"] = "base",
                ["App:Profiles:dev:Feature:Mode"] = "remote"
            })
            .Build();

        var resolution = configuration.ResolveConfigurationProfile(new()
        {
            ActiveProfileKey = "App:ActiveProfile",
            ProfilesSectionPath = "App:Profiles"
        });

        var profiled = configuration.CreateProfiledConfiguration(resolution);

        Assert.Equal("remote", profiled["Feature:Mode"]);
        Assert.Equal("3", profiled["Feature:RetryCount"]);
    }

    [Fact]
    public void AddConfigurationProfile_AddsOverlayToConfigurationManager()
    {
        IConfigurationManager configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Feature:Mode"] = "unprofiled",
            ["App:ActiveProfile"] = "dev",
            ["App:Profiles:base:Feature:Mode"] = "base",
            ["App:Profiles:dev:Extends"] = "base",
            ["App:Profiles:dev:Feature:Mode"] = "remote"
        });

        var context = configuration.AddConfigurationProfile(
            environment: new TestHostEnvironment { EnvironmentName = "Development" },
            options: new()
            {
                ActiveProfileKey = "App:ActiveProfile",
                ProfilesSectionPath = "App:Profiles"
            });

        Assert.Equal("Development", context.EnvironmentName);
        Assert.Equal("dev", context.ActiveProfile);
        Assert.Equal(["base", "dev"], context.AppliedProfiles);
        Assert.Equal("remote", configuration["Feature:Mode"]);
    }

    [Fact]
    public void AddConfigurationProfile_BootstrapCallback_UsesBootstrapViewAndReappliesProfilesAfterCallback()
    {
        IConfigurationManager configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Service:Endpoint"] = "https://base.example",
            ["App:ActiveProfile"] = "dev",
            ["App:Profiles:base:Service:Endpoint"] = "https://profile.example",
            ["App:Profiles:dev:Extends"] = "base"
        });

        var context = configuration.AddConfigurationProfile(
            environment: new TestHostEnvironment { EnvironmentName = "Development" },
            options: new()
            {
                ActiveProfileKey = "App:ActiveProfile",
                ProfilesSectionPath = "App:Profiles"
            },
            configureBootstrap: bootstrap =>
            {
                Assert.Equal("https://profile.example", bootstrap.Configuration["Service:Endpoint"]);
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["App:Profiles:dev:Service:Endpoint"] = "https://final.example"
                });
            });

        Assert.Equal("Development", context.EnvironmentName);
        Assert.Equal("dev", context.ActiveProfile);
        Assert.Equal(["base", "dev"], context.AppliedProfiles);
        Assert.Equal("https://final.example", configuration["Service:Endpoint"]);
    }

    sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = nameof(ConfigurationProfileConfigurationTests);

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
