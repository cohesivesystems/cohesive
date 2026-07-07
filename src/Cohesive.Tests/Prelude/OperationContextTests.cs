namespace Cohesive.Tests.Prelude;

public sealed class OperationContextTests
{
    [Fact]
    public void AmbientAccessor_Push_RestoresPriorContext()
    {
        using var services = new ServiceCollection()
            .AddCohesiveOperationContext()
            .BuildServiceProvider();

        var accessor = services.GetRequiredService<IOperationContextAccessor>();
        var factory = services.GetRequiredService<IOperationContextFactory>();
        var scopeFactory = services.GetRequiredService<IOperationContextScopeFactory>();

        var outer = factory.Create().WithItem("Scope", "outer");
        var inner = factory.Create().WithItem("Scope", "inner");

        Assert.Null(accessor.Current);

        using (scopeFactory.Push(outer))
        {
            Assert.Same(outer, accessor.Current);

            using (scopeFactory.Push(inner))
                Assert.Same(inner, accessor.Current);

            Assert.Same(outer, accessor.Current);
        }

        Assert.Null(accessor.Current);
    }
}
