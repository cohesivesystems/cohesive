using System.Text.Json.Serialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresRelationQueryTargetProfileTests
{
    [Fact]
    public void RepresentativeSelectionRemainsUnavailableUntilTheNativeCompilerProvesItsContract()
    {
        var plan = Cohesive.Relations.TestFixtures.RepresentativeSelectionFixture.Compile(
            Cohesive.Relations.TestFixtures.RepresentativeSelectionFixture.Document());
        var report = RelationQueryRealizationCompiler.Compile(plan,
            PostgresRelationQueryTargetProfile.Default, PostgresRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);
        var requirement = Assert.Single(report.Requirements, item => item.Capability is LogicalRelationQueryCapability
            { Kind: RelationQueryLogicalCapabilityKind.SelectRepresentative });
        Assert.False(report.IsRealizable);
        Assert.Contains(report.Decisions, decision => decision.Requirement == requirement.Id
            && decision is UnavailableRelationQueryRealizationDecision);
    }

    [Fact]
    public void SourceAcquisitionCapabilities_MatchTheNpgsqlReaderClosure()
    {
        HashSet<RelationQueryPrimitiveCapabilityKind> expected =
        [
            RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup,
            RelationQueryPrimitiveCapabilityKind.BatchedPredicateLookup,
            RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration,
            RelationQueryPrimitiveCapabilityKind.FieldProjection,
            RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead,
            RelationQueryPrimitiveCapabilityKind.RelationshipReferenceRead
        ];
        var actual = PostgresRelationQuerySourceTargetProfile.Default.Capabilities
            .Select(static evidence => evidence.Capability)
            .OfType<PrimitiveRelationQueryCapability>()
            .Select(static capability => capability.Kind)
            .ToHashSet();

        Assert.Equal(
            "cohesive.adapters.postgres.sql/source-reader-v1",
            PostgresRelationQuerySourceTargetProfile.ProfileId.Value);
        Assert.Empty(expected.Except(actual));
        Assert.Empty(actual.Except(expected));
    }

    [Fact]
    public void StructuralCapabilities_MatchTheCompilerClosure()
    {
        HashSet<(RelationQueryStructuralCapabilityRole Role, RelationQueryStructuralPathKind Path)> expected =
        [
            (RelationQueryStructuralCapabilityRole.BindingRead, RelationQueryStructuralPathKind.TopLevelField),
            (RelationQueryStructuralCapabilityRole.BindingRead, RelationQueryStructuralPathKind.NestedField),
            (RelationQueryStructuralCapabilityRole.ProjectionTarget, RelationQueryStructuralPathKind.TopLevelField),
            (RelationQueryStructuralCapabilityRole.ProjectionTarget, RelationQueryStructuralPathKind.NestedField),
            (RelationQueryStructuralCapabilityRole.GroupingTarget, RelationQueryStructuralPathKind.TopLevelField),
            (RelationQueryStructuralCapabilityRole.GroupingTarget, RelationQueryStructuralPathKind.NestedField),
            (RelationQueryStructuralCapabilityRole.AggregateTarget, RelationQueryStructuralPathKind.TopLevelField),
            (RelationQueryStructuralCapabilityRole.AggregateTarget, RelationQueryStructuralPathKind.NestedField),
            (RelationQueryStructuralCapabilityRole.OutputSelection, RelationQueryStructuralPathKind.TopLevelField),
            (RelationQueryStructuralCapabilityRole.OutputSelection, RelationQueryStructuralPathKind.NestedField),
            (RelationQueryStructuralCapabilityRole.CompleteValue, RelationQueryStructuralPathKind.RootValue)
        ];
        var actual = PostgresRelationQueryTargetProfile.Default.Capabilities
            .Select(static evidence => evidence.Capability)
            .OfType<StructuralRelationQueryCapability>()
            .Select(static capability => (capability.Role, capability.PathKind))
            .ToHashSet();

        Assert.Empty(expected.Except(actual));
        Assert.Empty(actual.Except(expected));
    }

    [Fact]
    public void PostExecutionOnlyGuarantees_AreNotAdvertised()
    {
        Assert.DoesNotContain(
            PostgresRelationQueryTargetProfile.Default.Capabilities,
            static evidence => evidence.Capability is GuaranteeRelationQueryCapability
            {
                Kind: RelationQueryGuaranteeCapabilityKind.OccurrenceProvenance
                    or RelationQueryGuaranteeCapabilityKind.InvariantEnforcement
            });
    }

    [Fact]
    public void IntervalOverlap_IsNotAdvertisedUntilItsExactDiscreteBoundaryClosureIsSupported()
    {
        Assert.DoesNotContain(
            PostgresRelationQueryTargetProfile.Default.Capabilities,
            static evidence => evidence.Capability is TemporalRelationQueryCapability
            {
                Capability: RelationQueryTemporalExecutionCapability.IntervalOverlap
            });
    }

    [Fact]
    public void RelationRootCorrelation_IsAdvertisedOnlyWithinTheSuppliedRootBoundary()
    {
        var evidence = Assert.Single(
            PostgresRelationQueryTargetProfile.Default.Capabilities,
            static candidate => candidate.Capability is GuaranteeRelationQueryCapability
            {
                Kind: RelationQueryGuaranteeCapabilityKind.RelationRootCorrelation
            });

        Assert.Contains(PostgresRelationQueryTargetProfile.SuppliedRelationRootBoundary, evidence.OperatingBoundaries);
        var boundary = Assert.Single(
            PostgresRelationQueryTargetProfile.Default.OperatingBoundaries,
            static candidate => candidate.Id == PostgresRelationQueryTargetProfile.SuppliedRelationRootBoundary);
        Assert.Equal(RelationQueryOperatingBoundaryKind.SuppliedRelationRoot, boundary.Kind);
    }

    [Fact]
    public void ExpressionCapabilities_MatchTheCompilerClosure()
    {
        HashSet<ExprCapabilityId> expected =
        [
            ExprCapabilities.Field,
            ExprCapabilities.NestedFieldPath,
            ExprCapabilities.Parameter,
            ExprCapabilities.Constant,
            ExprCapabilities.TypedField,
            ExprCapabilities.TypedLiteral,
            ExprCapabilities.Conditional,
            ExprCapabilities.ForUnary(UnaryOperator.Not),
            ExprCapabilities.ForBinary(BinaryOperator.Eq),
            ExprCapabilities.ForBinary(BinaryOperator.Ne),
            ExprCapabilities.ForBinary(BinaryOperator.Gt),
            ExprCapabilities.ForBinary(BinaryOperator.Ge),
            ExprCapabilities.ForBinary(BinaryOperator.Lt),
            ExprCapabilities.ForBinary(BinaryOperator.Le),
            ExprCapabilities.ForBinary(BinaryOperator.And),
            ExprCapabilities.ForBinary(BinaryOperator.Or),
            ExprCapabilities.ForAggregate(AggregateOperator.Count),
            ExprCapabilities.ForAggregate(AggregateOperator.Sum),
            ExprCapabilities.ForAggregate(AggregateOperator.Min),
            ExprCapabilities.ForAggregate(AggregateOperator.Max),
            ExprCapabilities.ForAggregate(AggregateOperator.Average),
            ExprCapabilities.ForFunction(ExprFunctionNames.EndsWith),
            ExprCapabilities.ForFunction(ExprFunctionNames.StartsWith),
            ExprCapabilities.ForFunction(ExprFunctionNames.TextContains)
        ];
        var actual = PostgresRelationQueryTargetProfile.Default.Capabilities
            .Select(static evidence => evidence.Capability)
            .OfType<ExpressionRelationQueryCapability>()
            .Where(static capability => capability.RequirementKind == ExprCapabilityRequirementKind.Operation)
            .Select(static capability => capability.Capability)
            .ToHashSet();

        Assert.Empty(expected.Except(actual));
        Assert.Empty(actual.Except(expected));
    }

    [Fact]
    public void CanonicalRowsAndAggregation_AreRealizableWithoutOccurrenceLineage()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var rows = author.Project(
            loads,
            (Load load) => new LoadRow
            {
                Id = load.Id,
                Amount = load.Amount
            });
        var totals = author.Aggregate<SourceQueryNode, LoadTotals>(
            loads.Node,
            aggregate => aggregate
                .Count(result => result.Count)
                .Value(
                    result => result.Average,
                    AggregateOperator.Average,
                    (Load load) => load.Amount,
                    loads.Binding));
        var query = author.BuildQuery(
            new("postgres-profile-query"),
            new("PostgresProfileQuery"),
            author.Rows(rows),
            author.Aggregation(totals));
        var plan = Compile(query.CreateDocument(), author.ShapeDocuments);

        var realization = RelationQueryRealizationCompiler.Compile(
            plan,
            PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);

        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
    }

    [Fact]
    public void SuppliedRootRelationshipRelation_IsRealizableWithoutOccurrenceLineage()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var customers = author.Traverse<Load, Customer>(loads, load => load.CustomerId);
        var rows = author.Project(
            customers,
            (Load load, Customer customer) => new LoadSearchRow
            {
                Id = load.Id,
                CustomerName = customer.Name
            });
        var relation = rows.BuildRelation((LoadSearchRow row) => row.Id);
        var plan = Compile(
            relation.CreateDocument(),
            author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument());

        var realization = RelationQueryRealizationCompiler.Compile(
            plan,
            PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);

        Assert.True(realization.IsRealizable, Format(realization.Diagnostics));
    }

    [Fact]
    public void InvariantBearingRelation_FailsClosedWithoutPostExecutionEnforcement()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<Load>();
        var rows = author.Project(
            loads,
            (Load load) => new LoadRow
            {
                Id = load.Id,
                Amount = load.Amount
            });
        var relation = rows.BuildRelation(
            (LoadRow row) => row.Id,
            invariants:
            [
                new("id-is-not-empty", (LoadRow row) => row.Id != "")
            ]);
        var plan = Compile(relation.CreateDocument(), author.ShapeDocuments);

        var realization = RelationQueryRealizationCompiler.Compile(
            plan,
            PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy,
            RelationQueryResultObservability.NotRequested);

        Assert.False(realization.IsRealizable);
        Assert.Contains(
            realization.Diagnostics,
            static diagnostic => diagnostic.Message.Contains(
                nameof(RelationQueryGuaranteeCapabilityKind.InvariantEnforcement),
                StringComparison.Ordinal));
    }

    static CompiledRelationQueryPlan Compile(
        RelationQueryDocument document,
        IEnumerable<ShapeGraphDocument> shapes,
        RelationshipCatalogDocument? relationships = null)
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(document, [.. shapes], relationships));
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        return Assert.IsType<CompiledRelationQueryPlan>(compilation.Plan);
    }

    static string Format<T>(IEnumerable<T> diagnostics) => string.Join(Environment.NewLine, diagnostics);

    sealed class Load
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerId")]
        public required string CustomerId { get; init; }

        [JsonPropertyName("amount")]
        public decimal Amount { get; init; }
    }

    sealed class Customer
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed class LoadRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("amount")]
        public decimal Amount { get; init; }
    }

    sealed class LoadTotals
    {
        [JsonPropertyName("count")]
        public long Count { get; init; }

        [JsonPropertyName("average")]
        public decimal Average { get; init; }
    }

    sealed class LoadSearchRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerName")]
        public required string CustomerName { get; init; }
    }
}
