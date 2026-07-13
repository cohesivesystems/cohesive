using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Tests;

public sealed class RelationshipCatalogTests
{
    static readonly GraphId DomainGraphId = new("domain/v1");
    static readonly QualifiedShapeId LoadShapeId = new(DomainGraphId, new("Load"));
    static readonly QualifiedShapeId CustomerShapeId = new(DomainGraphId, new("Customer"));
    static readonly QualifiedShapeId EquipmentShapeId = new(DomainGraphId, new("Equipment"));

    [Fact]
    public void Catalog_NormalizesOrderAndIndexesBothEndpoints()
    {
        var customer = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);
        var equipment = CreateRelationship(LoadShapeId, "EquipmentId", EquipmentShapeId);

        var catalog = new RelationshipCatalog([equipment, customer]);

        Assert.Equal(
            catalog.Relationships.OrderBy(static relationship => relationship.Id.Value, StringComparer.Ordinal),
            catalog.Relationships);
        Assert.Same(customer, catalog.GetRelationship(customer.Id));
        Assert.Equal(2, catalog.GetOutgoing(LoadShapeId).Length);
        Assert.Same(customer, Assert.Single(catalog.GetIncoming(CustomerShapeId)));
        Assert.Empty(catalog.GetOutgoing(CustomerShapeId));
    }

    [Fact]
    public void Catalog_TotalOrderingIsStableForInvalidDuplicatePathSpellings()
    {
        var topLevel = CreateRelationship(LoadShapeId, "a.b", CustomerShapeId) with
        {
            Id = new RelationshipId("duplicate")
        };
        var nested = topLevel with { SourceReference = FieldPath.Parse("a.b") };
        var first = new RelationshipCatalog([topLevel, nested]);
        var second = new RelationshipCatalog([nested, topLevel]);

        Assert.Equal(
            first.Relationships.Select(static relationship => relationship.SourceReference.Segments.Length),
            second.Relationships.Select(static relationship => relationship.SourceReference.Segments.Length));
        Assert.Equal(
            RelationshipCatalogFingerprinter.Compute(first),
            RelationshipCatalogFingerprinter.Compute(second));
    }

    [Fact]
    public void IdConvention_IsOrderIndependentAndIncludesEverySemanticInput()
    {
        var customer = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);
        var same = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);
        var unique = CreateRelationship(
            LoadShapeId,
            "CustomerId",
            CustomerShapeId,
            SourceReferenceUniqueness.GloballyUnique);

        Assert.Equal(customer.Id, same.Id);
        Assert.NotEqual(customer.Id, unique.Id);
        Assert.StartsWith(RelationshipIdConvention.Prefix, customer.Id.Value, StringComparison.Ordinal);
        Assert.Equal(customer.Id, RelationshipIdConvention.Create(customer));
    }

    [Fact]
    public void IdConvention_MatchesPortableNonBmpUtf8Vector()
    {
        var id = RelationshipIdConvention.Create(
            new QualifiedShapeId(new GraphId("domain/🚚/v1"), new ShapeId("Load😀")),
            FieldPath.FromField("Customer🧑🏽‍💻Id"),
            new QualifiedShapeId(new GraphId("domain/客户/v2"), new ShapeId("Customer雪")),
            ObservationIdentityRelationshipTargetKey.Instance,
            SourceReferenceUniqueness.GloballyUnique);

        Assert.Equal(
            "relationship:v1:sha256:bcfa6ff2add8acdda65e8f8300f1e6f47779d03eb227b38e66da866f49547a90",
            id.Value);
    }

    [Fact]
    public void ShapeValidation_DerivesCardinalityAndPresenceWithoutAssertingTargetExistence()
    {
        var graph = CreateDomainGraph();
        var relationship = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);
        var catalog = new RelationshipCatalog([relationship]);

        var validation = RelationshipCatalogValidator.Validate(catalog, graph);
        var sourceField = graph.GetShape(LoadShapeId.ShapeId).GetField("CustomerId");

        Assert.True(validation.IsValid);
        Assert.Equal(FieldPresence.Required, sourceField.Presence);
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, relationship.GetForwardCardinality(sourceField));
        Assert.Equal(RelationshipTraversalCardinality.Many, relationship.InverseCardinality);
        Assert.DoesNotContain(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code.Contains("targetExistence", StringComparison.Ordinal));

        var unique = relationship with { SourceReferenceUniqueness = SourceReferenceUniqueness.GloballyUnique };
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, unique.InverseCardinality);
    }

    [Fact]
    public void ShapeValidation_DiagnosesMissingFieldsNestedPathsAndEntityTargetMismatch()
    {
        var graph = CreateDomainGraph();
        var valid = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);
        var missing = valid with
        {
            Id = new("missing-field"),
            SourceReference = FieldPath.FromField("MissingId")
        };
        var nested = valid with
        {
            Id = new("nested-field"),
            SourceReference = FieldPath.Parse("Customer.Id")
        };
        var mismatch = valid with
        {
            Id = new("wrong-target"),
            TargetShape = EquipmentShapeId
        };

        var validation = RelationshipCatalogValidator.Validate(
            new RelationshipCatalog([missing, nested, mismatch]),
            graph);

        AssertDiagnostic(validation, "relationshipCatalog.relationship.sourceReferenceFieldMissing");
        AssertDiagnostic(validation, "relationshipCatalog.relationship.sourceReferenceNestedUnsupported");
        AssertDiagnostic(validation, "relationshipCatalog.relationship.entityReferenceTargetMismatch");
    }

    [Fact]
    public void StructuralValidation_DiagnosesDuplicateAndConflictingIdentifiers()
    {
        var id = new RelationshipId(RelationshipIdConvention.Prefix + new string('0', 64));
        var customer = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId) with { Id = id };
        var equipment = CreateRelationship(LoadShapeId, "EquipmentId", EquipmentShapeId) with { Id = id };
        var duplicateSemantics = customer with { Id = new("alternate-explicit-id") };
        var invalid = customer with
        {
            Id = default,
            SourceShape = default,
            SourceReferenceUniqueness = (SourceReferenceUniqueness)99
        };

        var validation = RelationshipCatalogValidator.Validate(
            new RelationshipCatalog([equipment, duplicateSemantics, customer, invalid]));

        AssertDiagnostic(validation, "relationshipCatalog.relationship.idMissing");
        AssertDiagnostic(validation, "relationshipCatalog.relationship.duplicateId");
        AssertDiagnostic(validation, "relationshipCatalog.relationship.conflictingId");
        AssertDiagnostic(validation, "relationshipCatalog.relationship.generatedIdCollision");
        AssertDiagnostic(validation, "relationshipCatalog.relationship.generatedIdMismatch");
        AssertDiagnostic(validation, "relationshipCatalog.relationship.duplicateSemantics");
        AssertDiagnostic(validation, "relationshipCatalog.relationship.endpointGraphIdMissing");
        AssertDiagnostic(validation, "relationshipCatalog.relationship.sourceReferenceUniquenessInvalid");
    }

    [Fact]
    public void ShapeValidation_DiagnosesReferenceTypesThatCannotAddressObservationIdentity()
    {
        var graph = CreateDomainGraph();
        var relationship = CreateRelationship(LoadShapeId, "Sequence", CustomerShapeId);

        var validation = RelationshipCatalogValidator.Validate(
            new RelationshipCatalog([relationship]),
            graph);

        AssertDiagnostic(
            validation,
            "relationshipCatalog.relationship.sourceReferenceIdentityIncompatible");
    }

    [Fact]
    public void ShapeValidation_DiagnosesInvalidSourceReferencePresenceAndNullability()
    {
        var invalidReference = new FieldDefinition(
            new("CustomerId"),
            new EntityReferenceTypeRef(new("Customer")),
            role: FieldRole.Reference) with
        {
            Presence = (FieldPresence)99,
            Nullability = (FieldNullability)99
        };
        var graph = new ShapeGraph(
            DomainGraphId,
            [
                new Shape(
                    LoadShapeId.ShapeId,
                    [invalidReference],
                    role: ShapeRoles.Entity),
                EntityShape("Customer")
            ]);
        var relationship = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);

        var validation = RelationshipCatalogValidator.Validate(
            new RelationshipCatalog([relationship]),
            graph);

        AssertDiagnostic(
            validation,
            "relationshipCatalog.relationship.sourceReferencePresenceInvalid");
        AssertDiagnostic(
            validation,
            "relationshipCatalog.relationship.sourceReferenceNullabilityInvalid");
    }

    [Fact]
    public void ShapeValidation_RequiresExactQualifiedGraphSnapshots()
    {
        var relationship = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);
        var wrongGraph = new ShapeGraph(
            new GraphId("other/v1"),
            CreateDomainGraph().Shapes);

        var validation = RelationshipCatalogValidator.Validate(
            new RelationshipCatalog([relationship]),
            wrongGraph);

        AssertDiagnostic(validation, "relationshipCatalog.relationship.endpointGraphMissing");
    }

    [Fact]
    public void CatalogAwareQueryValidation_ResolvesForwardAndInverseEndpointShapes()
    {
        var relationship = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);
        var catalog = new RelationshipCatalog([relationship]);

        var forward = CreateTraversalRelation(
            LoadShapeId,
            relationship.Id,
            RelationshipTraversalDirection.Forward);
        var inverse = CreateTraversalRelation(
            CustomerShapeId,
            relationship.Id,
            RelationshipTraversalDirection.Inverse);

        Assert.True(ValidateWithCatalog(forward, catalog).IsValid);
        Assert.True(ValidateWithCatalog(inverse, catalog).IsValid);
    }

    [Fact]
    public void CatalogAwareQueryValidation_DiagnosesUnknownRelationshipsWrongEndpointsAndResultConflicts()
    {
        var relationship = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);
        var catalog = new RelationshipCatalog([relationship]);
        var wrongEndpoint = CreateTraversalRelation(
            new QualifiedShapeId(new GraphId("other/v1"), LoadShapeId.ShapeId),
            relationship.Id,
            RelationshipTraversalDirection.Forward);
        var unknown = CreateTraversalRelation(
            LoadShapeId,
            new RelationshipId("unknown"),
            RelationshipTraversalDirection.Forward);
        var resultConflict = CreateTraversalRelation(
            LoadShapeId,
            relationship.Id,
            RelationshipTraversalDirection.Forward,
            reuseSourceAsResult: true);

        AssertDiagnostic(
            ValidateWithCatalog(wrongEndpoint, catalog).Validation,
            "relationQuery.traversal.sourceShapeMismatch");
        AssertDiagnostic(
            ValidateWithCatalog(unknown, catalog).Validation,
            "relationQuery.traversal.relationshipUnknown");
        AssertDiagnostic(
            ValidateWithCatalog(resultConflict, catalog).Validation,
            "relationQuery.traversal.resultShapeConflict");
    }

    [Fact]
    public void CatalogDocumentValidation_RetainsTheExactSnapshotAndFingerprint()
    {
        var relationship = CreateRelationship(LoadShapeId, "CustomerId", CustomerShapeId);
        var document = RelationshipCatalogDocument.FromCatalog(new([relationship]));
        var relation = CreateTraversalRelation(
            LoadShapeId,
            relationship.Id,
            RelationshipTraversalDirection.Forward);

        var result = RelationQueryDefinitionValidator.ValidateWithCatalog(relation, document);

        Assert.True(result.IsValid);
        Assert.Same(document, result.CatalogDocument);
        Assert.Same(document.CatalogFingerprint, result.CatalogFingerprint);
        Assert.Contains(result.BindingShapes, static binding =>
            binding.Node == new QueryNodeId("traversal")
            && binding.Binding == new ValueBindingId("source")
            && binding.Shape == LoadShapeId);
        Assert.Contains(result.BindingShapes, static binding =>
            binding.Node == new QueryNodeId("traversal")
            && binding.Binding == new ValueBindingId("related")
            && binding.Shape == CustomerShapeId);
    }

    static RelationshipDefinition CreateRelationship(
        QualifiedShapeId source,
        string field,
        QualifiedShapeId target,
        SourceReferenceUniqueness uniqueness = SourceReferenceUniqueness.NotGuaranteed)
    {
        var path = FieldPath.FromField(field);
        var id = RelationshipIdConvention.Create(
            source,
            path,
            target,
            ObservationIdentityRelationshipTargetKey.Instance,
            uniqueness);
        return new(
            id,
            source,
            path,
            target,
            ObservationIdentityRelationshipTargetKey.Instance,
            uniqueness);
    }

    static ShapeGraph CreateDomainGraph()
    {
        return new(
            DomainGraphId,
            [
                new Shape(
                    LoadShapeId.ShapeId,
                    [
                        new FieldDefinition(
                            new("CustomerId"),
                            new EntityReferenceTypeRef(new("Customer")),
                            presence: FieldPresence.Required,
                            role: FieldRole.Reference),
                        new FieldDefinition(
                            new("EquipmentId"),
                            new EntityReferenceTypeRef(new("Equipment")),
                            presence: FieldPresence.Optional,
                            nullability: FieldNullability.Nullable,
                            role: FieldRole.Reference),
                        new FieldDefinition(
                            new("Sequence"),
                            new ScalarTypeRef(ScalarTypeKind.Int32))
                    ],
                    role: ShapeRoles.Entity),
                EntityShape("Customer"),
                EntityShape("Equipment")
            ]);
    }

    static Shape EntityShape(string name) => new(
        new ShapeId(name),
        [new FieldDefinition(new("Name"), new ScalarTypeRef(ScalarTypeKind.String))],
        annotations: AnnotationMap.Create(ShapeAnnotationKeys.EntityType, AnnotationValue.FromString(name)),
        role: ShapeRoles.Entity);

    static IRRelationDefinition CreateTraversalRelation(
        QualifiedShapeId sourceShape,
        RelationshipId relationshipId,
        RelationshipTraversalDirection direction,
        bool reuseSourceAsResult = false)
    {
        var sourceNode = new QueryNodeId("source");
        var traversalNode = new QueryNodeId("traversal");
        var projectNode = new QueryNodeId("project");
        var sourceBinding = new ValueBindingId("source");
        var relatedBinding = reuseSourceAsResult ? sourceBinding : new ValueBindingId("related");
        var outputBinding = new ValueBindingId("output");
        var outputShape = new QualifiedShapeId(new GraphId("dto/v1"), new ShapeId("SearchDto"));

        return new(
            new RelationId("traversal-relation"),
            new RelationName("TraversalRelation"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceNode, sourceBinding, sourceShape),
                new TraverseRelationshipQueryNode(
                    traversalNode,
                    sourceNode,
                    sourceBinding,
                    relationshipId,
                    direction,
                    relatedBinding,
                    JoinKind.Left,
                    QueryInputRequirement.Optional),
                new ProjectQueryNode(
                    projectNode,
                    traversalNode,
                    outputBinding,
                    outputShape,
                    [
                        new ProjectionAssignment(
                            new QueryAssignmentId("constant"),
                            FieldPath.FromField("Value"),
                            Expr.Const(true))
                    ])
            ]),
            sourceBinding,
            new RelationOutputDefinition(
                projectNode,
                outputShape,
                RelationOutputMode.OnePerRoot));
    }

    static void AssertDiagnostic(DocumentValidationResult validation, string code) =>
        Assert.Contains(validation.Diagnostics, diagnostic =>
            string.Equals(diagnostic.Code, code, StringComparison.Ordinal));

    static RelationQueryCatalogValidationResult ValidateWithCatalog(
        RelationQueryDefinition definition,
        RelationshipCatalog catalog) =>
        RelationQueryDefinitionValidator.ValidateWithCatalog(
            definition,
            RelationshipCatalogDocument.FromCatalog(catalog));
}
