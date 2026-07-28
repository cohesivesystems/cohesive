using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using IRQueryDefinition = Cohesive.Relations.IR.QueryDefinition;

namespace Cohesive.Relations.Tests;

/// <summary>Tests the structural lowering substrate for canonical relation/query authoring.</summary>
public sealed class RelationQueryStructuralAuthoringTests
{
    [Fact]
    public void StructuralAuthoring_WithExplicitIdentities_IsCanonicalByteEquivalentToDirectIr()
    {
        var author = RelationQuery.Structural();
        var status = author.Parameter(
            new ScalarTypeRef(ScalarTypeKind.String),
            id: LoadCustomerRelationFixture.StatusParameterId);
        var loads = author.Source(
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadSourceNodeId,
            LoadCustomerRelationFixture.LoadBinding);
        var filtered = author.Filter(
            loads.Node,
            Expr.Eq(
                loads.Binding.Field(LoadCustomerRelationFixture.LoadStatusPath),
                status.Expression),
            LoadCustomerRelationFixture.StatusFilterNodeId);
        var customer = author.Traverse(
            filtered,
            loads.Binding,
            LoadCustomerRelationFixture.LoadCustomerRelationshipId,
            RelationshipTraversalDirection.Forward,
            JoinKind.Left,
            QueryInputRequirement.Required,
            LoadCustomerRelationFixture.CustomerTraversalNodeId,
            LoadCustomerRelationFixture.CustomerBinding);
        var projection = author.Project(
            customer.Node,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            [
                new(
                    LoadCustomerRelationFixture.SearchIdPath,
                    loads.Binding.Field(LoadCustomerRelationFixture.LoadIdPath),
                    LoadCustomerRelationFixture.SearchIdAssignmentId),
                new(
                    LoadCustomerRelationFixture.SearchCustomerNamePath,
                    customer.Binding.Field(LoadCustomerRelationFixture.CustomerNamePath),
                    LoadCustomerRelationFixture.SearchCustomerNameAssignmentId)
            ],
            LoadCustomerRelationFixture.ProjectionNodeId,
            LoadCustomerRelationFixture.SearchBinding);
        var rows = author.Rows(projection.Node, LoadCustomerRelationFixture.RowsResultId);
        var authored = author.BuildQuery(
            LoadCustomerRelationFixture.LoadSearchQueryId,
            LoadCustomerRelationFixture.LoadSearchQueryName,
            [rows]);

        IRQueryDefinition direct = new(
            LoadCustomerRelationFixture.LoadSearchQueryId,
            LoadCustomerRelationFixture.LoadSearchQueryName,
            new LogicalQueryDefinition(
                [
                    new SourceQueryNode(
                        LoadCustomerRelationFixture.LoadSourceNodeId,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new FilterQueryNode(
                        LoadCustomerRelationFixture.StatusFilterNodeId,
                        LoadCustomerRelationFixture.LoadSourceNodeId,
                        Expr.Eq(
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadStatusPath),
                            Expr.Param(LoadCustomerRelationFixture.StatusParameterId.Value))),
                    new TraverseRelationshipQueryNode(
                        LoadCustomerRelationFixture.CustomerTraversalNodeId,
                        LoadCustomerRelationFixture.StatusFilterNodeId,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadCustomerRelationshipId,
                        RelationshipTraversalDirection.Forward,
                        LoadCustomerRelationFixture.CustomerBinding,
                        JoinKind.Left,
                        QueryInputRequirement.Required),
                    new ProjectQueryNode(
                        LoadCustomerRelationFixture.ProjectionNodeId,
                        LoadCustomerRelationFixture.CustomerTraversalNodeId,
                        LoadCustomerRelationFixture.SearchBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new ProjectionAssignment(
                                LoadCustomerRelationFixture.SearchIdAssignmentId,
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Field(
                                    LoadCustomerRelationFixture.LoadBinding,
                                    LoadCustomerRelationFixture.LoadIdPath)),
                            new ProjectionAssignment(
                                LoadCustomerRelationFixture.SearchCustomerNameAssignmentId,
                                LoadCustomerRelationFixture.SearchCustomerNamePath,
                                Expr.Field(
                                    LoadCustomerRelationFixture.CustomerBinding,
                                    LoadCustomerRelationFixture.CustomerNamePath))
                        ])
                ],
                [
                    new QueryParameterDefinition(
                        LoadCustomerRelationFixture.StatusParameterId,
                        new ScalarTypeRef(ScalarTypeKind.String))
                ]),
            [
                new RowsQueryResultDefinition(
                    LoadCustomerRelationFixture.RowsResultId,
                    LoadCustomerRelationFixture.ProjectionNodeId)
            ]);

        Assert.True(authored.Validation.IsValid, Format(authored.Validation.Diagnostics));
        var authoredJson = RelationQueryJsonSerializer.Serialize(authored.CreateDocument(), indented: false);
        var directJson = RelationQueryJsonSerializer.Serialize(
            RelationQueryDocument.FromDefinition(direct),
            indented: false);
        Assert.Equal(directJson, authoredJson);
        Assert.All(
            authored.Provenance.Identities,
            static identity => Assert.Equal(RelationQueryAuthoringIdentityOrigin.Explicit, identity.Origin));
    }

    [Fact]
    public void WholeBindingExpressions_FailThroughStructuredCapabilityValidation()
    {
        var author = RelationQuery.Structural();
        var loads = author.Source(LoadCustomerRelationFixture.LoadShapeId);
        var filtered = author.Filter(loads.Node, Expr.BoundValue(loads.Binding.Id));
        var rows = author.Rows(filtered);

        var result = author.BuildQuery(new("whole-binding"), new("WholeBinding"), [rows]);

        Assert.Contains(
            result.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.capabilityUnsupported");
    }

    [Fact]
    public void ConventionIdentities_AreDeterministicAndFingerprintEquivalentAcrossCores()
    {
        var first = BuildConventionQuery();
        var second = BuildConventionQuery();

        Assert.True(first.Validation.IsValid, Format(first.Validation.Diagnostics));
        Assert.True(second.Validation.IsValid, Format(second.Validation.Diagnostics));
        Assert.Equal(
            RelationQueryDefinitionFingerprinter.Compute(first.Definition),
            RelationQueryDefinitionFingerprinter.Compute(second.Definition));
        Assert.True(first.Provenance.Identities.SequenceEqual(second.Provenance.Identities));
        Assert.NotEmpty(first.Provenance.Identities);
        Assert.All(first.Provenance.Identities, static identity =>
        {
            Assert.Equal(RelationQueryAuthoringIdentityOrigin.Convention, identity.Origin);
            Assert.Equal(RelationQueryAuthoringIdentityConvention.Version, identity.Convention);
        });
    }

    [Fact]
    public void ProducerSources_AreSeparateForNodesBindingsAssignmentsAndExpressionSites()
    {
        var withSources = BuildProvenanceQuery(includeSources: true);
        var withoutSources = BuildProvenanceQuery(includeSources: false);

        Assert.Equal(
            RelationQueryDefinitionFingerprinter.Compute(withSources.Definition),
            RelationQueryDefinitionFingerprinter.Compute(withoutSources.Definition));
        Assert.Empty(withoutSources.Provenance.Sources);

        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Node, "loads", null, "source-node");
        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Binding, "load", null, "source-binding");
        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Node, "active-loads", null, "filter-node");
        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Expression, "active-loads", "predicate", "predicate");
        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Node, "load-dto", null, "project-node");
        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Binding, "dto", null, "project-binding");
        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Assignment, "assign-id", null, "assignment");
        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Expression, "assign-id", "value", "assignment-value");
        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Result, "rows", null, "rows-result");
        AssertSource(withSources, RelationQueryAuthoringDecisionKind.Terminal, "provenance-query", "query", "query-terminal");

        var bindingIdentity = Assert.Single(withSources.Provenance.Identities, static identity =>
            identity.Kind == RelationQueryAuthoringIdentityKind.Binding && identity.Value == "load");
        Assert.Equal("source-binding", bindingIdentity.Source?.Reference);
    }

    [Fact]
    public void StructuralCore_SupportsEveryCanonicalLogicalNodeKindAndBothDefinitionTerminals()
    {
        var author = RelationQuery.Structural();
        var loads = author.Source(
            LoadCustomerRelationFixture.LoadShapeId,
            new("loads"),
            new("load"));
        var customers = author.Source(
            LoadCustomerRelationFixture.CustomerShapeId,
            new("customers"),
            new("customer-source"));
        var joined = author.Join(
            loads.Node,
            customers.Node,
            JoinKind.Inner,
            Expr.Const(true),
            new("explicit-join"));
        var history = author.Source(
            LoadCustomerRelationFixture.CustomerShapeId,
            new("history"),
            new("history-value"));
        var temporal = author.TemporalJoin(
            joined,
            history.Node,
            JoinKind.Left,
            Expr.Const(true),
            new TemporalPointInIntervalMatch(
                loads.Binding.Field("OccurredAt"),
                TemporalInterval.HalfOpen(
                    history.Binding.Field("ValidFrom"),
                    history.Binding.Field("ValidTo"),
                    TemporalNullBoundBehavior.Unbounded)),
            new("temporal-join"));
        var traversed = author.Traverse(
            temporal,
            loads.Binding,
            LoadCustomerRelationFixture.LoadCustomerRelationshipId,
            nodeId: new("customer-traversal"),
            resultBindingId: new("customer"));
        var expanded = author.Expand(
            traversed.Node,
            loads.Binding.Field("Tags"),
            new ScalarTypeRef(ScalarTypeKind.String),
            new("expand-tags"),
            new("tag"));
        var filtered = author.Filter(expanded.Node, Expr.Const(true), new("filter-tags"));
        var projected = author.Project(
            filtered,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            [
                new(
                    LoadCustomerRelationFixture.SearchIdPath,
                    loads.Binding.Field(LoadCustomerRelationFixture.LoadIdPath),
                    new("assign-load-id"))
            ],
            new("project"),
            new("search"));
        var distinct = author.Distinct(
            projected.Node,
            [new RelationQueryExpressionInput(projected.Binding.Field(LoadCustomerRelationFixture.SearchIdPath))],
            new("distinct"));
        var ordered = author.Order(
            distinct,
            [new(projected.Binding.Field(LoadCustomerRelationFixture.SearchIdPath))],
            new("order"));
        var paged = author.Page(ordered, new OffsetPageDefinition(limit: 25), new("page"));
        var aggregate = author.Aggregate(
            distinct,
            LoadCustomerRelationFixture.LoadAggregateShapeId,
            aggregates:
            [
                new(
                    LoadCustomerRelationFixture.AggregateLoadCountPath,
                    AggregateOperator.Count,
                    id: new("count"))
            ],
            nodeId: new("aggregate"),
            resultBindingId: new("aggregate-result"));
        var rows = author.Rows(paged, new("rows"));
        var aggregations = author.Aggregation(aggregate.Node, new("aggregation"));
        var query = author.BuildQuery(
            new("all-node-kinds"),
            new("AllNodeKinds"),
            [rows, aggregations]);

        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));
        Assert.Equal(11, query.Definition.Body.Nodes.Select(static node => node.GetType()).Distinct().Count());
        Assert.Contains(query.Definition.Body.Nodes, static node => node is SourceQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is FilterQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is TraverseRelationshipQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is JoinQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is TemporalJoinQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is ExpandCollectionQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is ProjectQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is DistinctQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is AggregateQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is OrderQueryNode);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is PageQueryNode);

        var relationAuthor = RelationQuery.Structural();
        var relationSource = relationAuthor.Source(
            LoadCustomerRelationFixture.LoadShapeId,
            new("relation-loads"),
            new("relation-load"));
        var relationProjection = relationAuthor.Project(
            relationSource.Node,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            [
                new(
                    LoadCustomerRelationFixture.SearchIdPath,
                    relationSource.Binding.Field(LoadCustomerRelationFixture.LoadIdPath),
                    new("relation-assign-id"))
            ],
            new("relation-project"),
            new("relation-result"));
        var relation = relationAuthor.BuildRelation(
            new("load-search-relation"),
            new("LoadSearchRelation"),
            relationSource.Binding,
            relationProjection.Node,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            RelationOutputMode.OnePerRoot,
            relationProjection.Binding.Field(LoadCustomerRelationFixture.SearchIdPath));

        Assert.True(relation.Validation.IsValid, Format(relation.Validation.Diagnostics));
    }

    [Fact]
    public void Build_UsesCanonicalValidatorAndRejectsForeignHandles()
    {
        var first = RelationQuery.Structural();
        var firstSource = first.Source(LoadCustomerRelationFixture.LoadShapeId);
        var invalid = first.Filter(
            firstSource.Node,
            Expr.Eq(Expr.Field(new ValueBindingId("missing"), "Id"), Expr.Const("x")));
        var rows = first.Rows(invalid);
        var authored = first.BuildQuery(new("invalid"), new("Invalid"), [rows]);

        Assert.False(authored.Validation.IsValid);
        Assert.Contains(
            authored.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == "relationQuery.expression.bindingMissing");

        var second = RelationQuery.Structural();
        var secondSource = second.Source(LoadCustomerRelationFixture.LoadShapeId);
        Assert.Throws<ArgumentException>(() => first.Filter(secondSource.Node, Expr.Const(true)));
        Assert.Throws<ArgumentException>(() => first.BuildRelation(
            new("foreign"),
            new("Foreign"),
            secondSource.Binding,
            firstSource.Node,
            LoadCustomerRelationFixture.LoadShapeId,
            RelationOutputMode.OnePerRoot));
    }

    [Fact]
    public void Terminal_SnapshotsCurrentSessionWithoutFreezingFurtherAuthoring()
    {
        var author = RelationQuery.Structural();
        var loads = author.Source(LoadCustomerRelationFixture.LoadShapeId);
        var projected = author.Project(
            loads.Node,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            [
                new(
                    LoadCustomerRelationFixture.SearchIdPath,
                    loads.Binding.Field(LoadCustomerRelationFixture.LoadIdPath))
            ]);
        var relation = author.BuildRelation(
            new("loads-relation"),
            new("LoadsRelation"),
            loads.Binding,
            projected.Node,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            RelationOutputMode.OnePerRoot);

        var filtered = author.Filter(projected.Node, Expr.Const(true));
        var rows = author.Rows(filtered);
        var query = author.BuildQuery(new("loads-query"), new("LoadsQuery"), [rows]);

        Assert.True(relation.Validation.IsValid, Format(relation.Validation.Diagnostics));
        Assert.True(query.Validation.IsValid, Format(query.Validation.Diagnostics));
        Assert.Equal(2, relation.Definition.Body.Nodes.Length);
        Assert.Equal(3, query.Definition.Body.Nodes.Length);
    }

    [Fact]
    public void FailedDeclarations_DoNotConsumeConventionOrdinals()
    {
        var baseline = BuildTransactionalOrdinalQuery(injectFailures: false);
        var retried = BuildTransactionalOrdinalQuery(injectFailures: true);

        Assert.True(baseline.Validation.IsValid, Format(baseline.Validation.Diagnostics));
        Assert.True(retried.Validation.IsValid, Format(retried.Validation.Diagnostics));
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(baseline.CreateDocument(), indented: false),
            RelationQueryJsonSerializer.Serialize(retried.CreateDocument(), indented: false));
        Assert.True(baseline.Provenance.Identities.SequenceEqual(retried.Provenance.Identities));

        Assert.Contains(
            retried.Provenance.Identities,
            static identity => identity.Value
                == RelationQueryAuthoringIdentityConvention.CreateParameterId(1).Value);
        Assert.Contains(
            retried.Provenance.Identities,
            static identity => identity.Value
                == RelationQueryAuthoringIdentityConvention.CreateNodeId(
                    RelationQueryWireNames.FilterNode,
                    2).Value);
        Assert.Contains(
            retried.Provenance.Identities,
            static identity => identity.Value
                == RelationQueryAuthoringIdentityConvention.CreateResultId(
                    RelationQueryWireNames.RowsResult,
                    2).Value);
    }

    [Fact]
    public void TerminalProvenance_IncludesOnlyResultsSelectedByTheDefinition()
    {
        var author = RelationQuery.Structural();
        var loads = author.Source(LoadCustomerRelationFixture.LoadShapeId);
        var projected = author.Project(
            loads.Node,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            [
                new(
                    LoadCustomerRelationFixture.SearchIdPath,
                    loads.Binding.Field(LoadCustomerRelationFixture.LoadIdPath))
            ]);
        var selected = author.Rows(
            projected.Node,
            new("selected"),
            new("test", "selected-result"));
        _ = author.Rows(
            projected.Node,
            new("unselected"),
            new("test", "unselected-result"));

        var query = author.BuildQuery(new("selected-query"), new("SelectedQuery"), [selected]);
        var relation = author.BuildRelation(
            new("selected-relation"),
            new("SelectedRelation"),
            loads.Binding,
            projected.Node,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            RelationOutputMode.OnePerRoot);

        var queryResultIdentities = query.Provenance.Identities
            .Where(static identity => identity.Kind == RelationQueryAuthoringIdentityKind.Result)
            .ToArray();
        var queryResultSources = query.Provenance.Sources
            .Where(static decision => decision.Kind == RelationQueryAuthoringDecisionKind.Result)
            .ToArray();
        Assert.Equal("selected", Assert.Single(queryResultIdentities).Value);
        Assert.Equal("selected-result", Assert.Single(queryResultSources).Source.Reference);
        Assert.DoesNotContain(query.Provenance.Identities, static identity => identity.Value == "unselected");
        Assert.DoesNotContain(query.Provenance.Sources, static decision => decision.Target == "unselected");
        Assert.DoesNotContain(
            relation.Provenance.Identities,
            static identity => identity.Kind == RelationQueryAuthoringIdentityKind.Result);
        Assert.DoesNotContain(
            relation.Provenance.Sources,
            static decision => decision.Kind == RelationQueryAuthoringDecisionKind.Result);
    }

    static RelationQueryAuthoringResult<IRQueryDefinition> BuildConventionQuery()
    {
        var author = RelationQuery.Structural();
        var source = author.Source(LoadCustomerRelationFixture.LoadShapeId);
        var projection = author.Project(
            source.Node,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            [
                new(
                    LoadCustomerRelationFixture.SearchIdPath,
                    source.Binding.Field(LoadCustomerRelationFixture.LoadIdPath))
            ]);
        var rows = author.Rows(projection.Node);
        return author.BuildQuery(new("convention-query"), new("ConventionQuery"), [rows]);
    }

    static RelationQueryAuthoringResult<IRQueryDefinition> BuildTransactionalOrdinalQuery(bool injectFailures)
    {
        var author = RelationQuery.Structural();
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        if (injectFailures)
        {
            Assert.Throws<ArgumentException>(() => author.Parameter(
                stringType,
                defaultValue: ObservationValue.FromString("invalid-required-default")));
        }

        var status = author.Parameter(stringType);
        var loads = author.Source(LoadCustomerRelationFixture.LoadShapeId);
        var firstFilter = author.Filter(
            loads.Node,
            Expr.Const(true),
            new QueryNodeId("first-filter"));
        if (injectFailures)
            Assert.Throws<ArgumentNullException>(() => author.Filter(firstFilter, null!));

        var secondFilter = author.Filter(
            firstFilter,
            Expr.Eq(
                loads.Binding.Field(LoadCustomerRelationFixture.LoadStatusPath),
                status.Expression));
        var projected = author.Project(
            secondFilter,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            [
                new(
                    LoadCustomerRelationFixture.SearchIdPath,
                    loads.Binding.Field(LoadCustomerRelationFixture.LoadIdPath))
            ]);
        var primary = author.Rows(projected.Node, new QueryResultId("primary"));
        if (injectFailures)
        {
            Assert.Throws<ArgumentException>(() =>
                author.Rows(projected.Node, new QueryResultId("primary")));
        }
        var secondary = author.Rows(projected.Node);
        return author.BuildQuery(
            new("transactional-ordinals"),
            new("TransactionalOrdinals"),
            [primary, secondary]);
    }

    static RelationQueryAuthoringResult<IRQueryDefinition> BuildProvenanceQuery(bool includeSources)
    {
        RelationQueryAuthoringSource? Source(string reference) =>
            includeSources ? new("expression-proof", reference) : null;

        var author = RelationQuery.Structural();
        var loads = author.Source(
            LoadCustomerRelationFixture.LoadShapeId,
            new("loads"),
            new("load"),
            Source("source-node"),
            Source("source-binding"));
        var filtered = author.Filter(
            loads.Node,
            Expr.Const(true),
            new("active-loads"),
            Source("filter-node"),
            Source("predicate"));
        var projected = author.Project(
            filtered,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            [
                new(
                    LoadCustomerRelationFixture.SearchIdPath,
                    loads.Binding.Field(LoadCustomerRelationFixture.LoadIdPath),
                    new("assign-id"),
                    Source("assignment"),
                    Source("assignment-value"))
            ],
            new("load-dto"),
            new("dto"),
            Source("project-node"),
            Source("project-binding"));
        var rows = author.Rows(projected.Node, new("rows"), Source("rows-result"));
        return author.BuildQuery(
            new("provenance-query"),
            new("ProvenanceQuery"),
            [rows],
            Source("query-terminal"));
    }

    static void AssertSource(
        RelationQueryAuthoringResult<IRQueryDefinition> authored,
        RelationQueryAuthoringDecisionKind kind,
        string target,
        string? role,
        string sourceReference)
    {
        var decision = Assert.Single(authored.Provenance.Sources, decision =>
            decision.Kind == kind
            && decision.Target == target
            && decision.Role == role);
        Assert.Equal(sourceReference, decision.Source.Reference);
    }

    static string Format(ImmutableArray<DocumentValidationDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}"));
}
