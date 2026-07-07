using Cohesive.Configuration;

namespace Cohesive.Tests.Configuration;

public sealed class DependencySelectionCatalogTests
{
    [Fact]
    public void Resolve_SelectsHighestPriorityMatchingRule()
    {
        var catalog = new DependencySelectionCatalogBuilder<Request, string>()
            .Add(
                name: "low",
                matches: static request => request.Kind == "entity",
                create: static request => $"low:{request.Id}",
                priority: 10)
            .Add(
                name: "high",
                matches: static request => request.Kind == "entity",
                create: static request => $"high:{request.Id}",
                priority: 20)
            .AddFallback(
                name: "fallback",
                create: static request => $"fallback:{request.Id}")
            .Build();

        var result = catalog.Resolve(new("entity", "shape-1"));

        Assert.Equal("high", result.RuleName);
        Assert.Equal("high:shape-1", result.Dependency);
    }

    [Fact]
    public void Resolve_UsesFallbackWhenNoSpecificRuleMatches()
    {
        var catalog = new DependencySelectionCatalogBuilder<Request, string>()
            .Add(
                name: "entity",
                matches: static request => request.Kind == "entity",
                create: static request => $"entity:{request.Id}")
            .AddFallback(
                name: "fallback",
                create: static request => $"fallback:{request.Id}")
            .Build();

        var result = catalog.Resolve(new("process", "proc-1"));

        Assert.Equal("fallback", result.RuleName);
        Assert.Equal("fallback:proc-1", result.Dependency);
    }

    [Fact]
    public void Resolve_ThrowsWhenMatchingRulesHaveSamePriority()
    {
        var catalog = new DependencySelectionCatalogBuilder<Request, string>()
            .Add(
                name: "first",
                matches: static request => request.Kind == "entity",
                create: static request => request.Id,
                priority: 10)
            .Add(
                name: "second",
                matches: static request => request.Kind == "entity",
                create: static request => request.Id,
                priority: 10)
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => catalog.Resolve(new("entity", "shape-1")));

        Assert.Contains("first", error.Message, StringComparison.Ordinal);
        Assert.Contains("second", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryResolve_ReturnsFalseWhenNoRuleMatches()
    {
        var catalog = new DependencySelectionCatalogBuilder<Request, string>()
            .Add(
                name: "entity",
                matches: static request => request.Kind == "entity",
                create: static request => request.Id)
            .Build();

        var resolved = catalog.TryResolve(new("process", "proc-1"), out var result);

        Assert.False(resolved);
        Assert.Null(result);
    }

    [Fact]
    public void Build_RejectsDuplicateRuleNames()
    {
        var builder = new DependencySelectionCatalogBuilder<Request, string>()
            .Add(
                name: "entity",
                matches: static _ => true,
                create: static request => request.Id)
            .Add(
                name: "entity",
                matches: static _ => true,
                create: static request => request.Id,
                priority: 10);

        var error = Assert.Throws<ArgumentException>(() => builder.Build());

        Assert.Contains("Duplicate dependency selection rule 'entity'", error.Message, StringComparison.Ordinal);
    }

    sealed record Request(string Kind, string Id);
}
