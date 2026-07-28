using Cohesive.Model.Expressions;
using Cohesive.Relations.IR;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;
using IRRelationDefinition = Cohesive.Relations.IR.RelationDefinition;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionAnalysisTests
{
    static readonly GraphId DomainGraph = new("domain/v1");
    static readonly GraphId DtoGraph = new("dto/v1");
    static readonly QualifiedShapeId LoadShape = Shape(DomainGraph, "Load");
    static readonly QualifiedShapeId CustomerShape = Shape(DomainGraph, "Customer");
    static readonly QualifiedShapeId SearchShape = Shape(DtoGraph, "LoadSearchDto");
    static readonly QualifiedShapeId AggregateShape = Shape(DtoGraph, "LoadAggregateDto");
    static readonly ValueBindingId Load = new("load");
    static readonly ValueBindingId Customer = new("customer");
    static readonly ValueBindingId Row = new("row");

    [Fact]
    public void Analyze_EnumeratesEveryQueryExpressionSiteAndCombinesRequirements()
    {
        var analysis = RelationQueryExpressionAnalyzer.Analyze(CreateRepresentativeQuery());

        Assert.Equal(
        [
            "query/search/node/aggregate/aggregate/assignment/total/filter",
            "query/search/node/aggregate/aggregate/assignment/total/value",
            "query/search/node/aggregate/aggregate/grouping/customer_id/key",
            "query/search/node/distinct/distinct/key/0",
            "query/search/node/expand/expand/collection",
            "query/search/node/filter/filter/predicate",
            "query/search/node/join/join/predicate",
            "query/search/node/order/order/key/0",
            "query/search/node/page/page/keyset/after/0",
            "query/search/node/project/project/assignment/load_id/value"
        ],
            analysis.Sites.Select(static site => site.Site.Id.Value));

        Assert.Equal(
            ExprResultCategory.Boolean,
            Site(analysis, "/node/filter/filter/predicate").Site.Expectation.Category);
        Assert.Equal(
            ExprResultCategory.Collection,
            Site(analysis, "/node/expand/expand/collection").Site.Expectation.Category);
        Assert.Equal(
            ExprDependencyKind.Parameter,
            Site(analysis, "/node/page/page/keyset/after/0").Site.Expectation.AllowedDependencies);
        Assert.Equal(new[] { "cursor", "status" }, analysis.Requirements.Parameters.ToArray());
        Assert.Contains(Load, analysis.Requirements.Bindings);
        Assert.Contains(Customer, analysis.Requirements.Bindings);
        Assert.Contains(Row, analysis.Requirements.Bindings);
    }

    [Fact]
    public void Analyze_ExposesTypedOriginsForEveryQueryExpressionSite()
    {
        var analysis = RelationQueryExpressionAnalyzer.Analyze(CreateRepresentativeQuery());

        Assert.Equal(
        [
            new(
                "query/search/node/aggregate/aggregate/assignment/total/filter",
                RelationQueryExpressionSiteKind.AggregateAssignmentFilter,
                Node: "aggregate",
                Assignment: "total"),
            new(
                "query/search/node/aggregate/aggregate/assignment/total/value",
                RelationQueryExpressionSiteKind.AggregateAssignmentValue,
                Node: "aggregate",
                Assignment: "total"),
            new(
                "query/search/node/aggregate/aggregate/grouping/customer_id/key",
                RelationQueryExpressionSiteKind.AggregateGroupingKey,
                Node: "aggregate",
                Assignment: "customer_id"),
            new(
                "query/search/node/distinct/distinct/key/0",
                RelationQueryExpressionSiteKind.DistinctKey,
                Node: "distinct",
                Ordinal: 0),
            new(
                "query/search/node/expand/expand/collection",
                RelationQueryExpressionSiteKind.ExpandCollection,
                Node: "expand"),
            new(
                "query/search/node/filter/filter/predicate",
                RelationQueryExpressionSiteKind.FilterPredicate,
                Node: "filter"),
            new(
                "query/search/node/join/join/predicate",
                RelationQueryExpressionSiteKind.JoinPredicate,
                Node: "join"),
            new(
                "query/search/node/order/order/key/0",
                RelationQueryExpressionSiteKind.OrderKey,
                Node: "order",
                Ordinal: 0),
            new(
                "query/search/node/page/page/keyset/after/0",
                RelationQueryExpressionSiteKind.KeysetBoundary,
                Node: "page",
                Ordinal: 0),
            new(
                "query/search/node/project/project/assignment/load_id/value",
                RelationQueryExpressionSiteKind.ProjectionAssignmentValue,
                Node: "project",
                Assignment: "load_id")
        ],
            analysis.SiteAnalyses.Select(ToExpectedOrigin));
        Assert.Equal(analysis.Sites.Length, analysis.SiteAnalyses.Length);
        for (var index = 0; index < analysis.Sites.Length; index++)
            Assert.Same(analysis.Sites[index], analysis.SiteAnalyses[index].Analysis);
    }

    [Fact]
    public void Analyze_UsesRelationOutputScopeForKeysAndInvariants()
    {
        var project = new QueryNodeId("project");
        var output = new ValueBindingId("output");
        var relation = new IRRelationDefinition(
            new("load-search"),
            new("LoadSearch"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(new("source"), Load, LoadShape),
                new ProjectQueryNode(
                    project,
                    new("source"),
                    output,
                    SearchShape,
                    [new(new("load_id"), FieldPath.FromField("LoadId"), Expr.Field(Load, "Id"))])
            ]),
            Load,
            new RelationOutputDefinition(
                project,
                SearchShape,
                RelationOutputMode.OnePerRoot,
                Expr.Field(output, "LoadId")),
            invariants:
            [
                new("has-id", Expr.Ne(Expr.Field(output, "LoadId"), Expr.Null())),
                new("is-valid", Expr.Const(true))
            ]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(relation);

        Assert.Equal(
        [
            "relation/load-search/invariant/has-id",
            "relation/load-search/invariant/is-valid",
            "relation/load-search/node/project/project/assignment/load_id/value",
            "relation/load-search/output/key"
        ],
            analysis.Sites.Select(static site => site.Site.Id.Value));
        var outputKey = Site(analysis, "/output/key");
        var scopedBinding = Assert.Single(outputKey.Site.Scope.Bindings);
        Assert.Equal(output, scopedBinding.Id);
        Assert.Equal(SearchShape, scopedBinding.Value.Shape);
        Assert.Equal(
        [
            new(
                "relation/load-search/invariant/has-id",
                RelationQueryExpressionSiteKind.RelationInvariant,
                InvariantName: "has-id"),
            new(
                "relation/load-search/invariant/is-valid",
                RelationQueryExpressionSiteKind.RelationInvariant,
                InvariantName: "is-valid"),
            new(
                "relation/load-search/node/project/project/assignment/load_id/value",
                RelationQueryExpressionSiteKind.ProjectionAssignmentValue,
                Node: "project",
                Assignment: "load_id"),
            new(
                "relation/load-search/output/key",
                RelationQueryExpressionSiteKind.RelationOutputKey)
        ],
            analysis.SiteAnalyses.Select(ToExpectedOrigin));
    }

    [Fact]
    public void Analyze_JoinPredicateUsesPreExtensionScopeWhileOutputReflectsOuterJoin()
    {
        var query = CreateJoinedProjectionQuery(JoinKind.Left, Expr.Eq(
            Expr.Field(Load, "CustomerId"),
            Expr.Field(Customer, "Id")));

        var analysis = RelationQueryExpressionAnalyzer.Analyze(query);

        var predicate = Site(analysis, "/node/join/join/predicate");
        Assert.All(
            predicate.Site.Scope.Bindings,
            static binding => Assert.Equal(ExprBindingAvailability.AlwaysPresent, binding.Availability));
        Assert.Equal(
            RelationQueryBindingAvailability.AlwaysPresent,
            BindingShape(analysis, "join", Load).Availability);
        Assert.Equal(
            RelationQueryBindingAvailability.MayBeAbsent,
            BindingShape(analysis, "join", Customer).Availability);
    }

    [Fact]
    public void Analyze_ExpansionCarriesItemTypeIntoDownstreamExpressionScope()
    {
        var item = new ValueBindingId("item");
        var itemType = new ScalarTypeRef(ScalarTypeKind.String);
        var query = new IRQueryDefinition(
            new("expanded"),
            new("Expanded"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(new("source"), Load, LoadShape),
                new ExpandCollectionQueryNode(
                    new("expand"),
                    new("source"),
                    Expr.Field(Load, "Tags"),
                    item,
                    itemType),
                new ProjectQueryNode(
                    new("project"),
                    new("expand"),
                    Row,
                    SearchShape,
                    [new(new("value"), FieldPath.FromField("Value"), Expr.Const("value"))])
            ]),
            [new RowsQueryResultDefinition(new("rows"), new("project"))]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(query);

        var expansion = Site(analysis, "/node/expand/expand/collection");
        Assert.DoesNotContain(expansion.Site.Scope.Bindings, binding => binding.Id == item);
        var projection = Site(analysis, "/node/project/project/assignment/value/value");
        var itemScope = Assert.Single(projection.Site.Scope.Bindings, binding => binding.Id == item);
        Assert.Equal(itemType, itemScope.Value.Type);
        Assert.Null(BindingShape(analysis, "expand", item).Shape);
    }

    [Fact]
    public void Analyze_MapsScopeFailuresToRelationQueryDiagnostics()
    {
        var predicate = Expr.And(
            Expr.Eq(Expr.Field(new ValueBindingId("missing"), "Id"), Expr.Const(1)),
            Expr.And(
                Expr.Eq(Expr.Param("undeclared"), Expr.Const(1)),
                Expr.Eq(Expr.CurrentItem(), Expr.Field("Id"))));
        var analysis = RelationQueryExpressionAnalyzer.Analyze(
            CreateJoinedProjectionQuery(JoinKind.Inner, predicate));

        AssertDiagnostic(analysis, "relationQuery.expression.bindingMissing");
        AssertDiagnostic(analysis, "relationQuery.expression.parameterMissing");
        AssertDiagnostic(analysis, "relationQuery.expression.currentItemUnsupported");
        AssertDiagnostic(analysis, "relationQuery.expression.fieldBindingAmbiguous");
    }

    [Fact]
    public void Analyze_KeysetBoundariesPermitParametersButRejectRowDependencies()
    {
        var parameterAnalysis = RelationQueryExpressionAnalyzer.Analyze(
            CreatePagedQuery(Expr.Param("cursor"), declareCursor: true));
        var fieldAnalysis = RelationQueryExpressionAnalyzer.Analyze(
            CreatePagedQuery(Expr.Field(Row, "LoadId"), declareCursor: false));

        Assert.DoesNotContain(
            parameterAnalysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.page.keysetBoundaryRowDependent");
        AssertDiagnostic(fieldAnalysis, "relationQuery.page.keysetBoundaryRowDependent");
        Assert.Empty(Site(fieldAnalysis, "/node/page/page/keyset/after/0").Site.Scope.Bindings);
    }

    [Fact]
    public void Analyze_RejectsLegacyAggregateExpressionsThroughTheRelationCapabilityProfile()
    {
        var aggregateExpression = new AggregateExpr(
            AggregateOperator.Sum,
            Expr.Field(Load, "Amount"),
            new ScalarTypeRef(ScalarTypeKind.Int64));
        var query = CreateProjectionQuery(aggregateExpression);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(query);

        AssertDiagnostic(analysis, "relationQuery.expression.aggregateUnsupported");
        Assert.Contains(
            ExprCapabilities.ForAggregate(AggregateOperator.Sum),
            analysis.Requirements.Capabilities
                .Where(static requirement => requirement.Kind == ExprCapabilityRequirementKind.Operation)
                .Select(static requirement => requirement.Capability));
    }

    [Fact]
    public void Analyze_PreservesPortableFunctionRequirementsForDownstreamTargetMatching()
    {
        var unsupported = Expr.Call(
            ExprFunctionNames.Append,
            Expr.Const(ObservationValue.FromArray([ObservationValue.FromString("a")])),
            Expr.Const("b"));

        var analysis = RelationQueryExpressionAnalyzer.Analyze(CreateProjectionQuery(unsupported));

        Assert.DoesNotContain(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.capabilityUnsupported");
        Assert.Contains(
            ExprCapabilities.ForFunction(ExprFunctionNames.Append),
            analysis.Requirements.Capabilities
                .Where(static requirement => requirement.Kind == ExprCapabilityRequirementKind.Operation)
                .Select(static requirement => requirement.Capability));
    }

    [Fact]
    public void Analyze_ExposesRootedRelationAmbientsButRejectsThemForUnrootedQueries()
    {
        var project = new QueryNodeId("project");
        var expression = Expr.Call(ExprFunctionNames.EntityId);
        var relation = new IRRelationDefinition(
            new("rooted"),
            new("Rooted"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(new("source"), Load, LoadShape),
                new ProjectQueryNode(
                    project,
                    new("source"),
                    Row,
                    SearchShape,
                    [new(new("value"), FieldPath.FromField("Value"), expression)])
            ]),
            Load,
            new RelationOutputDefinition(project, SearchShape, RelationOutputMode.OnePerRoot));

        var rooted = RelationQueryExpressionAnalyzer.Analyze(relation);
        var unrooted = RelationQueryExpressionAnalyzer.Analyze(CreateProjectionQuery(expression));

        Assert.DoesNotContain(
            rooted.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.ambientCapabilityUnavailable");
        AssertDiagnostic(unrooted, "relationQuery.expression.ambientCapabilityUnavailable");
        Assert.Contains(
            new ExprCapabilityRequirement(
                ExprCapabilities.EntityIdentity,
                ExprCapabilityRequirementKind.Ambient),
            rooted.Requirements.Capabilities);
    }

    [Fact]
    public void AnalyzeWithCatalog_ResolvesTraversalShapeAndAvailabilityIntoSiteScope()
    {
        var relationship = new RelationshipDefinition(
            new("Load.Customer"),
            LoadShape,
            FieldPath.FromField("CustomerId"),
            CustomerShape,
            ObservationIdentityRelationshipTargetKey.Instance);
        var catalogDocument = RelationshipCatalogDocument.FromCatalog(new([relationship]));
        var relation = CreateTraversalRelation();

        var analysis = RelationQueryExpressionAnalyzer.AnalyzeWithCatalog(relation, catalogDocument);

        var projection = Site(analysis, "/node/project/project/assignment/customer_name/value");
        var customer = Assert.Single(projection.Site.Scope.Bindings, binding => binding.Id == Customer);
        Assert.Equal(CustomerShape, customer.Value.Shape);
        Assert.Equal(ExprBindingAvailability.MayBeAbsent, customer.Availability);
        Assert.Equal(
            RelationQueryBindingAvailability.MayBeAbsent,
            BindingShape(analysis, "traverse", Customer).Availability);
        Assert.Equal(CustomerShape, BindingShape(analysis, "traverse", Customer).Shape);
        Assert.Equal(catalogDocument.CatalogFingerprint, analysis.CatalogFingerprint);
    }

    [Fact]
    public void Analyze_IsIndependentOfAssignmentDeclarationOrder()
    {
        ProjectionAssignment[] assignments =
        [
            new(new("b"), FieldPath.FromField("B"), Expr.Field(Load, "B")),
            new(new("a"), FieldPath.FromField("A"), Expr.Field(Load, "A"))
        ];
        var first = RelationQueryExpressionAnalyzer.Analyze(CreateProjectionQuery(assignments));
        var second = RelationQueryExpressionAnalyzer.Analyze(CreateProjectionQuery([.. assignments.Reverse()]));

        Assert.Equal(
            first.Sites.Select(static site => site.Site.Id),
            second.Sites.Select(static site => site.Site.Id));
        Assert.Equal(
            first.SiteAnalyses.Select(ToExpectedOrigin),
            second.SiteAnalyses.Select(ToExpectedOrigin));
        Assert.Equal(first.Requirements.Fields.ToArray(), second.Requirements.Fields.ToArray());
        Assert.Equal(first.Requirements.Bindings.ToArray(), second.Requirements.Bindings.ToArray());
        Assert.Equal(first.Requirements.Capabilities.ToArray(), second.Requirements.Capabilities.ToArray());
    }

    [Fact]
    public void Analyze_ReturnsCombinedValidationForMissingBodiesAndExpectationMismatches()
    {
        var missingBody = CreateProjectionQuery(Expr.Const(true)) with { Body = null! };
        var nonBooleanFilter = new IRQueryDefinition(
            new("invalid-filter"),
            new("InvalidFilter"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(new("source"), Load, LoadShape),
                new FilterQueryNode(new("filter"), new("source"), Expr.Const("not-boolean")),
                new ProjectQueryNode(
                    new("project"),
                    new("filter"),
                    Row,
                    SearchShape,
                    [new(new("value"), FieldPath.FromField("Value"), Expr.Const(true))])
            ]),
            [new RowsQueryResultDefinition(new("rows"), new("project"))]);

        AssertDiagnostic(
            RelationQueryExpressionAnalyzer.Analyze(missingBody),
            "relationQuery.body.missing");
        AssertDiagnostic(
            RelationQueryExpressionAnalyzer.Analyze(nonBooleanFilter),
            "relationQuery.expression.resultCategoryMismatch");
    }

    [Fact]
    public void Analyze_DefaultExplicitBindingProducesStructuredDiagnostic()
    {
        var expression = new FieldExpr(
            FieldPath.FromField("Id"),
            default(ValueBindingId));

        var analysis = RelationQueryExpressionAnalyzer.Analyze(CreateProjectionQuery(expression));

        AssertDiagnostic(analysis, "relationQuery.expression.bindingInvalid");
    }

    [Fact]
    public void Analyze_MalformedParameterPresenceProducesStructuredValidation()
    {
        var query = CreateProjectionQuery(Expr.Const("value"));
        var malformedParameter = new QueryParameterDefinition(
            new("probe"),
            new ScalarTypeRef(ScalarTypeKind.String)) with
        {
            Presence = (FieldPresence)999,
            DefaultValue = ObservationValue.Undefined
        };
        var malformed = query with
        {
            Body = query.Body with { Parameters = [malformedParameter] }
        };

        var analysis = RelationQueryExpressionAnalyzer.Analyze(malformed);

        AssertDiagnostic(analysis, "relationQuery.parameter.presenceInvalid");
    }

    [Fact]
    public void Analyze_MalformedParameterDefaultKindProducesStructuredValidation()
    {
        var query = CreateProjectionQuery(Expr.Const("value"));
        var malformedParameter = new QueryParameterDefinition(
            new("probe"),
            new ScalarTypeRef(ScalarTypeKind.String)) with
        {
            DefaultKind = (QueryParameterDefaultKind)999
        };
        var malformed = query with
        {
            Body = query.Body with { Parameters = [malformedParameter] }
        };

        var analysis = RelationQueryExpressionAnalyzer.Analyze(malformed);

        AssertDiagnostic(analysis, "relationQuery.parameter.defaultKindInvalid");
    }

    [Fact]
    public void Analyze_DefaultValueWithoutDefaultKindProducesStructuredValidation()
    {
        var query = CreateProjectionQuery(Expr.Const("value"));
        var malformedParameter = new QueryParameterDefinition(
            new("probe"),
            new ScalarTypeRef(ScalarTypeKind.String),
            FieldPresence.Optional) with
        {
            DefaultValue = ObservationValue.FromString("active")
        };
        var malformed = query with
        {
            Body = query.Body with { Parameters = [malformedParameter] }
        };

        var analysis = RelationQueryExpressionAnalyzer.Analyze(malformed);

        AssertDiagnostic(analysis, "relationQuery.parameter.defaultUnexpected");
    }

    [Fact]
    public void Analyze_RejectsParameterDefaultsThatContradictTheirDeclaredType()
    {
        var query = CreateProjectionQuery(Expr.Param("probe"));
        var malformed = query with
        {
            Body = query.Body with
            {
                Parameters =
                [
                    new(
                        new("probe"),
                        new ScalarTypeRef(ScalarTypeKind.Int64),
                        FieldPresence.Optional,
                        ObservationValue.FromString("not-an-integer"))
                ]
            }
        };

        AssertDiagnostic(
            RelationQueryExpressionAnalyzer.Analyze(malformed),
            "relationQuery.parameter.defaultTypeMismatch");
    }

    [Theory]
    [InlineData("2026-07-17T12:34:56Z", false)]
    [InlineData("2026-07-17T12:34:56+02:30", false)]
    [InlineData("2026-07-17T12:34:56", true)]
    public void Analyze_InstantParameterDefaultsRequireExplicitOffset(
        string defaultValue,
        bool expectsMismatch)
    {
        var query = CreateProjectionQuery(Expr.Param("probe"));
        var definition = query with
        {
            Body = query.Body with
            {
                Parameters =
                [
                    new(
                        new("probe"),
                        new ScalarTypeRef(ScalarTypeKind.Instant),
                        FieldPresence.Optional,
                        ObservationValue.FromString(defaultValue))
                ]
            }
        };

        var analysis = RelationQueryExpressionAnalyzer.Analyze(definition);
        var hasMismatch = analysis.Validation.Diagnostics.Any(
            static diagnostic => diagnostic.Code == "relationQuery.parameter.defaultTypeMismatch");

        Assert.Equal(expectsMismatch, hasMismatch);
    }

    [Fact]
    public void Analyze_MalformedNestedParameterTypeProducesDiagnosticsWithoutThrowing()
    {
        var query = CreateProjectionQuery(Expr.Param("probe"));
        var malformed = query with
        {
            Body = query.Body with
            {
                Parameters =
                [
                    new(
                        new("probe"),
                        new ArrayTypeRef(null!),
                        FieldPresence.Optional,
                        ObservationValue.FromArray([ObservationValue.FromString("value")]))
                ]
            }
        };

        var analysis = RelationQueryExpressionAnalyzer.Analyze(malformed);

        AssertDiagnostic(analysis, "relationQuery.type.arrayElementMissing");
    }

    [Fact]
    public void Analyze_MissingCollectionEntriesProduceDiagnosticsWithoutThrowing()
    {
        var projection = CreateProjectionQuery(Expr.Const("value"));
        var malformedProjection = projection with
        {
            Body = projection.Body with
            {
                Nodes =
                [
                    .. projection.Body.Nodes.Select(static node => node is ProjectQueryNode project
                        ? project with { Assignments = [null!] }
                        : node)
                ]
            }
        };
        var representative = CreateRepresentativeQuery();
        var malformedAggregateAndOrder = representative with
        {
            Body = representative.Body with
            {
                Nodes =
                [
                    .. representative.Body.Nodes.Select(static node => node switch
                    {
                        AggregateQueryNode aggregate => aggregate with
                        {
                            Groupings = [null!],
                            Aggregates = [null!]
                        },
                        OrderQueryNode order => order with { Orderings = [null!] },
                        _ => node
                    })
                ]
            }
        };

        var projectionAnalysis = RelationQueryExpressionAnalyzer.Analyze(malformedProjection);
        var aggregateAnalysis = RelationQueryExpressionAnalyzer.Analyze(malformedAggregateAndOrder);

        AssertDiagnostic(projectionAnalysis, "relationQuery.project.assignmentMissing");
        AssertDiagnostic(aggregateAnalysis, "relationQuery.aggregate.groupingMissing");
        AssertDiagnostic(aggregateAnalysis, "relationQuery.aggregate.assignmentMissing");
        AssertDiagnostic(aggregateAnalysis, "relationQuery.order.orderingMissing");
    }

    [Fact]
    public void Analyze_InvalidTargetSegmentsAreRejectedWithoutShapeSnapshots()
    {
        var malformedTarget = new FieldPath([default]);
        var query = CreateProjectionQuery(
        [
            new(new("invalid"), malformedTarget, Expr.Const("value"))
        ]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(query);

        AssertDiagnostic(analysis, "relationQuery.fieldPath.segmentInvalid");
    }

    [Fact]
    public void Analyze_WithShapeSnapshotsAppliesProjectionAndGroupingTargetContracts()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var dtoGraph = new ShapeGraph(
            DtoGraph,
            [
                new Shape(
                    SearchShape.ShapeId,
                    [
                        new(new("Value"), stringType),
                        new(new("LoadId"), stringType)
                    ]),
                new Shape(
                    AggregateShape.ShapeId,
                    [
                        new(new("CustomerId"), stringType),
                        new(new("Total"), new ScalarTypeRef(ScalarTypeKind.Decimal))
                    ])
            ]);
        var projection = CreateProjectionQuery(Expr.Const(42));

        var withoutSnapshots = RelationQueryExpressionAnalyzer.Analyze(projection);
        var withSnapshots = RelationQueryExpressionAnalyzer.Analyze(projection, [dtoGraph]);
        var missingTarget = RelationQueryExpressionAnalyzer.Analyze(
            CreateProjectionQuery(
            [
                new(
                    new("missing"),
                    FieldPath.FromField("Missing"),
                    Expr.Const("value"))
            ]),
            [dtoGraph]);
        var aggregateDefinition = CreateRepresentativeQuery();
        var aggregate = RelationQueryExpressionAnalyzer.Analyze(aggregateDefinition, [dtoGraph]);
        var incompatibleAggregateGraph = new ShapeGraph(
            DtoGraph,
            [
                new Shape(
                    SearchShape.ShapeId,
                    [
                        new(new("Value"), stringType),
                        new(new("LoadId"), stringType)
                    ]),
                new Shape(
                    AggregateShape.ShapeId,
                    [
                        new(new("CustomerId"), stringType),
                        new(new("Total"), stringType)
                    ])
            ]);
        var incompatibleAggregate = RelationQueryExpressionAnalyzer.Analyze(
            aggregateDefinition,
            [incompatibleAggregateGraph]);
        var integralAggregateGraph = new ShapeGraph(
            DtoGraph,
            [
                new Shape(
                    SearchShape.ShapeId,
                    [
                        new(new("Value"), stringType),
                        new(new("LoadId"), stringType)
                    ]),
                new Shape(
                    AggregateShape.ShapeId,
                    [
                        new(new("CustomerId"), stringType),
                        new(new("Total"), new ScalarTypeRef(ScalarTypeKind.Int64))
                    ])
            ]);
        var integralAggregate = RelationQueryExpressionAnalyzer.Analyze(
            aggregateDefinition,
            [integralAggregateGraph]);
        var aggregateWithMissingTarget = aggregateDefinition with
        {
            Body = aggregateDefinition.Body with
            {
                Nodes =
                [
                    .. aggregateDefinition.Body.Nodes.Select(static node => node is AggregateQueryNode aggregateNode
                        ? aggregateNode with
                        {
                            Aggregates =
                            [
                                .. aggregateNode.Aggregates.Select(static assignment => assignment with
                                {
                                    Target = FieldPath.FromField("Missing")
                                })
                            ]
                        }
                        : node)
                ]
            }
        };
        var aggregateMissingTarget = RelationQueryExpressionAnalyzer.Analyze(
            aggregateWithMissingTarget,
            [dtoGraph]);

        Assert.DoesNotContain(
            withoutSnapshots.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultTypeMismatch");
        AssertDiagnostic(withSnapshots, "relationQuery.expression.resultTypeMismatch");
        AssertDiagnostic(missingTarget, "relationQuery.expression.targetFieldUnknown");
        AssertDiagnostic(aggregateMissingTarget, "relationQuery.expression.targetFieldUnknown");
        AssertDiagnostic(incompatibleAggregate, "relationQuery.expression.resultTypeMismatch");
        Assert.DoesNotContain(
            integralAggregate.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultTypeMismatch");
        Assert.Equal(
            stringType,
            Site(withSnapshots, "/node/project/project/assignment/value/value")
                .Site.Expectation.Value?.Type);
        Assert.Equal(
            stringType,
            Site(aggregate, "/node/aggregate/aggregate/grouping/customer_id/key")
                .Site.Expectation.Value?.Type);
        Assert.Same(dtoGraph, Assert.Single(withSnapshots.ShapeGraphs));
    }

    [Fact]
    public void Analyze_AllowsCurrentItemInsideScopedFunctions()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var collectionType = new ArrayTypeRef(stringType);
        var select = new CallExpr(
            ExprFunctionNames.Select,
            [
                new LiteralExpr(
                    collectionType,
                    ObservationValue.FromArray([ObservationValue.FromString("value")])),
                Expr.CurrentItem()
            ],
            collectionType);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(CreateProjectionQuery(select));

        Assert.True(analysis.Requirements.RequiresCurrentItem);
        Assert.Contains(
            new ExprCapabilityRequirement(
                ExprCapabilities.CurrentItem,
                ExprCapabilityRequirementKind.Operation),
            analysis.Requirements.Capabilities);
        Assert.DoesNotContain(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code is
                "relationQuery.expression.currentItemUnsupported" or
                "relationQuery.expression.capabilityUnsupported");
    }

    [Fact]
    public void Analyze_StructuredAnyKeepsElementReadsScopedAndCorrelated()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var stops = new QueryParameterId("stops");
        var expression = Expr.Any(
            Expr.Param(stops.Value),
            Expr.And(
                Expr.Eq(Expr.Field("item.Location"), Expr.Const("SEA")),
                Expr.Eq(Expr.Field("item.Type"), Expr.Const("Pickup"))));
        var query = CreateProjectionQuery(expression);
        query = query with
        {
            Body = query.Body with
            {
                Parameters =
                [
                    new(
                        stops,
                        new ArrayTypeRef(new ObjectTypeRef(
                        [
                            new("Location", stringType),
                            new("Type", stringType)
                        ])))
                ]
            }
        };

        var analysis = RelationQueryExpressionAnalyzer.Analyze(query);
        var site = Site(analysis, "/node/project/project/assignment/value/value");

        Assert.True(site.Requirements.RequiresCurrentItem);
        Assert.Contains(
            new ExprCapabilityRequirement(
                ExprCapabilities.ForFunction(ExprFunctionNames.Any),
                ExprCapabilityRequirementKind.Operation),
            site.Requirements.Capabilities);
        Assert.Equal(
            ["item.Location", "item.Type"],
            site.Requirements.Fields
                .Where(static field => field.Root == ExprFieldRootKind.CurrentItem)
                .Select(static field => field.Path.ToString())
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.DoesNotContain(
            site.Requirements.Fields,
            static field => field.Root == ExprFieldRootKind.Binding);
        Assert.DoesNotContain(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code.StartsWith(
                "relationQuery.expression.",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_StructuredAnyResolvesNamedCollectionElementFieldsFromShapeSnapshot()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var stopType = new TypeId("Stop");
        var graph = new ShapeGraph(
            DomainGraph,
            [
                new Shape(
                    LoadShape.ShapeId,
                    [
                        new(
                            new("Stops"),
                            new NamedTypeRef(stopType),
                            cardinality: FieldCardinality.Many)
                    ])
            ],
            [
                new TypeDefinition.Structural(
                    stopType,
                    [
                        new(new("Location"), stringType),
                        new(new("Sequence"), new ScalarTypeRef(ScalarTypeKind.Int64))
                    ])
            ]);
        var valid = CreateStructuredAnyQuery("item.Location");
        var invalid = CreateStructuredAnyQuery("item.Locaton");
        var invalidDomain = CreateStructuredAnyQuery("item.Sequence");

        var validAnalysis = RelationQueryExpressionAnalyzer.Analyze(valid, [graph]);
        var invalidAnalysis = RelationQueryExpressionAnalyzer.Analyze(invalid, [graph]);
        var invalidDomainAnalysis = RelationQueryExpressionAnalyzer.Analyze(invalidDomain, [graph]);
        var validSite = Site(validAnalysis, "/node/filter/filter/predicate");

        Assert.DoesNotContain(
            validAnalysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.fieldPathUnknown");
        Assert.Contains(
            validSite.Requirements.Fields,
            static field => field.Root == ExprFieldRootKind.Binding
                && field.Binding == Load
                && field.Path.ToString() == "Stops");
        Assert.Contains(
            validSite.Requirements.Fields,
            static field => field.Root == ExprFieldRootKind.CurrentItem
                && field.Path.ToString() == "item.Location");
        Assert.True(validSite.Site.Scope.TryGetBinding(Load, out var loadBinding));
        var loadType = Assert.IsType<ObjectTypeRef>(loadBinding.Value.Type);
        Assert.IsType<ObjectTypeRef>(Assert.Single(
            loadType.Fields,
            static field => field.Name == "Stops").Type);
        Assert.Contains(
            invalidAnalysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.fieldPathUnknown"
                && diagnostic.SchemaLocation == "/arguments/1/arguments/0"
                && diagnostic.Message.Contains("item.Locaton", StringComparison.Ordinal));
        Assert.Contains(
            invalidDomainAnalysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultCategoryMismatch"
                && diagnostic.SchemaLocation == "/arguments/1/arguments/0");

        static IRQueryDefinition CreateStructuredAnyQuery(string itemPath)
        {
            var source = new QueryNodeId("source");
            var filter = new QueryNodeId("filter");
            return new(
                new("named-stop-any"),
                new("NamedStopAny"),
                new LogicalQueryDefinition(
                [
                    new SourceQueryNode(source, Load, LoadShape),
                    new FilterQueryNode(
                        filter,
                        source,
                        Expr.Any(
                            Expr.Field(Load, "Stops"),
                            Expr.EndsWith(Expr.Field(itemPath), Expr.Const("A"))))
                ]),
                [new RowsQueryResultDefinition(new("rows"), filter)]);
        }
    }

    [Fact]
    public void Analyze_StructuredAnyExpandsNamedArrayElementsWithoutExpandingSingleNamedFields()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var stopType = new TypeId("Stop");
        var detailsType = new TypeId("LoadDetails");
        var graph = new ShapeGraph(
            DomainGraph,
            [
                new Shape(
                    LoadShape.ShapeId,
                    [
                        new(
                            new("Stops"),
                            new ArrayTypeRef(new NamedTypeRef(stopType))),
                        new(
                            new("Details"),
                            new NamedTypeRef(detailsType))
                    ])
            ],
            [
                new TypeDefinition.Structural(
                    stopType,
                    [
                        new(new("Location"), stringType)
                    ]),
                new TypeDefinition.Structural(
                    detailsType,
                    [
                        new(new("Description"), stringType)
                    ])
            ]);
        var source = new QueryNodeId("source");
        var filter = new QueryNodeId("filter");
        var query = new IRQueryDefinition(
            new("named-stop-array-any"),
            new("NamedStopArrayAny"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(source, Load, LoadShape),
                new FilterQueryNode(
                    filter,
                    source,
                    Expr.Any(
                        Expr.Field(Load, "Stops"),
                        Expr.EndsWith(Expr.Field("item.Location"), Expr.Const("A"))))
            ]),
            [new RowsQueryResultDefinition(new("rows"), filter)]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(query, [graph]);
        var site = Site(analysis, "/node/filter/filter/predicate");

        Assert.DoesNotContain(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code.StartsWith(
                "relationQuery.expression.",
                StringComparison.Ordinal));
        Assert.True(site.Site.Scope.TryGetBinding(Load, out var loadBinding));
        var loadType = Assert.IsType<ObjectTypeRef>(loadBinding.Value.Type);
        var stops = Assert.IsType<ArrayTypeRef>(
            Assert.Single(loadType.Fields, static field => field.Name == "Stops").Type);
        Assert.IsType<ObjectTypeRef>(stops.ElementType);
        Assert.IsType<NamedTypeRef>(
            Assert.Single(loadType.Fields, static field => field.Name == "Details").Type);
    }

    [Fact]
    public void Analyze_OmitsAmbiguousDuplicateInvariantSites()
    {
        var project = new QueryNodeId("project");
        var relation = new IRRelationDefinition(
            new("invalid-invariants"),
            new("InvalidInvariants"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(new("source"), Load, LoadShape),
                new ProjectQueryNode(
                    project,
                    new("source"),
                    Row,
                    SearchShape,
                    [new(new("value"), FieldPath.FromField("Value"), Expr.Const(true))])
            ]),
            Load,
            new RelationOutputDefinition(project, SearchShape, RelationOutputMode.OnePerRoot),
            [
                new("same", Expr.Const(true)),
                new("same", Expr.Const(true)),
                new("same-0", Expr.Const(true))
            ]);

        var sites = RelationQueryExpressionAnalyzer.Analyze(relation).Sites;
        var invariantSiteIds = sites
            .Select(static site => site.Site.Id.Value)
            .Where(static id => id.Contains("/invariant/", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(["relation/invalid-invariants/invariant/same-0"], invariantSiteIds);
        AssertDiagnostic(
            RelationQueryExpressionAnalyzer.Analyze(relation),
            "relationQuery.expression.site.duplicate");
    }

    [Fact]
    public void Analyze_OmitsAmbiguousDuplicateAssignmentSites()
    {
        var query = CreateProjectionQuery(
        [
            new(new("duplicate"), FieldPath.FromField("A"), Expr.Const("a")),
            new(new("duplicate"), FieldPath.FromField("B"), Expr.Const("b"))
        ]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(query);

        AssertDiagnostic(analysis, "relationQuery.expression.site.duplicate");
        Assert.DoesNotContain(
            analysis.Sites,
            static site => site.Site.Id.Value.Contains("/assignment/duplicate/", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_TopLevelMissingEntriesProduceStructuredDiagnostics()
    {
        var query = CreateProjectionQuery(Expr.Const("value"));
        var malformed = query with
        {
            Body = query.Body with
            {
                Nodes = [null!],
                Parameters = [null!]
            },
            Results = [null!]
        };

        var analysis = RelationQueryExpressionAnalyzer.Analyze(malformed);

        AssertDiagnostic(analysis, "relationQuery.node.entryMissing");
        AssertDiagnostic(analysis, "relationQuery.parameter.entryMissing");
        AssertDiagnostic(analysis, "relationQuery.query.resultMissing");
        AssertDiagnostic(analysis, "relationQuery.body.nodesEmpty");
    }

    [Fact]
    public void Analyze_AmbiguousNodeAndParameterIdsAreQuarantinedDeterministically()
    {
        var query = CreateProjectionQuery(Expr.Param("value"));
        LogicalQueryNode[] nodes =
        [
            new SourceQueryNode(new("source"), Load, LoadShape),
            new SourceQueryNode(new("source"), Customer, CustomerShape),
            Assert.Single(query.Body.Nodes.OfType<ProjectQueryNode>())
        ];
        QueryParameterDefinition[] parameters =
        [
            new(new("value"), new ScalarTypeRef(ScalarTypeKind.String)),
            new(new("value"), new ScalarTypeRef(ScalarTypeKind.Int64))
        ];
        var forward = query with
        {
            Body = query.Body with { Nodes = [.. nodes], Parameters = [.. parameters] }
        };
        var reverse = query with
        {
            Body = query.Body with
            {
                Nodes = [.. nodes.Reverse()],
                Parameters = [.. parameters.Reverse()]
            }
        };

        var first = RelationQueryExpressionAnalyzer.Analyze(forward);
        var second = RelationQueryExpressionAnalyzer.Analyze(reverse);

        AssertDiagnostic(first, "relationQuery.node.duplicateId");
        AssertDiagnostic(first, "relationQuery.parameter.duplicateId");
        AssertDiagnostic(first, "relationQuery.expression.parameterMissing");
        Assert.Equal(first.Diagnostics.ToArray(), second.Diagnostics.ToArray());
        Assert.Equal(first.Requirements.Parameters.ToArray(), second.Requirements.Parameters.ToArray());
        Assert.Empty(Site(first, "/node/project/project/assignment/value/value").Site.Scope.Parameters);
    }

    [Fact]
    public void Analyze_InvalidRelationOutputModeProducesStructuredValidation()
    {
        var relation = CreateTraversalRelation();
        var malformed = relation with
        {
            Output = relation.Output with { Mode = (RelationOutputMode)999 }
        };

        AssertDiagnostic(
            RelationQueryExpressionAnalyzer.Analyze(malformed),
            "relationQuery.relation.outputModeInvalid");
        Assert.Throws<ArgumentOutOfRangeException>(() => new RelationOutputDefinition(
            relation.Output.Node,
            relation.Output.Shape,
            (RelationOutputMode)999));
    }

    [Fact]
    public void Analyze_ArrayParametersAndDefaultsHonorEvaluatedTargetContracts()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var collectionGraph = new ShapeGraph(
            DtoGraph,
            [
                new Shape(
                    SearchShape.ShapeId,
                    [
                        new FieldDefinition(
                            new("Value"),
                            stringType,
                            cardinality: FieldCardinality.Many)
                    ])
            ]);
        var arrayQuery = CreateProjectionQuery(Expr.Param("values"));
        arrayQuery = arrayQuery with
        {
            Body = arrayQuery.Body with
            {
                Parameters = [new(new("values"), new ArrayTypeRef(stringType))]
            }
        };
        var requiredGraph = new ShapeGraph(
            DtoGraph,
            [new Shape(SearchShape.ShapeId, [new(new("Value"), stringType)])]);
        var defaultQuery = CreateProjectionQuery(Expr.Param("value"));
        defaultQuery = defaultQuery with
        {
            Body = defaultQuery.Body with
            {
                Parameters =
                [
                    new(
                        new("value"),
                        stringType,
                        FieldPresence.Optional,
                        ObservationValue.FromString("default"))
                ]
            }
        };

        var arrayAnalysis = RelationQueryExpressionAnalyzer.Analyze(arrayQuery, [collectionGraph]);
        var defaultAnalysis = RelationQueryExpressionAnalyzer.Analyze(defaultQuery, [requiredGraph]);
        var scopedDefault = Assert.Single(
            Site(defaultAnalysis, "/node/project/project/assignment/value/value").Site.Scope.Parameters);

        Assert.DoesNotContain(
            arrayAnalysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultTypeMismatch");
        Assert.DoesNotContain(
            defaultAnalysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultTypeMismatch");
        Assert.Equal(FieldPresence.Optional, scopedDefault.InvocationPresence);
        Assert.Equal(FieldPresence.Required, scopedDefault.Value.Presence);
    }

    [Fact]
    public void Analyze_MaybeAbsentUnresolvedRelationProducesRequiredTargetMismatch()
    {
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var dtoGraph = new ShapeGraph(
            DtoGraph,
            [new Shape(SearchShape.ShapeId, [new(new("CustomerName"), stringType)])]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(CreateTraversalRelation(), [dtoGraph]);
        var customerName = Site(analysis, "/node/project/project/assignment/customer_name/value");

        Assert.Equal(FieldPresence.Optional, customerName.KnownResult?.Presence);
        AssertDiagnostic(analysis, "relationQuery.expression.resultTypeMismatch");
    }

    [Fact]
    public void Analyze_QuarantinesInvalidShapeSnapshotsWithoutFirstWinsTargetResolution()
    {
        var stringShape = new Shape(
            SearchShape.ShapeId,
            [new(new("Value"), new ScalarTypeRef(ScalarTypeKind.String))]);
        var integerShape = new Shape(
            SearchShape.ShapeId,
            [new(new("Value"), new ScalarTypeRef(ScalarTypeKind.Int64))]);
        var firstGraph = new ShapeGraph(DtoGraph, [stringShape, integerShape]);
        var secondGraph = new ShapeGraph(DtoGraph, [integerShape, stringShape]);
        var query = CreateProjectionQuery(Expr.Const("value"));

        var first = RelationQueryExpressionAnalyzer.Analyze(query, [firstGraph]);
        var second = RelationQueryExpressionAnalyzer.Analyze(query, [secondGraph]);

        Assert.Contains(first.Diagnostics, static diagnostic =>
            diagnostic.Code.StartsWith("shapeGraph.", StringComparison.Ordinal)
            && diagnostic.Location?.StartsWith("/shapeGraphs/dto%2Fv1", StringComparison.Ordinal) == true);
        Assert.Equal(first.Diagnostics.ToArray(), second.Diagnostics.ToArray());
        Assert.DoesNotContain(
            first.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultTypeMismatch");
        Assert.Same(firstGraph, Assert.Single(first.ShapeGraphs));
    }

    [Fact]
    public void Analyze_QuarantinesShapeSnapshotsWithInvalidFieldValueMetadata()
    {
        var malformedField = new FieldDefinition(
            new("Value"),
            new ScalarTypeRef(ScalarTypeKind.String)) with
        {
            Presence = (FieldPresence)999
        };
        var graph = new ShapeGraph(
            DtoGraph,
            [new Shape(SearchShape.ShapeId, [malformedField])]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(
            CreateProjectionQuery(Expr.Const("value")),
            [graph]);

        AssertDiagnostic(analysis, "shapeGraph.field.presence.invalid");
        Assert.DoesNotContain(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.resultTypeMismatch");
        Assert.Null(Site(analysis, "/node/project/project/assignment/value/value").Site.Expectation.Value);
        Assert.Same(graph, Assert.Single(analysis.ShapeGraphs));
    }

    [Fact]
    public void Analyze_DiagnosesDefaultShapeIdentityWithoutConstructingFalseShapeContracts()
    {
        var query = CreateProjectionQuery(Expr.Field(Load, "Id"));
        var malformed = query with
        {
            Body = query.Body with
            {
                Nodes =
                [
                    .. query.Body.Nodes.Select(static node => node is SourceQueryNode source
                        ? source with { Shape = default }
                        : node)
                ]
            }
        };

        var analysis = RelationQueryExpressionAnalyzer.Analyze(malformed);

        AssertDiagnostic(analysis, "relationQuery.shape.graphIdMissing");
        AssertDiagnostic(analysis, "relationQuery.shape.idMissing");
        Assert.All(
            analysis.Sites.SelectMany(static site => site.Site.Scope.Bindings),
            static binding => Assert.Null(binding.Value.Shape));
    }

    [Fact]
    public void Analyze_DiagnosesBindingShapeAbsentFromSuppliedGraph()
    {
        var graph = new ShapeGraph(
            DomainGraph,
            [new Shape(new("DifferentShape"), [])]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(
            CreateProjectionQuery(Expr.Field(Load, "Id")),
            [graph]);

        AssertDiagnostic(analysis, "relationQuery.binding.shapeUnknown");
    }

    [Fact]
    public void Analyze_OrderKeysAndMinMaxUsePortableComparableSemantics()
    {
        var representative = CreateRepresentativeQuery();
        var invalidOrder = representative with
        {
            Body = representative.Body with
            {
                Nodes =
                [
                    .. representative.Body.Nodes.Select(static node => node is OrderQueryNode order
                        ? order with
                        {
                            Orderings =
                            [
                                new(Expr.Const(ObservationValue.FromObject(
                                    new Dictionary<string, ObservationValue>())))
                            ]
                        }
                        : node)
                ]
            }
        };
        var aggregate = new IRQueryDefinition(
            new("minimum"),
            new("Minimum"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(new("source"), Load, LoadShape),
                new AggregateQueryNode(
                    new("aggregate"),
                    new("source"),
                    Row,
                    AggregateShape,
                    aggregates:
                    [
                        new(
                            new("minimum"),
                            FieldPath.FromField("Minimum"),
                            AggregateOperator.Min,
                            Expr.Const("z"))
                    ])
            ]),
            [new AggregationQueryResultDefinition(new("result"), new("aggregate"))]);
        var aggregateGraph = new ShapeGraph(
            DtoGraph,
            [
                new Shape(
                    AggregateShape.ShapeId,
                    [new(new("Minimum"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]);

        AssertDiagnostic(
            RelationQueryExpressionAnalyzer.Analyze(invalidOrder),
            "relationQuery.expression.resultCategoryMismatch");
        Assert.DoesNotContain(
            RelationQueryExpressionAnalyzer.Analyze(aggregate, [aggregateGraph]).Diagnostics,
            static diagnostic => diagnostic.Code is
                "relationQuery.expression.resultCategoryMismatch" or
                "relationQuery.expression.resultTypeMismatch");
    }

    [Fact]
    public void Analyze_StructuralAggregatesRequireExactOperandAndTargetTypes()
    {
        var sourceGraph = new ShapeGraph(
            DomainGraph,
            [
                new Shape(
                    LoadShape.ShapeId,
                    [
                        new(new("DecimalValue"), new ScalarTypeRef(ScalarTypeKind.Decimal)),
                        new(new("IntegerValue"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                        new(new("TextValue"), new ScalarTypeRef(ScalarTypeKind.String))
                    ])
            ]);
        var resultGraph = new ShapeGraph(
            DtoGraph,
            [
                new Shape(
                    AggregateShape.ShapeId,
                    [
                        new(new("DecimalSum"), new ScalarTypeRef(ScalarTypeKind.Decimal)),
                        new(new("MismatchedSum"), new ScalarTypeRef(ScalarTypeKind.Int64)),
                        new(new("TextMinimum"), new ScalarTypeRef(ScalarTypeKind.String)),
                        new(new("MismatchedMinimum"), new ScalarTypeRef(ScalarTypeKind.Date)),
                        new(new("Average"), new ScalarTypeRef(ScalarTypeKind.Decimal))
                    ])
            ]);
        var query = new IRQueryDefinition(
            new("aggregate-types"),
            new("AggregateTypes"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(new("source"), Load, LoadShape),
                new AggregateQueryNode(
                    new("aggregate"),
                    new("source"),
                    Row,
                    AggregateShape,
                    aggregates:
                    [
                        new(
                            new("decimal-sum"),
                            FieldPath.FromField("DecimalSum"),
                            AggregateOperator.Sum,
                            Expr.Field(Load, "DecimalValue")),
                        new(
                            new("mismatched-sum"),
                            FieldPath.FromField("MismatchedSum"),
                            AggregateOperator.Sum,
                            Expr.Field(Load, "DecimalValue")),
                        new(
                            new("text-minimum"),
                            FieldPath.FromField("TextMinimum"),
                            AggregateOperator.Min,
                            Expr.Field(Load, "TextValue")),
                        new(
                            new("mismatched-minimum"),
                            FieldPath.FromField("MismatchedMinimum"),
                            AggregateOperator.Min,
                            Expr.Field(Load, "TextValue")),
                        new(
                            new("average"),
                            FieldPath.FromField("Average"),
                            AggregateOperator.Average,
                            Expr.Field(Load, "IntegerValue"))
                    ])
            ]),
            [new AggregationQueryResultDefinition(new("result"), new("aggregate"))]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(query, [sourceGraph, resultGraph]);

        Assert.Equal(
        [
            "/definition/body/nodes/aggregate/aggregates/mismatched-minimum/target",
            "/definition/body/nodes/aggregate/aggregates/mismatched-sum/target"
        ],
            analysis.Diagnostics
                .Where(static diagnostic => diagnostic.Code == "relationQuery.expression.resultTypeMismatch")
                .Select(static diagnostic => diagnostic.Location ?? string.Empty)
                .ToArray());
    }

    [Fact]
    public void Analyze_TargetCategoryRejectsCategoryOnlyObjectResultForTextField()
    {
        var graph = new ShapeGraph(
            DtoGraph,
            [
                new Shape(
                    SearchShape.ShapeId,
                    [new(new("Value"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(
            CreateProjectionQuery(Expr.Call(ExprFunctionNames.Object)),
            [graph]);

        AssertDiagnostic(analysis, "relationQuery.expression.resultCategoryMismatch");
    }

    [Fact]
    public void Analyze_CanonicalStringTemporalLiteralSatisfiesTemporalTarget()
    {
        var graph = new ShapeGraph(
            DtoGraph,
            [
                new Shape(
                    SearchShape.ShapeId,
                    [new(new("Value"), new ScalarTypeRef(ScalarTypeKind.Date))])
            ]);

        var analysis = RelationQueryExpressionAnalyzer.Analyze(
            CreateProjectionQuery(Expr.Const("2026-07-14")),
            [graph]);

        Assert.DoesNotContain(
            analysis.Diagnostics,
            static diagnostic => diagnostic.Code is
                "relationQuery.expression.resultCategoryMismatch" or
                "relationQuery.expression.resultTypeMismatch");
    }

    static IRQueryDefinition CreateRepresentativeQuery()
    {
        var aggregate = new QueryNodeId("aggregate");
        var distinct = new QueryNodeId("distinct");
        var expand = new QueryNodeId("expand");
        var filter = new QueryNodeId("filter");
        var join = new QueryNodeId("join");
        var order = new QueryNodeId("order");
        var page = new QueryNodeId("page");
        var project = new QueryNodeId("project");
        return new(
            id: new("search"),
            name: new("Search"),
            body: new(nodes:
                [
                    new SourceQueryNode(new("loads"), Load, LoadShape),
                    new FilterQueryNode(
                        filter,
                        new("loads"),
                        Expr.Eq(Expr.Field(Load, "Status"), Expr.Param("status"))),
                    new SourceQueryNode(new("customers"), Customer, CustomerShape),
                    new JoinQueryNode(
                        join,
                        filter,
                        new("customers"),
                        JoinKind.Inner,
                        Expr.Eq(Expr.Field(Load, "CustomerId"), Expr.Field(Customer, "Id"))),
                    new ExpandCollectionQueryNode(
                        expand,
                        join,
                        Expr.Field(Load, "Tags"),
                        new("tag"),
                        new ScalarTypeRef(ScalarTypeKind.String)),
                    new ProjectQueryNode(
                        project,
                        expand,
                        Row,
                        SearchShape,
                        [new(new("load_id"), FieldPath.FromField("LoadId"), Expr.Field(Load, "Id"))]),
                    new DistinctQueryNode(distinct, project, [Expr.Field(Row, "LoadId")]),
                    new OrderQueryNode(order, distinct, [new(Expr.Field(Row, "LoadId"))]),
                    new PageQueryNode(page, order, new KeysetPageDefinition(25, [Expr.Param("cursor")])),
                    new AggregateQueryNode(
                        aggregate,
                        join,
                        new("aggregateRow"),
                        AggregateShape,
                        groupings:
                        [new(new("customer_id"), FieldPath.FromField("CustomerId"), Expr.Field(Customer, "Id"))],
                        aggregates:
                        [
                            new(new("total"),
                                FieldPath.FromField("Total"),
                                AggregateOperator.Sum,
                                Expr.Field(Load, "Amount"),
                                Expr.Eq(Expr.Field(Load, "Active"), Expr.Const(true)))
                        ])
                ],
                parameters:
                [
                    new(new("cursor"), new ScalarTypeRef(ScalarTypeKind.String)),
                    new(new("status"), new ScalarTypeRef(ScalarTypeKind.String))
                ]),
            results:
            [
                new RowsQueryResultDefinition(new("rows"), page),
                new AggregationQueryResultDefinition(new("aggregation"), aggregate)
            ]);
    }

    static IRQueryDefinition CreateJoinedProjectionQuery(JoinKind kind, Expr predicate)
    {
        var project = new QueryNodeId("project");
        return new(
            new("joined"),
            new("Joined"),
            new(
            [
                new SourceQueryNode(new("loads"), Load, LoadShape),
                new SourceQueryNode(new("customers"), Customer, CustomerShape),
                new JoinQueryNode(new("join"), new("loads"), new("customers"), kind, predicate),
                new ProjectQueryNode(
                    project,
                    new("join"),
                    Row,
                    SearchShape,
                    [new(new("load_id"), FieldPath.FromField("LoadId"), Expr.Field(Load, "Id"))])
            ]),
            [new RowsQueryResultDefinition(new("rows"), project)]);
    }

    static IRQueryDefinition CreatePagedQuery(Expr boundary, bool declareCursor)
    {
        var project = new QueryNodeId("project");
        var order = new QueryNodeId("order");
        var page = new QueryNodeId("page");
        return new(
            new("paged"),
            new("Paged"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(new("source"), Load, LoadShape),
                    new ProjectQueryNode(
                        project,
                        new("source"),
                        Row,
                        SearchShape,
                        [new(new("load_id"), FieldPath.FromField("LoadId"), Expr.Field(Load, "Id"))]),
                    new OrderQueryNode(order, project, [new(Expr.Field(Row, "LoadId"))]),
                    new PageQueryNode(page, order, new KeysetPageDefinition(10, [boundary]))
                ],
                parameters: declareCursor
                    ? [new(new("cursor"), new ScalarTypeRef(ScalarTypeKind.String))]
                    : []),
            [new RowsQueryResultDefinition(new("rows"), page)]);
    }

    static IRQueryDefinition CreateProjectionQuery(Expr value) =>
        CreateProjectionQuery(
        [
            new ProjectionAssignment(new("value"), FieldPath.FromField("Value"), value)
        ]);

    static IRQueryDefinition CreateProjectionQuery(IEnumerable<ProjectionAssignment> assignments)
    {
        var project = new QueryNodeId("project");
        return new(
            new("projection"),
            new("Projection"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(new("source"), Load, LoadShape),
                new ProjectQueryNode(project, new("source"), Row, SearchShape, [.. assignments])
            ]),
            [new RowsQueryResultDefinition(new("rows"), project)]);
    }

    static IRRelationDefinition CreateTraversalRelation()
    {
        var project = new QueryNodeId("project");
        return new(
            new("traversal"),
            new("Traversal"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(new("source"), Load, LoadShape),
                new TraverseRelationshipQueryNode(
                    new("traverse"),
                    new("source"),
                    Load,
                    new("Load.Customer"),
                    RelationshipTraversalDirection.Forward,
                    Customer,
                    JoinKind.Left,
                    QueryInputRequirement.Optional),
                new ProjectQueryNode(
                    project,
                    new("traverse"),
                    Row,
                    SearchShape,
                    [
                        new(
                            new("customer_name"),
                            FieldPath.FromField("CustomerName"),
                            Expr.Field(Customer, "Name"))
                    ])
            ]),
            Load,
            new RelationOutputDefinition(project, SearchShape, RelationOutputMode.OnePerRoot));
    }

    static ExprAnalysisResult Site(RelationQueryExpressionAnalysisResult analysis, string suffix) =>
        Assert.Single(analysis.Sites, site => site.Site.Id.Value.EndsWith(suffix, StringComparison.Ordinal));

    static RelationQueryBindingShape BindingShape(
        RelationQueryExpressionAnalysisResult analysis,
        string node,
        ValueBindingId binding) =>
        Assert.Single(analysis.BindingShapes, item =>
            item.Node == new QueryNodeId(node) && item.Binding == binding);

    static void AssertDiagnostic(RelationQueryExpressionAnalysisResult analysis, string code) =>
        Assert.Contains(analysis.Diagnostics, diagnostic =>
            string.Equals(diagnostic.Code, code, StringComparison.Ordinal));

    static ExpectedSiteOrigin ToExpectedOrigin(RelationQueryExpressionSiteAnalysis site) => new(
        site.Analysis.Site.Id.Value,
        site.Kind,
        site.Node?.Value,
        site.Assignment?.Value,
        site.Ordinal,
        site.InvariantName);

    static QualifiedShapeId Shape(GraphId graph, string shape) => new(graph, new(shape));

    readonly record struct ExpectedSiteOrigin(
        string Id,
        RelationQueryExpressionSiteKind Kind,
        string? Node = null,
        string? Assignment = null,
        int? Ordinal = null,
        string? InvariantName = null);
}
