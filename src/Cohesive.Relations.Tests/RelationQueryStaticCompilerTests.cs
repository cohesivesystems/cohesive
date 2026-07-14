using System.Collections.Immutable;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryStaticCompilerTests
{
    [Fact]
    public void Compile_EnrichedRelationBuildsExactAcquisitionLineageAndDependencyViews()
    {
        var result = Compile(LoadCustomerRelationFixture.BaselineRelationDocument);

        var plan = SuccessfulPlan(result);
        Assert.Equal(
            [
                LoadCustomerRelationFixture.SearchCustomerNamePath,
                LoadCustomerRelationFixture.SearchIdPath,
                null
            ],
            plan.RequirementGraph.Outputs.Select(static output => output.Field?.Path));

        var source = Assert.Single(plan.InputContract.Sources);
        Assert.Equal(LoadCustomerRelationFixture.LoadSourceNodeId, source.Node);
        Assert.Equal(LoadCustomerRelationFixture.LoadBinding, source.Binding);
        Assert.Equal(LoadCustomerRelationFixture.LoadShapeId, source.Shape);
        Assert.Equal(RelationQuerySourceInputRole.RelationRoot, source.Role);
        Assert.Equal(QueryInputRequirement.Required, source.Requirement);
        Assert.Equal(
            [
                LoadCustomerRelationFixture.LoadCustomerIdPath,
                LoadCustomerRelationFixture.LoadIdPath
            ],
            source.Fields.Select(static field => field.Input.Field.Path));

        var traversal = Assert.Single(plan.InputContract.Traversals);
        Assert.Equal(LoadCustomerRelationFixture.CustomerTraversalNodeId, traversal.Input.Traversal);
        Assert.Equal(LoadCustomerRelationFixture.LoadCustomerRelationship, traversal.Definition);
        Assert.Equal(RelationshipTraversalDirection.Forward, traversal.Input.Direction);
        Assert.Equal(LoadCustomerRelationFixture.LoadBinding, traversal.From);
        Assert.Equal(LoadCustomerRelationFixture.CustomerBinding, traversal.Result);
        Assert.Equal(LoadCustomerRelationFixture.CustomerShapeId, traversal.ResultShape);
        Assert.Equal(JoinKind.Left, traversal.JoinKind);
        Assert.Equal(QueryInputRequirement.Required, traversal.Requirement);
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, traversal.Cardinality);
        Assert.Equal(
            [LoadCustomerRelationFixture.CustomerNamePath],
            traversal.Fields.Select(static field => field.Input.Field.Path));

        var identity = Assert.Single(plan.InputContract.Identities);
        Assert.Equal(LoadCustomerRelationFixture.CustomerTraversalNodeId, identity.Input.Producer);
        Assert.Equal(LoadCustomerRelationFixture.CustomerBinding, identity.Input.Binding);
        Assert.Equal(LoadCustomerRelationFixture.CustomerShapeId, identity.Input.Shape);
        Assert.Empty(plan.InputContract.Parameters);

        Assert.Same(plan.RequirementGraph, plan.InputContract.Requirements);
        Assert.Same(plan.RequirementGraph, plan.Lineage.Requirements);
        Assert.Same(plan.RequirementGraph, plan.DependencyManifest.Requirements);
        Assert.Equal(
            plan.RequirementGraph.Outputs.Select(static output => output.Id),
            plan.Lineage.Entries.Select(static entry => entry.Output.Id));
        Assert.Equal(
            plan.RequirementGraph.Inputs.Select(static input => input.Id),
            plan.DependencyManifest.Entries.Select(static entry => entry.Input.Id));

        var idOutput = FieldOutput(plan, LoadCustomerRelationFixture.SearchIdPath);
        var customerNameOutput = FieldOutput(plan, LoadCustomerRelationFixture.SearchCustomerNamePath);
        var rowOutput = RowOutput(plan);
        Assert.Equal(
            [(LoadCustomerRelationFixture.LoadShapeId, LoadCustomerRelationFixture.LoadIdPath)],
            LineageFields(plan, idOutput, RelationQueryRequirementEffect.Value).ToArray());
        Assert.Equal(
            [(LoadCustomerRelationFixture.CustomerShapeId, LoadCustomerRelationFixture.CustomerNamePath)],
            LineageFields(plan, customerNameOutput, RelationQueryRequirementEffect.Value).ToArray());
        Assert.Equal(
            [(LoadCustomerRelationFixture.LoadShapeId, LoadCustomerRelationFixture.LoadIdPath)],
            LineageFields(plan, rowOutput, RelationQueryRequirementEffect.Identity).ToArray());

        var customerNameInput = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Field.Shape == LoadCustomerRelationFixture.CustomerShapeId
                && input.Field.Path == LoadCustomerRelationFixture.CustomerNamePath);
        var customerNameEdge = Assert.Single(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == customerNameInput.Id
                && edge.Output.Id == customerNameOutput.Id
                && edge.Effect == RelationQueryRequirementEffect.Value);
        var customerNameTrace = Assert.Single(customerNameEdge.Traces);
        Assert.Equal(
            [
                RelationQueryExpressionSiteKind.ProjectionAssignmentValue,
                null
            ],
            customerNameTrace.Steps.Select(static step => step.SiteKind));
        Assert.Equal(
            LoadCustomerRelationFixture.SearchCustomerNameAssignmentId,
            customerNameTrace.Steps[0].Assignment);
        Assert.Equal(
            [
                LoadCustomerRelationFixture.ProjectionNodeId,
                LoadCustomerRelationFixture.CustomerTraversalNodeId
            ],
            customerNameTrace.Steps.Select(static step => step.Node));

        var reference = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Field.Shape == LoadCustomerRelationFixture.LoadShapeId
                && input.Field.Path == LoadCustomerRelationFixture.LoadCustomerIdPath);
        Assert.Contains(
            plan.DependencyManifest.Entries.Single(entry => entry.Input.Id == reference.Id).Impacts,
            impact => impact.Effect == RelationQueryRequirementEffect.Correlation);
        Assert.DoesNotContain(
            plan.Lineage.Entries.SelectMany(static entry => entry.Contributions),
            contribution => contribution.Input.Id == reference.Id);
    }

    [Fact]
    public void Compile_IdOnlyDemandBypassesOptionalAtMostOneTraversal()
    {
        var result = Compile(
            LoadCustomerRelationFixture.OptionalTraversalRelationDocument,
            RelationFields(LoadCustomerRelationFixture.SearchIdPath));

        var plan = SuccessfulPlan(result);
        Assert.Equal(RelationQueryCompilationDemandOrigin.Explicit, plan.DemandOrigin);
        Assert.Equal(
            [
                LoadCustomerRelationFixture.LoadSourceNodeId,
                LoadCustomerRelationFixture.ProjectionNodeId
            ],
            plan.LogicalPlan.RetainedNodes.ToArray());
        var project = Assert.Single(
            plan.LogicalPlan.Nodes,
            node => node.Node == LoadCustomerRelationFixture.ProjectionNodeId);
        var input = Assert.Single(project.Inputs);
        Assert.Equal(LoadCustomerRelationFixture.CustomerTraversalNodeId, input.CanonicalInput);
        Assert.Equal(LoadCustomerRelationFixture.LoadSourceNodeId, input.EffectiveInput);
        var bypass = Assert.Single(input.Bypasses);
        Assert.Equal(
            RelationQueryLogicalBypassKind.OptionalAtMostOneLeftRelationshipTraversal,
            bypass.Kind);
        Assert.Equal(LoadCustomerRelationFixture.CustomerTraversalNodeId, bypass.Node);
        Assert.Equal(RelationshipTraversalCardinality.AtMostOne, bypass.Cardinality);

        Assert.Empty(plan.InputContract.Traversals);
        Assert.Empty(plan.InputContract.Identities);
        Assert.DoesNotContain(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Field.Shape == LoadCustomerRelationFixture.CustomerShapeId);
        var source = Assert.Single(plan.InputContract.Sources);
        Assert.Equal(
            [LoadCustomerRelationFixture.LoadIdPath],
            source.Fields.Select(static field => field.Input.Field.Path));
    }

    [Fact]
    public void Compile_RequiredOrInnerTraversalCannotBePrunedFromIdOnlyDemand()
    {
        var innerOptional = LoadCustomerRelationFixture.CreateRelationDocument(
            new LoadCustomerTraversalOptions(
                JoinKind.Inner,
                QueryInputRequirement.Optional,
                RelationOutputMode.ZeroOrOnePerRoot,
                LoadCustomerProjectionMode.Enriched));

        foreach (var document in new[]
                 {
                     LoadCustomerRelationFixture.BaselineRelationDocument,
                     LoadCustomerRelationFixture.InnerTraversalRelationDocument,
                     innerOptional
                 })
        {
            var plan = SuccessfulPlan(Compile(
                document,
                RelationFields(LoadCustomerRelationFixture.SearchIdPath)));

            Assert.Contains(LoadCustomerRelationFixture.CustomerTraversalNodeId, plan.LogicalPlan.RetainedNodes);
            Assert.Single(plan.InputContract.Traversals);
            Assert.Single(plan.InputContract.Identities);
            Assert.Contains(
                plan.InputContract.Sources.Single().Fields,
                field => field.Input.Field.Path == LoadCustomerRelationFixture.LoadCustomerIdPath);
        }
    }

    [Fact]
    public void Compile_TraversalEffectsReflectJoinMembershipAndIndependentFanOutCardinality()
    {
        var leftAtMostOne = SuccessfulPlan(Compile(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            RelationFields(LoadCustomerRelationFixture.SearchCustomerNamePath)));
        Assert.Equal(
            [RelationQueryRequirementEffect.Acquisition],
            RelationshipEffects(leftAtMostOne));

        var optionalLeftAtMostOne = SuccessfulPlan(Compile(
            LoadCustomerRelationFixture.OptionalTraversalRelationDocument,
            RelationFields(LoadCustomerRelationFixture.SearchCustomerNamePath)));
        Assert.Equal(
            [RelationQueryRequirementEffect.Acquisition],
            RelationshipEffects(optionalLeftAtMostOne));

        var innerAtMostOne = SuccessfulPlan(Compile(
            LoadCustomerRelationFixture.InnerTraversalRelationDocument,
            RelationFields(LoadCustomerRelationFixture.SearchCustomerNamePath)));
        Assert.Equal(
            [
                RelationQueryRequirementEffect.Membership,
                RelationQueryRequirementEffect.Acquisition
            ],
            RelationshipEffects(innerAtMostOne));

        var leftMany = SuccessfulPlan(Compile(
            CreateInverseTraversalQueryDocument(JoinKind.Left),
            QueryFields(
                LoadCustomerRelationFixture.RowsResultId,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath)));
        Assert.Equal(
            [
                RelationQueryRequirementEffect.Acquisition,
                RelationQueryRequirementEffect.Cardinality
            ],
            RelationshipEffects(leftMany));

        var innerMany = SuccessfulPlan(Compile(
            CreateInverseTraversalQueryDocument(JoinKind.Inner),
            QueryFields(
                LoadCustomerRelationFixture.RowsResultId,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath)));
        Assert.Equal(
            [
                RelationQueryRequirementEffect.Membership,
                RelationQueryRequirementEffect.Acquisition,
                RelationQueryRequirementEffect.Cardinality
            ],
            RelationshipEffects(innerMany));
    }

    [Fact]
    public void Compile_QueryResultDemandRetainsOnlyTheDemandedBranch()
    {
        var rowsPlan = SuccessfulPlan(Compile(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            QueryFields(
                LoadCustomerRelationFixture.RowsResultId,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath)));

        Assert.Equal(
            [LoadCustomerRelationFixture.RowsResultId],
            rowsPlan.RequirementGraph.Outputs
                .Select(static output => output.QueryResult!.Value)
                .Distinct());
        Assert.Contains(LoadCustomerRelationFixture.PageNodeId, rowsPlan.LogicalPlan.RetainedNodes);
        Assert.Contains(LoadCustomerRelationFixture.OrderNodeId, rowsPlan.LogicalPlan.RetainedNodes);
        Assert.Contains(LoadCustomerRelationFixture.ProjectionNodeId, rowsPlan.LogicalPlan.RetainedNodes);
        Assert.DoesNotContain(LoadCustomerRelationFixture.AggregateNodeId, rowsPlan.LogicalPlan.RetainedNodes);
        Assert.DoesNotContain(
            rowsPlan.InputContract.Capabilities,
            capability => capability.Input.Capability.Capability
                == ExprCapabilities.ForAggregate(AggregateOperator.Count));

        var aggregatePlan = SuccessfulPlan(Compile(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            QueryFields(
                LoadCustomerRelationFixture.AggregationResultId,
                LoadCustomerRelationFixture.LoadAggregateShapeId,
                LoadCustomerRelationFixture.AggregateLoadCountPath)));

        Assert.Equal(
            [LoadCustomerRelationFixture.AggregationResultId],
            aggregatePlan.RequirementGraph.Outputs
                .Select(static output => output.QueryResult!.Value)
                .Distinct());
        Assert.Contains(LoadCustomerRelationFixture.AggregateNodeId, aggregatePlan.LogicalPlan.RetainedNodes);
        Assert.DoesNotContain(LoadCustomerRelationFixture.PageNodeId, aggregatePlan.LogicalPlan.RetainedNodes);
        Assert.DoesNotContain(LoadCustomerRelationFixture.OrderNodeId, aggregatePlan.LogicalPlan.RetainedNodes);
        Assert.DoesNotContain(LoadCustomerRelationFixture.ProjectionNodeId, aggregatePlan.LogicalPlan.RetainedNodes);
        Assert.Contains(
            aggregatePlan.InputContract.Capabilities,
            capability => capability.Input.Capability.Capability
                == ExprCapabilities.ForAggregate(AggregateOperator.Count));
        Assert.DoesNotContain(
            aggregatePlan.InputContract.Capabilities,
            capability => capability.Input.Capability.Capability
                == ExprCapabilities.ForAggregate(AggregateOperator.Sum));
    }

    [Fact]
    public void Compile_FilterOrderAndPageDependenciesDoNotBecomeValueLineage()
    {
        var plan = SuccessfulPlan(Compile(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            QueryFields(
                LoadCustomerRelationFixture.RowsResultId,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath)));

        var status = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadStatusPath);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == status.Id
                && edge.Effect == RelationQueryRequirementEffect.Membership);

        var statusParameter = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryParameterInput>(),
            input => input.Parameter == LoadCustomerRelationFixture.StatusParameterId);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == statusParameter.Id
                && edge.Effect == RelationQueryRequirementEffect.Membership);
        var cursorParameter = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryParameterInput>(),
            input => input.Parameter == LoadCustomerRelationFixture.CursorParameterId);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == cursorParameter.Id
                && edge.Effect == RelationQueryRequirementEffect.Pagination);

        var loadId = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == loadId.Id
                && edge.Effect == RelationQueryRequirementEffect.Ordering);

        var lineageInputs = plan.Lineage.Entries
            .SelectMany(static entry => entry.Contributions)
            .Select(static contribution => contribution.Input.Id)
            .ToHashSet();
        Assert.DoesNotContain(status.Id, lineageInputs);
        Assert.DoesNotContain(statusParameter.Id, lineageInputs);
        Assert.DoesNotContain(cursorParameter.Id, lineageInputs);
        Assert.Contains(loadId.Id, lineageInputs);

        Assert.Contains(plan.DependencyManifest.Entries, entry => entry.Input.Id == status.Id);
        Assert.Contains(plan.DependencyManifest.Entries, entry => entry.Input.Id == statusParameter.Id);
        Assert.Contains(plan.DependencyManifest.Entries, entry => entry.Input.Id == cursorParameter.Id);
    }

    [Fact]
    public void Compile_AggregationSeparatesGroupingValueFilterAndOperatorRequirements()
    {
        var plan = SuccessfulPlan(Compile(
            LoadCustomerRelationFixture.RepresentativeQueryDocument,
            QueryFields(
                LoadCustomerRelationFixture.AggregationResultId,
                LoadCustomerRelationFixture.LoadAggregateShapeId,
                LoadCustomerRelationFixture.AggregateCustomerNamePath,
                LoadCustomerRelationFixture.AggregateTotalAmountPath,
                LoadCustomerRelationFixture.AggregateLoadCountPath)));

        var customerName = FieldInput(
            plan,
            LoadCustomerRelationFixture.CustomerShapeId,
            LoadCustomerRelationFixture.CustomerNamePath);
        var amount = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadAmountPath);
        var active = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadActivePath);
        var status = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadStatusPath);

        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == customerName.Id
                && edge.Effect == RelationQueryRequirementEffect.Grouping);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == customerName.Id
                && edge.Effect == RelationQueryRequirementEffect.Value);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == amount.Id
                && edge.Effect == RelationQueryRequirementEffect.Aggregation);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == active.Id
                && edge.Effect == RelationQueryRequirementEffect.Membership);

        Assert.Contains(
            plan.InputContract.Capabilities,
            capability => capability.Input.Capability.Capability
                == ExprCapabilities.ForAggregate(AggregateOperator.Count));
        Assert.Contains(
            plan.InputContract.Capabilities,
            capability => capability.Input.Capability.Capability
                == ExprCapabilities.ForAggregate(AggregateOperator.Sum));
        var capabilityIds = plan.InputContract.Capabilities
            .Select(static capability => capability.Input.Id)
            .ToHashSet();
        Assert.All(
            plan.RequirementGraph.Edges.Where(edge => capabilityIds.Contains(edge.Input.Id)),
            static edge => Assert.Equal(RelationQueryRequirementEffect.Evaluation, edge.Effect));
        Assert.DoesNotContain(
            plan.Lineage.Entries.SelectMany(static entry => entry.Contributions),
            contribution => capabilityIds.Contains(contribution.Input.Id));

        var totalOutput = FieldOutput(plan, LoadCustomerRelationFixture.AggregateTotalAmountPath);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == customerName.Id
                && edge.Output.Id == totalOutput.Id
                && edge.Effect == RelationQueryRequirementEffect.Grouping);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == status.Id
                && edge.Output.Id == totalOutput.Id
                && edge.Effect == RelationQueryRequirementEffect.Membership);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == active.Id
                && edge.Output.Id == totalOutput.Id
                && edge.Effect == RelationQueryRequirementEffect.Membership);
        var loadSource = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQuerySourceSetInput>(),
            input => input.Source == LoadCustomerRelationFixture.LoadSourceNodeId);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == loadSource.Id
                && edge.Output.Id == totalOutput.Id
                && edge.Effect == RelationQueryRequirementEffect.Aggregation);
        var sumCapability = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryCapabilityInput>(),
            input => input.Capability.Capability
                == ExprCapabilities.ForAggregate(AggregateOperator.Sum));
        var sumCapabilityEdge = Assert.Single(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == sumCapability.Id
                && edge.Output.Id == totalOutput.Id);
        var sumCapabilityTrace = Assert.Single(sumCapabilityEdge.Traces);
        var operationStep = Assert.Single(sumCapabilityTrace.Steps);
        Assert.Equal(RelationQueryRequirementTraceStepKind.AggregateOperation, operationStep.Kind);
        Assert.Equal(LoadCustomerRelationFixture.AggregateNodeId, operationStep.Node);
        Assert.Equal(
            LoadCustomerRelationFixture.AggregateTotalAmountAssignmentId,
            operationStep.Assignment);
        Assert.Contains(
            LineageFields(plan, totalOutput, RelationQueryRequirementEffect.Aggregation),
            field => field == (LoadCustomerRelationFixture.LoadShapeId, LoadCustomerRelationFixture.LoadAmountPath));
        Assert.DoesNotContain(
            plan.Lineage.Entries.SelectMany(static entry => entry.Contributions),
            contribution => contribution.Input.Id == active.Id);
    }

    [Fact]
    public void Compile_ExplicitJoinRequiresBothSourcesAndCorrelationButNoRelationshipTraversal()
    {
        var plan = SuccessfulPlan(Compile(
            LoadCustomerRelationFixture.ExplicitJoinQueryDocument,
            QueryFields(
                LoadCustomerRelationFixture.RowsResultId,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath)));

        Assert.Equal(
            [
                LoadCustomerRelationFixture.CustomerSourceNodeId,
                LoadCustomerRelationFixture.LoadSourceNodeId
            ],
            plan.InputContract.Sources.Select(static source => source.Node));
        Assert.All(
            plan.InputContract.Sources,
            static source => Assert.Equal(QueryInputRequirement.Required, source.Requirement));
        Assert.All(
            plan.InputContract.Sources,
            source => Assert.Equal(
                [
                    RelationQueryRequirementEffect.Membership,
                    RelationQueryRequirementEffect.Cardinality
                ],
                plan.RequirementGraph.Edges
                    .Where(edge => edge.Input.Id == source.Input.Id)
                    .Select(static edge => edge.Effect)
                    .Distinct()
                    .OrderBy(static effect => (int)effect)));
        Assert.Empty(plan.InputContract.Traversals);
        Assert.Empty(plan.InputContract.Identities);
        Assert.Contains(LoadCustomerRelationFixture.ExplicitJoinNodeId, plan.LogicalPlan.RetainedNodes);

        var loadReference = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadCustomerIdPath);
        var customerId = FieldInput(
            plan,
            LoadCustomerRelationFixture.CustomerShapeId,
            LoadCustomerRelationFixture.CustomerIdPath);
        var predicateEffects = new[]
        {
            RelationQueryRequirementEffect.Membership,
            RelationQueryRequirementEffect.Correlation,
            RelationQueryRequirementEffect.Cardinality
        };
        foreach (var input in new[] { loadReference, customerId })
        {
            Assert.Equal(
                predicateEffects,
                plan.RequirementGraph.Edges
                    .Where(edge => edge.Input.Id == input.Id)
                    .Select(static edge => edge.Effect)
                    .Distinct()
                    .OrderBy(static effect => (int)effect));
            Assert.Equal(
                predicateEffects,
                plan.DependencyManifest.Entries
                    .Single(entry => entry.Input.Id == input.Id)
                    .Impacts
                    .Select(static impact => impact.Effect)
                    .Distinct()
                    .OrderBy(static effect => (int)effect));
            Assert.All(
                plan.RequirementGraph.Edges.Where(edge => edge.Input.Id == input.Id),
                static edge => Assert.All(
                    edge.Traces,
                    static trace => Assert.Contains(
                        trace.Steps,
                        static step => step.SiteKind == RelationQueryExpressionSiteKind.JoinPredicate)));
        }
    }

    [Fact]
    public void Compile_CollectionExpansionAffectsBothMembershipAndCardinality()
    {
        var collectionParameter = new QueryParameterId("items");
        var sourceNode = new QueryNodeId("expand-source");
        var expansionNode = new QueryNodeId("expand-items");
        var projectionNode = new QueryNodeId("expand-project");
        var itemBinding = new ValueBindingId("item");
        var resultBinding = new ValueBindingId("expanded-result");
        var itemType = new ScalarTypeRef(ScalarTypeKind.String);
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new QueryId("expand-query"),
            new QueryName("ExpandQuery"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(
                        sourceNode,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new ExpandCollectionQueryNode(
                        expansionNode,
                        sourceNode,
                        Expr.Param(collectionParameter.Value),
                        itemBinding,
                        itemType),
                    new ProjectQueryNode(
                        projectionNode,
                        expansionNode,
                        resultBinding,
                        LoadCustomerRelationFixture.LoadSearchShapeId,
                        [
                            new(
                                new QueryAssignmentId("expanded-id"),
                                LoadCustomerRelationFixture.SearchIdPath,
                                Expr.Const(ObservationValue.FromString("expanded")))
                        ])
                ],
                parameters:
                [
                    new QueryParameterDefinition(collectionParameter, new ArrayTypeRef(itemType))
                ]),
            [new RowsQueryResultDefinition(LoadCustomerRelationFixture.RowsResultId, projectionNode)]);
        var plan = SuccessfulPlan(Compile(
            RelationQueryDocument.FromDefinition(definition),
            QueryFields(
                LoadCustomerRelationFixture.RowsResultId,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                LoadCustomerRelationFixture.SearchIdPath)));

        var parameter = Assert.Single(plan.RequirementGraph.Inputs.OfType<RelationQueryParameterInput>());
        Assert.Equal(collectionParameter, parameter.Parameter);
        Assert.Equal(
            [
                RelationQueryRequirementEffect.Membership,
                RelationQueryRequirementEffect.Cardinality
            ],
            plan.RequirementGraph.Edges
                .Where(edge => edge.Input.Id == parameter.Id)
                .Select(static edge => edge.Effect)
                .Distinct()
                .OrderBy(static effect => (int)effect));
        Assert.All(
            plan.RequirementGraph.Edges.Where(edge => edge.Input.Id == parameter.Id),
            static edge => Assert.All(
                edge.Traces,
                static trace => Assert.Contains(
                    trace.Steps,
                    static step => step.SiteKind == RelationQueryExpressionSiteKind.ExpandCollection)));

        var source = Assert.Single(plan.InputContract.Sources);
        Assert.Equal(
            [
                RelationQueryRequirementEffect.Membership,
                RelationQueryRequirementEffect.Cardinality
            ],
            plan.RequirementGraph.Edges
                .Where(edge => edge.Input.Id == source.Input.Id)
                .Select(static edge => edge.Effect)
                .Distinct()
                .OrderBy(static effect => (int)effect));
    }

    [Fact]
    public void Compile_ExplicitJoinRowsetsPreserveIncomingAggregationEffect()
    {
        var aggregateNode = new QueryNodeId("join-total");
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new QueryId("join-aggregate"),
            new QueryName("JoinAggregate"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new SourceQueryNode(
                    LoadCustomerRelationFixture.CustomerSourceNodeId,
                    LoadCustomerRelationFixture.CustomerBinding,
                    LoadCustomerRelationFixture.CustomerShapeId),
                new JoinQueryNode(
                    LoadCustomerRelationFixture.ExplicitJoinNodeId,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.CustomerSourceNodeId,
                    JoinKind.Inner,
                    Expr.Eq(
                        Expr.Field(
                            LoadCustomerRelationFixture.LoadBinding,
                            LoadCustomerRelationFixture.LoadCustomerIdPath),
                        Expr.Field(
                            LoadCustomerRelationFixture.CustomerBinding,
                            LoadCustomerRelationFixture.CustomerIdPath))),
                new AggregateQueryNode(
                    aggregateNode,
                    LoadCustomerRelationFixture.ExplicitJoinNodeId,
                    LoadCustomerRelationFixture.AggregateBinding,
                    LoadCustomerRelationFixture.LoadAggregateShapeId,
                    aggregates:
                    [
                        new(
                            LoadCustomerRelationFixture.AggregateTotalAmountAssignmentId,
                            LoadCustomerRelationFixture.AggregateTotalAmountPath,
                            AggregateOperator.Sum,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadAmountPath))
                    ])
            ]),
            [
                new AggregationQueryResultDefinition(
                    LoadCustomerRelationFixture.AggregationResultId,
                    aggregateNode)
            ]);
        var plan = SuccessfulPlan(Compile(
            RelationQueryDocument.FromDefinition(definition),
            QueryFields(
                LoadCustomerRelationFixture.AggregationResultId,
                LoadCustomerRelationFixture.LoadAggregateShapeId,
                LoadCustomerRelationFixture.AggregateTotalAmountPath)));

        Assert.All(
            plan.InputContract.Sources,
            source => Assert.Equal(
                [
                    RelationQueryRequirementEffect.Membership,
                    RelationQueryRequirementEffect.Cardinality,
                    RelationQueryRequirementEffect.Aggregation
                ],
                plan.RequirementGraph.Edges
                    .Where(edge => edge.Input.Id == source.Input.Id)
                    .Select(static edge => edge.Effect)
                    .Distinct()
                    .OrderBy(static effect => (int)effect)));
    }

    [Fact]
    public void Compile_RowOperatorsPreserveAggregationAlongsideTheirLocalEffects()
    {
        var collectionParameter = new QueryParameterId("operator-items");
        var sourceNode = new QueryNodeId("operator-source");
        var expansionNode = new QueryNodeId("operator-expand");
        var distinctNode = new QueryNodeId("operator-distinct");
        var pageNode = new QueryNodeId("operator-page");
        var aggregateNode = new QueryNodeId("operator-aggregate");
        var itemType = new ScalarTypeRef(ScalarTypeKind.String);
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new QueryId("operator-aggregate-query"),
            new QueryName("OperatorAggregateQuery"),
            new LogicalQueryDefinition(
                nodes:
                [
                    new SourceQueryNode(
                        sourceNode,
                        LoadCustomerRelationFixture.LoadBinding,
                        LoadCustomerRelationFixture.LoadShapeId),
                    new ExpandCollectionQueryNode(
                        expansionNode,
                        sourceNode,
                        Expr.Param(collectionParameter.Value),
                        new ValueBindingId("operator-item"),
                        itemType),
                    new DistinctQueryNode(
                        distinctNode,
                        expansionNode,
                        [
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadIdPath)
                        ]),
                    new PageQueryNode(
                        pageNode,
                        distinctNode,
                        new OffsetPageDefinition(limit: 50)),
                    new AggregateQueryNode(
                        aggregateNode,
                        pageNode,
                        LoadCustomerRelationFixture.AggregateBinding,
                        LoadCustomerRelationFixture.LoadAggregateShapeId,
                        aggregates:
                        [
                            new(
                                LoadCustomerRelationFixture.AggregateTotalAmountAssignmentId,
                                LoadCustomerRelationFixture.AggregateTotalAmountPath,
                                AggregateOperator.Sum,
                                Expr.Field(
                                    LoadCustomerRelationFixture.LoadBinding,
                                    LoadCustomerRelationFixture.LoadAmountPath))
                        ])
                ],
                parameters:
                [
                    new QueryParameterDefinition(collectionParameter, new ArrayTypeRef(itemType))
                ]),
            [
                new AggregationQueryResultDefinition(
                    LoadCustomerRelationFixture.AggregationResultId,
                    aggregateNode)
            ]);
        var plan = SuccessfulPlan(Compile(
            RelationQueryDocument.FromDefinition(definition),
            QueryFields(
                LoadCustomerRelationFixture.AggregationResultId,
                LoadCustomerRelationFixture.LoadAggregateShapeId,
                LoadCustomerRelationFixture.AggregateTotalAmountPath)));

        var source = Assert.Single(plan.InputContract.Sources);
        Assert.Equal(
            [
                RelationQueryRequirementEffect.Membership,
                RelationQueryRequirementEffect.Cardinality,
                RelationQueryRequirementEffect.Aggregation,
                RelationQueryRequirementEffect.Pagination
            ],
            plan.RequirementGraph.Edges
                .Where(edge => edge.Input.Id == source.Input.Id)
                .Select(static edge => edge.Effect)
                .Distinct()
                .OrderBy(static effect => (int)effect));
        var distinctKey = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadIdPath);
        var totalOutput = FieldOutput(plan, LoadCustomerRelationFixture.AggregateTotalAmountPath);
        Assert.Equal(
            [
                RelationQueryRequirementEffect.Membership,
                RelationQueryRequirementEffect.Cardinality
            ],
            plan.RequirementGraph.Edges
                .Where(edge => edge.Input.Id == distinctKey.Id
                    && edge.Output.Id == totalOutput.Id
                    && edge.Traces.SelectMany(static trace => trace.Steps).Any(
                        static step => step.SiteKind == RelationQueryExpressionSiteKind.DistinctKey))
                .Select(static edge => edge.Effect)
                .Distinct()
                .OrderBy(static effect => (int)effect));
    }

    [Fact]
    public void Compile_IsDeterministicAcrossRepeatedCompilation()
    {
        var first = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);
        var second = Compile(LoadCustomerRelationFixture.RepresentativeQueryDocument);

        Assert.Equal(CompilationSignature(SuccessfulPlan(first)), CompilationSignature(SuccessfulPlan(second)));
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    [Fact]
    public void Compile_OperationalExpressionMayReadAnUnassignedOptionalProjectionField()
    {
        var projectNode = new QueryNodeId("project-load-only");
        var filterNode = new QueryNodeId("filter-missing-customer-name");
        var resultId = new QueryResultId("loads-without-customer-name");
        var definition = new QueryDefinition(
            new QueryId("optional-projection-filter"),
            new QueryName("OptionalProjectionFilter"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new ProjectQueryNode(
                    projectNode,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            LoadCustomerRelationFixture.SearchIdAssignmentId,
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadIdPath))
                    ]),
                new FilterQueryNode(
                    filterNode,
                    projectNode,
                    Expr.Eq(
                        Expr.Field(
                            LoadCustomerRelationFixture.SearchBinding,
                            LoadCustomerRelationFixture.SearchCustomerNamePath),
                        Expr.Null()))
            ]),
            [new RowsQueryResultDefinition(resultId, filterNode)]);
        var document = RelationQueryDocument.FromDefinition(definition);
        var demand = QueryFields(
            resultId,
            LoadCustomerRelationFixture.LoadSearchShapeId,
            LoadCustomerRelationFixture.SearchIdPath);

        var plan = SuccessfulPlan(RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            demand: demand)));

        Assert.DoesNotContain(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Field.Shape == LoadCustomerRelationFixture.LoadSearchShapeId
                && input.Field.Path == LoadCustomerRelationFixture.SearchCustomerNamePath);
        Assert.DoesNotContain(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Field.Shape == LoadCustomerRelationFixture.CustomerShapeId
                && input.Field.Path == LoadCustomerRelationFixture.CustomerNamePath);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input is RelationQueryCapabilityInput
                && edge.Effect == RelationQueryRequirementEffect.Evaluation);
    }

    [Fact]
    public void Compile_ScopedCurrentItemDependencyIsInternalToItsExpressionSite()
    {
        var projectNode = new QueryNodeId("project-any-value");
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var values = new LiteralExpr(
            new ArrayTypeRef(stringType),
            ObservationValue.FromArray([ObservationValue.FromString("value")]));
        var anyValue = Expr.Call(
            ExprFunctionNames.Any,
            values,
            Expr.Eq(Expr.CurrentItem(), Expr.Const("value")));
        var definition = new Cohesive.Relations.IR.RelationDefinition(
            new RelationId("current-item-projection"),
            new RelationName("CurrentItemProjection"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new ProjectQueryNode(
                    projectNode,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadShapeId,
                    [
                        new(
                            new QueryAssignmentId("assign-active"),
                            LoadCustomerRelationFixture.LoadActivePath,
                            anyValue)
                    ])
            ]),
            LoadCustomerRelationFixture.LoadBinding,
            new RelationOutputDefinition(
                projectNode,
                LoadCustomerRelationFixture.LoadShapeId,
                RelationOutputMode.OnePerRoot));
        var document = RelationQueryDocument.FromDefinition(definition);
        var demand = RelationQueryCompilationDemand.ForRelationFields(
        [
            new(
                LoadCustomerRelationFixture.LoadShapeId,
                LoadCustomerRelationFixture.LoadActivePath)
        ]);

        var plan = SuccessfulPlan(RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            demand: demand)));

        Assert.Contains(
            plan.RequirementGraph.Inputs.OfType<RelationQueryCapabilityInput>(),
            input => input.Capability.Capability == ExprCapabilities.CurrentItem);
        Assert.DoesNotContain(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Binding == LoadCustomerRelationFixture.SearchBinding);
    }

    [Fact]
    public void Compile_UnresolvableNestedProjectionPathFallsBackToParentField()
    {
        var sourceGraphId = new GraphId("nested-source/v1");
        var targetGraphId = new GraphId("nested-target/v1");
        var rootShapeId = new ShapeId("Root");
        var detailsShapeId = new ShapeId("Details");
        var detailsType = new NamedTypeRef(new TypeId(detailsShapeId.Value));
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var profilePath = FieldPath.FromField("Profile");
        var profileCodePath = FieldPath.Parse("Profile.Code");
        var sourceRoot = new QualifiedShapeId(sourceGraphId, rootShapeId);
        var targetRoot = new QualifiedShapeId(targetGraphId, rootShapeId);
        var sourceDocument = ShapeGraphDocument.FromGraph(new ShapeGraph(
            sourceGraphId,
            [
                new Shape(rootShapeId, [new(new FieldName("Profile"), detailsType)])
            ],
            [
                new TypeDefinition.Structural(
                    detailsType.TypeId,
                    [new(new FieldName("Name"), stringType)])
            ]));
        var targetDocument = ShapeGraphDocument.FromGraph(new ShapeGraph(
            targetGraphId,
            [
                new Shape(rootShapeId, [new(new FieldName("Profile"), detailsType)])
            ],
            [
                new TypeDefinition.Structural(
                    detailsType.TypeId,
                    [new(new FieldName("Code"), stringType)])
            ]));
        var sourceBinding = new ValueBindingId("source");
        var targetBinding = new ValueBindingId("target");
        var sourceNode = new QueryNodeId("source");
        var projectNode = new QueryNodeId("project");
        var definition = new Cohesive.Relations.IR.RelationDefinition(
            new RelationId("nested-profile"),
            new RelationName("NestedProfile"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(sourceNode, sourceBinding, sourceRoot),
                new ProjectQueryNode(
                    projectNode,
                    sourceNode,
                    targetBinding,
                    targetRoot,
                    [
                        new(
                            new QueryAssignmentId("assign-profile"),
                            profilePath,
                            Expr.Field(sourceBinding, profilePath))
                    ])
            ]),
            sourceBinding,
            new RelationOutputDefinition(projectNode, targetRoot, RelationOutputMode.OnePerRoot));
        var result = RelationQueryStaticCompiler.Compile(new(
            RelationQueryDocument.FromDefinition(definition),
            [sourceDocument, targetDocument],
            demand: RelationQueryCompilationDemand.ForRelationFields(
            [
                new(targetRoot, profileCodePath)
            ])));

        var plan = SuccessfulPlan(result);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryCompilationDiagnosticCodes.FieldSelectionConservative
                && diagnostic.Severity == DiagnosticSeverity.Warning);
        var source = Assert.Single(plan.InputContract.Sources);
        Assert.Contains(source.Fields, field => field.Input.Field.Path == profilePath);
        Assert.DoesNotContain(source.Fields, field => field.Input.Field.Path == profileCodePath);
    }

    [Fact]
    public void Compile_StructuralProjectionRequiresCompleteRequiredDescendantCoverage()
    {
        var sourceGraphId = new GraphId("flat-source/v1");
        var targetGraphId = new GraphId("structural-target/v1");
        var sourceShapeId = new ShapeId("FlatAddress");
        var targetShapeId = new ShapeId("AddressDto");
        var addressTypeId = new TypeId("Address");
        var stringType = new ScalarTypeRef(ScalarTypeKind.String);
        var streetPath = FieldPath.FromField("Street");
        var cityPath = FieldPath.FromField("City");
        var addressPath = FieldPath.FromField("Address");
        var addressStreetPath = FieldPath.Parse("Address.Street");
        var addressCityPath = FieldPath.Parse("Address.City");
        var sourceShape = new QualifiedShapeId(sourceGraphId, sourceShapeId);
        var targetShape = new QualifiedShapeId(targetGraphId, targetShapeId);
        var sourceDocument = ShapeGraphDocument.FromGraph(new ShapeGraph(
            sourceGraphId,
            [
                new Shape(
                    sourceShapeId,
                    [
                        new(new FieldName("Street"), stringType),
                        new(new FieldName("City"), stringType)
                    ])
            ]));
        var targetDocument = ShapeGraphDocument.FromGraph(new ShapeGraph(
            targetGraphId,
            [
                new Shape(
                    targetShapeId,
                    [
                        new(
                            new FieldName("Address"),
                            new NamedTypeRef(addressTypeId))
                    ])
            ],
            [
                new TypeDefinition.Structural(
                    addressTypeId,
                    [
                        new(new FieldName("Street"), stringType),
                        new(new FieldName("City"), stringType)
                    ])
            ]));
        var sourceBinding = new ValueBindingId("flat");
        var resultBinding = new ValueBindingId("dto");
        var sourceNode = new QueryNodeId("flat-source");
        var projectNode = new QueryNodeId("address-project");

        RelationQueryDocument CreateDocument(bool includeCity)
        {
            List<ProjectionAssignment> assignments =
            [
                new(
                    new QueryAssignmentId("assign-street"),
                    addressStreetPath,
                    Expr.Field(sourceBinding, streetPath))
            ];
            if (includeCity)
            {
                assignments.Add(new(
                    new QueryAssignmentId("assign-city"),
                    addressCityPath,
                    Expr.Field(sourceBinding, cityPath)));
            }

            return RelationQueryDocument.FromDefinition(new Cohesive.Relations.IR.RelationDefinition(
                new RelationId("structural-address"),
                new RelationName("StructuralAddress"),
                new LogicalQueryDefinition(
                [
                    new SourceQueryNode(sourceNode, sourceBinding, sourceShape),
                    new ProjectQueryNode(
                        projectNode,
                        sourceNode,
                        resultBinding,
                        targetShape,
                        [.. assignments])
                ]),
                sourceBinding,
                new RelationOutputDefinition(
                    projectNode,
                    targetShape,
                    RelationOutputMode.OnePerRoot)));
        }

        var incomplete = RelationQueryStaticCompiler.Compile(new(
            CreateDocument(includeCity: false),
            [sourceDocument, targetDocument]));
        Assert.False(incomplete.IsSuccessful);
        Assert.Null(incomplete.Plan);
        var missing = Assert.Single(
            incomplete.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryCompilationDiagnosticCodes.OutputFieldUnassigned);
        Assert.Contains(addressCityPath.ToString(), missing.Message, StringComparison.Ordinal);

        var complete = SuccessfulPlan(RelationQueryStaticCompiler.Compile(new(
            CreateDocument(includeCity: true),
            [sourceDocument, targetDocument])));
        var addressOutput = FieldOutput(complete, addressPath);
        var addressLineage = LineageFields(
            complete,
            addressOutput,
            RelationQueryRequirementEffect.Value);
        Assert.All(addressLineage, field => Assert.Equal(sourceShape, field.Shape));
        Assert.Equal(
            [
                cityPath.ToString(),
                streetPath.ToString()
            ],
            addressLineage.Select(static field => field.Path.ToString()));
    }

    [Fact]
    public void Compile_InvalidDemandEmitsStableDiagnosticAndNoPlan()
    {
        var result = Compile(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            RelationQueryCompilationDemand.ForRelationFields(
            [
                new(
                    LoadCustomerRelationFixture.CustomerShapeId,
                    LoadCustomerRelationFixture.CustomerNamePath)
            ]));

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Plan);
        Assert.NotNull(result.ExpressionAnalysis);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryCompilationDiagnosticCodes.RelationFieldInvalid);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("/demand/relationFields/0", diagnostic.Location);
    }

    [Fact]
    public void Compile_MalformedShapeEnvelopeReturnsStructuredDiagnostic()
    {
        var malformedShape = LoadCustomerRelationFixture.DomainShapeGraphDocument with
        {
            Graph = null!
        };

        var result = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            [malformedShape, LoadCustomerRelationFixture.DtoShapeGraphDocument],
            LoadCustomerRelationFixture.RelationshipCatalogDocument));

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "shapeGraph.graph.missing");
        Assert.Equal("/shapeGraphs/missing/graph", diagnostic.Location);
    }

    [Fact]
    public void Compile_MalformedRelationshipCatalogEnvelopeReturnsStructuredDiagnostic()
    {
        var malformedCatalog = LoadCustomerRelationFixture.RelationshipCatalogDocument with
        {
            Catalog = null!
        };

        var result = RelationQueryStaticCompiler.Compile(new(
            LoadCustomerRelationFixture.BaselineRelationDocument,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            malformedCatalog));

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Plan);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "relationshipCatalog.catalog.missing");
        Assert.Equal("/catalog", diagnostic.Location);
    }

    [Fact]
    public void Compile_AllDeclaredSkipsUnassignedOptionalFieldsButExplicitDemandRejectsThem()
    {
        var loadOnly = LoadCustomerRelationFixture.CreateRelationDocument(
            LoadCustomerTraversalOptions.Optional with
            {
                ProjectionMode = LoadCustomerProjectionMode.LoadOnly
            });

        var allDeclared = Compile(loadOnly);
        var plan = SuccessfulPlan(allDeclared);
        Assert.Equal(
            new FieldPath?[] { LoadCustomerRelationFixture.SearchIdPath, null },
            plan.RequirementGraph.Outputs.Select(static output => output.Field?.Path));
        Assert.Empty(plan.InputContract.Traversals);

        var explicitOptional = Compile(
            loadOnly,
            RelationFields(LoadCustomerRelationFixture.SearchCustomerNamePath));
        Assert.False(explicitOptional.IsSuccessful);
        Assert.Null(explicitOptional.Plan);
        Assert.Contains(
            explicitOptional.Diagnostics,
            diagnostic => diagnostic.Code == RelationQueryCompilationDiagnosticCodes.OutputFieldUnassigned
                && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Compile_ProvenanceRetainsExactSemanticSnapshotsAndFingerprints()
    {
        var definition = LoadCustomerRelationFixture.BaselineRelationDocument;
        var catalog = LoadCustomerRelationFixture.RelationshipCatalogDocument;
        var request = new RelationQueryCompilationRequest(
            definition,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            catalog);

        var plan = SuccessfulPlan(RelationQueryStaticCompiler.Compile(request));

        Assert.Equal(RelationQueryCompilationDemandOrigin.Convention, request.DemandOrigin);
        Assert.Equal(RelationQueryCompilationDemandOrigin.Convention, plan.DemandOrigin);
        Assert.Same(definition, plan.Provenance.DefinitionDocument);
        Assert.Equal(definition.DefinitionFingerprint, plan.Provenance.DefinitionFingerprint);
        Assert.Equal(RelationQueryCompilationProvenance.CurrentCompilerProfile, plan.Provenance.CompilerProfile);
        Assert.Equal(2, plan.Provenance.ShapeDocuments.Length);
        Assert.Same(
            LoadCustomerRelationFixture.DomainShapeGraphDocument,
            plan.Provenance.ShapeDocuments[0]);
        Assert.Same(
            LoadCustomerRelationFixture.DtoShapeGraphDocument,
            plan.Provenance.ShapeDocuments[1]);
        Assert.Same(catalog, plan.Provenance.RelationshipCatalogDocument);
        Assert.Equal(catalog.CatalogFingerprint, plan.Provenance.RelationshipCatalogFingerprint);
    }

    [Fact]
    public void Compile_ConstantDerivedOutputHasAnExplicitEmptyLineageEntry()
    {
        var sourceNode = new QueryNodeId("constant-load");
        var projectNode = new QueryNodeId("constant-project");
        var resultBinding = new ValueBindingId("constant-result");
        var definition = new Cohesive.Relations.IR.RelationDefinition(
            new RelationId("constant-search"),
            new RelationName("ConstantSearch"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    sourceNode,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new ProjectQueryNode(
                    projectNode,
                    sourceNode,
                    resultBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            new QueryAssignmentId("constant-id"),
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Const(ObservationValue.FromString("constant")))
                    ])
            ]),
            LoadCustomerRelationFixture.LoadBinding,
            new(
                projectNode,
                LoadCustomerRelationFixture.LoadSearchShapeId,
                RelationOutputMode.OnePerRoot));
        var document = RelationQueryDocument.FromDefinition(definition);

        var plan = SuccessfulPlan(RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments)));

        var output = FieldOutput(plan, LoadCustomerRelationFixture.SearchIdPath);
        var lineage = Assert.Single(plan.Lineage.Entries, entry => entry.Output.Id == output.Id);
        Assert.Empty(lineage.Contributions);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Output.Id == output.Id
                && edge.Input is RelationQueryCapabilityInput
                && edge.Effect == RelationQueryRequirementEffect.Evaluation);
    }

    [Fact]
    public void Compile_RelationInvariantAcrossAggregateRemainsValidationNotValueLineage()
    {
        var aggregateNode = new QueryNodeId("totals");
        var definition = new Cohesive.Relations.IR.RelationDefinition(
            new RelationId("aggregate-validation"),
            new RelationName("AggregateValidation"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.LoadBinding,
                    LoadCustomerRelationFixture.LoadShapeId),
                new AggregateQueryNode(
                    aggregateNode,
                    LoadCustomerRelationFixture.LoadSourceNodeId,
                    LoadCustomerRelationFixture.AggregateBinding,
                    LoadCustomerRelationFixture.LoadAggregateShapeId,
                    aggregates:
                    [
                        new(
                            LoadCustomerRelationFixture.AggregateLoadCountAssignmentId,
                            LoadCustomerRelationFixture.AggregateLoadCountPath,
                            AggregateOperator.Count),
                        new(
                            LoadCustomerRelationFixture.AggregateTotalAmountAssignmentId,
                            LoadCustomerRelationFixture.AggregateTotalAmountPath,
                            AggregateOperator.Sum,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadAmountPath))
                    ])
            ]),
            LoadCustomerRelationFixture.LoadBinding,
            new RelationOutputDefinition(
                aggregateNode,
                LoadCustomerRelationFixture.LoadAggregateShapeId,
                RelationOutputMode.Set),
            [
                new Cohesive.Model.InvariantDefinition(
                    "positive-total",
                    Expr.Gt(
                        Expr.Field(
                            LoadCustomerRelationFixture.AggregateBinding,
                            LoadCustomerRelationFixture.AggregateTotalAmountPath),
                        Expr.Const(ObservationValue.FromDecimal(0m))))
            ]);
        var document = RelationQueryDocument.FromDefinition(definition);
        var demand = RelationQueryCompilationDemand.ForRelationFields(
        [
            new(
                LoadCustomerRelationFixture.LoadAggregateShapeId,
                LoadCustomerRelationFixture.AggregateLoadCountPath)
        ]);

        var plan = SuccessfulPlan(RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            demand: demand)));

        var amount = FieldInput(
            plan,
            LoadCustomerRelationFixture.LoadShapeId,
            LoadCustomerRelationFixture.LoadAmountPath);
        Assert.Contains(
            plan.RequirementGraph.Edges,
            edge => edge.Input.Id == amount.Id
                && edge.Effect == RelationQueryRequirementEffect.Validation);
        Assert.DoesNotContain(
            plan.Lineage.Entries.SelectMany(static entry => entry.Contributions),
            contribution => contribution.Input.Id == amount.Id);
    }

    static RelationQueryCompilationResult Compile(
        RelationQueryDocument document,
        RelationQueryCompilationDemand? demand = null) =>
        RelationQueryStaticCompiler.Compile(new(
            document,
            LoadCustomerRelationFixture.ShapeGraphDocuments,
            LoadCustomerRelationFixture.RelationshipCatalogDocument,
            demand));

    static RelationQueryDocument CreateInverseTraversalQueryDocument(JoinKind joinKind)
    {
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new QueryId($"inverse-loads-{joinKind}"),
            new QueryName($"InverseLoads{joinKind}"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    LoadCustomerRelationFixture.CustomerSourceNodeId,
                    LoadCustomerRelationFixture.CustomerBinding,
                    LoadCustomerRelationFixture.CustomerShapeId),
                new TraverseRelationshipQueryNode(
                    LoadCustomerRelationFixture.CustomerTraversalNodeId,
                    LoadCustomerRelationFixture.CustomerSourceNodeId,
                    LoadCustomerRelationFixture.CustomerBinding,
                    LoadCustomerRelationFixture.LoadCustomerRelationshipId,
                    RelationshipTraversalDirection.Inverse,
                    LoadCustomerRelationFixture.LoadBinding,
                    joinKind,
                    QueryInputRequirement.Required),
                new ProjectQueryNode(
                    LoadCustomerRelationFixture.ProjectionNodeId,
                    LoadCustomerRelationFixture.CustomerTraversalNodeId,
                    LoadCustomerRelationFixture.SearchBinding,
                    LoadCustomerRelationFixture.LoadSearchShapeId,
                    [
                        new(
                            LoadCustomerRelationFixture.SearchIdAssignmentId,
                            LoadCustomerRelationFixture.SearchIdPath,
                            Expr.Field(
                                LoadCustomerRelationFixture.LoadBinding,
                                LoadCustomerRelationFixture.LoadIdPath))
                    ])
            ]),
            [
                new RowsQueryResultDefinition(
                    LoadCustomerRelationFixture.RowsResultId,
                    LoadCustomerRelationFixture.ProjectionNodeId)
            ]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryRequirementEffect[] RelationshipEffects(CompiledRelationQueryPlan plan)
    {
        var relationship = Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryRelationshipInput>());
        RelationQueryRequirementEffect[] effects =
        [
            .. plan.RequirementGraph.Edges
                .Where(edge => edge.Input.Id == relationship.Id)
                .Select(static edge => edge.Effect)
                .Distinct()
                .OrderBy(static effect => (int)effect)
        ];
        Assert.Equal(
            effects,
            plan.DependencyManifest.Entries
                .Single(entry => entry.Input.Id == relationship.Id)
                .Impacts
                .Select(static impact => impact.Effect)
                .Distinct()
                .OrderBy(static effect => (int)effect));
        return effects;
    }

    static CompiledRelationQueryPlan SuccessfulPlan(RelationQueryCompilationResult result)
    {
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static RelationQueryCompilationDemand RelationFields(params FieldPath[] paths) =>
        RelationQueryCompilationDemand.ForRelationFields(paths.Select(path =>
            new RelationQueryFieldReference(LoadCustomerRelationFixture.LoadSearchShapeId, path)));

    static RelationQueryCompilationDemand QueryFields(
        QueryResultId result,
        QualifiedShapeId shape,
        params FieldPath[] paths) =>
        RelationQueryCompilationDemand.ForQueryResults(
        [
            QueryResultDemand.SelectedFields(
                result,
                paths.Select(path => new RelationQueryFieldReference(shape, path)))
        ]);

    static RelationQueryOutputReference FieldOutput(
        CompiledRelationQueryPlan plan,
        FieldPath path) =>
        Assert.Single(plan.RequirementGraph.Outputs, output => output.Field?.Path == path);

    static RelationQueryOutputReference RowOutput(CompiledRelationQueryPlan plan) =>
        Assert.Single(plan.RequirementGraph.Outputs, static output => output.Field is null);

    static RelationQueryFieldInput FieldInput(
        CompiledRelationQueryPlan plan,
        QualifiedShapeId shape,
        FieldPath path) =>
        Assert.Single(
            plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>(),
            input => input.Field.Shape == shape && input.Field.Path == path);

    static ImmutableArray<(QualifiedShapeId Shape, FieldPath Path)> LineageFields(
        CompiledRelationQueryPlan plan,
        RelationQueryOutputReference output,
        RelationQueryRequirementEffect effect) =>
    [
        .. plan.Lineage.Entries
            .Where(entry => entry.Output.Id == output.Id)
            .SelectMany(static entry => entry.Contributions)
            .Where(contribution => contribution.Effect == effect)
            .Select(static contribution => contribution.Input)
            .OfType<RelationQueryFieldInput>()
            .Select(static input => (input.Field.Shape, input.Field.Path))
            .OrderBy(static field => field.Shape.GraphId.Value, StringComparer.Ordinal)
            .ThenBy(static field => field.Shape.ShapeId.Value, StringComparer.Ordinal)
            .ThenBy(static field => field.Path.ToString(), StringComparer.Ordinal)
    ];

    static string CompilationSignature(CompiledRelationQueryPlan plan)
    {
        IEnumerable<string> logical = plan.LogicalPlan.Nodes.Select(node =>
            $"logical:{node.Node.Value}:{string.Join(',', node.Inputs.Select(input =>
                $"{input.CanonicalInput.Value}>{input.EffectiveInput.Value}[{string.Join(',', input.Bypasses.Select(bypass => bypass.Node.Value))}]"))}");
        IEnumerable<string> inputs = plan.RequirementGraph.Inputs.Select(input =>
            $"input:{input.GetType().Name}:{input.Id.Value}");
        IEnumerable<string> outputs = plan.RequirementGraph.Outputs.Select(output =>
            $"output:{output.Id.Value}:{output.Kind}:{output.Node.Value}:{output.Field?.ToString()}");
        IEnumerable<string> edges = plan.RequirementGraph.Edges.Select(edge =>
            $"edge:{edge.Input.Id.Value}:{edge.Output.Id.Value}:{edge.Effect}:{edge.Requirement}:{string.Join('|', edge.Traces.Select(TraceSignature))}");
        return string.Join(Environment.NewLine, logical.Concat(inputs).Concat(outputs).Concat(edges));
    }

    static string TraceSignature(RelationQueryRequirementTrace trace) =>
        string.Join(
            '>',
            trace.Steps.Select(step =>
                $"{step.Kind}:{step.Node.Value}:{step.SiteKind}:{step.ExpressionSite?.Value}:{step.Assignment?.Value}:{step.Ordinal}:{step.InvariantName}"));
}
