using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionAuthoringTests
{
    [Fact]
    public void ExpressionRelationConvention_IsDeterministicAndEndpointScoped()
    {
        var root = ClrRelationshipShapeConvention.GetQualifiedShapeId<Load>();
        var output = ClrRelationshipShapeConvention.GetQualifiedShapeId<LoadDto>();

        var first = RelationQueryExpressionRelationConvention.CreateId(root, output);
        var second = RelationQueryExpressionRelationConvention.CreateId(root, output);

        Assert.Equal(first, second);
        Assert.StartsWith(RelationQueryExpressionRelationConvention.IdPrefix, first.Value, StringComparison.Ordinal);
        Assert.NotEqual(
            first,
            RelationQueryExpressionRelationConvention.CreateId(
                root,
                output,
                RelationOutputMode.ManyPerRoot));
        Assert.NotEqual(first, RelationQueryExpressionRelationConvention.CreateId(output, root));
        Assert.Equal(
            new RelationName(nameof(LoadDto)),
            RelationQueryExpressionRelationConvention.CreateName(typeof(LoadDto)));
        Assert.Throws<ArgumentException>(() =>
            RelationQueryExpressionRelationConvention.CreateId(default, output));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RelationQueryExpressionRelationConvention.CreateId(root, output, (RelationOutputMode)999));

        var unicodeId = RelationQueryExpressionRelationConvention.CreateId(
            new QualifiedShapeId(new GraphId("graph/😀"), new ShapeId("Load🚚")),
            new QualifiedShapeId(new GraphId("graph/検索"), new ShapeId("LoadSearchDto📦")));
        Assert.Equal(
            "relation:v1:sha256:77f19d902638392ee39da503c549cc10c9856625f729c4be6f75fab91a18c93d",
            unicodeId.Value);
    }

    [Fact]
    public void SimpleDtoRelation_IsCanonicallyEquivalentToStructuralAuthoring()
    {
        var expression = RelationQuery.Expression();
        var loads = expression.Source<Load>();
        var documents = expression.Project(
            loads,
            (Load load) => new LoadDto
            {
                Id = load.Id,
                Status = load.Status
            });
        var authored = documents.BuildRelation((LoadDto document) => document.Id);
        var relationId = RelationQueryExpressionRelationConvention.CreateId(
            loads.Binding.Shape!.Value,
            documents.Binding.Shape!.Value);
        var relationName = RelationQueryExpressionRelationConvention.CreateName(typeof(LoadDto));

        var structural = RelationQuery.Structural();
        var source = structural.Source(loads.Binding.Shape!.Value);
        var projected = structural.Project(
            source.Node,
            documents.Binding.Shape!.Value,
            [
                new(FieldPath.FromField("id"), source.Binding.Field("id")),
                new(FieldPath.FromField("status"), source.Binding.Field("status"))
            ]);
        var expected = structural.BuildRelation(
            relationId,
            relationName,
            source.Binding,
            projected.Node,
            documents.Binding.Shape.Value,
            RelationOutputMode.OnePerRoot,
            projected.Binding.Field("id"));

        Assert.True(authored.Validation.IsValid, Format(authored.Validation));
        Assert.Equal(relationId, authored.Definition.Id);
        Assert.Equal(relationName, authored.Definition.Name);
        var relationIdentity = Assert.Single(
            authored.Provenance.Identities,
            static identity => identity.Kind == RelationQueryAuthoringIdentityKind.Relation);
        Assert.Equal(RelationQueryAuthoringIdentityOrigin.Convention, relationIdentity.Origin);
        Assert.Equal(RelationQueryExpressionRelationConvention.Version, relationIdentity.Convention);
        Assert.Equal(
            expected.CreateDocument().DefinitionFingerprint,
            authored.CreateDocument().DefinitionFingerprint);
        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(expected.CreateDocument(), indented: false),
            RelationQueryJsonSerializer.Serialize(authored.CreateDocument(), indented: false));
    }

    [Fact]
    public void EnumFilter_LowersNamedMembersAndFieldsThroughTheFluentSurface()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var filtered = author.Filter(
            loads.Node,
            (Load load) => load.ProcessingStatus == LoadProcessingStatus.Ready
                           && load.ExpectedProcessingStatus == load.ProcessingStatus
                           && load.OccurredAt.EqualsExact(load.ExpectedOccurredAt),
            loads.Binding,
            sourceReference: "load/filter-status");
        var rows = author.Rows(filtered, loads.Binding, id: "rows");
        var query = author.BuildQuery(
            new("load-status-query"),
            new("LoadStatusQuery"),
            rows);

        Assert.True(query.Validation.IsValid, Format(query.Validation));
        var predicate = Assert.Single(query.Definition.Body.Nodes.OfType<FilterQueryNode>()).Predicate;
        var enumLiteral = Assert.Single(Descendants(predicate).OfType<LiteralExpr>());
        Assert.Equal(
            nameof(LoadProcessingStatus),
            Assert.IsType<EnumTypeRef>(enumLiteral.Type).Name);
        Assert.Equal(
            ObservationValue.FromString(nameof(LoadProcessingStatus.Ready)),
            enumLiteral.Value);
        Assert.Equal(5, Descendants(predicate).OfType<FieldExpr>().Count());
    }

    [Fact]
    public void EagerCollectionProjectionAndInt64Count_AuthorThroughTheFluentSurface()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var documents = author.Project(
            loads,
            load => new LoadStopsDto
            {
                Id = load.Id,
                Stops = load.Stops
                    .Select(stop => new StopDto { Location = stop.Location })
                    .ToArray(),
                StopCount = load.Stops.LongCount()
            });

        var relation = documents.BuildRelation(document => document.Id);

        Assert.True(relation.Validation.IsValid, Format(relation.Validation));
        var projection = Assert.Single(relation.Definition.Body.Nodes.OfType<ProjectQueryNode>());
        Assert.Contains(
            projection.Assignments.SelectMany(assignment => Descendants(assignment.Value)).OfType<CallExpr>(),
            call => call.Function == ExprFunctionNames.Select);
        Assert.Contains(
            projection.Assignments.SelectMany(assignment => Descendants(assignment.Value)).OfType<CallExpr>(),
            call => call.Function == ExprFunctionNames.Count);
    }

    [Fact]
    public void PipelineShorthands_AreCanonicallyEquivalentToExpandedJoinedAuthoring()
    {
        Expression<Func<Load, Customer, LoadSearchDto>> projection =
            (load, customer) => new LoadSearchDto
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name,
                CustomerType = customer.Type,
                EquipmentNumber = ""
            };
        Expression<Func<LoadSearchDto, string>> key = document => document.Id;

        var ergonomic = RelationQuery.Expression();
        var ergonomicLoads = ergonomic.Source<Load>();
        var ergonomicCustomers = ergonomic.Traverse<Load, Customer>(
            ergonomicLoads,
            load => load.CustomerId);
        var ergonomicDocuments = ergonomic.Project(ergonomicCustomers, projection);
        var ergonomicRelation = ergonomicDocuments.BuildRelation(key);

        var expanded = RelationQuery.Expression();
        var expandedRelationship = expanded.Relationship<Load, Customer>(load => load.CustomerId);
        var expandedLoads = expanded.Source<Load>();
        var expandedCustomers = expanded.Traverse(
            expandedLoads.Node,
            expandedLoads.Binding,
            expandedRelationship);
        var expandedDocuments = expanded.Project(
            expandedCustomers.Node,
            projection,
            expandedLoads.Binding,
            expandedCustomers.Binding);
        var expandedRelation = expanded.BuildRelation(expandedLoads, expandedDocuments, key);

        Assert.Equal(
            RelationQueryJsonSerializer.Serialize(expandedRelation.CreateDocument(), indented: false),
            RelationQueryJsonSerializer.Serialize(ergonomicRelation.CreateDocument(), indented: false));
        Assert.Equal(
            RelationshipCatalogJsonSerializer.Serialize(
                expanded.CreateRelationshipCatalogDocument(),
                indented: false),
            RelationshipCatalogJsonSerializer.Serialize(
                ergonomic.CreateRelationshipCatalogDocument(),
                indented: false));
        Assert.Contains(
            ergonomicRelation.Provenance.Configuration,
            decision => decision.Setting == RelationQueryExpressionRelationConvention.RootBindingSetting
                        && decision.Value == ergonomicLoads.Binding.Id.Value
                        && decision.Origin == RelationQueryAuthoringValueOrigin.Convention);
        Assert.DoesNotContain(
            expandedRelation.Provenance.Configuration,
            static decision => decision.Setting == RelationQueryExpressionRelationConvention.RootBindingSetting);
    }

    [Fact]
    public void RelationshipRegistry_DeduplicatesEquivalentDefinitionsAndRejectsConflictingIds()
    {
        var author = RelationQuery.Expression();
        var id = new RelationshipId("load/reference");

        var first = author.Relationship<Load, Customer>(load => load.CustomerId, id);
        var repeated = author.Relationship<Load, Customer>(load => load.CustomerId, id);

        Assert.Same(first.Definition, repeated.Definition);
        Assert.Single(author.RelationshipCatalog.Relationships);
        Assert.Throws<InvalidOperationException>(() =>
            author.Relationship<Load, Equipment>(load => load.EquipmentId, id));
        Assert.Single(author.RelationshipCatalog.Relationships);
    }

    [Fact]
    public void RejectedInlineTraversal_DoesNotCommitRelationshipOrConsumeStructuralIdentity()
    {
        var rejected = RelationQuery.Expression();
        var rejectedLoads = rejected.Source<Load>();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            rejected.Traverse<Load, Customer>(
                rejectedLoads,
                load => load.CustomerId,
                joinKind: JoinKind.Right));
        Assert.Throws<ArgumentException>(() =>
            rejected.Traverse<Load, Customer>(
                rejectedLoads,
                load => load.CustomerId,
                producerReference: " "));
        Assert.Empty(rejected.RelationshipCatalog.Relationships);

        var recoveredCustomers = rejected.Traverse<Load, Customer>(
            rejectedLoads,
            load => load.CustomerId);

        var clean = RelationQuery.Expression();
        var cleanLoads = clean.Source<Load>();
        var cleanCustomers = clean.Traverse<Load, Customer>(cleanLoads, load => load.CustomerId);

        Assert.Equal(cleanCustomers.Node.Id, recoveredCustomers.Node.Id);
        Assert.Equal(cleanCustomers.Binding.Id, recoveredCustomers.Binding.Id);
        Assert.Equal(
            RelationshipCatalogJsonSerializer.Serialize(clean.CreateRelationshipCatalogDocument(), indented: false),
            RelationshipCatalogJsonSerializer.Serialize(rejected.CreateRelationshipCatalogDocument(), indented: false));
    }

    [Fact]
    public void FluentTerminal_RequiresRetainedSingleSourceContext()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var documentsWithoutContext = author.Project(
            loads.Node,
            (Load load) => new LoadDto { Id = load.Id, Status = load.Status },
            loads.Binding);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            documentsWithoutContext.BuildRelation((LoadDto document) => document.Id));

        Assert.Contains("pass the intended root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StructuralEscapeHatch_CanReturnToTypedExpressionAuthoring()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var structuralFilter = author.Structural.Filter(
            loads.Node,
            Expr.EndsWith(
                loads.Binding.Structural.Field("status"),
                Expr.Const("Ready")));
        var documents = author.Project(
            structuralFilter,
            (Load load) => new LoadDto { Id = load.Id, Status = load.Status },
            loads.Binding);

        var relation = author.BuildRelation(
            new RelationId("load-dto-structural-escape"),
            new RelationName("LoadDtoStructuralEscape"),
            loads.Binding,
            documents.Node,
            documents.Binding,
            (LoadDto document) => document.Id);

        Assert.True(relation.Validation.IsValid, Format(relation.Validation));
        Assert.Contains(
            relation.Definition.Body.Nodes,
            node => node.Id == structuralFilter.Id);
    }

    [Fact]
    public void ConventionRelationWithoutKey_DerivesTerminalIdentityAndName()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var documents = author.Project(
            loads,
            (Load load) => new LoadDto { Id = load.Id, Status = load.Status });

        var relation = documents.BuildRelation();
        var explicitlyNamed = author.BuildRelation(
            loads,
            documents,
            id: new RelationId("load-dto/custom"),
            name: new RelationName("CustomLoadDto"));
        var explicitlyIdentified = author.BuildRelation(
            loads,
            documents,
            id: new RelationId("load-dto/identified"));
        var explicitlyConfigured = author.BuildRelation(
            loads,
            documents,
            name: new RelationName("NamedByProducer"),
            sourceReference: "relations/load-dto");

        Assert.True(relation.Validation.IsValid, Format(relation.Validation));
        Assert.Null(relation.Definition.Output.Key);
        Assert.Equal(
            RelationQueryExpressionRelationConvention.CreateId(
                loads.Binding.Shape!.Value,
                documents.Binding.Shape!.Value),
            relation.Definition.Id);
        Assert.Equal(
            RelationQueryExpressionRelationConvention.CreateName(typeof(LoadDto)),
            relation.Definition.Name);
        Assert.Equal(new RelationId("load-dto/custom"), explicitlyNamed.Definition.Id);
        Assert.Equal(new RelationName("CustomLoadDto"), explicitlyNamed.Definition.Name);
        var conventionalIdentity = Assert.Single(
            relation.Provenance.Identities,
            static identity => identity.Kind == RelationQueryAuthoringIdentityKind.Relation);
        Assert.Equal(RelationQueryAuthoringIdentityOrigin.Convention, conventionalIdentity.Origin);
        var conventionalName = Assert.Single(
            relation.Provenance.Configuration,
            static decision => decision.Setting == RelationQueryExpressionRelationConvention.NameSetting);
        var conventionalReference = Assert.Single(
            relation.Provenance.Configuration,
            static decision => decision.Setting == RelationQueryExpressionRelationConvention.SourceReferenceSetting);
        Assert.Equal(RelationQueryAuthoringValueOrigin.Convention, conventionalName.Origin);
        Assert.Equal(RelationQueryExpressionRelationConvention.Version, conventionalName.Convention);
        Assert.Equal(RelationQueryAuthoringValueOrigin.Convention, conventionalReference.Origin);
        Assert.Equal(RelationQueryExpressionRelationConvention.Version, conventionalReference.Convention);
        var explicitIdentity = Assert.Single(
            explicitlyNamed.Provenance.Identities,
            static identity => identity.Kind == RelationQueryAuthoringIdentityKind.Relation);
        Assert.Equal(RelationQueryAuthoringIdentityOrigin.Explicit, explicitIdentity.Origin);
        Assert.Null(explicitIdentity.Convention);
        var explicitName = Assert.Single(
            explicitlyNamed.Provenance.Configuration,
            static decision => decision.Setting == RelationQueryExpressionRelationConvention.NameSetting);
        var defaultReference = Assert.Single(
            explicitlyNamed.Provenance.Configuration,
            static decision => decision.Setting == RelationQueryExpressionRelationConvention.SourceReferenceSetting);
        Assert.Equal(RelationQueryAuthoringValueOrigin.Explicit, explicitName.Origin);
        Assert.Null(explicitName.Convention);
        Assert.Equal(RelationQueryAuthoringValueOrigin.Convention, defaultReference.Origin);
        var identifiedName = Assert.Single(
            explicitlyIdentified.Provenance.Configuration,
            static decision => decision.Setting == RelationQueryExpressionRelationConvention.NameSetting);
        Assert.Equal(RelationQueryAuthoringValueOrigin.Convention, identifiedName.Origin);
        Assert.Equal(RelationQueryExpressionRelationConvention.Version, identifiedName.Convention);
        var partiallyExplicitIdentity = Assert.Single(
            explicitlyConfigured.Provenance.Identities,
            static identity => identity.Kind == RelationQueryAuthoringIdentityKind.Relation);
        Assert.Equal(RelationQueryAuthoringIdentityOrigin.Convention, partiallyExplicitIdentity.Origin);
        Assert.All(
            explicitlyConfigured.Provenance.Configuration,
            static decision => Assert.Equal(RelationQueryAuthoringValueOrigin.Explicit, decision.Origin));
        Assert.Contains(
            explicitlyConfigured.Provenance.Configuration,
            static decision => decision.Setting == RelationQueryExpressionRelationConvention.NameSetting
                               && decision.Value == "NamedByProducer");
        Assert.Contains(
            explicitlyConfigured.Provenance.Configuration,
            static decision => decision.Setting == RelationQueryExpressionRelationConvention.SourceReferenceSetting
                               && decision.Value == "relations/load-dto");
    }

    [Fact]
    public void AuthoringManifest_RejectsDuplicateEffectiveConfigurationSettings()
    {
        var source = new RelationQueryAuthoringSource("test", "relation/test");
        var decision = new RelationQueryAuthoringConfigurationDecision(
            "relation/test",
            RelationQueryExpressionRelationConvention.NameSetting,
            "Test",
            RelationQueryAuthoringValueOrigin.Explicit,
            source: source);

        var exception = Assert.Throws<ArgumentException>(() =>
            new RelationQueryAuthoringManifest(configuration: [decision, decision]));

        Assert.Equal("configuration", exception.ParamName);
    }

    [Fact]
    public void BoundNodeInverseTraversal_UsesItsFocusedTargetBinding()
    {
        var author = RelationQuery.Expression();
        var loadCustomer = author.Relationship<Load, Customer>(load => load.CustomerId);
        var customers = author.Source<Customer>();

        var loads = author.TraverseInverse(customers, loadCustomer);

        Assert.Equal(loadCustomer.SourceShape, loads.Binding.Shape);
        Assert.False(string.IsNullOrWhiteSpace(loads.Node.Id.Value));
    }

    [Fact]
    public void EnrichedRelationQueryAggregationAndEvaluation_LowerThroughOneTypedSession()
    {
        var author = RelationQuery.Expression();
        var loadCustomer = author.Relationship<Load, Customer>(
            load => load.CustomerId,
            new RelationshipId("load/customer"));
        var loadEquipment = author.Relationship<Load, Equipment>(
            load => load.EquipmentId,
            new RelationshipId("load/equipment"));
        var loads = author.Source<Load>();
        var customers = author.Traverse(loads, loadCustomer);
        var equipment = author.Traverse(
            customers,
            loads.Binding,
            loadEquipment);
        var relationDocuments = author.Project(
            equipment,
            (Load load, Customer customer, Equipment unit) => new LoadSearchDto
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name,
                CustomerType = customer.Type,
                EquipmentNumber = unit.Number
            },
            loads.Binding,
            customers.Binding,
            sourceReference: "load-search/relation-projection");
        var relation = relationDocuments.BuildRelation(
            (LoadSearchDto document) => document.Id,
            invariants:
            [
                new(
                    "customer-name-required",
                    document => document.CustomerName != "",
                    "A load search document requires its customer name.")
            ],
            id: new RelationId("load-search"),
            name: new RelationName("LoadSearch"));

        var location = author.Parameter<string>("location");
        var filtered = author.Filter(
            equipment.Node,
            (Load load, Customer customer, Equipment _) =>
                customer.Name == "Acme"
                && load.Stops.Any(stop => stop.Location == location.Value),
            loads.Binding,
            customers.Binding,
            equipment.Binding,
            sourceReference: "load-search/filter");
        var queryDocuments = author.Project(
            filtered,
            (Load load, Customer customer, Equipment unit) => new LoadSearchDto
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name,
                CustomerType = customer.Type,
                EquipmentNumber = unit.Number
            },
            loads.Binding,
            customers.Binding,
            equipment.Binding,
            sourceReference: "load-search/query-projection");
        var summary = author.Aggregate<FilterQueryNode, LoadSearchSummary>(
            filtered,
            aggregate => aggregate
                .Group(
                    result => result.CustomerType,
                    (Customer customer) => customer.Type,
                    customers.Binding)
                .Count(result => result.Count),
            sourceReference: "load-search/summary");
        var total = author.Aggregate<FilterQueryNode, LoadSearchTotal>(
            filtered,
            aggregate => aggregate.Count(result => result.Count),
            sourceReference: "load-search/total");
        var rows = author.Rows(queryDocuments, id: "rows");
        var aggregation = author.Aggregation(summary, id: "summary");
        var totalAggregation = author.Aggregation(total, id: "total");
        var query = author.BuildQuery(
            new QueryId("load-search-query"),
            new QueryName("LoadSearchQuery"),
            rows,
            aggregation,
            totalAggregation);

        Assert.True(relation.Validation.IsValid, Format(relation.Validation));
        Assert.True(query.Validation.IsValid, Format(query.Validation));
        Assert.Empty(relation.Definition.Body.Parameters);
        Assert.Equal(location.Id, Assert.Single(query.Definition.Body.Parameters).Id);
        Assert.Equal(3, query.Definition.Results.Length);
        Assert.Empty(Assert.Single(query.Definition.Body.Nodes.OfType<AggregateQueryNode>(),
            node => node.Id == total.Node.Id).Groupings);
        Assert.Single(author.ShapeDocuments);

        var any = Assert.Single(
            Descendants(Assert.Single(query.Definition.Body.Nodes.OfType<FilterQueryNode>()).Predicate)
                .OfType<CallExpr>(),
            call => call.Function == ExprFunctionNames.Any);
        Assert.Equal("stops", Assert.IsType<FieldExpr>(any.Arguments[0]).Path.ToString());
        var itemFields = Descendants(any.Arguments[1]).OfType<FieldExpr>().ToArray();
        Assert.Contains(itemFields, field => field.Path.ToString() == "item.location");
        Assert.Contains(
            Descendants(any.Arguments[1]).OfType<ParameterExpr>(),
            parameter => parameter.Parameter == location.Id.Value);

        var evaluation = author.Evaluate(
                query,
                new RelationQueryEvaluationId("request/42"))
            .Set(location, "Seattle")
            .Select(rows, document => document.Id, document => document.CustomerName)
            .Select(aggregation)
            .Select(totalAggregation)
            .Build();

        Assert.Equal(ObservationValue.FromString("Seattle"), Assert.Single(evaluation.Parameters).Value);
        Assert.Equal(3, evaluation.Demand.QueryResults.Length);
        Assert.Equal(author.ShapeDocuments.Length, evaluation.Compilation.ShapeDocuments.Length);
        Assert.Equal(author.RelationshipCatalog.Count, evaluation.Compilation.RelationshipCatalogDocument?.Catalog.Count);
        var relationEvaluation = author.Evaluate(
                relation,
                new RelationQueryEvaluationId("request/relation/42"))
            .Supply(
                [new Load { Id = "load-42", CustomerId = "customer-7", EquipmentId = "equipment-3" }],
                static load => load.Id)
            .Build();
        Assert.Equal("load-42", Assert.Single(relationEvaluation.SuppliedRoots!.Observations).Id);
        Assert.DoesNotContain(
            EnumerateObjectGraph(query.Definition),
            static value => value is Expression or MemberInfo or Type or Delegate);
    }

    [Fact]
    public void UnsupportedCapture_ProducesActionableDiagnosticWithoutExecutingGetter()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var capture = new ThrowingCapture();

        var exception = Assert.Throws<RelationQueryExpressionAuthoringException>(() =>
            author.Filter(
                loads.Node,
                (Load load) => load.Status == capture.Value,
                loads.Binding,
                sourceReference: "loads/captured-status"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(RelationQueryExpressionDiagnosticCodes.CapturedValueUnsupported, diagnostic.Code);
        Assert.Equal("loads/captured-status", diagnostic.SourceReference);
        Assert.Equal(0, capture.ReadCount);
        Assert.NotNull(diagnostic.Suggestion);
    }

    [Fact]
    public void ParameterFromAnotherSession_IsRejectedDuringExpressionLowering()
    {
        var parameterOwner = RelationQuery.Expression();
        var foreignStatus = parameterOwner.Parameter<string>("status");
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();

        var exception = Assert.Throws<RelationQueryExpressionAuthoringException>(() =>
            author.Filter(
                loads.Node,
                (Load load) => load.Status == foreignStatus.Value,
                loads.Binding,
                sourceReference: "loads/foreign-status"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(RelationQueryExpressionDiagnosticCodes.ParameterMarkerInvalid, diagnostic.Code);
        Assert.Equal("loads/foreign-status", diagnostic.SourceReference);
        Assert.Contains("another expression-authoring session", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterDefaultsWithoutCanonicalJsonEncoding_FailBeforeCoreCommit()
    {
        AssertRejectedWithoutCommit(new byte[] { 1, 2, 3 });
        AssertRejectedWithoutCommit(DateTimeOffset.UnixEpoch);
        AssertRejectedWithoutCommit(new DateOnly(2026, 7, 17));
        AssertRejectedWithoutCommit(new TimeOnly(12, 34, 56));
        AssertRejectedWithoutCommit(TimeSpan.FromMinutes(5));
        AssertRejectedWithoutCommit(double.NaN);
        AssertRejectedWithoutCommit(ObservationValue.Undefined);
        AssertRejectedWithoutCommit(ObservationValue.FromArray(
        [
            ObservationValue.FromObject(new Dictionary<string, ObservationValue>
            {
                ["payload"] = ObservationValue.FromBytes(new byte[] { 1 })
            })
        ]));

        static void AssertRejectedWithoutCommit<T>(T unsupportedDefault)
        {
            var author = RelationQuery.Expression();

            var exception = Assert.Throws<NotSupportedException>(() =>
                author.Parameter("value", unsupportedDefault));
            Assert.True(
                exception.Message.Contains("canonical relation/query JSON", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("metadata-aware", StringComparison.Ordinal),
                $"Unexpected rejection message: {exception.Message}");

            // Reusing the same identifier proves the failed declaration never reached the canonical core.
            var replacement = author.Parameter<string>("value");
            var loads = author.Source<Load>();
            var filtered = author.Filter(
                loads.Node,
                (Load load) => load.Status == replacement.Value,
                loads.Binding);
            var query = author.BuildQuery(
                new QueryId("persistable-default-rejection"),
                new QueryName("PersistableDefaultRejection"),
                author.Rows(filtered, loads.Binding));

            Assert.True(query.Validation.IsValid, Format(query.Validation));
            Assert.Single(query.Definition.Body.Parameters);
            _ = RelationQueryJsonSerializer.Serialize(query.CreateDocument(), indented: false);
        }
    }

    [Fact]
    public void MetadataDependentAndOpaqueParameterTypes_FailBeforeCoreCommit()
    {
        AssertRejectedWithoutCommit<TimeOnly>();
        AssertRejectedWithoutCommit<ParameterObject>();
        AssertRejectedWithoutCommit<ParameterObject[]>();
        AssertRejectedWithoutCommit<ParameterStatus>();

        static void AssertRejectedWithoutCommit<T>()
        {
            var author = RelationQuery.Expression();
            if (typeof(T) != typeof(TimeOnly))
            {
                _ = author.Clr.Shape<ParameterRoot>();
            }

            var exception = Assert.Throws<NotSupportedException>(() => author.Parameter<T>("value"));
            Assert.Contains("metadata-aware", exception.Message, StringComparison.Ordinal);

            var replacement = author.Parameter<string>("value");
            Assert.Equal(new QueryParameterId("value"), replacement.Id);
        }
    }

    [Fact]
    public void UnsupportedParameterDefaultType_DoesNotEvaluateGettersOrCommit()
    {
        var author = RelationQuery.Expression();
        _ = author.Clr.Shape<ThrowingCapture>();
        var unsupportedDefault = new ThrowingCapture();

        var exception = Assert.Throws<NotSupportedException>(() =>
            author.Parameter("value", unsupportedDefault));

        Assert.Contains("metadata-aware", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, unsupportedDefault.ReadCount);

        var replacement = author.Parameter<string>("value");
        Assert.Equal(new QueryParameterId("value"), replacement.Id);
    }

    [Fact]
    public void ShapeDocuments_IncludeOnlyGraphsReferencedByTheExpressionSession()
    {
        var clr = new RelationQueryClrAuthoringContext();
        _ = clr.Shape<UnusedShape>(new QualifiedShapeId(
            new GraphId("cohesive.tests.unused/v1"),
            new ShapeId("unused")));
        var author = RelationQuery.Expression(clr);

        var loads = author.Source<Load>();

        Assert.Equal(2, clr.ShapeDocuments.Length);
        var document = Assert.Single(author.ShapeDocuments);
        Assert.Equal(loads.Binding.Shape!.Value.GraphId, document.Graph.Id);
    }

    [Fact]
    public void SelectedTerminal_PrunesUnusedSiblingParametersAndNodes()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var customers = author.Source<Customer>();
        var unused = author.Parameter<string>("unused-customer-name");
        _ = author.Filter(
            customers.Node,
            (Customer customer) => customer.Name == unused.Value,
            customers.Binding);
        var rows = author.Rows(loads, id: "loads");

        var query = author.BuildQuery(new("loads-only"), new("LoadsOnly"), rows);

        Assert.Empty(query.Definition.Body.Parameters);
        Assert.Equal(loads.Node.Id, Assert.Single(query.Definition.Body.Nodes).Id);
    }

    [Fact]
    public void ForeignShapeAndUnrelatedOutputBindings_AreRejectedBeforeTerminalConstruction()
    {
        var foreignShape = new RelationQueryClrAuthoringContext().Shape<Load>();
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var customers = author.Source<Customer>();
        var loadCustomer = author.Relationship<Load, Customer>(load => load.CustomerId);
        var foreignLoads = RelationQuery.Expression().Source<Load>();
        var inlineAuthor = RelationQuery.Expression();

        Assert.Throws<InvalidOperationException>(() => author.Source(foreignShape));
        Assert.Throws<ArgumentException>(() => author.Traverse(foreignLoads, loadCustomer));
        Assert.Throws<ArgumentException>(() =>
            inlineAuthor.Traverse<Load, Customer>(foreignLoads, load => load.CustomerId));
        Assert.Empty(inlineAuthor.RelationshipCatalog.Relationships);
        Assert.Throws<ArgumentException>(() => author.Rows(loads.Node, customers.Binding));
        Assert.Throws<ArgumentException>(() => author.BuildRelation(
            new RelationId("invalid-output-binding"),
            new RelationName("InvalidOutputBinding"),
            loads.Binding,
            loads.Node,
            customers.Binding));
    }

    [Fact]
    public void LogicalOperations_RejectStaleAndUnrelatedBindingsBeforeLowering()
    {
        var author = RelationQuery.Expression();
        var loadCustomer = author.Relationship<Load, Customer>(
            load => load.CustomerId,
            new RelationshipId("load/customer/visibility"));
        var loads = author.Source<Load>();
        var customers = author.Source<Customer>();
        var unrelatedLoads = author.Source<Load>();
        var projected = author.Project(
            loads.Node,
            (Load load) => new LoadDto { Id = load.Id, Status = load.Status },
            loads.Binding);

        Assert.Throws<ArgumentException>(() => author.Traverse(
            projected.Node,
            loads.Binding,
            loadCustomer));
        Assert.Throws<ArgumentException>(() => author.Filter(
            projected.Node,
            (Load load) => load.Status == "Ready",
            loads.Binding));
        Assert.Throws<ArgumentException>(() => author.Filter(
            projected.Node,
            (Load load) => load.Status == "Ready",
            unrelatedLoads.Binding));
        Assert.Throws<ArgumentException>(() => author.Project(
            projected.Node,
            (Load load) => new LoadDto { Id = load.Id, Status = load.Status },
            loads.Binding));
        Assert.Throws<ArgumentException>(() => author.Join(
            projected.Node,
            customers.Node,
            JoinKind.Inner,
            (Load load, Customer customer) => load.CustomerId == customer.Id,
            loads.Binding,
            customers.Binding));
        Assert.Throws<ArgumentException>(() => author.Expand(
            projected.Node,
            (Load load) => load.Stops,
            loads.Binding));
        Assert.Throws<ArgumentException>(() => author.Order(
            projected.Node,
            (Load load) => load.Id,
            loads.Binding));
        Assert.Throws<ArgumentException>(() => author.Order(
            projected.Node,
            author.Ordering((Load load) => load.Id, loads.Binding)));
        Assert.Throws<ArgumentException>(() => author.Distinct(
            projected.Node,
            (Load load) => load.Id,
            loads.Binding));
        Assert.Throws<ArgumentException>(() => author.Rows(projected.Node, loads.Binding));
        Assert.Throws<ArgumentException>(() => author.BuildRelation(
            new RelationId("invalid-stale-output-binding"),
            new RelationName("InvalidStaleOutputBinding"),
            loads.Binding,
            projected.Node,
            loads.Binding));

        var ordered = author.Order(
            projected.Node,
            (LoadDto document) => document.Id,
            projected.Binding);
        Assert.Throws<ArgumentException>(() => author.Page(
            ordered,
            new KeysetPageDefinition(
                limit: 10,
                after: [loads.Binding.Structural.Field("id")])));

        var cursor = author.Parameter<string>("cursor");
        var paged = author.Page(
            ordered,
            new KeysetPageDefinition(limit: 10, after: [Expr.Param(cursor.Id.Value)]));
        var rows = author.Rows(paged, projected.Binding);
        var query = author.BuildQuery(new("visible-bindings"), new("VisibleBindings"), rows);

        Assert.True(query.Validation.IsValid, Format(query.Validation));
    }

    [Fact]
    public void AggregateTargetContractsAndExpandedComplexShape_AreValidatedEagerly()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();

        Assert.Throws<ArgumentException>(() => author.Aggregate<SourceQueryNode, InvalidCountSummary>(
            loads.Node,
            aggregate => aggregate.Count(result => result.Count)));

        var expanded = author.Expand(
            loads.Node,
            (Load load) => load.Stops,
            loads.Binding);

        Assert.NotNull(expanded.Binding.Shape);
        Assert.Contains(
            author.ShapeDocuments.SelectMany(static document => document.Graph.Shapes),
            shape => shape.Id == expanded.Binding.Shape.Value.ShapeId);
    }

    [Fact]
    public void Expand_PreservesImportedCollectionItemMemberMapping()
    {
        var graphId = new GraphId("imported/load-customers/v1");
        var loadShapeId = new ShapeId("load.wire");
        var loadShape = new QualifiedShapeId(graphId, loadShapeId);
        var customerType = ClrShapeIdentityConvention.GetTypeId(typeof(ImportedCollectionCustomer));
        var document = ShapeGraphDocument.FromGraph(new ShapeGraph(
            graphId,
            [
                new Shape(
                    loadShapeId,
                    [
                        new FieldDefinition(
                            new("customers"),
                            new NamedTypeRef(customerType),
                            cardinality: FieldCardinality.Many)
                    ])
            ],
            [
                new TypeDefinition.Structural(
                    customerType,
                    [new StructuralField(new("display_name"), new ScalarTypeRef(ScalarTypeKind.String))])
            ]));
        var customers = typeof(ImportedCollectionLoad).GetProperty(nameof(ImportedCollectionLoad.Customers))!;
        var customerName = typeof(ImportedCollectionCustomer).GetProperty(
            nameof(ImportedCollectionCustomer.Name))!;
        var context = new RelationQueryClrAuthoringContext();
        var importedLoad = context.Shape<ImportedCollectionLoad>(
            document,
            loadShape,
            new Dictionary<PropertyInfo, FieldPath>
            {
                [customers] = FieldPath.FromField("customers"),
                [customerName] = FieldPath.FromField("display_name")
            });
        var author = RelationQuery.Expression(context);
        var loads = author.Source(importedLoad);
        var conventionalCustomers = author.Source<ImportedCollectionCustomer>();
        var expanded = author.Expand(
            loads.Node,
            (ImportedCollectionLoad load) => load.Customers,
            loads.Binding);
        var projected = author.Project(
            expanded.Node,
            (ImportedCollectionCustomer customer) => new ImportedCollectionCustomerDto
            {
                Name = customer.Name
            },
            expanded.Binding);
        var filteredConventionalCustomers = author.Filter(
            conventionalCustomers.Node,
            (ImportedCollectionCustomer customer) => customer.Name == "Conventional",
            conventionalCustomers.Binding);
        var rows = author.Rows(projected, id: "imported");
        var conventionalRows = author.Rows(
            filteredConventionalCustomers,
            conventionalCustomers.Binding,
            id: "conventional");

        var query = author.BuildQuery(
            new("imported-expanded-customers"),
            new("ImportedExpandedCustomers"),
            rows,
            conventionalRows);

        Assert.True(query.Validation.IsValid, Format(query.Validation));
        Assert.Null(expanded.Binding.Shape);
        var projection = Assert.Single(
            query.Definition.Body.Nodes.OfType<ProjectQueryNode>(),
            node => node.Id == projected.Node.Id);
        var source = Assert.IsType<FieldExpr>(Assert.Single(projection.Assignments).Value);
        Assert.Equal(expanded.Binding.Id, source.Binding);
        Assert.Equal(FieldPath.FromField("display_name"), source.Path);
        var conventionalSource = Assert.Single(
            Descendants(Assert.Single(
                query.Definition.Body.Nodes.OfType<FilterQueryNode>(),
                node => node.Id == filteredConventionalCustomers.Id).Predicate)
                .OfType<FieldExpr>());
        Assert.Equal(conventionalCustomers.Binding.Id, conventionalSource.Binding);
        Assert.Equal(FieldPath.FromField(nameof(ImportedCollectionCustomer.Name)), conventionalSource.Path);
    }

    [Fact]
    public void TemporalJoin_UsesTypedExplicitPointAndIntervalOperands()
    {
        var author = RelationQuery.Expression();
        var events = author.Source<LoadEvent>();
        var versions = author.Source<LoadVersion>();
        var temporal = author.TemporalJoin(
            events.Node,
            versions.Node,
            JoinKind.Left,
            (LoadEvent occurrence, LoadVersion version) => occurrence.LoadId == version.LoadId,
            events.Binding,
            versions.Binding,
            match => match.PointInInterval(
                (LoadEvent occurrence) => occurrence.OccurredAt,
                events.Binding,
                match.Interval(
                    match.Bound(
                        (LoadVersion version) => version.ValidFrom,
                        versions.Binding,
                        TemporalBoundaryInclusion.Inclusive),
                    match.Bound(
                        (LoadVersion version) => version.ValidTo,
                        versions.Binding,
                        TemporalBoundaryInclusion.Exclusive,
                        TemporalNullBoundBehavior.Unbounded))));
        var projected = author.Project(
            temporal,
            (LoadEvent occurrence, LoadVersion version) => new TemporalLoadDto
            {
                LoadId = occurrence.LoadId,
                Status = version.Status
            },
            events.Binding,
            versions.Binding);
        var rows = author.Rows(projected);
        var query = author.BuildQuery(new QueryId("temporal-loads"), new QueryName("TemporalLoads"), rows);

        Assert.True(query.Validation.IsValid, Format(query.Validation));
        var node = Assert.Single(query.Definition.Body.Nodes.OfType<TemporalJoinQueryNode>());
        var point = Assert.IsType<TemporalPointInIntervalMatch>(node.Match);
        Assert.IsType<FieldExpr>(point.Point);
        var upper = Assert.IsType<ExpressionTemporalIntervalBound>(point.Interval.Upper);
        Assert.Equal(TemporalNullBoundBehavior.Unbounded, upper.NullBehavior);
    }

    static IEnumerable<Expr> Descendants(Expr root)
    {
        yield return root;
        switch (root)
        {
            case UnaryExpr unary:
                foreach (var item in Descendants(unary.Operand))
                {
                    yield return item;
                }

                break;
            case BinaryExpr binary:
                foreach (var item in Descendants(binary.Left))
                {
                    yield return item;
                }

                foreach (var item in Descendants(binary.Right))
                {
                    yield return item;
                }

                break;
            case ConditionalExpr conditional:
                foreach (var item in Descendants(conditional.Test))
                {
                    yield return item;
                }

                foreach (var item in Descendants(conditional.IfTrue))
                {
                    yield return item;
                }

                foreach (var item in Descendants(conditional.IfFalse))
                {
                    yield return item;
                }

                break;
            case CallExpr call:
                foreach (var argument in call.Arguments)
                {
                    foreach (var item in Descendants(argument))
                    {
                        yield return item;
                    }
                }

                break;
        }
    }

    static IEnumerable<object> EnumerateObjectGraph(object root)
    {
        HashSet<object> visited = new(ReferenceEqualityComparer.Instance);
        Stack<object?> pending = new();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (current is null || current is string || current.GetType().IsPrimitive || current.GetType().IsEnum)
            {
                continue;
            }

            if (!visited.Add(current))
            {
                continue;
            }

            yield return current;
            if (current is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    pending.Push(item);
                }

                continue;
            }

            foreach (var property in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetMethod is not null
                    && property.GetIndexParameters().Length == 0
                    && !property.PropertyType.IsByRefLike)
                {
                    pending.Push(property.GetValue(current));
                }
            }
        }
    }

    static string Format(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    sealed class ThrowingCapture
    {
        public int ReadCount { get; private set; }

        public string Value
        {
            get
            {
                ReadCount++;
                throw new InvalidOperationException("The getter must not execute.");
            }
        }
    }

    sealed class Load
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("customerId")]
        public string CustomerId { get; init; } = string.Empty;

        [JsonPropertyName("equipmentId")]
        public string EquipmentId { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; init; } = string.Empty;

        public LoadProcessingStatus ProcessingStatus { get; init; }

        public LoadProcessingStatus ExpectedProcessingStatus { get; init; }

        public DateTimeOffset OccurredAt { get; init; }

        public DateTimeOffset ExpectedOccurredAt { get; init; }

        [JsonPropertyName("stops")]
        public Stop[] Stops { get; init; } = [];
    }

    enum LoadProcessingStatus : byte
    {
        Pending,
        Ready
    }

    sealed record ParameterObject(string Name);

    sealed record ParameterRoot(
        ParameterObject Value,
        ParameterObject[] Values,
        ParameterStatus Status);

    enum ParameterStatus
    {
        Pending,
        Complete
    }

    sealed class UnusedShape
    {
        public string Id { get; init; } = string.Empty;
    }

    sealed class Stop
    {
        [JsonPropertyName("location")]
        public string Location { get; init; } = string.Empty;
    }

    sealed class ImportedCollectionLoad
    {
        public ImportedCollectionCustomer[] Customers { get; init; } = [];
    }

    sealed class ImportedCollectionCustomer
    {
        public string Name { get; init; } = string.Empty;
    }

    sealed class ImportedCollectionCustomerDto
    {
        public string Name { get; init; } = string.Empty;
    }

    sealed class Customer
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;
    }

    sealed class Equipment
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("number")]
        public string Number { get; init; } = string.Empty;
    }

    sealed class LoadDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; init; } = string.Empty;
    }

    sealed class LoadStopsDto
    {
        public string Id { get; init; } = string.Empty;

        public StopDto[] Stops { get; init; } = [];

        public long StopCount { get; init; }
    }

    sealed class StopDto
    {
        public string Location { get; init; } = string.Empty;
    }

    sealed class LoadSearchDto
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("customerId")]
        public string CustomerId { get; init; } = string.Empty;

        [JsonPropertyName("customerName")]
        public string CustomerName { get; init; } = string.Empty;

        [JsonPropertyName("customerType")]
        public string CustomerType { get; init; } = string.Empty;

        [JsonPropertyName("equipmentNumber")]
        public string EquipmentNumber { get; init; } = string.Empty;
    }

    sealed class LoadSearchSummary
    {
        [JsonPropertyName("customerType")]
        public string CustomerType { get; init; } = string.Empty;

        [JsonPropertyName("count")]
        public long Count { get; init; }
    }

    sealed class LoadSearchTotal
    {
        [JsonPropertyName("count")]
        public long Count { get; init; }
    }

    sealed class InvalidCountSummary
    {
        public int Count { get; init; }
    }

    sealed class LoadEvent
    {
        public string LoadId { get; init; } = string.Empty;

        public DateTimeOffset OccurredAt { get; init; }
    }

    sealed class LoadVersion
    {
        public string LoadId { get; init; } = string.Empty;

        public DateTimeOffset ValidFrom { get; init; }

        public DateTimeOffset? ValidTo { get; init; }

        public string Status { get; init; } = string.Empty;
    }

    sealed class TemporalLoadDto
    {
        public string LoadId { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }
}
