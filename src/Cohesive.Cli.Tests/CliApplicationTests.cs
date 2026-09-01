using System.CommandLine;
using Cohesive.Cli.Testing;
using Cohesive.Configuration;
using Microsoft.Extensions.Configuration;

namespace Cohesive.Cli.Tests;

public sealed class CliApplicationTests
{
    [Fact]
    public void ServiceResolution_WithoutAttachedProvider_ReturnsNullForOptionalAndThrowsForRequired()
    {
        var context = new CliCommandContext(
            configurationRoot: new ConfigurationBuilder().Build(),
            parseResult: new RootCommand().Parse([]),
            cancellationToken: CancellationToken.None);

        Assert.Null(context.GetService(typeof(TestDependency)));
        var error = Assert.Throws<InvalidOperationException>(context.GetRequiredService<TestDependency>);
        Assert.Contains("does not have an attached service provider", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_ParsesCommandOptionsIntoTypedConfiguration()
    {
        var envPrefix = $"COHESIVE_CLI_{Guid.NewGuid():N}_";
        try
        {
            Environment.SetEnvironmentVariable($"{envPrefix}MODEL", "logistics-encoder");

            TrainCommandConfiguration? captured = null;
            var app = new CliApplication(description: "Training jobs")
                .WithConfiguration(builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["profile"] = "local-default",
                    ["scheduler:poll-interval"] = "15"
                }))
                .WithEnvironmentVariablePrefix(envPrefix);

            app.Command<TrainCommandConfiguration>("train", "Start a training run")
                .OnExecute((CliCommandContext<TrainCommandConfiguration> context) =>
                {
                    captured = context.Configuration;
                    return 0;
                });

            var exitCode = await app.InvokeAsync(
                [
                    "train",
                    "--dataset", "ds_shipments_v3",
                    "--profile", "azure-dev"
                ]);

            Assert.Equal(0, exitCode);
            Assert.NotNull(captured);
            Assert.Equal("ds_shipments_v3", captured!.Dataset);
            Assert.Equal("logistics-encoder", captured.Model);
            Assert.Equal("azure-dev", captured.Profile);
            Assert.Equal(TimeSpan.FromSeconds(15), captured.Scheduler.PollInterval);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"{envPrefix}MODEL", null);
        }
    }

    [Fact]
    public async Task InvokeAsync_ParsesSubcommandsIntoTypedConfiguration()
    {
        StopTrainingCommandConfiguration? captured = null;

        var app = new CliApplication(description: "Training jobs");
        app.Command<TrainCommandConfiguration>("train", "Start a training run")
            .OnExecute((Action<CliCommandContext<TrainCommandConfiguration>>)(_ => throw new Xunit.Sdk.XunitException("Root train handler should not be called.")))
            .SubCommand<StopTrainingCommandConfiguration>("stop", "Stop a training run")
            .OnExecute((CliCommandContext<StopTrainingCommandConfiguration> context) =>
            {
                captured = context.Configuration;
                return 0;
            });

        var exitCode = await app.InvokeAsync(["train", "stop", "--run-id", "123"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal("123", captured!.RunId);
    }

    [Fact]
    public async Task InvokeAsync_UsesPositionalArgumentsWhenConfigured()
    {
        StopTrainingCommandConfiguration? captured = null;

        var app = new CliApplication("Training jobs");
        var command = app.Command<StopTrainingCommandConfiguration>(name: "stop", description: "Stop a training run");
        command.Argument(configuration => configuration.RunId).WithName("run-id");
        command.OnExecute((CliCommandContext<StopTrainingCommandConfiguration> context) =>
        {
            captured = context.Configuration;
            return 0;
        });

        var exitCode = await app.InvokeAsync(["stop", "run-123"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal("run-123", captured!.RunId);
    }

    [Fact]
    public async Task InvokeAsync_BindsRepeatedOptionsIntoStringCollections()
    {
        CollectionCommandConfiguration? captured = null;

        var app = new CliApplication("Training jobs");
        app.Command<CollectionCommandConfiguration>("train", "Start a training run")
            .OnExecute((CliCommandContext<CollectionCommandConfiguration> context) =>
            {
                captured = context.Configuration;
                return 0;
            });

        var exitCode = await app.InvokeAsync(["train", "--projection-ids", "projection-a", "--projection-ids", "projection-b"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal(["projection-a", "projection-b"], captured!.ProjectionIds);
    }

    [Fact]
    public async Task InvokeAsync_BindsCommaSeparatedOptionsIntoStringCollections()
    {
        CollectionCommandConfiguration? captured = null;

        var app = new CliApplication("Training jobs");
        app.Command<CollectionCommandConfiguration>("train", "Start a training run")
            .OnExecute((CliCommandContext<CollectionCommandConfiguration> context) =>
            {
                captured = context.Configuration;
                return 0;
            });

        var exitCode = await app.InvokeAsync(["train", "--projection-ids", "projection-a, projection-b", "--projection-ids", "projection-c"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(captured);
        Assert.Equal(["projection-a", "projection-b", "projection-c"], captured!.ProjectionIds);
    }

    [Fact]
    public async Task RunOrFallbackAsync_RunsFallbackWhenNoRegisteredCommandMatches()
    {
        var app = new CliApplication("Training jobs");
        app.Command<TrainCommandConfiguration>("train", "Start a training run")
            .OnExecute((Action<CliCommandContext<TrainCommandConfiguration>>)(_ => throw new Xunit.Sdk.XunitException("CLI handler should not be called.")));

        IReadOnlyList<string>? capturedArgs = null;
        var exitCode = await app.RunOrFallbackAsync(
            ["--urls", "http://localhost:5000"],
            runDefaultAsync: (args, _) =>
            {
                capturedArgs = args;
                return Task.FromResult(17);
            });

        Assert.Equal(17, exitCode);
        Assert.Equal(["--urls", "http://localhost:5000"], capturedArgs);
    }

    [Fact]
    public async Task InvokeAsync_AppliesApplicationParameterConventionsToExistingCommands()
    {
        var app = new CliApplication("Training jobs");
        app.Command<CollectionCommandConfiguration>("train", "Start a training run")
            .OnExecute((CliCommandContext<CollectionCommandConfiguration> _) => 0);
        app.ConfigureParameters<CollectionCommandConfiguration>(options =>
            options.Map(configuration => configuration.ProjectionIds).WithAllowedValues("projection-a")
        );

        var exitCode = await app.InvokeAsync(["train", "--projection-ids", "projection-b"]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task InvokeAsync_ValidationPipeline_WritesErrorsAndSkipsHandler()
    {
        var handlerCalled = false;
        var app = new CliApplication("Training jobs");
        app.Validate<TrainCommandConfiguration>((TrainCommandConfiguration config) => config.Dataset == "blocked" ? new[] { "Dataset 'blocked' is not allowed." } : []);
        app.Command<TrainCommandConfiguration>("train", "Start a training run")
            .OnExecute((CliCommandContext<TrainCommandConfiguration> _) =>
            {
                handlerCalled = true;
                return 0;
            });

        var result = await CliApplicationTestHarness.InvokeAsync(
            app,
            ["train", "--dataset", "blocked", "--model", "encoder", "--profile", "local"]);

        Assert.Equal(1, result.ExitCode);
        Assert.False(handlerCalled);
        Assert.Contains("Dataset 'blocked' is not allowed.", result.ErrorOutput);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    public sealed class TrainCommandConfiguration
    {
        [ConfigurationParameter("dataset", CliKey = "dataset", Description = "Dataset identifier", Required = true)]
        public string Dataset { get; init; } = string.Empty;

        [ConfigurationParameter("model", CliKey = "model", Description = "Model identifier", Required = true)]
        public string Model { get; init; } = string.Empty;

        [ConfigurationParameter("profile", CliKey = "profile", Description = "Runtime profile", Required = true)]
        public string Profile { get; init; } = string.Empty;

        [ConfigurationParameter("scheduler")]
        public SchedulerConfiguration Scheduler { get; init; } = new();
    }

    public sealed class SchedulerConfiguration
    {
        [ConfigurationParameter("poll-interval", CliKey = "poll-interval", TimeUnit = ConfigurationTimeUnit.Seconds)]
        public TimeSpan PollInterval { get; init; }
    }

    public sealed class StopTrainingCommandConfiguration
    {
        [ConfigurationParameter("run-id", CliKey = "run-id", Description = "Training run identifier", Required = true)]
        public string RunId { get; init; } = string.Empty;
    }

    public sealed class CollectionCommandConfiguration
    {
        [ConfigurationParameter("projection-ids", CliKey = "projection-ids", Description = "Projection identifiers")]
        public string[] ProjectionIds { get; init; } = [];
    }

    sealed class TestDependency;
}
