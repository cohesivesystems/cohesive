using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryClrAuthoringContextTests
{
    [Fact]
    public void ConventionRegistration_UsesOneDeterministicMetadataSnapshotForIdsTypesAndNestedPaths()
    {
        var context = new RelationQueryClrAuthoringContext();
        var load = context.Shape<Load>();
        _ = context.Shape<AnotherRoot>();

        var path = load.ResolveMemberPath(
        [
            Property<Load>(nameof(Load.Customer)),
            Property<Customer>(nameof(Customer.Name))
        ]);
        var customerList = Assert.IsType<ArrayTypeRef>(context.GetTypeRef(typeof(IReadOnlyList<Customer>)));
        var customerType = Assert.IsType<NamedTypeRef>(customerList.ElementType);
        var nestedItemPath = context.ResolveMemberPath(
            typeof(Customer),
            [Property<Customer>(nameof(Customer.Name))]);
        var firstDocuments = context.ShapeDocuments;
        var secondDocuments = context.ShapeDocuments;
        var reverseContext = new RelationQueryClrAuthoringContext();
        _ = reverseContext.Shape<AnotherRoot>();
        _ = reverseContext.Shape<Load>();
        var reverseDocument = Assert.Single(reverseContext.ShapeDocuments);

        Assert.Equal(FieldPath.Parse("customer.display_name"), path);
        Assert.Equal(FieldPath.FromField("display_name"), nestedItemPath);
        Assert.Equal(ClrShapeIdentityConvention.GetTypeId(typeof(Customer)), customerType.TypeId);
        Assert.Equal(
            ClrRelationshipShapeConvention.GraphIdPrefix + typeof(Load).Assembly.GetName().Name,
            load.Id.GraphId.Value);
        Assert.Equal(ClrShapeIdentityConvention.GetShapeId(typeof(Load)), load.Id.ShapeId);
        Assert.Equal(RelationQueryClrIdentityOrigin.Convention, load.IdentityOrigin);
        var resolution = load.ResolveMemberPathWithProvenance(
        [
            Property<Load>(nameof(Load.Customer)),
            Property<Customer>(nameof(Customer.Name))
        ]);
        Assert.Equal(path, resolution.Path);
        Assert.Equal(
            [RelationQueryClrIdentityOrigin.Metadata, RelationQueryClrIdentityOrigin.Metadata],
            resolution.SegmentOrigins.ToArray());
        Assert.Single(firstDocuments);
        Assert.Same(firstDocuments[0].Graph, secondDocuments[0].Graph);
        Assert.Equal(
            firstDocuments[0].Graph.Shapes
                .Select(static shape => shape.Id.Value)
                .OrderBy(static id => id, StringComparer.Ordinal),
            firstDocuments[0].Graph.Shapes.Select(static shape => shape.Id.Value));
        Assert.Equal(
            firstDocuments[0].Graph.Shapes.Select(static shape => shape.Id),
            reverseDocument.Graph.Shapes.Select(static shape => shape.Id));
        Assert.Equal(
            firstDocuments[0].Graph.NamedTypes.Select(static type => type.Id),
            reverseDocument.Graph.NamedTypes.Select(static type => type.Id));
        Assert.Same(firstDocuments[0].Graph, load.Document.Graph);
    }

    [Fact]
    public void ExplicitQualifiedId_OverridesAttributeShapeIdentity()
    {
        var context = new RelationQueryClrAuthoringContext();
        var explicitId = new QualifiedShapeId(new("domain/shapes/v2"), new("load.explicit"));

        var load = context.Shape<AttributedLoad>(explicitId);

        Assert.Equal(explicitId, load.Id);
        Assert.Equal(RelationQueryClrIdentityOrigin.Explicit, load.IdentityOrigin);
        Assert.Equal(explicitId, context.Shape<AttributedLoad>().Id);
        Assert.NotNull(load.Document.Graph.GetShape(explicitId));
        Assert.Null(load.Document.Graph.TryGetShape(new ShapeId("load.attribute")));
    }

    [Fact]
    public void ImportedDocumentAndMemberOverrides_AreAuthoritativeAndComposeNestedPaths()
    {
        var graphId = new GraphId("imported/domain/v4");
        var shapeId = new ShapeId("load.wire");
        var qualified = new QualifiedShapeId(graphId, shapeId);
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [
                new Shape(
                    shapeId,
                    [new FieldDefinition(new("joined"), new JsonTypeRef(JsonTypeKind.Object))])
            ]));
        var customer = Property<ImportedLoad>(nameof(ImportedLoad.Customer));
        var name = Property<ImportedCustomer>(nameof(ImportedCustomer.Name));
        var context = new RelationQueryClrAuthoringContext();

        var load = context.Shape<ImportedLoad>(
            document,
            qualified,
            new Dictionary<PropertyInfo, FieldPath>
            {
                [customer] = FieldPath.Parse("joined.customer"),
                [name] = FieldPath.FromField("display_name")
            });

        Assert.Equal(qualified, load.Id);
        Assert.Equal(RelationQueryClrIdentityOrigin.Imported, load.IdentityOrigin);
        Assert.Equal(
            FieldPath.Parse("joined.customer.display_name"),
            load.ResolveMemberPath([customer, name]));
        Assert.Equal(
            [
                RelationQueryClrIdentityOrigin.Explicit,
                RelationQueryClrIdentityOrigin.Explicit,
                RelationQueryClrIdentityOrigin.Explicit
            ],
            load.ResolveMemberPathWithProvenance([customer, name]).SegmentOrigins.ToArray());
        Assert.Equal(
            FieldPath.Parse("joined.customer.display_name"),
            context.ResolveMemberPath(typeof(ImportedLoad), [customer, name]));
        Assert.Same(document, load.Document);
        Assert.Equal(qualified, context.Shape<ImportedLoad>().Id);
        Assert.Same(document, Assert.Single(context.ShapeDocuments));
        Assert.Throws<ArgumentException>(() => context.Shape<ImportedLoad>(
            document,
            new QualifiedShapeId(graphId, new ShapeId("missing"))));
    }

    [Fact]
    public void ImportedInlineObjectPath_PreservesManyValuedFieldCardinality()
    {
        var graphId = new GraphId("imported/inline-collection/v1");
        var qualified = new QualifiedShapeId(graphId, new ShapeId("collection-root"));
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [
                new Shape(
                    qualified.ShapeId,
                    [
                        new FieldDefinition(
                            new("container"),
                            new ObjectTypeRef(
                            [
                                new ObjectFieldTypeDef(
                                    name: "values",
                                    type: new ScalarTypeRef(ScalarTypeKind.String),
                                    cardinality: FieldCardinality.Many)
                            ]))
                    ])
            ]));
        var values = Property<ImportedInlineCollectionRoot>(nameof(ImportedInlineCollectionRoot.Values));
        var context = new RelationQueryClrAuthoringContext();

        var root = context.Shape<ImportedInlineCollectionRoot>(
            document,
            qualified,
            new Dictionary<PropertyInfo, FieldPath>
            {
                [values] = FieldPath.Parse("container.values")
            });

        Assert.Equal(FieldPath.Parse("container.values"), root.ResolveMemberPath([values]));
        var rootType = Assert.IsType<ObjectTypeRef>(root.Type);
        var containerType = Assert.IsType<ObjectTypeRef>(Assert.Single(rootType.Fields).Type);
        Assert.Equal(FieldCardinality.Many, Assert.Single(containerType.Fields).Cardinality);
    }

    [Fact]
    public void ImportedInlineObjectPath_RejectsNullableFieldForNonNullableClrMember()
    {
        var graphId = new GraphId("imported/inline-nullability/v1");
        var qualified = new QualifiedShapeId(graphId, new ShapeId("required-root"));
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [
                new Shape(
                    qualified.ShapeId,
                    [
                        new FieldDefinition(
                            new("container"),
                            new ObjectTypeRef(
                            [
                                new ObjectFieldTypeDef(
                                    name: "name",
                                    type: new ScalarTypeRef(ScalarTypeKind.String),
                                    nullability: FieldNullability.Nullable)
                            ]))
                    ])
            ]));
        var name = Property<ImportedInlineRequiredRoot>(nameof(ImportedInlineRequiredRoot.Name));
        var context = new RelationQueryClrAuthoringContext();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            context.Shape<ImportedInlineRequiredRoot>(
                document,
                qualified,
                new Dictionary<PropertyInfo, FieldPath>
                {
                    [name] = FieldPath.Parse("container.name")
                }));

        Assert.Contains("Nullable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportedShape_RetainsNestedClosedEmptyObjectType()
    {
        var graphId = new GraphId("imported/empty-object/v1");
        var qualified = new QualifiedShapeId(graphId, new ShapeId("empty-root"));
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [
                new Shape(
                    qualified.ShapeId,
                    [
                        new FieldDefinition(
                            new("Payload"),
                            new ObjectTypeRef([]))
                    ])
            ]));
        var context = new RelationQueryClrAuthoringContext();

        var root = context.Shape<ImportedEmptyRoot>(document, qualified);

        var rootType = Assert.IsType<ObjectTypeRef>(root.Type);
        Assert.Empty(Assert.IsType<ObjectTypeRef>(Assert.Single(rootType.Fields).Type).Fields);
    }

    [Fact]
    public void TypedImportedStructuralShape_ValidatesNestedOverridesFieldByField()
    {
        var graphId = new GraphId("imported/typed-domain/v1");
        var shapeId = new ShapeId("load.wire");
        var qualified = new QualifiedShapeId(graphId, shapeId);
        var customerType = ClrShapeIdentityConvention.GetTypeId(typeof(ImportedCustomer));
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [
                new Shape(
                    shapeId,
                    [new FieldDefinition(new("customer"), new NamedTypeRef(customerType))])
            ],
            [
                new TypeDefinition.Structural(
                    customerType,
                    [new StructuralField(new("display_name"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]));
        var customer = Property<ImportedLoad>(nameof(ImportedLoad.Customer));
        var name = Property<ImportedCustomer>(nameof(ImportedCustomer.Name));
        var overrides = new Dictionary<PropertyInfo, FieldPath>
        {
            [customer] = FieldPath.FromField("customer"),
            [name] = FieldPath.FromField("display_name")
        };
        var context = new RelationQueryClrAuthoringContext();

        var load = context.Shape<ImportedLoad>(document, qualified, overrides);

        Assert.Equal(
            FieldPath.Parse("customer.display_name"),
            load.ResolveMemberPath([customer, name]));
        Assert.Equal(
            FieldPath.FromField("display_name"),
            context.ResolveMemberPath(typeof(ImportedCustomer), [name]));

        var invalidGraphId = new GraphId("imported/typed-domain/invalid");
        var invalidQualified = new QualifiedShapeId(invalidGraphId, shapeId);
        var invalidDocument = ShapeGraphDocument.FromGraph(new ShapeGraph(
            invalidGraphId,
            [
                new Shape(
                    shapeId,
                    [new FieldDefinition(new("customer"), new NamedTypeRef(customerType))])
            ],
            [
                new TypeDefinition.Structural(
                    customerType,
                    [new StructuralField(new("display_name"), new ScalarTypeRef(ScalarTypeKind.Int64))])
            ]));
        var invalidContext = new RelationQueryClrAuthoringContext();

        Assert.Throws<InvalidOperationException>(() => invalidContext.Shape<ImportedLoad>(
            invalidDocument,
            invalidQualified,
            overrides));
    }

    [Fact]
    public void MetadataProfiles_AreIsolatedAcrossContexts()
    {
        var left = new RelationQueryClrAuthoringContext(new RelationQueryClrMetadataProfile(
            "test/left",
            "v1",
            [new PrefixFieldMetadataProvider("left_")]));
        var right = new RelationQueryClrAuthoringContext(new RelationQueryClrMetadataProfile(
            "test/right",
            "v1",
            [new PrefixFieldMetadataProvider("right_")]));
        var member = Property<AnotherRoot>(nameof(AnotherRoot.Id));

        var leftShape = left.Shape<AnotherRoot>();
        var rightShape = right.Shape<AnotherRoot>();
        var leftPath = leftShape.ResolveMemberPath([member]);
        var rightPath = rightShape.ResolveMemberPath([member]);

        Assert.Equal(FieldPath.FromField("left_Id"), leftPath);
        Assert.Equal(FieldPath.FromField("right_Id"), rightPath);
        Assert.NotEqual(leftShape.Id.GraphId, rightShape.Id.GraphId);
        Assert.Contains("test/left", leftShape.Id.GraphId.Value, StringComparison.Ordinal);
        Assert.Contains("test/right", rightShape.Id.GraphId.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void ContextCache_IsSafeForConcurrentIdempotentRegistration()
    {
        var context = new RelationQueryClrAuthoringContext();
        ConcurrentBag<QualifiedShapeId> ids = [];

        Parallel.For(0, 16, _ => ids.Add(context.Shape<Load>().Id));

        Assert.Single(ids.Distinct());
        Assert.Single(context.ShapeDocuments);
    }

    [Fact]
    public void RepeatedInferredRegistration_RejectsConflictingSemanticRole()
    {
        var conventional = new RelationQueryClrAuthoringContext();
        _ = conventional.Shape<AnotherRoot>(ShapeRoles.Entity);

        var conventionalException = Assert.Throws<ArgumentException>(() =>
            conventional.Shape<AnotherRoot>(ShapeRoles.ValueObject));
        Assert.Contains("semantic role", conventionalException.Message, StringComparison.Ordinal);

        var explicitContext = new RelationQueryClrAuthoringContext();
        var explicitId = new QualifiedShapeId(
            new GraphId("test/explicit-role/v1"),
            new ShapeId("another-root"));
        _ = explicitContext.Shape<AnotherRoot>(explicitId, ShapeRoles.Entity);

        var explicitException = Assert.Throws<ArgumentException>(() =>
            explicitContext.Shape<AnotherRoot>(explicitId, ShapeRoles.ValueObject));
        Assert.Contains("semantic role", explicitException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportedShape_RejectsDefiniteScalarPresenceAndNamedTypeMismatches()
    {
        AssertImportedRootRejected<AnotherRoot>(
            new FieldDefinition(new("Id"), new ScalarTypeRef(ScalarTypeKind.Int64)),
            namedTypes: []);
        AssertImportedRootRejected<AnotherRoot>(
            new FieldDefinition(
                new("Id"),
                new ScalarTypeRef(ScalarTypeKind.String),
                presence: FieldPresence.Optional,
                nullability: FieldNullability.Nullable),
            namedTypes: []);

        var structuralId = new TypeId("test.imported.status-structure");
        AssertImportedRootRejected<EnumRoot>(
            new FieldDefinition(new("Status"), new NamedTypeRef(structuralId)),
            namedTypes:
            [
                new TypeDefinition.Structural(
                    structuralId,
                    [new StructuralField(new("Value"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]);
    }

    [Fact]
    public void BytesProperty_RemainsOneScalarBytesField()
    {
        var context = new RelationQueryClrAuthoringContext();

        var shape = context.Shape<BinaryRoot>().Document.Graph.GetShape(
            context.Shape<BinaryRoot>().Id);

        var payload = Assert.Single(shape.Fields);
        Assert.Equal(FieldCardinality.Single, payload.Cardinality);
        Assert.Equal(new ScalarTypeRef(ScalarTypeKind.Bytes), payload.Type);
    }

    static void AssertImportedRootRejected<T>(
        FieldDefinition field,
        TypeDefinition[] namedTypes)
        where T : notnull
    {
        var graphId = new GraphId(
            $"test/imported/{typeof(T).Name}/{field.Type.GetType().Name}/{field.Presence}/{field.Nullability}");
        var id = new QualifiedShapeId(graphId, new ShapeId(typeof(T).Name));
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [new Shape(id.ShapeId, [field])],
            [.. namedTypes]));
        var context = new RelationQueryClrAuthoringContext();

        var exception = Assert.Throws<InvalidOperationException>(() => context.Shape<T>(document, id));

        Assert.Contains("incompatible", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    static PropertyInfo Property<T>(string name) =>
        typeof(T).GetProperty(name, BindingFlags.Instance | BindingFlags.Public)
        ?? throw new InvalidOperationException($"Test property '{typeof(T).Name}.{name}' was not found.");

    sealed record Load(
        [property: JsonPropertyName("customer")] Customer Customer,
        IReadOnlyList<Stop> Stops);

    sealed record Customer(
        [property: JsonPropertyName("display_name")] string Name);

    sealed record Stop(string Location);

    sealed record AnotherRoot(string Id);

    [ShapeDefinition("load.attribute")]
    sealed record AttributedLoad(string Id);

    sealed record ImportedLoad(ImportedCustomer Customer);

    sealed record ImportedCustomer(string Name);

    sealed record ImportedInlineCollectionRoot(IReadOnlyList<string> Values);

    sealed record ImportedInlineRequiredRoot(string Name);

    sealed record ImportedEmptyRoot(JsonObject Payload);

    enum ImportStatus
    {
        Pending,
        Complete
    }

    sealed record EnumRoot(ImportStatus Status);

    sealed record BinaryRoot(byte[] Payload);

    sealed class PrefixFieldMetadataProvider(string prefix) : IClrShapeMetadataProvider
    {
        public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context) =>
            context.Target == ClrShapeMetadataTarget.Field
                ? new() { FieldName = new(prefix + context.Property!.Name) }
                : ClrShapeMetadata.Empty;
    }
}
