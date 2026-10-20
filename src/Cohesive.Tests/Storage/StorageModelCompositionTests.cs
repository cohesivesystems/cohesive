using System.Collections.Immutable;
using Cohesive.Storage;

namespace Cohesive.Tests.Storage;

public sealed class StorageModelCompositionTests
{
    sealed record Composition(ImmutableArray<object> Bindings) : IStorageComposition;

    [Fact]
    public void Composition_retains_exact_sources_once_and_freezes_membership()
    {
        var builder = new StorageModelBuilder();
        var first = builder.Define(() => new object());
        var second = builder.Define(() => new object());
        var prepared = builder.Compose(() => new Composition([first, second]));
        Assert.Same(prepared, builder.Composition);
        Assert.Equal(new[] { first, second }, builder.Build());
        Assert.Throws<InvalidOperationException>(() => builder.Define(() => new object()));
        Assert.Throws<InvalidOperationException>(() => builder.Compose(() => new Composition([first])));
    }

    [Fact]
    public void Foreign_source_rejection_does_not_publish_a_partial_composition()
    {
        var builder = new StorageModelBuilder();
        var source = builder.Define(() => new object());
        Assert.Throws<InvalidOperationException>(() => builder.Compose(() => new Composition([new object()])));
        Assert.Null(builder.Composition);
        var prepared = builder.Compose(() => new Composition([source]));
        Assert.Same(prepared, builder.Composition);
    }
}
