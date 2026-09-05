using System.CommandLine;
using System.Text;
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
    public async Task Help_UsesInvocationOutputChannels()
    {
        var app = new CliApplication("Training jobs");
        app.Command<TrainCommandConfiguration>("train", "Start a training run")
            .OnExecute((CliCommandContext<TrainCommandConfiguration> _) => 0);

        var result = await CliApplicationTestHarness.InvokeAsync(app, ["train", "--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Start a training run", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(result.ErrorOutput);
    }

    [Fact]
    public async Task InvokeAsync_ExposesApplicationIoToCommandContext()
    {
        await using MemoryStream standardInput = new(Encoding.UTF8.GetBytes("fixture-input"));
        await using MemoryStream standardOutput = new();
        using StringWriter standardError = new();
        var io = CommandIo.Null(
            standardInput: standardInput,
            standardOutput: standardOutput,
            standardError: standardError);
        var app = new CliApplication(description: "Training jobs", io);
        app.Command<TrainCommandConfiguration>("train")
            .OnExecute(async (CliCommandContext<TrainCommandConfiguration> context) =>
            {
                Assert.Same(io, context.Io);
                Assert.Same(standardInput, context.Io.StandardInput);
                Assert.Same(standardOutput, context.Io.StandardOutput);
                context.Io.WriteLine(await context.Io.ReadUtf8TextAsync(CommandIo.StandardStreamPath));
                return 0;
            });

        var exitCode = await app.InvokeAsync(
            ["train", "--dataset", "shipments", "--model", "encoder", "--profile", "local"]);

        Assert.Equal(0, exitCode);
        Assert.Equal($"fixture-input{Environment.NewLine}", Encoding.UTF8.GetString(standardOutput.ToArray()));
        Assert.Empty(standardError.ToString());
    }

    [Fact]
    public async Task RunAsync_LinksExplicitInvocationCancellation()
    {
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var app = new CliApplication("Training jobs");
        app.Command<TrainCommandConfiguration>("train")
            .OnExecute(async (CliCommandContext<TrainCommandConfiguration> context) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    // Observe cancellation in the handler itself. A separate registration can be disposed
                    // by this continuation before the cancelling thread reaches that callback.
                    cancellationObserved.SetResult();
                    throw;
                }
                return 0;
            });

        var invocation = app.RunAsync(
            ["train", "--dataset", "shipments", "--model", "encoder", "--profile", "local"],
            cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(0, await invocation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Validate_AcceptsTypedMethodGroupWithoutDelegateCast()
    {
        var handlerCalled = false;
        var app = new CliApplication("Training jobs");
        app.Command<TrainCommandConfiguration>("train")
            .Validate(RejectBlockedDataset)
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
        Assert.Contains("Dataset 'blocked' is not allowed.", result.ErrorOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowStandardInputForAtMostOne_DerivesEffectiveOptionNames()
    {
        var app = new CliApplication("Artifact verification");
        var command = app.Command<InputCommandConfiguration>("verify");
        command.Map(configuration => configuration.ManifestPath).WithCliName("authority");
        command.Map(configuration => configuration.JsonLinesPath).WithCliName("records");
        command
            .AllowStandardInputForAtMostOne(
                configuration => configuration.ManifestPath,
                configuration => configuration.JsonLinesPath)
            .OnExecute((CliCommandContext<InputCommandConfiguration> _) => 0);

        var result = await CliApplicationTestHarness.InvokeAsync(
            app,
            ["verify", "--authority", "-", "--records", "-"]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("'--authority' and '--records'", result.ErrorOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--manifest", result.ErrorOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--jsonl", result.ErrorOutput, StringComparison.Ordinal);
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
        app.Validate<TrainCommandConfiguration>(RejectBlockedDataset);
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

    public sealed class InputCommandConfiguration
    {
        [ConfigurationParameter("manifest", Required = true)]
        public string ManifestPath { get; init; } = string.Empty;

        [ConfigurationParameter("jsonl", Required = true)]
        public string JsonLinesPath { get; init; } = string.Empty;
    }

    static IReadOnlyList<string> RejectBlockedDataset(TrainCommandConfiguration configuration) =>
        string.Equals(configuration.Dataset, "blocked", StringComparison.Ordinal)
            ? ["Dataset 'blocked' is not allowed."]
            : [];

    sealed class TestDependency;
}
