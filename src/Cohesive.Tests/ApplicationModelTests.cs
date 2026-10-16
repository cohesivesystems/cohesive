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

    public sealed record FirstBindings(string Database);
    public sealed record SecondBindings(string Database);
    public sealed class NativeStorage : Cohesive.Storage.StorageModel
    {
        public NativeStorage()
        {
            First = Storage.Define(() => new FirstBindings("orders"));
            Second = Storage.Define(() => new SecondBindings("inventory"));
        }
        public FirstBindings First { get; }
        public SecondBindings Second { get; }
        public void AddLate() => Storage.Define(() => new FirstBindings("late"));
    }

    [Fact]
    public void Storage_model_owns_multiple_native_binding_types_in_declaration_order()
    {
        var model = ModelActivator.Create<NativeStorage>();
        Assert.Collection(model.Definition,
            value => Assert.Same(model.First, value),
            value => Assert.Same(model.Second, value));
        Assert.Equal(model.Definition, model.Definition);
        Assert.Throws<InvalidOperationException>(model.AddLate);
    }

    [Fact]
    public void Storage_builder_runs_each_recipe_once_and_rejects_edits_after_build()
    {
        var calls = 0;
        var builder = new Cohesive.Storage.StorageModelBuilder();
        var native = builder.Define(() => { calls++; return new FirstBindings("orders"); });
        Assert.Equal(1, calls);
        Assert.Same(native, Assert.Single(builder.Build()));
        Assert.Equal(builder.Build(), builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.Define(() => { calls++; return native; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Failed_storage_recipe_does_not_publish_a_contribution()
    {
        var builder = new Cohesive.Storage.StorageModelBuilder();
        var failure = new InvalidOperationException("invalid mapping");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => builder.Define<object>(() => throw failure)));
        Assert.Throws<InvalidOperationException>(() => builder.Define<object>(() => null!));
        Assert.Throws<ArgumentNullException>(() => builder.Define<object>(null!));
        Assert.Empty(builder.Build());
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
