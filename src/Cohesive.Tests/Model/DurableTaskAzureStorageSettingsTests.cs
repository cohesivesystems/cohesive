using Cohesive.Adapters.AzureStorage;
using Cohesive.Adapters.DurableTask.AzureStorage;
using DurableTask.AzureStorage;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Model;

public sealed class DurableTaskAzureStorageSettingsTests
{
    [Fact]
    public void ConfigureDurableTaskAzureStorage_UsesNamedAzureStorageProfile()
    {
        var sp = CreateServiceProvider(new Dictionary<string, AzureBlobStorageOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [AzureBlobStorageOptions.DefaultName] = new() { ConnectionString = "UseDevelopmentStorage=true" },
            ["Durable"] = new() { ConnectionString = "UseDevelopmentStorage=true" }
        });
        var settings = new AzureStorageOrchestrationServiceSettings();

        settings.ConfigureDurableTaskAzureStorage(sp, new DurableTaskAzureStorageSettings
        {
            TaskHubName = "sample-training-dev",
            AzureStorageName = "Durable"
        });

        Assert.Equal("sampletrainingdev", settings.TaskHubName);
        Assert.NotNull(settings.StorageAccountClientProvider);
    }

    [Fact]
    public void ConfigureDurableTaskAzureStorage_UsesDefaultAzureStorageProfileWhenNameIsOmitted()
    {
        var sp = CreateServiceProvider(new Dictionary<string, AzureBlobStorageOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [AzureBlobStorageOptions.DefaultName] = new() { ConnectionString = "UseDevelopmentStorage=true" }
        });
        var settings = new AzureStorageOrchestrationServiceSettings();

        settings.ConfigureDurableTaskAzureStorage(sp, new DurableTaskAzureStorageSettings
        {
            TaskHubName = "9-",
            AzureStorageName = null
        });

        Assert.Equal("a9x", settings.TaskHubName);
        Assert.NotNull(settings.StorageAccountClientProvider);
    }

    [Fact]
    public void ConfigureDurableTaskAzureStorage_ReportsMissingAzureStorageProfile()
    {
        var sp = CreateServiceProvider(new Dictionary<string, AzureBlobStorageOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [AzureBlobStorageOptions.DefaultName] = new() { ConnectionString = "UseDevelopmentStorage=true" }
        });
        var settings = new AzureStorageOrchestrationServiceSettings();

        var ex = Assert.Throws<InvalidOperationException>(() => settings.ConfigureDurableTaskAzureStorage(sp, new DurableTaskAzureStorageSettings
        {
            TaskHubName = "sample-training-dev",
            AzureStorageName = "missing"
        }));

        Assert.Equal("Azure blob storage 'missing' is not configured.", ex.Message);
    }

    static ServiceProvider CreateServiceProvider(IReadOnlyDictionary<string, AzureBlobStorageOptions> accounts)
    {
        ServiceCollection services = [];
        services.RegisterAzureBlobStorageClients(accounts);
        return services.BuildServiceProvider();
    }
}
