using Cohesive.Host.Composition;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Prelude;
using Cohesive.Execution;

namespace Cohesive.Tests;

public sealed class ApplicationModelTests
{
    public sealed class Dependency : IAuthoringModel
    {
        public bool Completed { get; private set; }
        public int CompletionCount { get; private set; }
        public void Complete() { Completed = true; CompletionCount++; }
    }
    public sealed class Consumer(Dependency dependency)
    {
        public Dependency Dependency { get; } = dependency;
        public bool PreparedAtConstruction { get; } = dependency.Completed;
    }
    public sealed class First : IExecutionDefinitionModel
    {
        public ExecutionDefinitionDocument Document { get; } = new InstanceConstantProcess("first").Definition.Document;
    }
    public sealed class Second : IExecutionDefinitionModel
    {
        public ExecutionDefinitionDocument Document { get; } = new InstanceConstantProcess("second").Definition.Document;
    }
    public sealed class CycleA(CycleB other) { public CycleB Other { get; } = other; }
    public sealed class CycleB(CycleA other) { public CycleA Other { get; } = other; }

    public sealed class Application(Dependency dependency, Consumer consumer) : ApplicationModel
    {
        public Dependency Dependency { get; } = dependency;
        public Consumer Consumer { get; } = consumer;
    }
    public interface ISettings { string Name { get; } }
    public sealed class Settings : ISettings { public string Name => "selected"; }
    public sealed class Configured(ISettings settings) { public ISettings Settings { get; } = settings; }
    public sealed class DisposableModel : IDisposable { public void Dispose() { } }
    public sealed class InvalidDependency : IAuthoringModel
    {
        public void Complete() => throw new InvalidOperationException("invalid declaration");
    }
    public sealed class MustNotConstruct
    {
        public MustNotConstruct(InvalidDependency dependency) => throw new Exception("Consumer must not run before invalid dependency fails.");
    }

    public sealed class NativeStorage(Func<object> define) : Cohesive.Storage.StorageModel<object>(define);

    [Fact]
    public void Storage_model_reuses_its_native_builder_result_and_caches_failure()
    {
        var calls = 0;
        var native = new object();
        var model = new NativeStorage(() => { calls++; return native; });
        Assert.Equal(0, calls);
        ((IAuthoringModel)model).Complete();
        Assert.Same(native, model.Bindings);
        Assert.Same(native, model.Bindings);
        Assert.Equal(1, calls);
        var failure = new InvalidOperationException("invalid mapping");
        var invalid = new NativeStorage(() => throw failure);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => ((IAuthoringModel)invalid).Complete()));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => invalid.Bindings));
    }

    [Fact]
    public void Activation_infers_shared_dependencies_and_prepares_them_before_constructor_consumption()
    {
        var first = ModelActivator.Create<Application>();
        var second = ModelActivator.Create<Application>();
        Assert.Same(first.Dependency, first.Consumer.Dependency);
        Assert.True(first.Consumer.PreparedAtConstruction);
        Assert.Same(first.Dependency, first.Composition.Get<Dependency>());
        Assert.Equal(1, first.Dependency.CompletionCount);
        Assert.NotSame(first.Dependency, second.Dependency);
    }

    [Fact]
    public void Standard_DI_registration_selects_an_interface_implementation() =>
        Assert.Equal("selected", ModelActivator.Create<Configured>(services => services.AddSingleton<ISettings, Settings>()).Settings.Name);

    [Fact]
    public void Activation_rejects_missing_interface_registration() =>
        Assert.Contains(nameof(ISettings), Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<Configured>()).Message);

    [Fact]
    public void Activation_reports_cycle_path()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<CycleA>());
        Assert.Contains(nameof(CycleA), error.Message);
        Assert.Contains(nameof(CycleB), error.Message);
    }

    [Fact]
    public void Invalid_dependency_fails_before_consumer_constructor() =>
        Assert.Equal("invalid declaration", Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<MustNotConstruct>()).Message);

    [Fact]
    public void Runtime_resource_and_scoped_registration_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ModelActivator.Create<DisposableModel>());
        Assert.Throws<ArgumentException>(() => ModelActivator.Create<Configured>(services => services.AddScoped<ISettings, Settings>()));
    }

    [Fact]
    public void Constructor_order_is_resolved_and_authoring_completed_once()
    {
        var builder = new ApplicationModelBuilder().Add<Consumer>().Add<Dependency>();
        var model = builder.Build();
        Assert.Same(model.Get<Dependency>(), model.Get<Consumer>().Dependency);
        Assert.True(model.Get<Dependency>().Completed);
        Assert.True(model.Get<Consumer>().PreparedAtConstruction);
        Assert.Same(model, builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.Add<First>());
    }

    [Fact]
    public void Conflicting_process_revision_fails_composition_with_canonical_diagnostics()
    {
        var builder = new ApplicationModelBuilder().Add<First>().Add<Second>();
        var error = Assert.Throws<ApplicationModelPreparationException>(() => builder.Build());
        Assert.Contains(error.Validation.Diagnostics, d => d.Code == ExecutionDefinitionDiagnosticCodes.CatalogRevisionDuplicate);
        Assert.Same(error, Assert.Throws<ApplicationModelPreparationException>(() => builder.Build()));
    }

    [Fact]
    public void Missing_dependency_fails_at_setup() => Assert.Throws<InvalidOperationException>(
        () => new ApplicationModelBuilder().Add<Consumer>().Build());

    [Fact]
    public void Cyclic_dependency_fails_at_setup() => Assert.Throws<InvalidOperationException>(
        () => new ApplicationModelBuilder().Add<CycleA>().Add<CycleB>().Build());

    [Fact]
    public void Failed_preparation_closes_authoring_and_caches_failure()
    {
        var count = 0;
        var state = new AuthoringState<object>(() => { count++; throw new ArgumentException("invalid"); });
        var error = Assert.Throws<ArgumentException>(() => state.Value);
        Assert.Same(error, Assert.Throws<ArgumentException>(() => state.Value));
        Assert.Equal(1, count);
        Assert.Throws<InvalidOperationException>(() => state.Edit(() => 0));
    }

    [Fact]
    public void Concurrent_freezing_prepares_once()
    {
        var count = 0;
        var state = new AuthoringState<object>(() => { Interlocked.Increment(ref count); return new(); });
        var results = new object[64];
        Parallel.For(0, results.Length, i => results[i] = state.Value);
        Assert.All(results, value => Assert.Same(results[0], value));
        Assert.Equal(1, count);
    }
}
