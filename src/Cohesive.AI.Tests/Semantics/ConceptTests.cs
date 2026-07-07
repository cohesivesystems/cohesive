using System.Collections.Immutable;
using Cohesive.AI.Semantics;

namespace Cohesive.AI.Tests.Semantics;

public sealed class ConceptTests
{
    [Fact]
    public void Equality_UsesStructuralComparisonForLexicalFormsAndProperties()
    {
        var left = new Concept(
            conceptId: "shipment.location",
            label: "Shipment Location",
            lexicalForms: ["Destination", "LOC"],
            properties: ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [
                    ("kind", "location"), 
                    ("valueCategory", "City"), 
                    ("unit", "mi")
                ])
            );
        var right = new Concept(
            conceptId: "shipment.location",
            label: "Shipment Location",
            lexicalForms: ["destination", "loc"],
            properties: ImmutableDictionary.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                [
                    ("VALUECATEGORY", "City"),
                    ("unit", "mi"),
                    ("kind", "location")
                ])
            );
        Assert.Equal(left, right);
        Assert.True(left == right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }
}
