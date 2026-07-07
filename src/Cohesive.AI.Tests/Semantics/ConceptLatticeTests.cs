using Cohesive.AI.Semantics;

namespace Cohesive.AI.Tests.Semantics;

public sealed class ConceptLatticeTests
{
    [Fact]
    public void Build_CreatesExpectedIntermediateConcept_ForPartyRoleContext()
    {
        var context = new FormalContext<string, string>(
            objects: [TestData.Edi204Codes.Shipper, TestData.Edi204Codes.ShipTo, TestData.Edi204Codes.BillTo],
            attributes: ["party", "shipping", "destination", "financial"],
            incidence: new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                [TestData.Edi204Codes.Shipper] = ["party", "shipping"],
                [TestData.Edi204Codes.ShipTo] = ["party", "shipping", "destination"],
                [TestData.Edi204Codes.BillTo] = ["party", "financial"]
            });

        var lattice = ConceptLatticeBuilder.Build(context);
        var shippingNode = Assert.Single(lattice.Nodes.Where(x => x.Concept.Extent.SetEquals([TestData.Edi204Codes.Shipper, TestData.Edi204Codes.ShipTo]) && x.Concept.Intent.SetEquals(["party", "shipping"])));

        Assert.NotEmpty(lattice.Nodes);
        Assert.Contains(shippingNode.Parents, parentId => parentId >= 0);
    }

    [Fact]
    public void Compile_ProducesDenseLookupAndDistanceTables()
    {
        var context = new FormalContext<string, string>(
            objects: [TestData.Edi204Codes.Shipper, TestData.Edi204Codes.ShipTo, TestData.Edi204Codes.BillTo],
            attributes: ["party", "shipping", "destination", "financial"],
            incidence: new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                [TestData.Edi204Codes.Shipper] = ["party", "shipping"],
                [TestData.Edi204Codes.ShipTo] = ["party", "shipping", "destination"],
                [TestData.Edi204Codes.BillTo] = ["party", "financial"]
            });

        var lattice = ConceptLatticeBuilder.Build(context);
        var shippingNode = Assert.Single(lattice.Nodes.Where(x => x.Concept.Extent.SetEquals([TestData.Edi204Codes.Shipper, TestData.Edi204Codes.ShipTo]) && x.Concept.Intent.SetEquals(["party", "shipping"])));
        var index = ConceptLatticeCompiler.Compile(lattice);
        var reasoner = new ConceptLatticeReasoner<string, string>(index);

        Assert.True(reasoner.NodeContainsObject(shippingNode.Id, index.DenseObjectByValue[TestData.Edi204Codes.Shipper]));
        Assert.False(reasoner.NodeContainsObject(shippingNode.Id, index.DenseObjectByValue[TestData.Edi204Codes.BillTo]));
        Assert.True(reasoner.NodeContainsAttribute(shippingNode.Id, index.DenseAttributeByValue["party"]));
        Assert.Equal(0, reasoner.ShortestPathDistance(shippingNode.Id, shippingNode.Id));
    }
}
