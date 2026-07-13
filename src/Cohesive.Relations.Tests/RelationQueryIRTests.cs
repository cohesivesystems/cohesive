using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Tests;

/// <summary>
/// Tests the portable serialization, fingerprint, and validation contracts of relation/query IR.
/// </summary>
public sealed class RelationQueryIRTests
{
    static readonly ValueBindingId LoadBinding = new("load");
    static readonly ValueBindingId CustomerBinding = new("customer");
    static readonly ValueBindingId SearchBinding = new("loadSearch");

    [Fact]
    public void RelationDocument_RoundTrip_PreservesRepresentativeJoinedProjection()
    {
        var document = RelationQueryDocument.FromDefinition(CreateLoadSearchRelation());

        var json = RelationQueryJsonSerializer.Serialize(document, indented: false);
        var roundTripped = RelationQueryJsonSerializer.Deserialize(json);
        var roundTrippedJson = RelationQueryJsonSerializer.Serialize(roundTripped, indented: false);

        var relation = Assert.IsType<IRRelationDefinition>(roundTripped.Definition);
        Assert.Contains(relation.Body.Nodes, static node => node is TraverseRelationshipQueryNode);
        Assert.Contains(relation.Body.Nodes, static node => node is ProjectQueryNode);
        Assert.Equal(document.DefinitionFingerprint, roundTripped.DefinitionFingerprint);
        Assert.True(RelationQueryDocumentSemanticValidator.Validate(roundTripped).IsValid);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(roundTrippedJson)));
    }

    [Fact]
    public void QueryDocument_RoundTrip_PreservesRowsAndAggregationBranches()
    {
        var document = RelationQueryDocument.FromDefinition(CreateLoadSearchQuery());

        var json = RelationQueryJsonSerializer.Serialize(document, indented: false);
        var roundTripped = RelationQueryJsonSerializer.Deserialize(json);
        var roundTrippedJson = RelationQueryJsonSerializer.Serialize(roundTripped, indented: false);

        var query = Assert.IsType<IRQueryDefinition>(roundTripped.Definition);
        Assert.Contains(query.Results, static result => result is RowsQueryResultDefinition);
        Assert.Contains(query.Results, static result => result is AggregationQueryResultDefinition);
        Assert.Contains(query.Body.Nodes, static node => node is PageQueryNode);
        Assert.Contains(query.Body.Nodes, static node => node is AggregateQueryNode);
        Assert.Equal(document.DefinitionFingerprint, roundTripped.DefinitionFingerprint);
        Assert.True(RelationQueryDocumentSemanticValidator.Validate(roundTripped).IsValid);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(roundTrippedJson)));
    }

    [Fact]
    public void Deserialize_RejectsUnknownRootAndNestedProperties()
    {
        var json = RelationQueryJsonSerializer.Serialize(
            RelationQueryDocument.FromDefinition(CreateLoadSearchRelation()),
            indented: false);

        var rootWithUnknownProperty = JsonNode.Parse(json)!.AsObject();
        rootWithUnknownProperty["unexpected"] = true;
        Assert.Throws<JsonException>(() =>
            RelationQueryJsonSerializer.Deserialize(rootWithUnknownProperty.ToJsonString()));

        var nodeWithUnknownProperty = JsonNode.Parse(json)!.AsObject();
        nodeWithUnknownProperty["definition"]!["body"]!["nodes"]![0]!["unexpected"] = true;
        Assert.Throws<JsonException>(() =>
            RelationQueryJsonSerializer.Deserialize(nodeWithUnknownProperty.ToJsonString()));
    }

    [Fact]
    public void LogicalNodesAndPageDefinitions_RoundTripEveryDiscriminator()
    {
        var sourceId = new QueryNodeId("source");
        var rightSourceId = new QueryNodeId("right_source");
        var leftBinding = new ValueBindingId("left");
        var rightBinding = new ValueBindingId("right");
        var options = RelationQueryJsonSerializer.CreateOptions();
        LogicalQueryNode[] nodes =
        [
            new SourceQueryNode(sourceId, leftBinding, new ShapeId("Left")),
            new FilterQueryNode(new QueryNodeId("filter"), sourceId, Expr.Const(true)),
            new TraverseRelationshipQueryNode(
                id: new("traverse"),
                input: sourceId,
                from: leftBinding,
                relationship: new("Left.Right"),
                result: rightBinding,
                joinKind: JoinKind.Left,
                requirement: QueryInputRequirement.Optional),
            new JoinQueryNode(
                id: new("join"),
                left: sourceId,
                right: rightSourceId,
                kind: JoinKind.Inner,
                predicate: Expr.Eq(Expr.Field(leftBinding, "Id"), Expr.Field(rightBinding, "LeftId"))),
            new ExpandCollectionQueryNode(
                id: new("expand_collection"),
                input: sourceId,
                collection: Expr.Field(leftBinding, "Items"),
                itemBinding: new ValueBindingId("item"),
                itemType: new ScalarTypeRef(ScalarTypeKind.String)),
            new ProjectQueryNode(
                id: new("project"),
                input: sourceId,
                resultBinding: new ValueBindingId("projected"),
                resultShape: new ShapeId("Projected"),
                assignments: [new ProjectionAssignment(new QueryAssignmentId("value"), FieldPath.FromField("Value"), Expr.Field(leftBinding, "Value"))]),
            new DistinctQueryNode(new QueryNodeId("distinct"), sourceId, [Expr.Field(leftBinding, "Id")]),
            new AggregateQueryNode(
                new QueryNodeId("aggregate"),
                sourceId,
                new ValueBindingId("aggregate"),
                new ShapeId("Aggregate"),
                aggregates:
                [
                    new QueryAggregateAssignment(
                        new QueryAssignmentId("count"),
                        FieldPath.FromField("Count"),
                        AggregateOperator.Count)
                ]),
            new OrderQueryNode(new QueryNodeId("order"), sourceId, [new QueryOrdering(Expr.Field(leftBinding, "Id"))]),
            new PageQueryNode(new QueryNodeId("page"), sourceId, new OffsetPageDefinition(limit: 10))
        ];

        foreach (var node in nodes)
        {
            var json = JsonSerializer.Serialize(node, options);
            var roundTripped = JsonSerializer.Deserialize<LogicalQueryNode>(json, options);

            Assert.NotNull(roundTripped);
            Assert.Equal(node.GetType(), roundTripped.GetType());
            Assert.Equal(node.Id, roundTripped.Id);
            Assert.Contains(RelationQueryWireNames.NodeDiscriminator, json, StringComparison.Ordinal);
        }

        QueryPageDefinition[] pages =
        [
            new OffsetPageDefinition(limit: 10, offset: 20),
            new KeysetPageDefinition(limit: 10, after: [Expr.Param("cursor")])
        ];
        foreach (var page in pages)
        {
            var json = JsonSerializer.Serialize(page, options);
            var roundTripped = JsonSerializer.Deserialize<QueryPageDefinition>(json, options);

            Assert.NotNull(roundTripped);
            Assert.Equal(page.GetType(), roundTripped.GetType());
            Assert.Contains(RelationQueryWireNames.PageDiscriminator, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Deserialize_AcceptsOutOfOrderMetadataAndRejectsUnknownNodeOrNumericEnum()
    {
        var json = RelationQueryJsonSerializer.Serialize(
            RelationQueryDocument.FromDefinition(CreateLoadSearchRelation()),
            indented: false);

        var outOfOrder = JsonNode.Parse(json)!.AsObject();
        var definition = outOfOrder["definition"]!.AsObject();
        var definitionKind = definition[RelationQueryWireNames.DefinitionDiscriminator]!.GetValue<string>();
        definition.Remove(RelationQueryWireNames.DefinitionDiscriminator);
        definition[RelationQueryWireNames.DefinitionDiscriminator] = definitionKind;
        Assert.NotNull(RelationQueryJsonSerializer.Deserialize(outOfOrder.ToJsonString()));

        var unknownNode = JsonNode.Parse(json)!.AsObject();
        unknownNode["definition"]!["body"]!["nodes"]![0]![RelationQueryWireNames.NodeDiscriminator] = "unknown";
        Assert.Throws<JsonException>(() => RelationQueryJsonSerializer.Deserialize(unknownNode.ToJsonString()));

        var numericEnum = JsonNode.Parse(json)!.AsObject();
        var traversal = numericEnum["definition"]!["body"]!["nodes"]!.AsArray()
            .Single(static node => node![RelationQueryWireNames.NodeDiscriminator]!.GetValue<string>() == RelationQueryWireNames.TraverseRelationshipNode)!;
        traversal["joinKind"] = (int)JoinKind.Left;
        Assert.Throws<JsonException>(() => RelationQueryJsonSerializer.Deserialize(numericEnum.ToJsonString()));

        var numericAttributedEnum = JsonNode.Parse(RelationQueryJsonSerializer.Serialize(
            RelationQueryDocument.FromDefinition(CreateLoadSearchQuery()),
            indented: false))!.AsObject();
        numericAttributedEnum["definition"]!["body"]!["parameters"]![0]!["type"]!["kind"] =
            (int)ScalarTypeKind.String;
        Assert.Throws<JsonException>(() =>
            RelationQueryJsonSerializer.Deserialize(numericAttributedEnum.ToJsonString()));
    }

    [Fact]
    public void Fingerprint_IgnoresDocumentMetadata()
    {
        var definition = CreateLoadSearchRelation();
        var first = RelationQueryDocument.FromDefinition(
            definition,
            new RelationQueryDocumentMetadata(
                origin: DocumentOrigin.User,
                name: "Load search",
                producer: "relations-dsl",
                createdAtUtc: new DateTimeOffset(2026, 7, 12, 8, 0, 0, TimeSpan.Zero)));
        var second = RelationQueryDocument.FromDefinition(
            definition,
            new RelationQueryDocumentMetadata(
                origin: DocumentOrigin.Generated,
                name: "Ari load search proposal",
                producer: "ari",
                createdAtUtc: new DateTimeOffset(2026, 7, 12, 9, 0, 0, TimeSpan.Zero)));

        Assert.NotEqual(
            RelationQueryJsonSerializer.Serialize(first, indented: false),
            RelationQueryJsonSerializer.Serialize(second, indented: false));
        Assert.Equal(first.DefinitionFingerprint, second.DefinitionFingerprint);
    }

    [Fact]
    public void Fingerprint_ChangesWhenProjectionSemanticsChange()
    {
        var customerName = RelationQueryDefinitionFingerprinter.Compute(
            CreateLoadSearchRelation(customerNameField: "Name"));
        var customerLegalName = RelationQueryDefinitionFingerprinter.Compute(
            CreateLoadSearchRelation(customerNameField: "LegalName"));

        Assert.NotEqual(customerName.Value, customerLegalName.Value);
    }

    [Fact]
    public void Fingerprint_NormalizesSetLikeDefinitionCollections()
    {
        var definition = CreateLoadSearchRelation();
        var reordered = definition with
        {
            Body = definition.Body with { Nodes = [.. definition.Body.Nodes.Reverse()] }
        };

        Assert.Equal(
            RelationQueryDefinitionFingerprinter.Compute(definition),
            RelationQueryDefinitionFingerprinter.Compute(reordered));
    }

    [Fact]
    public void Fingerprint_MatchesKnownCanonicalizationVector()
    {
        var fingerprint = RelationQueryDefinitionFingerprinter.Compute(CreateLoadSearchRelation());

        Assert.Equal("6dc1f31c3accde2ce1e52a2e670ba93a6b815430e3e6b7b9044de0b235345aa6", fingerprint.Value);
    }

    [Fact]
    public void Fingerprint_CanonicalizesUnicodeNumericAndObjectScalars()
    {
        var negativeZero = WithScalarProjection(
            Expr.Const(-0d),
            Expr.Const("café / 雪"),
            Expr.Const(1e30),
            Expr.Const(ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["z"] = ObservationValue.FromInt64(2),
                ["a"] = ObservationValue.FromInt64(1)
            })));
        var positiveZero = WithScalarProjection(
            Expr.Const(0d),
            Expr.Const("café / 雪"),
            Expr.Const(1e30),
            Expr.Const(ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["a"] = ObservationValue.FromInt64(1),
                ["z"] = ObservationValue.FromInt64(2)
            })));

        var negativeFingerprint = RelationQueryDefinitionFingerprinter.Compute(negativeZero);
        var positiveFingerprint = RelationQueryDefinitionFingerprinter.Compute(positiveZero);

        Assert.Equal(negativeFingerprint, positiveFingerprint);
        Assert.Equal("7c79bdc27d7ee11ca8c7efcc0b8685ed928796cc3b98fbec2a0f71e1cc539980", negativeFingerprint.Value);
    }

    [Fact]
    public void TryDeserialize_ReturnsStructuredVersionFingerprintAndRequiredIdDiagnostics()
    {
        var json = RelationQueryJsonSerializer.Serialize(
            RelationQueryDocument.FromDefinition(CreateLoadSearchRelation()),
            indented: false);

        var tampered = JsonNode.Parse(json)!.AsObject();
        tampered["definitionFingerprint"]!["value"] = new string('0', 64);
        var tamperedResult = RelationQueryJsonSerializer.TryDeserialize(tampered.ToJsonString(), out var tamperedDocument);
        Assert.NotNull(tamperedDocument);
        AssertDiagnostic(tamperedResult, "relationQuery.fingerprint.mismatch");

        var missingNodeId = JsonNode.Parse(json)!.AsObject();
        missingNodeId["definition"]!["body"]!["nodes"]![0]!.AsObject().Remove("id");
        var missingIdResult = RelationQueryJsonSerializer.TryDeserialize(missingNodeId.ToJsonString(), out _);
        AssertDiagnostic(missingIdResult, "relationQuery.node.idMissing");

        var unversionedResult = RelationQueryJsonSerializer.TryDeserialize("""{"relation":{}}""", out var unversionedDocument);
        Assert.Null(unversionedDocument);
        AssertDiagnostic(unversionedResult, "relationQuery.schemaVersion.missing");
    }

    [Fact]
    public void TryDeserialize_RejectsDuplicateJsonObjectProperties()
    {
        var json = RelationQueryJsonSerializer.Serialize(
            RelationQueryDocument.FromDefinition(CreateLoadSearchRelation()),
            indented: false);
        var duplicate = json.Replace(
            "\"schemaVersion\":",
            "\"schemaVersion\":\"relation-query/v1\",\"schemaVersion\":",
            StringComparison.Ordinal);

        var result = RelationQueryJsonSerializer.TryDeserialize(duplicate, out var document);

        Assert.Null(document);
        AssertDiagnostic(result, "relationQuery.json.duplicateProperty");
    }

    [Fact]
    public void DocumentValidator_RejectsUnsupportedVersionAndTamperedFingerprint()
    {
        var document = RelationQueryDocument.FromDefinition(CreateLoadSearchRelation());
        var unsupportedVersion = document with { SchemaVersion = "relation-query/v99" };
        var tamperedFingerprint = document with
        {
            DefinitionFingerprint = document.DefinitionFingerprint with { Value = new string('0', 64) }
        };

        AssertDiagnostic(
            RelationQueryDocumentSemanticValidator.Validate(unsupportedVersion),
            "relationQuery.schemaVersion.unsupported");
        AssertDiagnostic(
            RelationQueryDocumentSemanticValidator.Validate(tamperedFingerprint),
            "relationQuery.fingerprint.mismatch");
    }

    [Fact]
    public void DefinitionValidator_ReportsDuplicateNodeAndAssignmentIdentities()
    {
        var sourceId = new QueryNodeId("source");
        var projectId = new QueryNodeId("project");
        var duplicateAssignmentId = new QueryAssignmentId("assign_name");
        var definition = new IRRelationDefinition(
            id: new RelationId("load_search"),
            name: new RelationName("LoadSearch"),
            body: new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceId, LoadBinding, new ShapeId("Load")),
                new SourceQueryNode(sourceId, new ValueBindingId("duplicate"), new ShapeId("Customer")),
                new ProjectQueryNode(
                    projectId,
                    sourceId,
                    SearchBinding,
                    new ShapeId("LoadSearchDto"),
                    assignments:
                    [
                        new ProjectionAssignment(duplicateAssignmentId, FieldPath.FromField("Name"), Expr.Field(LoadBinding, "Name")),
                        new ProjectionAssignment(duplicateAssignmentId, FieldPath.FromField("Name"), Expr.Field(LoadBinding, "DisplayName"))
                    ])
            ]),
            rootBinding: LoadBinding,
            output: new RelationOutputDefinition(
                projectId,
                new ShapeId("LoadSearchDto"),
                RelationOutputMode.OnePerRoot));

        var result = RelationQueryDefinitionValidator.Validate(definition);

        AssertDiagnostic(result, "relationQuery.node.duplicateId");
        AssertDiagnostic(result, "relationQuery.assignment.duplicateId");
        AssertDiagnostic(result, "relationQuery.assignment.duplicateTarget");
    }

    [Fact]
    public void DefinitionValidator_ReportsMissingInputAndInvisibleBinding()
    {
        var projectId = new QueryNodeId("project");
        var definition = new IRRelationDefinition(
            id: new RelationId("invalid_load_search"),
            name: new RelationName("InvalidLoadSearch"),
            body: new LogicalQueryDefinition(
            [
                new SourceQueryNode(new QueryNodeId("source"), LoadBinding, new ShapeId("Load")),
                new ProjectQueryNode(
                    projectId,
                    new QueryNodeId("missing_input"),
                    SearchBinding,
                    new ShapeId("LoadSearchDto"),
                    assignments:
                    [
                        new ProjectionAssignment(
                            new QueryAssignmentId("assign_customer_name"),
                            FieldPath.FromField("CustomerName"),
                            Expr.Field(CustomerBinding, "Name"))
                    ])
            ]),
            rootBinding: LoadBinding,
            output: new RelationOutputDefinition(
                projectId,
                new ShapeId("LoadSearchDto"),
                RelationOutputMode.OnePerRoot));

        var result = RelationQueryDefinitionValidator.Validate(definition);

        AssertDiagnostic(result, "relationQuery.node.inputMissing");
        AssertDiagnostic(result, "relationQuery.expression.bindingMissing");
        AssertDiagnostic(result, "relationQuery.node.unreachable");
    }

    [Fact]
    public void DefinitionValidator_ReportsOutputShapeMismatchAndInvalidKeysetPlacement()
    {
        var relation = CreateLoadSearchRelation();
        var mismatchedRelation = relation with
        {
            Output = relation.Output with { Shape = new ShapeId("UnexpectedSearchDto") }
        };

        var sourceId = new QueryNodeId("source");
        var pageId = new QueryNodeId("page");
        var invalidKeysetQuery = new IRQueryDefinition(
            id: new QueryId("loads_after"),
            name: new QueryName("LoadsAfter"),
            body: new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceId, LoadBinding, new ShapeId("Load")),
                new PageQueryNode(pageId, sourceId, new KeysetPageDefinition(limit: 50))
            ]),
            results: [new RowsQueryResultDefinition(new QueryResultId("rows"), pageId)]);

        AssertDiagnostic(
            RelationQueryDefinitionValidator.Validate(mismatchedRelation),
            "relationQuery.relation.outputShapeMismatch");
        AssertDiagnostic(
            RelationQueryDefinitionValidator.Validate(invalidKeysetQuery),
            "relationQuery.page.keysetRequiresOrder");
    }

    [Fact]
    public void DefinitionValidator_RejectsBypassedConstructorInvariantsAndDefaultIdentifiers()
    {
        var relation = CreateLoadSearchRelation();
        var source = Assert.Single(relation.Body.Nodes.OfType<SourceQueryNode>());
        var project = Assert.Single(relation.Body.Nodes.OfType<ProjectQueryNode>());
        var invalid = relation with
        {
            RootBinding = default,
            Output = relation.Output with { Shape = default },
            Body = relation.Body with
            {
                Nodes =
                [
                    source with { Id = default, Binding = default, Shape = default },
                    project with { Assignments = [] }
                ]
            }
        };

        var result = RelationQueryDefinitionValidator.Validate(invalid);

        AssertDiagnostic(result, "relationQuery.node.idMissing");
        AssertDiagnostic(result, "relationQuery.binding.idMissing");
        AssertDiagnostic(result, "relationQuery.shape.idMissing");
        AssertDiagnostic(result, "relationQuery.project.assignmentsEmpty");
        AssertDiagnostic(result, "relationQuery.relation.rootBindingIdMissing");
    }

    [Fact]
    public void DefinitionValidator_RequiresUniqueNonemptyRelationInvariantNames()
    {
        var relation = CreateLoadSearchRelation();
        var namedInvariant = new InvariantDefinition("output_is_valid", Expr.Const(true));
        var invalid = relation with
        {
            Invariants =
            [
                null!,
                namedInvariant with { Name = " " },
                namedInvariant,
                namedInvariant with { Expression = Expr.Const(false) }
            ]
        };

        var result = RelationQueryDefinitionValidator.Validate(invalid);

        AssertDiagnostic(result, "relationQuery.relation.invariantMissing");
        AssertDiagnostic(result, "relationQuery.relation.invariantNameMissing");
        AssertDiagnostic(result, "relationQuery.relation.invariantDuplicateName");
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "relationQuery.relation.invariantNameMissing"
                && diagnostic.Location == "/definition/invariants/1/name");
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == "relationQuery.relation.invariantDuplicateName"
                && diagnostic.Location == "/definition/invariants/3/name");

        var exception = Assert.Throws<ArgumentException>(() => RelationQueryDocument.FromDefinition(invalid));
        Assert.Contains("relationQuery.relation.invariantDuplicateName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefinitionValidator_RejectsImplicitExpressionScopesAndUnsupportedTraversalJoin()
    {
        var relation = CreateLoadSearchRelation();
        var source = Assert.Single(relation.Body.Nodes.OfType<SourceQueryNode>());
        var traversal = Assert.Single(relation.Body.Nodes.OfType<TraverseRelationshipQueryNode>());
        var project = Assert.Single(relation.Body.Nodes.OfType<ProjectQueryNode>());
        var invalid = relation with
        {
            Body = relation.Body with
            {
                Nodes =
                [
                    source,
                    traversal with { JoinKind = JoinKind.Full },
                    project with
                    {
                        Assignments =
                        [
                            new ProjectionAssignment(
                                new QueryAssignmentId("typed_unbound"),
                                FieldPath.FromField("Typed"),
                                new FieldRefExpr(FieldPath.FromField("Id"), new ScalarTypeRef(ScalarTypeKind.String))),
                            new ProjectionAssignment(
                                new QueryAssignmentId("current_item"),
                                FieldPath.FromField("Current"),
                                Expr.CurrentItem())
                        ]
                    }
                ]
            }
        };

        var result = RelationQueryDefinitionValidator.Validate(invalid);

        AssertDiagnostic(result, "relationQuery.traversal.joinKindInvalid");
        AssertDiagnostic(result, "relationQuery.expression.fieldBindingAmbiguous");
        AssertDiagnostic(result, "relationQuery.expression.currentItemUnsupported");
    }

    [Fact]
    public void DefinitionValidator_RejectsLossyValuesAndOpaqueExpressionReturnTypes()
    {
        var relation = CreateLoadSearchRelation();
        var project = Assert.Single(relation.Body.Nodes.OfType<ProjectQueryNode>());
        var invalid = relation with
        {
            Body = relation.Body with
            {
                Nodes =
                [
                    .. relation.Body.Nodes.Where(static node => node is not ProjectQueryNode),
                    project with
                    {
                        Assignments =
                        [
                            new ProjectionAssignment(
                                new QueryAssignmentId("bytes"),
                                FieldPath.FromField("Bytes"),
                                Expr.Const(ObservationValue.FromBytes(new byte[] { 1, 2, 3 }))),
                            new ProjectionAssignment(
                                new QueryAssignmentId("conditional"),
                                FieldPath.FromField("Conditional"),
                                Expr.If(Expr.Const(true), Expr.Const("yes"), Expr.Const("no"))),
                            new ProjectionAssignment(
                                new QueryAssignmentId("call"),
                                FieldPath.FromField("Call"),
                                Expr.Call("normalize", Expr.Const("value")))
                        ]
                    }
                ]
            }
        };

        var result = RelationQueryDefinitionValidator.Validate(invalid);

        AssertDiagnostic(result, "relationQuery.value.kindUnsupported");
        AssertDiagnostic(result, "relationQuery.type.opaqueRuntimeUnsupported");
        var exception = Assert.Throws<ArgumentException>(() => RelationQueryDocument.FromDefinition(invalid));
        Assert.Contains("relationQuery.value.kindUnsupported", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefinitionValidator_AllowsOrderedPagedAggregationAndRejectsMultiBindingRows()
    {
        var sourceId = new QueryNodeId("loads");
        var aggregateId = new QueryNodeId("aggregate");
        var orderId = new QueryNodeId("order");
        var pageId = new QueryNodeId("page");
        var aggregateBinding = new ValueBindingId("aggregateRow");
        var valid = new IRQueryDefinition(
            id: new QueryId("counts"),
            name: new QueryName("Counts"),
            body: new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceId, LoadBinding, new ShapeId("Load")),
                new AggregateQueryNode(
                    aggregateId,
                    sourceId,
                    aggregateBinding,
                    new ShapeId("LoadCount"),
                    aggregates:
                    [
                        new QueryAggregateAssignment(
                            new QueryAssignmentId("count"),
                            FieldPath.FromField("Count"),
                            AggregateOperator.Count)
                    ]),
                new OrderQueryNode(orderId, aggregateId, [new QueryOrdering(Expr.Field(aggregateBinding, "Count"))]),
                new PageQueryNode(pageId, orderId, new OffsetPageDefinition(limit: 10))
            ]),
            results: [new AggregationQueryResultDefinition(new QueryResultId("counts"), pageId)]);

        Assert.True(RelationQueryDefinitionValidator.Validate(valid).IsValid);

        var rightSourceId = new QueryNodeId("customers");
        var joinId = new QueryNodeId("joined");
        var multiBindingRows = new IRQueryDefinition(
            id: new QueryId("joined_rows"),
            name: new QueryName("JoinedRows"),
            body: new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceId, LoadBinding, new ShapeId("Load")),
                new SourceQueryNode(rightSourceId, CustomerBinding, new ShapeId("Customer")),
                new JoinQueryNode(
                    joinId,
                    sourceId,
                    rightSourceId,
                    JoinKind.Inner,
                    Expr.Eq(Expr.Field(LoadBinding, "CustomerId"), Expr.Field(CustomerBinding, "Id")))
            ]),
            results: [new RowsQueryResultDefinition(new QueryResultId("rows"), joinId)]);

        AssertDiagnostic(
            RelationQueryDefinitionValidator.Validate(multiBindingRows),
            "relationQuery.query.resultBindingAmbiguous");
    }

    [Fact]
    public void DefinitionValidator_PreservesAggregationAncestryThroughJoinAndProjection()
    {
        var sourceId = new QueryNodeId("loads");
        var aggregateId = new QueryNodeId("aggregate");
        var customerSourceId = new QueryNodeId("customers");
        var joinId = new QueryNodeId("join");
        var projectId = new QueryNodeId("project");
        var aggregateBinding = new ValueBindingId("aggregateRow");
        var outputBinding = new ValueBindingId("output");
        var query = new IRQueryDefinition(
            id: new QueryId("enriched_counts"),
            name: new QueryName("EnrichedCounts"),
            body: new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceId, LoadBinding, new ShapeId("Load")),
                new AggregateQueryNode(
                    aggregateId,
                    sourceId,
                    aggregateBinding,
                    new ShapeId("LoadCount"),
                    groupings:
                    [
                        new QueryGrouping(
                            new QueryAssignmentId("customer_id"),
                            FieldPath.FromField("CustomerId"),
                            Expr.Field(LoadBinding, "CustomerId"))
                    ],
                    aggregates:
                    [
                        new QueryAggregateAssignment(
                            new QueryAssignmentId("count"),
                            FieldPath.FromField("Count"),
                            AggregateOperator.Count)
                    ]),
                new SourceQueryNode(customerSourceId, CustomerBinding, new ShapeId("Customer")),
                new JoinQueryNode(
                    joinId,
                    aggregateId,
                    customerSourceId,
                    JoinKind.Left,
                    Expr.Eq(Expr.Field(aggregateBinding, "CustomerId"), Expr.Field(CustomerBinding, "Id"))),
                new ProjectQueryNode(
                    projectId,
                    joinId,
                    outputBinding,
                    new ShapeId("EnrichedLoadCount"),
                    assignments:
                    [
                        new ProjectionAssignment(
                            new QueryAssignmentId("customer_name"),
                            FieldPath.FromField("CustomerName"),
                            Expr.Field(CustomerBinding, "Name")),
                        new ProjectionAssignment(
                            new QueryAssignmentId("load_count"),
                            FieldPath.FromField("LoadCount"),
                            Expr.Field(aggregateBinding, "Count"))
                    ])
            ]),
            results: [new AggregationQueryResultDefinition(new QueryResultId("counts"), projectId)]);

        Assert.True(RelationQueryDefinitionValidator.Validate(query).IsValid);
    }

    static IRRelationDefinition CreateLoadSearchRelation(string customerNameField = "Name")
    {
        var sourceId = new QueryNodeId("loads");
        var customerId = new QueryNodeId("customers");
        var projectId = new QueryNodeId("project_load_search");
        var outputShape = new ShapeId("LoadSearchDto");

        return new(
            id: new RelationId("load_search"),
            name: new RelationName("LoadSearch"),
            body: new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceId, LoadBinding, new ShapeId("Load")),
                new TraverseRelationshipQueryNode(
                    customerId,
                    sourceId,
                    LoadBinding,
                    new RelationshipId("Load.Customer"),
                    CustomerBinding,
                    JoinKind.Left,
                    QueryInputRequirement.Required),
                new ProjectQueryNode(
                    projectId,
                    customerId,
                    SearchBinding,
                    outputShape,
                    assignments:
                    [
                        new ProjectionAssignment(
                            new QueryAssignmentId("assign_load_id"),
                            FieldPath.FromField("LoadId"),
                            Expr.Field(LoadBinding, "Id")),
                        new ProjectionAssignment(
                            new QueryAssignmentId("assign_customer_name"),
                            FieldPath.FromField("CustomerName"),
                            Expr.Field(CustomerBinding, customerNameField))
                    ])
            ]),
            rootBinding: LoadBinding,
            output: new RelationOutputDefinition(
                projectId,
                outputShape,
                RelationOutputMode.OnePerRoot,
                key: Expr.Field(SearchBinding, "LoadId")));
    }

    static IRQueryDefinition CreateLoadSearchQuery()
    {
        var sourceId = new QueryNodeId("loads");
        var filterId = new QueryNodeId("active_loads");
        var customerId = new QueryNodeId("customers");
        var projectId = new QueryNodeId("project_rows");
        var orderId = new QueryNodeId("order_rows");
        var pageId = new QueryNodeId("page_rows");
        var aggregateId = new QueryNodeId("aggregate_customers");
        var rowBinding = new ValueBindingId("row");

        return new(
            id: new QueryId("active_load_search"),
            name: new QueryName("ActiveLoadSearch"),
            body: new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(sourceId, LoadBinding, new ShapeId("Load")),
                    new FilterQueryNode(
                        filterId,
                        sourceId,
                        Expr.Eq(Expr.Field(LoadBinding, "Status"), Expr.Param("status"))),
                    new TraverseRelationshipQueryNode(
                        customerId,
                        filterId,
                        LoadBinding,
                        new RelationshipId("Load.Customer"),
                        CustomerBinding,
                        JoinKind.Left,
                        QueryInputRequirement.Optional),
                    new ProjectQueryNode(
                        projectId,
                        customerId,
                        rowBinding,
                        new ShapeId("LoadSearchRow"),
                        assignments:
                        [
                            new ProjectionAssignment(
                                new QueryAssignmentId("assign_row_id"),
                                FieldPath.FromField("LoadId"),
                                Expr.Field(LoadBinding, "Id")),
                            new ProjectionAssignment(
                                new QueryAssignmentId("assign_row_customer"),
                                FieldPath.FromField("CustomerName"),
                                Expr.Field(CustomerBinding, "Name"))
                        ]),
                    new OrderQueryNode(
                        orderId,
                        projectId,
                        [new QueryOrdering(Expr.Field(rowBinding, "LoadId"))]),
                    new PageQueryNode(
                        pageId,
                        orderId,
                        new OffsetPageDefinition(limit: 25)),
                    new AggregateQueryNode(
                        aggregateId,
                        customerId,
                        new ValueBindingId("customerAggregate"),
                        new ShapeId("LoadByCustomerAggregation"),
                        groupings:
                        [
                            new QueryGrouping(
                                new QueryAssignmentId("group_customer"),
                                FieldPath.FromField("CustomerName"),
                                Expr.Field(CustomerBinding, "Name"))
                        ],
                        aggregates:
                        [
                            new QueryAggregateAssignment(
                                new QueryAssignmentId("count_loads"),
                                FieldPath.FromField("LoadCount"),
                                AggregateOperator.Count)
                        ])
                ],
                parameters:
                [
                    new QueryParameterDefinition(
                        new QueryParameterId("status"),
                        new ScalarTypeRef(ScalarTypeKind.String))
                ]),
            results:
            [
                new RowsQueryResultDefinition(new QueryResultId("rows"), pageId),
                new AggregationQueryResultDefinition(new QueryResultId("by_customer"), aggregateId)
            ]);
    }

    static IRRelationDefinition WithScalarProjection(params Expr[] values)
    {
        var relation = CreateLoadSearchRelation();
        var project = Assert.Single(relation.Body.Nodes.OfType<ProjectQueryNode>());
        ImmutableArray<ProjectionAssignment> assignments =
        [
            .. values.Select((value, index) => new ProjectionAssignment(
                new QueryAssignmentId($"scalar_{index}"),
                FieldPath.FromField($"Scalar{index}"),
                value))
        ];
        return relation with
        {
            Body = relation.Body with
            {
                Nodes =
                [
                    .. relation.Body.Nodes.Where(static node => node is not ProjectQueryNode),
                    project with { Assignments = assignments }
                ]
            }
        };
    }

    static void AssertDiagnostic(DocumentValidationResult result, string code)
    {
        Assert.Contains(result.Diagnostics, diagnostic =>
            string.Equals(diagnostic.Code, code, StringComparison.Ordinal));
    }
}
