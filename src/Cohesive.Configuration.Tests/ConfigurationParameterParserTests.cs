using System.Text;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Configuration.Tests;

public sealed class ConfigurationParameterParserTests
{
    [Fact]
    public void Parse_MergesJsonEnvironmentVariablesAndCliIntoNestedConfiguration()
    {
        var environmentVariablePrefix = $"COHESIVE_TEST_{Guid.NewGuid():N}_";
        try
        {
            Environment.SetEnvironmentVariable(
                $"{environmentVariablePrefix}SERVICE__ENDPOINT",
                "https://env.example"
                );

            ConfigurationParameterOptions<ApplicationConfiguration> options = new();
            options.Map(x => x.Service.Mode).WithCliName("mode").WithCliShortName("m").WithDescription("Execution mode");
            options.MapEnum(("dev", ExecutionMode.Development), ("prod", ExecutionMode.Production));

            var parsed = ConfigurationParameterParser.Parse(
                args:
                [
                    "--service-poll-interval", "45",
                    "-m", "prod",
                    "--api-key", "12345"
                ],
                configure: builder => builder.AddJsonStream(JsonStream("""
                    {
                      "service": {
                        "endpoint": "https://json.example",
                        "poll-interval": "15",
                        "mode": "dev"
                      },
                      "api": {
                        "api-key": "json-key"
                      }
                    }
                    """)),
                options: options,
                environmentVariablePrefix: environmentVariablePrefix
                );

            Assert.Equal("https://env.example", parsed.Service.Endpoint);
            Assert.Equal(TimeSpan.FromSeconds(45), parsed.Service.PollInterval);
            Assert.Equal(ExecutionMode.Production, parsed.Service.Mode);
            Assert.Equal("12345", parsed.Api.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{environmentVariablePrefix}SERVICE__ENDPOINT", null);
        }
    }

    [Fact]
    public void Parse_UsesExpressionBuilderOverrides_ForNestedSectionsAndProperties()
    {
        ConfigurationParameterOptions<DatabaseApplicationConfiguration> options = new();
        options.Map(x => x.Database).WithNameOverride("db");
        options.Map(x => x.Database.Port).WithCliName("db-port").WithCliShortName("p");

        var configuration = ConfigurationParameterParser.BuildConfiguration(
            args: ["-p", "15432"],
            configure: builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["db:host"] = "db.example"
            }),
            options: options);

        var parsed = ConfigurationParameterParser.Parse(configuration, options);

        Assert.Equal("db.example", parsed.Database.Host);
        Assert.Equal(15432, parsed.Database.Port);
    }

    [Fact]
    public void Parse_ThrowsForMissingRequiredAndInvalidAllowedValues()
    {
        ConfigurationParameterOptions<ApplicationConfiguration> options = new();
        options.Map(x => x.Service.Mode).WithAllowedValues("dev", "prod");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["service:mode"] = "qa"
            })
            .Build();

        var exception = Assert.Throws<ConfigurationParameterParseException>(() =>
            ConfigurationParameterParser.Parse(configuration, options));

        Assert.Contains(exception.Errors, error => error.Contains("api:api-key", StringComparison.Ordinal));
        Assert.Contains(exception.Errors, error => error.Contains("[dev, prod]", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_ReturnsMergedMetadata()
    {
        ConfigurationParameterOptions<ApplicationConfiguration> options = new();
        options.Map(x => x.Service.Mode)
            .WithCliName("execution-mode")
            .WithDescription("Execution mode")
            .IsRequired();
        options.MapEnum(
            ("dev", ExecutionMode.Development),
            ("prod", ExecutionMode.Production));

        var descriptors = ConfigurationParameterParser.Describe(options);

        var mode = Assert.Single(descriptors, descriptor => descriptor.Path.ToString() == "Service.Mode");
        Assert.Equal("service:mode", mode.ConfigurationKey);
        Assert.Equal("--execution-mode", mode.CliName);
        Assert.Equal("Execution mode", mode.Description);
        Assert.True(mode.Required);
        Assert.Equal(["dev", "prod"], mode.AllowedValues);

        var apiKey = Assert.Single(descriptors, descriptor => descriptor.Path.ToString() == "Api.ApiKey");
        Assert.Equal("--api-key", apiKey.CliName);
        Assert.Equal("-k", apiKey.CliShortName);
    }

    [Fact]
    public void Parse_BindsPrivateSetterProperties_AndSkipsReadOnlyProperties()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["mutable-value"] = "bound",
                ["nested:private-number"] = "42"
            })
            .Build();

        var parsed = ConfigurationParameterParser.Parse<EncapsulatedConfiguration>(configuration);

        Assert.Equal("bound", parsed.MutableValue);
        Assert.Equal(42, parsed.Nested.PrivateNumber);
        Assert.Equal("constant", parsed.ReadOnlyValue);
        Assert.Equal("fixed", parsed.Nested.ReadOnlyNestedValue);
    }

    [Fact]
    public void Parse_BindsStringCollections_FromIndexedAndCommaSeparatedConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["projection-ids:0"] = "projection-a",
                ["projection-ids:1"] = "projection-b",
                ["training-example-ids"] = "example-a, example-b"
            })
            .Build();

        var parsed = ConfigurationParameterParser.Parse<CollectionConfiguration>(configuration);

        Assert.NotNull(parsed.ProjectionIds);
        Assert.NotNull(parsed.TrainingExampleIds);
        Assert.Equal(["projection-a", "projection-b"], parsed.ProjectionIds);
        Assert.Equal(["example-a", "example-b"], parsed.TrainingExampleIds.ToArray());
    }

    [Fact]
    public void Parse_BindsStringCollections_FromRepeatedAndCommaSeparatedCliArguments()
    {
        var parsed = ConfigurationParameterParser.Parse<CollectionConfiguration>(
            args:
            [
                "--projection-ids", "projection-a",
                "--projection-ids", "projection-b",
                "--training-example-ids", "example-a, example-b"
            ]);

        Assert.NotNull(parsed.ProjectionIds);
        Assert.NotNull(parsed.TrainingExampleIds);
        Assert.Equal(["projection-a", "projection-b"], parsed.ProjectionIds);
        Assert.Equal(["example-a", "example-b"], parsed.TrainingExampleIds.ToArray());
    }

    [Fact]
    public void Describe_DoesNotIncludeReadOnlyProperties()
    {
        var descriptors = ConfigurationParameterParser.Describe<EncapsulatedConfiguration>();

        Assert.DoesNotContain(descriptors, descriptor => descriptor.Path.ToString() == "ReadOnlyValue");
        Assert.DoesNotContain(descriptors, descriptor => descriptor.Path.ToString() == "Nested.ReadOnlyNestedValue");
        Assert.Contains(descriptors, descriptor => descriptor.Path.ToString() == "MutableValue");
        Assert.Contains(descriptors, descriptor => descriptor.Path.ToString() == "Nested.PrivateNumber");
    }

    static MemoryStream JsonStream(string json) => new(Encoding.UTF8.GetBytes(json));

    public sealed class ApplicationConfiguration
    {
        [ConfigurationParameter("service")]
        public ServiceConfiguration Service { get; init; } = new();

        [ConfigurationParameter("api")]
        public ApiConfiguration Api { get; init; } = new();
    }
    
    public sealed class ServiceConfiguration
    {
        [ConfigurationParameter("endpoint", CliKey = "service-endpoint", Description = "Service endpoint", Required = true)]
        public string Endpoint { get; init; } = string.Empty;

        [ConfigurationParameter("poll-interval", TimeUnit = ConfigurationTimeUnit.Seconds)]
        public TimeSpan PollInterval { get; init; }

        [ConfigurationParameter("mode")]
        public ExecutionMode Mode { get; init; }
    }

    public sealed class ApiConfiguration
    {
        [ConfigurationParameter("api-key", CliKey = "api-key", CliShortKey = "k", Description = "Shared API key", Required = true)]
        public string ApiKey { get; init; } = string.Empty;
    }

    public sealed class DatabaseApplicationConfiguration
    {
        public DatabaseConfiguration Database { get; init; } = new();
    }

    public sealed class DatabaseConfiguration
    {
        public string Host { get; init; } = string.Empty;

        public int Port { get; init; }
    }

    public sealed class EncapsulatedConfiguration
    {
        [ConfigurationParameter("mutable-value")]
        public string MutableValue { get; private set; } = string.Empty;

        [ConfigurationParameter("read-only-value", Required = true)]
        public string ReadOnlyValue => "constant";

        [ConfigurationParameter("nested")]
        public EncapsulatedNestedConfiguration Nested { get; private set; } = new();
    }

    public sealed class EncapsulatedNestedConfiguration
    {
        [ConfigurationParameter("private-number")]
        public int PrivateNumber { get; private set; }

        [ConfigurationParameter("read-only-nested-value", Required = true)]
        public string ReadOnlyNestedValue => "fixed";
    }

    public sealed class CollectionConfiguration
    {
        [ConfigurationParameter("projection-ids")]
        public string[]? ProjectionIds { get; init; }

        [ConfigurationParameter("training-example-ids")]
        public IEnumerable<string>? TrainingExampleIds { get; init; }
    }

    public enum ExecutionMode
    {
        Development,
        Production
    }
}
