using Cohesive.Cli;
using Cohesive.Cli.Testing;
using Cohesive.Host.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cohesive.Host.Tests.Cli;

public sealed class CliHostContextBuilderExtensionsTests
{
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

    sealed class ScopedDependencyScope(IServiceProvider provider) : IServiceScope
    {
        readonly IServiceProvider provider = provider;

        public IServiceProvider ServiceProvider => provider;

        public void Dispose()
        {
            if (provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
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
