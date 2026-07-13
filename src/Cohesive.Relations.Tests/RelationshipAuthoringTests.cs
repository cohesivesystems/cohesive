using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationshipAuthoringTests
{
    static readonly GraphId DomainGraphId = new("domain/v1");
    static readonly QualifiedShapeId LoadShapeId = new(DomainGraphId, new("Load"));
    static readonly QualifiedShapeId CustomerShapeId = new(DomainGraphId, new("Customer"));

    [Fact]
    public void DirectBuilder_UsesConventionIdsAndSupportsExplicitIdsAndGlobalUniqueness()
    {
        var sourceReference = FieldPath.FromField("customer_id");
        var conventional = Relationship
            .From(LoadShapeId)
            .Reference(sourceReference)
            .To(CustomerShapeId);
        var globallyUnique = Relationship
            .From(LoadShapeId)
            .Reference(sourceReference)
            .To(
                CustomerShapeId,
                sourceReferenceUniqueness: SourceReferenceUniqueness.GloballyUnique);
        var explicitId = new RelationshipId("load-customer");
        var explicitlyIdentified = Relationship
            .From(LoadShapeId)
            .Reference(sourceReference)
            .To(CustomerShapeId, explicitId);

        Assert.Equal(RelationshipIdConvention.Create(conventional), conventional.Id);
        Assert.NotEqual(conventional.Id, globallyUnique.Id);
        Assert.Equal(SourceReferenceUniqueness.GloballyUnique, globallyUnique.SourceReferenceUniqueness);
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, globallyUnique.InverseCardinality);
        Assert.Equal(explicitId, explicitlyIdentified.Id);
    }

    [Fact]
    public void TypedBuilder_LowersJsonPropertyNamesAndMatchesDirectSemantics()
    {
        var direct = Relationship
            .From(LoadShapeId)
            .Reference(FieldPath.FromField("customer_id"))
            .To(
                CustomerShapeId,
                sourceReferenceUniqueness: SourceReferenceUniqueness.GloballyUnique);
        var typed = Relationship
            .From<Load>(LoadShapeId)
            .Reference(static load => load.CustomerId)
            .To(
                CustomerShapeId,
                sourceReferenceUniqueness: SourceReferenceUniqueness.GloballyUnique);

        Assert.Equal(FieldPath.FromField("customer_id"), typed.SourceReference);
        Assert.Equal(direct, typed);
    }

    [Fact]
    public void TypedConvention_IsDeterministicAssemblyQualifiedAndClrShapeCompatible()
    {
        var first = Relationship
            .From<Load>()
            .Reference(static load => load.CustomerId)
            .To<Customer>();
        var second = Relationship
            .From<Load>()
            .Reference(static load => load.CustomerId)
            .To<Customer>();

        Assert.Equal(first, second);
        Assert.Equal(ClrRelationshipShapeConvention.GetQualifiedShapeId<Load>(), first.SourceShape);
        Assert.Equal(ClrRelationshipShapeConvention.GetQualifiedShapeId<Customer>(), first.TargetShape);
        Assert.Equal(
            ClrRelationshipShapeConvention.GraphIdPrefix + typeof(Load).Assembly.GetName().Name,
            first.SourceShape.GraphId.Value);
        Assert.Equal(
            $"{ClrRelationshipShapeConvention.ShapeIdPrefix}{typeof(Load).FullName}",
            first.SourceShape.ShapeId.Value);

        var graph = new ClrShapeGraphBuilder()
            .AddShape<Load>()
            .AddShape<Customer>()
            .Build(first.SourceShape.GraphId);

        Assert.NotNull(graph.GetShape(first.SourceShape.ShapeId));
        Assert.NotNull(graph.GetShape(first.TargetShape.ShapeId));
    }

    [Fact]
    public void TypedConvention_MatchesClrShapeGraphBuilderForGenericShapes()
    {
        var relationship = Relationship
            .From<GenericLoad<Customer>>()
            .Reference(static load => load.CustomerId)
            .To<Customer>();
        var graph = new ClrShapeGraphBuilder()
            .AddShape<GenericLoad<Customer>>()
            .Build(relationship.SourceShape.GraphId);

        Assert.Equal(
            ClrShapeIdentityConvention.GetShapeId(typeof(GenericLoad<Customer>)),
            relationship.SourceShape.ShapeId);
        Assert.NotNull(graph.GetShape(relationship.SourceShape.ShapeId));
    }

    [Fact]
    public void Builders_RejectNestedReferencesBeforeCreatingDefinitions()
    {
        Assert.Throws<ArgumentException>(() => Relationship
            .From(LoadShapeId)
            .Reference(FieldPath.Parse("Customer.Id")));
        Assert.Throws<ArgumentException>(() => Relationship
            .From<NestedLoad>(LoadShapeId)
            .Reference(static load => load.Customer.Id));
    }

    [Fact]
    public void TypedBuilder_DoesNotRetainClrAuthoringArtifacts()
    {
        var relationship = Relationship
            .From<Load>()
            .Reference(static load => load.CustomerId)
            .To<Customer>();

        Assert.DoesNotContain(
            EnumerateObjectGraph(relationship),
            static value => value is Type or MemberInfo or Expression);
    }

    static IEnumerable<object> EnumerateObjectGraph(object root)
    {
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        return Enumerate(root, visited);

        static IEnumerable<object> Enumerate(object? value, ISet<object> visited)
        {
            if (value is null)
                yield break;

            yield return value;
            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string or decimal)
                yield break;

            if (!type.IsValueType && !visited.Add(value))
                yield break;

            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    foreach (var nested in Enumerate(item, visited))
                        yield return nested;
                }

                yield break;
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0)
                    continue;

                foreach (var nested in Enumerate(property.GetValue(value), visited))
                    yield return nested;
            }
        }
    }

    sealed record Load([property: JsonPropertyName("customer_id")] string CustomerId);

    sealed record Customer(string Id);

    sealed record NestedLoad(Customer Customer);

    sealed record GenericLoad<T>(
        [property: JsonPropertyName("customer_id")] string CustomerId,
        T Value);
}
