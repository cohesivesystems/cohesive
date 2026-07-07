using System.Collections.Immutable;
using Cohesive.AI.Semantics;

namespace Cohesive.AI.Tests.Semantics;

public sealed class OntologyTests
{
    [Fact]
    public void Union_MergesEdi204OntologySlices()
    {
        var transportationSlice = CreateOntology(
            concepts:
            [
                CreateConcept(TestData.Edi204ConceptIds.ShipmentReferenceNumber, kind: Edi204ConceptMetadata.Kinds.Identifier, valueCategory: Edi204ConceptMetadata.ValueCategories.Identifier),
                CreateConcept(TestData.Edi204ConceptIds.DatePickupType, kind: Edi204ConceptMetadata.Kinds.Code, valueCategory: Edi204ConceptMetadata.ValueCategories.Code),
                CreateConcept(TestData.Edi204ConceptIds.DatePickupRequested, kind: Edi204ConceptMetadata.Kinds.Time, valueCategory: Edi204ConceptMetadata.ValueCategories.Date)
            ],
            relations:
            [
                new(sourceConceptId: TestData.Edi204ConceptIds.DatePickupRequested, targetConceptId: TestData.Edi204ConceptIds.DatePickupType, relationTypeId: StandardRelationTypeIds.SubConceptOf)
            ]);

        var partiesAndWeightSlice = CreateOntology(
            concepts:
            [
                CreateConcept(TestData.Edi204ConceptIds.PartyShipToName, kind: Edi204ConceptMetadata.Kinds.Attribute, valueCategory: Edi204ConceptMetadata.ValueCategories.Name, lexicalForms: ["ship to name", "consignee name"]),
                CreateConcept(TestData.Edi204ConceptIds.ShipmentWeightValue, kind: Edi204ConceptMetadata.Kinds.Measure, valueCategory: Edi204ConceptMetadata.ValueCategories.Quantity),
                CreateConcept(TestData.Edi204ConceptIds.ShipmentWeightUom, kind: Edi204ConceptMetadata.Kinds.Code, valueCategory: Edi204ConceptMetadata.ValueCategories.Code, lexicalForms: ["uom", "unit"])
            ]);

        var ontology = Ontology.Union(transportationSlice, partiesAndWeightSlice);

        Assert.Equal(6, ontology.Concepts.Count);
        var pickupRelation = Assert.Single(ontology.Relations);
        Assert.Equal(StandardRelationTypeIds.SubConceptOf, pickupRelation.RelationTypeId);
        Assert.Equal(TestData.Edi204ConceptIds.DatePickupRequested, pickupRelation.SourceConceptId);
        Assert.Equal(TestData.Edi204ConceptIds.DatePickupType, pickupRelation.TargetConceptId);

        var shipToName = ontology.Concepts[TestData.Edi204ConceptIds.PartyShipToName];
        Assert.Equal(Edi204ConceptMetadata.ValueCategories.Name, shipToName.Properties[Edi204ConceptMetadata.PropertyNames.ValueCategory]);
        Assert.Equal(["ship to name", "consignee name"], shipToName.LexicalForms.ToArray());
    }

    [Fact]
    public void Constructor_NormalizesSymmetricEquivalentRelations_ForEdi204LocationAliases()
    {
        var ontology = CreateOntology(
            concepts:
            [
                CreateConcept(TestData.Edi204ConceptIds.ShipmentLocationCity, kind: Edi204ConceptMetadata.Kinds.Location, valueCategory: Edi204ConceptMetadata.ValueCategories.City),
                CreateConcept(TestData.Edi204ConceptIds.ShipmentLocationMunicipality, kind: Edi204ConceptMetadata.Kinds.Location, valueCategory: Edi204ConceptMetadata.ValueCategories.City)
            ],
            relations:
            [
                new(TestData.Edi204ConceptIds.ShipmentLocationMunicipality, TestData.Edi204ConceptIds.ShipmentLocationCity, StandardRelationTypeIds.EquivalentTo),
                new(TestData.Edi204ConceptIds.ShipmentLocationCity, TestData.Edi204ConceptIds.ShipmentLocationMunicipality, StandardRelationTypeIds.EquivalentTo)
            ]);

        var relation = Assert.Single(ontology.Relations);
        Assert.Equal(StandardRelationTypeIds.EquivalentTo, relation.RelationTypeId);
        Assert.Equal(TestData.Edi204ConceptIds.ShipmentLocationCity, relation.SourceConceptId);
        Assert.Equal(TestData.Edi204ConceptIds.ShipmentLocationMunicipality, relation.TargetConceptId);
    }

    [Fact]
    public void Constructor_RemovesDuplicateAndSelfRelations_ForEdi204LocationHierarchy()
    {
        var ontology = CreateOntology(
            concepts:
            [
                CreateConcept(TestData.Edi204ConceptIds.ShipmentLocation, kind: Edi204ConceptMetadata.Kinds.Location),
                CreateConcept(TestData.Edi204ConceptIds.ShipmentLocationCity, kind: Edi204ConceptMetadata.Kinds.Location, valueCategory: Edi204ConceptMetadata.ValueCategories.City),
                CreateConcept(TestData.Edi204ConceptIds.ShipmentLocationState, kind: Edi204ConceptMetadata.Kinds.Location, valueCategory: Edi204ConceptMetadata.ValueCategories.StateOrProvince)
            ],
            relations:
            [
                new(TestData.Edi204ConceptIds.ShipmentLocationCity, TestData.Edi204ConceptIds.ShipmentLocation, StandardRelationTypeIds.PartOf),
                new(TestData.Edi204ConceptIds.ShipmentLocationCity, TestData.Edi204ConceptIds.ShipmentLocation, StandardRelationTypeIds.PartOf),
                new(TestData.Edi204ConceptIds.ShipmentLocation, TestData.Edi204ConceptIds.ShipmentLocation, StandardRelationTypeIds.PartOf),
                new(TestData.Edi204ConceptIds.ShipmentLocationState, TestData.Edi204ConceptIds.ShipmentLocation, StandardRelationTypeIds.PartOf)
            ]);

        Assert.Equal(2, ontology.Relations.Length);
        Assert.Equal(TestData.Edi204ConceptIds.ShipmentLocationCity, ontology.Relations[0].SourceConceptId);
        Assert.Equal(TestData.Edi204ConceptIds.ShipmentLocation, ontology.Relations[0].TargetConceptId);
        Assert.Equal(TestData.Edi204ConceptIds.ShipmentLocationState, ontology.Relations[1].SourceConceptId);
        Assert.Equal(TestData.Edi204ConceptIds.ShipmentLocation, ontology.Relations[1].TargetConceptId);
    }

    [Fact]
    public void Union_UsesLaterConceptMetadata_ForSameEdi204ConceptId()
    {
        var baseSlice = CreateOntology(
            concepts:
            [
                CreateConcept(
                    TestData.Edi204ConceptIds.PartyShipToName,
                    label: "Ship To",
                    kind: Edi204ConceptMetadata.Kinds.Attribute,
                    lexicalForms: ["shipto"]
                    )
            ]);

        var refinedSlice = CreateOntology(
            concepts:
            [
                CreateConcept(
                    TestData.Edi204ConceptIds.PartyShipToName,
                    label: "Ship To Name",
                    kind: Edi204ConceptMetadata.Kinds.Attribute,
                    valueCategory: Edi204ConceptMetadata.ValueCategories.Name,
                    lexicalForms: ["ship to name", "consignee name"]
                    )
            ]);

        var ontology = Ontology.Union(baseSlice, refinedSlice);

        var concept = Assert.Single(ontology.Concepts).Value;
        Assert.Equal("Ship To Name", concept.Label);
        Assert.Equal(Edi204ConceptMetadata.ValueCategories.Name, concept.Properties[Edi204ConceptMetadata.PropertyNames.ValueCategory]);
        Assert.Equal(["ship to name", "consignee name"], concept.LexicalForms.ToArray());
    }

    [Fact]
    public void Constructor_SortsRelationsDeterministically_ForEdi204ConceptGraph()
    {
        var ontology = CreateOntology(
            concepts:
            [
                CreateConcept(TestData.Edi204ConceptIds.DatePickupType, kind: Edi204ConceptMetadata.Kinds.Code, valueCategory: Edi204ConceptMetadata.ValueCategories.Code),
                CreateConcept(TestData.Edi204ConceptIds.DatePickupRequested, kind: Edi204ConceptMetadata.Kinds.Time, valueCategory: Edi204ConceptMetadata.ValueCategories.Date),
                CreateConcept(TestData.Edi204ConceptIds.ShipmentLocation, kind: Edi204ConceptMetadata.Kinds.Location),
                CreateConcept(TestData.Edi204ConceptIds.ShipmentLocationCity, kind: Edi204ConceptMetadata.Kinds.Location, valueCategory: Edi204ConceptMetadata.ValueCategories.City),
                CreateConcept(TestData.Edi204ConceptIds.ShipmentLocationState, kind: Edi204ConceptMetadata.Kinds.Location, valueCategory: Edi204ConceptMetadata.ValueCategories.StateOrProvince),
                CreateConcept(TestData.Edi204ConceptIds.ShipmentLocationMunicipality, kind: Edi204ConceptMetadata.Kinds.Location, valueCategory: Edi204ConceptMetadata.ValueCategories.City)
            ],
            relations:
            [
                new(TestData.Edi204ConceptIds.ShipmentLocationState, TestData.Edi204ConceptIds.ShipmentLocation, StandardRelationTypeIds.PartOf),
                new(TestData.Edi204ConceptIds.ShipmentLocationMunicipality, TestData.Edi204ConceptIds.ShipmentLocationCity, StandardRelationTypeIds.EquivalentTo),
                new(TestData.Edi204ConceptIds.DatePickupRequested, TestData.Edi204ConceptIds.DatePickupType, StandardRelationTypeIds.SubConceptOf)
            ]);

        Assert.Equal(
            [
                new ConceptRelation(TestData.Edi204ConceptIds.DatePickupRequested, TestData.Edi204ConceptIds.DatePickupType, StandardRelationTypeIds.SubConceptOf),
                new ConceptRelation(TestData.Edi204ConceptIds.ShipmentLocationCity, TestData.Edi204ConceptIds.ShipmentLocationMunicipality, StandardRelationTypeIds.EquivalentTo),
                new ConceptRelation(TestData.Edi204ConceptIds.ShipmentLocationState, TestData.Edi204ConceptIds.ShipmentLocation, StandardRelationTypeIds.PartOf)
            ],
            ontology.Relations.ToArray());
    }

    static Ontology CreateOntology(ImmutableArray<Concept> concepts, ImmutableArray<ConceptRelation> relations = default)
    {
        var conceptMap = ImmutableDictionary.CreateBuilder<string, Concept>(StringComparer.Ordinal);
        foreach (var concept in concepts)
            conceptMap[concept.ConceptId] = concept;
        return new(concepts: [..conceptMap], relations: relations);
    }

    static Concept CreateConcept(string conceptId, string? label = null, string? kind = null, string? valueCategory = null, ImmutableArray<string> lexicalForms = default)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(kind))
            properties[Edi204ConceptMetadata.PropertyNames.Kind] = kind;
        if (!string.IsNullOrWhiteSpace(valueCategory))
            properties[Edi204ConceptMetadata.PropertyNames.ValueCategory] = valueCategory;

        return new(
            conceptId: conceptId,
            label: label,
            lexicalForms: lexicalForms,
            properties: properties.Count == 0 ? null : properties.ToImmutable()
            );
    }

    static class Edi204ConceptMetadata
    {
        public static class PropertyNames
        {
            public const string Kind = "kind";
            public const string ValueCategory = "valueCategory";
        }

        public static class Kinds
        {
            public const string Identifier = "identifier";
            public const string Code = "code";
            public const string Time = "time";
            public const string Attribute = "attribute";
            public const string Measure = "measure";
            public const string Location = "location";
        }

        public static class ValueCategories
        {
            public const string Identifier = "Identifier";
            public const string Code = "Code";
            public const string Date = "Date";
            public const string Name = "Name";
            public const string Quantity = "Quantity";
            public const string City = "City";
            public const string StateOrProvince = "StateOrProvince";
        }
    }
}
