using Cohesive.Transitions.Authoring;
using System.Text.Json.Serialization;

namespace Cohesive.Tests.Modeling;

public sealed class DomainDiscoveryTests
{
    [Fact]
    public void Discovery_reuses_explicit_projection_and_is_scoped_sorted_and_cached()
    {
        var discovery = DomainDiscovery.For<Module>();
        Assert.Same(discovery, DomainDiscovery.For<Module>());
        var derived = new DomainModelBuilder().Version("1").Discover(discovery).Build();
        var explicitBuilder = new DomainModelBuilder().Version("1");
        explicitBuilder.Entity<Customer>("customer");
        explicitBuilder.Entity<Order>("order");
        var declared = explicitBuilder.Build();
        Assert.Equal(new[] { "customer", "order" }, derived.Entities.Select(e => e.Name.Value));
        Assert.Equal(declared.Entities.Select(e => e.StateShape.QualifiedId), derived.Entities.Select(e => e.StateShape.QualifiedId));
        Assert.True(discovery.Verify(declared, DomainDiscoveryCoverage.ExactMembership).IsValid);
    }

    [Fact]
    public void Verification_is_deferred_and_aggregates_missing_and_conflicting_shapes()
    {
        var builder = new DomainModelBuilder().VerifyDiscovery(DomainDiscovery.For<Module>());
        builder.Entity<Other>("order");
        var error = Assert.Throws<DomainDiscoveryException>(() => builder.Build());
        Assert.Equal(new[] { "domain.discovery.entityMissing", "domain.discovery.shapeMismatch" },
            error.Validation.Diagnostics.Select(d => d.Code));
        Assert.Contains("customer", error.Message);
        Assert.Contains("order", error.Message);
    }

    [Fact]
    public void Exclusions_are_explicit_and_do_not_mutate_the_cached_catalog()
    {
        var original = DomainDiscovery.For<Module>();
        var filtered = original.Exclude<Customer>("Owned by the customer service.");
        Assert.Single(filtered.Entities);
        Assert.Equal(2, original.Entities.Length);
        Assert.Equal("Owned by the customer service.", filtered.Exclusions[typeof(Customer)]);
        Assert.Throws<ArgumentException>(() => original.Exclude<Other>("Not part of this domain"));
        Assert.Throws<ArgumentException>(() => original.Exclude<Customer>(" "));
        var builder = new DomainModelBuilder().VerifyDiscovery(filtered, DomainDiscoveryCoverage.ExactMembership);
        builder.Entity<Order>("order");
        Assert.Single(builder.Build().Entities);
    }

    [Fact]
    public void Exact_membership_rejects_extras_while_coverage_allows_them()
    {
        var discovery = DomainDiscovery.For<Module>();
        var builder = new DomainModelBuilder().Discover(discovery);
        builder.Entity<Other>("extra");
        var model = builder.Build();
        Assert.True(discovery.Verify(model).IsValid);
        Assert.Equal("domain.discovery.entityUnexpected",
            Assert.Single(discovery.Verify(model, DomainDiscoveryCoverage.ExactMembership).Diagnostics).Code);
    }

    [Fact]
    public void Duplicate_discovery_fails_without_partially_changing_builder()
    {
        var builder = new DomainModelBuilder();
        builder.Entity<Order>("order");
        Assert.Throws<ArgumentException>(() => builder.Discover(DomainDiscovery.For<Module>()));
        Assert.Single(builder.Build().Entities);
        Assert.Throws<DomainDiscoveryException>(() => DomainDiscovery.For<DuplicateModule>());
    }

    [Fact]
    public void Empty_module_fails_and_instance_model_freezes_discovery()
    {
        Assert.Throws<DomainDiscoveryException>(() => DomainDiscovery.For<EmptyModule>());
        var model = new DiscoveredModel();
        Assert.Equal(2, model.Definition.Entities.Length);
        Assert.Same(model.Definition, model.Definition);
        Assert.Throws<InvalidOperationException>(model.AddAgain);
    }

    sealed class DiscoveredModel : DomainModel
    {
        public DiscoveredModel() : base("1") => Discover(DomainDiscovery.For<Module>());
        public void AddAgain() => Discover(DomainDiscovery.For<Module>());
    }
    sealed class Module;
    sealed class DuplicateModule;
    sealed class EmptyModule;
    [DomainEntity(typeof(Module), "order")]
    sealed record Order([property: JsonPropertyName("id")] string Id, int Quantity);
    [DomainEntity(typeof(Module), "customer")]
    sealed record Customer(string Id);
    sealed record Other(int Id);
    [DomainEntity(typeof(DuplicateModule), "duplicate")]
    sealed record First(string Id);
    [DomainEntity(typeof(DuplicateModule), "duplicate")]
    sealed record Second(string Id);
}
