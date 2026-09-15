using Cohesive.Adapters.Cosmos;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Cosmos;

public sealed class CosmosClientFactoryTests
{
    const string EmulatorMasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    [Fact]
    public void CreateCosmosClient_WithAccountKeyAndDefaultCredentialEnabled_PrefersAccountKeyPath()
    {
        var client = CosmosClientFactory.Shared.CreateCosmosClient(new()
        {
            Endpoint = "https://localhost:8081/",
            AccountKey = EmulatorMasterKey,
            AllowInsecureServerCertificate = true,
            UseDefaultCredential = true
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void CreateCosmosClient_WithAccountKeyAndDefaultCredentialDisabled_UsesAccountKeyPath()
    {
        var client = CosmosClientFactory.Shared.CreateCosmosClient(new()
        {
            Endpoint = "https://localhost:8081/",
            AccountKey = EmulatorMasterKey,
            AllowInsecureServerCertificate = true,
            UseDefaultCredential = false
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void CreateCosmosClient_WithoutAccountKeyOrCredential_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CosmosClientFactory.Shared.CreateCosmosClient(new()
        {
            Endpoint = "https://localhost:8081/",
            UseDefaultCredential = false
        }));

        Assert.Contains("Cosmos endpoint is not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateCosmosClient_DefaultsToPrivacySafeNativeOperationTracing()
    {
        var factory = new CosmosClientFactory();

        using CosmosClient client = factory.CreateCosmosClient(CreateOptions());
        CosmosClientTelemetryOptions telemetry = client.ClientOptions.CosmosClientTelemetryOptions;

        Assert.False(telemetry.DisableDistributedTracing);
        Assert.True(telemetry.DisableSendingMetricsToService);
        Assert.Equal(QueryTextMode.None, telemetry.QueryTextMode);
        Assert.Equal(TimeSpan.FromSeconds(3), telemetry.CosmosThresholdOptions.NonPointOperationLatencyThreshold);
        Assert.Equal(TimeSpan.FromSeconds(1), telemetry.CosmosThresholdOptions.PointOperationLatencyThreshold);
        Assert.Null(telemetry.CosmosThresholdOptions.RequestChargeThreshold);
        Assert.Null(telemetry.CosmosThresholdOptions.PayloadSizeThresholdInBytes);
        Assert.Equal("Azure.Cosmos.Operation", CosmosClientFactory.OperationActivitySourceName);
    }

    [Fact]
    public void CreateCosmosClient_PreservesNativeTelemetryOverrides()
    {
        var factory = new CosmosClientFactory();
        var options = CreateOptions() with
        {
            TelemetryOptions = new()
            {
                DisableDistributedTracing = true,
                DisableSendingMetricsToService = false,
                QueryTextMode = QueryTextMode.ParameterizedOnly,
                CosmosThresholdOptions = new()
                {
                    NonPointOperationLatencyThreshold = TimeSpan.FromSeconds(9),
                    PointOperationLatencyThreshold = TimeSpan.FromSeconds(4),
                    RequestChargeThreshold = 42.5,
                    PayloadSizeThresholdInBytes = 65_536
                }
            }
        };

        using CosmosClient client = factory.CreateCosmosClient(options);
        CosmosClientTelemetryOptions telemetry = client.ClientOptions.CosmosClientTelemetryOptions;

        Assert.True(telemetry.DisableDistributedTracing);
        Assert.False(telemetry.DisableSendingMetricsToService);
        Assert.Equal(QueryTextMode.ParameterizedOnly, telemetry.QueryTextMode);
        Assert.Equal(TimeSpan.FromSeconds(9), telemetry.CosmosThresholdOptions.NonPointOperationLatencyThreshold);
        Assert.Equal(TimeSpan.FromSeconds(4), telemetry.CosmosThresholdOptions.PointOperationLatencyThreshold);
        Assert.Equal(42.5, telemetry.CosmosThresholdOptions.RequestChargeThreshold);
        Assert.Equal(65_536, telemetry.CosmosThresholdOptions.PayloadSizeThresholdInBytes);
    }

    [Fact]
    public void CreateCosmosClient_ReusesEquivalentTelemetryConfiguration()
    {
        var factory = new CosmosClientFactory();

        CosmosClient first = factory.CreateCosmosClient(CreateOptions());
        CosmosClient second = factory.CreateCosmosClient(CreateOptions());

        Assert.Same(first, second);
        first.Dispose();
    }

    [Fact]
    public void CreateCosmosClient_DoesNotReuseIncompatibleTelemetryConfiguration()
    {
        var factory = new CosmosClientFactory();
        CosmosClientFactoryOptions enabled = CreateOptions();
        CosmosClientFactoryOptions disabled = CreateOptions() with
        {
            TelemetryOptions = new()
            {
                DisableDistributedTracing = true,
                DisableSendingMetricsToService = true,
                QueryTextMode = QueryTextMode.None
            }
        };

        using CosmosClient first = factory.CreateCosmosClient(enabled);
        using CosmosClient second = factory.CreateCosmosClient(disabled);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void CreateCosmosClient_SnapshotsTelemetryBeforeCaching()
    {
        var factory = new CosmosClientFactory();
        CosmosClientFactoryOptions options = CreateOptions();

        using CosmosClient first = factory.CreateCosmosClient(options);
        options.TelemetryOptions.DisableDistributedTracing = true;
        using CosmosClient second = factory.CreateCosmosClient(options);

        Assert.False(first.ClientOptions.CosmosClientTelemetryOptions.DisableDistributedTracing);
        Assert.True(second.ClientOptions.CosmosClientTelemetryOptions.DisableDistributedTracing);
        Assert.NotSame(first, second);
    }

    static CosmosClientFactoryOptions CreateOptions() => new()
    {
        Endpoint = "https://localhost:8081/",
        AccountKey = EmulatorMasterKey,
        AllowInsecureServerCertificate = true,
        UseDefaultCredential = false
    };
}
