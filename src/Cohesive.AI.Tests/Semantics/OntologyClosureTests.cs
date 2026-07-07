using Cohesive.AI.Semantics;

namespace Cohesive.AI.Tests.Semantics;

public sealed class OntologyClosureTests
{
    static readonly Ontology SharedOntology = CreateOntology();
    static readonly OntologyClosure SharedClosure = OntologyClosure.Create(SharedOntology);

    [Fact]
    public void Builder_AddParentAndChild_EmitsSubConceptRelations_ForEdi204PartyRoles()
    {
        Assert.Contains(
            new ConceptRelation(TestData.Edi204ConceptIds.PartyBillTo, TestData.Edi204ConceptIds.PartyRole, StandardRelationTypeIds.SubConceptOf),
            SharedOntology.Relations
            );
        Assert.Contains(
            new ConceptRelation(TestData.Edi204ConceptIds.PartyShipTo, TestData.Edi204ConceptIds.PartyRole, StandardRelationTypeIds.SubConceptOf),
            SharedOntology.Relations
            );
    }

    [Fact]
    public void Closure_ResolvesScopedSymbolRules_ForEdi204N101QualifierDomain()
    {
        Assert.True(SharedClosure.TryGetScopedMeaning(TestData.Edi204Scopes.QualifiedN101, TestData.Edi204Codes.ShipTo, out var shipToConceptId));
        Assert.Equal(TestData.Edi204ConceptIds.PartyShipTo, shipToConceptId);

        Assert.True(SharedClosure.TryGetScopedMeaning(TestData.Edi204Scopes.QualifiedN101, TestData.Edi204Codes.BillTo, out var billToConceptId));
        Assert.Equal(TestData.Edi204ConceptIds.PartyBillTo, billToConceptId);

        Assert.True(SharedClosure.TryGetAllowedSymbols(TestData.Edi204Scopes.QualifiedN101, out var allowedSymbols));
        Assert.Equal(TestData.Edi204Codes.BillTo, allowedSymbols[0]);
        Assert.Equal(TestData.Edi204Codes.ShipTo, allowedSymbols[1]);
    }

    [Fact]
    public void Closure_AppliesDisjointAndSubConceptRules_ForEdi204PartySpecializations()
    {
        Assert.True(SharedClosure.IsSubConceptOf(TestData.Edi204ConceptIds.PartyShipTo, TestData.Edi204ConceptIds.PartyRole));
        Assert.True(SharedClosure.IsSubConceptOf(TestData.Edi204ConceptIds.PartyBillTo, TestData.Edi204ConceptIds.PartyRole));
        Assert.True(SharedClosure.IsDisjoint(TestData.Edi204ConceptIds.PartyShipTo, TestData.Edi204ConceptIds.PartyBillTo));
    }

    [Fact]
    public void Closure_ComputesTaxonomyHopDistance_ForAncestorPairs()
    {
        Assert.True(SharedClosure.TryGetTaxonomyDistance(TestData.Edi204ConceptIds.PartyShipTo, TestData.Edi204ConceptIds.PartyRole, out var shipToToRoleDistance));
        Assert.Equal(1, shipToToRoleDistance);

        Assert.True(SharedClosure.TryGetTaxonomyDistance(TestData.Edi204ConceptIds.PartyShipTo, TestData.Edi204ConceptIds.PartyBillTo, out var shipToToBillToDistance));
        Assert.Equal(2, shipToToBillToDistance);
    }

    static Ontology CreateOntology() => new OntologyBuilder()
        .AddConcept(new(TestData.Edi204ConceptIds.PartyRole, "Party Role"))
        .AddConcept(new(TestData.Edi204ConceptIds.PartyShipTo, "Ship To"))
        .AddConcept(new(TestData.Edi204ConceptIds.PartyBillTo, "Bill To"))
        .AddParent(childConceptId: TestData.Edi204ConceptIds.PartyShipTo, parentConceptId: TestData.Edi204ConceptIds.PartyRole)
        .AddChild(parentConceptId: TestData.Edi204ConceptIds.PartyRole, childConceptId: TestData.Edi204ConceptIds.PartyBillTo)
        .AddDisjoint(TestData.Edi204ConceptIds.PartyShipTo, TestData.Edi204ConceptIds.PartyBillTo)
        .AddScopedMeaning(scope: TestData.Edi204Scopes.QualifiedN101, symbol: TestData.Edi204Codes.ShipTo, TestData.Edi204ConceptIds.PartyShipTo)
        .AddScopedMeaning(scope: TestData.Edi204Scopes.QualifiedN101, symbol: TestData.Edi204Codes.BillTo, TestData.Edi204ConceptIds.PartyBillTo)
        .AddAllowedSymbols(TestData.Edi204Scopes.QualifiedN101, TestData.Edi204Codes.ShipTo, TestData.Edi204Codes.BillTo)
        .Build();
}
