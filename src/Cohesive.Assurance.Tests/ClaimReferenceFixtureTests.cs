using Cohesive.Authoring;
using Cohesive.Assurance.Examples;

namespace Cohesive.Assurance.Tests;

public sealed class ClaimReferenceFixtureTests
{
    public sealed record Inventory(decimal OnHand, decimal Reserved);

    [Fact]
    public void ArticleDeclarationsSupplyTheirOwnCompleteReferenceCatalog()
    {
        var refs = new FixtureReferences("example://fulfillment", revision: "1");
        var inventory = refs.Define("subjects/inventory", "Inventory with decimal OnHand and Reserved quantities.");
        var observation = refs.Define("contracts/observations", "Inventory states bind State; source coverage and closure are explicit.");
        var source = refs.Define("sources/article-claims", "Inventory safety declaration in the fulfillment walkthrough.");
        var config = refs.Define("configuration/authoring", "Typed claim authoring using decimal quantities.");
        var population = refs.Define("selectors/inventory", "Inventory states in the selected fulfillment scope.");
        var input = new ExpressionAuthoring().Input<Inventory>("inventory");
        var claim = Claim.Define("inventory.available", "1", inventory, observation, source, config)
            .AtEveryCommittedState(population, InitialStateSelection.Include)
            .Require(input, ClaimInputRole.State, i => i.Reserved <= i.OnHand).Build();
        Assert.True(claim.ValidateReferences(refs.References).IsValid);
        Assert.Equal("example://fulfillment/subjects/inventory", inventory.Source.Value);
        Assert.Equal("1", inventory.Revision);
        Assert.StartsWith("sha256:cohesive-assurance-reference-fixture/v1:", inventory.Fingerprint);
    }

    [Fact]
    public void FingerprintsAreDeterministicTextSensitiveAndIndependentOfDeclarationOrder()
    {
        var first = new FixtureReferences("example://fulfillment/", "1");
        var b = first.Define("b", "Second definition");
        var a = first.Define("a", "First definition\nwith exact line endings.");
        var second = new FixtureReferences("example://fulfillment", "1");
        Assert.Equal(a, second.Define("a", "First definition\nwith exact line endings."));
        Assert.Equal(b, second.Define("b", "Second definition"));
        Assert.Equal<ClaimReference>(first.References, second.References);
        Assert.Same(a, first.Define("a", "First definition\nwith exact line endings."));
        Assert.Throws<ArgumentException>(() => first.Define("a", "Changed definition"));
        var next = new FixtureReferences("example://fulfillment", "2").Define("a", "Changed definition");
        Assert.NotEqual(a.Fingerprint, next.Fingerprint);
        Assert.Equal("2", next.Revision);
    }

    [Fact]
    public void CatalogsAreImmutableSnapshots()
    {
        var refs = new FixtureReferences("example://fulfillment", "1");
        refs.Define("a", "One");
        var retained = refs.References;
        Assert.True(retained.Equals(refs.References)); // Unchanged reads reuse the same immutable storage.
        refs.Define("b", "Two");
        Assert.Single(retained);
        Assert.Equal(2, refs.References.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../other")]
    [InlineData("a/../b")]
    [InlineData("/a")]
    [InlineData("a//b")]
    [InlineData("a?x")]
    [InlineData("a#x")]
    [InlineData("a%2fb")]
    [InlineData("a\\b")]
    [InlineData("a b")]
    public void InvalidPathsCannotCreateAmbiguousFixtureReferences(string path)
    {
        var refs = new FixtureReferences("example://fulfillment", "1");
        Assert.Throws<ArgumentException>(() => refs.Define(path, "Definition"));
        Assert.Empty(refs.References);
    }
}
