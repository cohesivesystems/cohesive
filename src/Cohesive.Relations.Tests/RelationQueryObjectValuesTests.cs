using System.Collections.Immutable;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryObjectValuesTests
{
    [Fact]
    public void SelectCanonical_FusesTopLevelFieldsIntoOneDeterministicObject()
    {
        var shape = LoadCustomerRelationFixture.LoadShapeId;
        var value = Object(
            ("Ignored", ObservationValue.FromString("ignored")),
            ("Second", ObservationValue.FromInt64(2)),
            ("First", ObservationValue.FromInt64(1)));
        ImmutableArray<RelationQueryFieldReference> fields =
        [
            new(shape, FieldPath.FromField("First")),
            new(shape, FieldPath.FromField("Missing")),
            new(shape, FieldPath.FromField("Second"))
        ];

        var selected = RelationQueryObjectValues.SelectCanonical(value, fields);

        Assert.Equal(["First", "Second"], selected.Fields!.Keys);
        Assert.Equal(1L, selected.Fields["First"].Int64);
        Assert.Equal(2L, selected.Fields["Second"].Int64);
    }

    [Fact]
    public void SelectCanonical_PreservesNestedFieldSemantics()
    {
        var shape = LoadCustomerRelationFixture.LoadShapeId;
        var value = Object(
            ("First", ObservationValue.FromInt64(1)),
            ("Nested", Object(
                ("Ignored", ObservationValue.FromString("ignored")),
                ("Name", ObservationValue.FromString("selected")))));
        ImmutableArray<RelationQueryFieldReference> fields =
        [
            new(shape, FieldPath.FromField("First")),
            new(shape, new FieldPath([
                FieldPathSegment.ForField("Nested"),
                FieldPathSegment.ForField("Name")
            ]))
        ];

        var selected = RelationQueryObjectValues.SelectCanonical(value, fields);

        Assert.Equal(1L, selected.Fields!["First"].Int64);
        Assert.Equal("selected", selected.Fields["Nested"].Fields!["Name"].String);
        Assert.False(selected.Fields["Nested"].Fields!.ContainsKey("Ignored"));
    }

    static ObservationValue Object(params (string Name, ObservationValue Value)[] fields) =>
        ObservationValue.FromObject(fields.ToImmutableDictionary(
            static field => field.Name,
            static field => field.Value,
            StringComparer.Ordinal));
}
