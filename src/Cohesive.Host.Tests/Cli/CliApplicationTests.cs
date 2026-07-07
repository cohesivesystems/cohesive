using Cohesive.Host.Cli;
using Cohesive.Host.Cli.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace Cohesive.Host.Tests.Cli;

public sealed class CliApplicationTests
{
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
        app.Command<CollectionCommandConfiguration>("train", "Start a training run").OnExecute((CliCommandContext<CollectionCommandConfiguration> _) => 0);
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
        app.Validate<TrainCommandConfiguration>((TrainCommandConfiguration config) => config.Dataset == "blocked" ? new[] {"Dataset 'blocked' is not allowed."} : []);
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

    [Fact]
    public async Task InvokeAsync_UseHostContext_BindsHandlerDependenciesAndStopsHost()
    {
        TestTrainCommandContext? capturedContext = null;
        TestDependency? capturedDependency = null;
        RecordingHost? host = null;

        var app = new CliApplication("Training jobs");
        app.Command<TrainCommandConfiguration>("train", "Start a training run").OnExecute(ExecuteAsync);
        app.UseHostContext<TrainCommandConfiguration, TestTrainCommandContext>(
            createHost: _ => host = new(new SingleServiceProvider(new("dep-001"))),
            createContext: static context => new(context, stage: "host")
            );

        var validationErrors = app.ValidateDynamicHandlers();
        Assert.Empty(validationErrors);
        var descriptor = Assert.Single(app.Commands);
        Assert.Equal("train", descriptor.Path);
        Assert.Equal(typeof(TestTrainCommandContext), descriptor.EffectiveContextType);
        Assert.NotNull(descriptor.DynamicHandler);

        var exitCode = await app.InvokeAsync(["train", "--dataset", "ds_shipments_v3", "--model", "encoder", "--profile", "local"]);

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedContext);
        Assert.NotNull(capturedDependency);
        Assert.NotNull(host);
        Assert.Equal("ds_shipments_v3", capturedContext!.Configuration.Dataset);
        Assert.Equal("host", capturedContext.Stage);
        Assert.Equal("dep-001", capturedDependency!.Id);
        Assert.Equal(1, host!.StartCount);
        Assert.Equal(1, host.StopCount);
        Assert.Equal(1, host.DisposeCount);
        return;

        Task<int> ExecuteAsync(TestTrainCommandContext context, TestDependency dependency)
        {
            capturedContext = context;
            capturedDependency = dependency;
            return Task.FromResult(0);
        }
    }

    [Fact]
    public async Task InvokeAsync_UseHostContext_CreatesPerInvocationServiceScope()
    {
        var disposedCount = 0;
        TestScopedDependency? capturedDependency = null;
        RecordingHost? host = null;

        var services = new ScopedDependencyRootProvider(() => disposedCount++);

        var app = new CliApplication("Training jobs");
        app.Command<TrainCommandConfiguration>("train", "Start a training run")
            .OnExecute((TestTrainCommandContext _, TestScopedDependency dependency) =>
            {
                capturedDependency = dependency;
                return 0;
            });
        app.UseHostContext<TrainCommandConfiguration, TestTrainCommandContext>(
            createHost: _ => host = new RecordingHost(services),
            createContext: static context => new TestTrainCommandContext(context, stage: "scoped")
        );

        var result = await CliApplicationTestHarness.InvokeAsync(
            app,
            ["train", "--dataset", "ds_shipments_v3", "--model", "encoder", "--profile", "local"]);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(capturedDependency);
        Assert.True(capturedDependency!.Disposed);
        Assert.Equal(1, disposedCount);
        Assert.NotNull(host);
        Assert.Equal(1, host!.DisposeCount);
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

    public sealed record TestDependency(string Id);

    sealed class TestScopedDependency(Action onDispose) : IDisposable
    {
        readonly Action onDispose = onDispose;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            onDispose();
        }
    }

    sealed class ScopedDependencyRootProvider(Action onDispose) : IServiceProvider, IServiceScopeFactory
    {
        readonly Action onDispose = onDispose;

        public object? GetService(Type serviceType) => serviceType == typeof(IServiceScopeFactory) ? this : null;

        public IServiceScope CreateScope() => new ScopedDependencyScope(new ScopedDependencyScopeProvider(onDispose));
    }

    sealed class ScopedDependencyScope : IServiceScope
    {
        readonly IServiceProvider provider;

        public ScopedDependencyScope(IServiceProvider provider) => this.provider = provider;

        public IServiceProvider ServiceProvider => provider;

        public void Dispose()
        {
            if (provider is IDisposable disposable)
                disposable.Dispose();
        }
    }

    sealed class ScopedDependencyScopeProvider(Action onDispose) : IServiceProvider, IDisposable
    {
        readonly TestScopedDependency dependency = new(onDispose);

        public object? GetService(Type serviceType) => serviceType == typeof(TestScopedDependency) ? dependency : null;

        public void Dispose() => dependency.Dispose();
    }

    sealed class SingleServiceProvider(TestDependency dependency) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(TestDependency) ? dependency : null;
    }

    sealed class RecordingHost(IServiceProvider services) : IHost
    {
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public IServiceProvider Services { get; } = services;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCount++;
    }

    sealed class TestTrainCommandContext(
        CliCommandContext<TrainCommandConfiguration> context,
        string stage
        ) : CliCommandContext<TrainCommandConfiguration>(context)
    {
        public string Stage { get; } = stage;
    }
}
