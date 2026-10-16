using Cohesive.Host.Composition;
using Cohesive.Prelude;
using Cohesive.Execution;

namespace Cohesive.Tests;

public sealed class ApplicationModelTests
{
    public sealed class Dependency : IAuthoringModel
    {
        public bool Completed { get; private set; }
        public void Complete() => Completed = true;
    }
    public sealed class Consumer(Dependency dependency)
    {
        public Dependency Dependency { get; } = dependency;
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

    [Fact]
    public void Constructor_order_is_resolved_and_authoring_completed_once()
    {
        var builder = new ApplicationModelBuilder().Add<Consumer>().Add<Dependency>();
        var model = builder.Build();
        Assert.Same(model.Get<Dependency>(), model.Get<Consumer>().Dependency);
        Assert.True(model.Get<Dependency>().Completed);
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
