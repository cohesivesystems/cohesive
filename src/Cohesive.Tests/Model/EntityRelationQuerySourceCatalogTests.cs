using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Storage;

namespace Cohesive.Tests.Model;

public sealed class EntityRelationQuerySourceCatalogTests
{
    [Fact]
    public void Catalog_AuthorsExactDeterministicPlacementFromCanonicalRegistration()
    {
        var fixture = CreateQueryFixture();
        var registration = CreateRegistration(fixture, [Stored("load-b", "Beta"), Stored("load-a", "Alpha")]);
        var catalog = new EntityRelationQuerySourceCatalog([registration]);

        var authored = catalog.Place(fixture.Plan).RequireValue();
        var binding = Assert.Single(authored.Placement.Bindings);
        var source = Assert.Single(authored.Placement.SourceInstances);

        Assert.Equal(registration.Source, source);
        Assert.Equal(registration.Source.Id, binding.Source);
        Assert.Equal(RelationQuerySourceAcquisitionKind.BoundedEnumeration, binding.Acquisition);
        Assert.Equal(registration.IdentitySourceSelector, binding.Identity!.SourceSelector);
        Assert.All(binding.Fields, field =>
            Assert.Equal(registration.FieldSourceSelector(field.SemanticPath), field.SourceSelector));
        Assert.Equal(registration.Reader, Assert.Single(catalog.SourceReaders));
        Assert.True(catalog.TryGetSource(fixture.SourceShape.Id, out var resolved));
        Assert.Same(registration, resolved);
    }

    [Fact]
    public void Catalog_PlacesSuppliedRelationRootWithoutRepositoryRegistration()
    {
        var author = RelationQuery.Expression();
        var loads = author.Source<StoredLoad>();
        var rows = author.Project(loads, (StoredLoad load) => new StoredLoadRow
        {
            Id = load.Id,
            Name = load.Name
        });
        var relation = rows.BuildRelation(row => row.Id);
        var evaluation = author.Evaluate(relation, new("tests/storage/supplied-relation")).Build();
        var plan = Compile(evaluation);
        var catalog = new EntityRelationQuerySourceCatalog([]);

        var placement = catalog.Resolve(plan);
        var binding = Assert.Single(placement.Bindings);

        Assert.Equal(RelationQuerySourceAcquisitionKind.Supplied, binding.Acquisition);
        Assert.StartsWith("source/cohesive.storage/supplied/", binding.Source.Value, StringComparison.Ordinal);
        Assert.Empty(catalog.SourceReaders);
    }

    [Fact]
    public void Catalog_FailsClosedForMissingSelectorAndCapabilityIncompatibleSources()
    {
        var fixture = CreateQueryFixture();
        var missing = new EntityRelationQuerySourceCatalog([]).Place(fixture.Plan);
        var valid = CreateRegistration(fixture, [Stored("load-a", "Alpha")]);
        var selectorFailure = new EntityRelationQuerySourceRegistration(
            valid.Shape,
            valid.Source,
            valid.Reader,
            fieldSourceSelector: static _ => string.Empty);
        var invalidProfile = new RelationQueryTargetCapabilityProfile(
            new("tests/incompatible-source"),
            new("tests/incompatible-source/v1"),
            ["foreign-definition/v1"],
            [fixture.Plan.Provenance.CompilerProfile]);
        var incompatibleInstance = new RelationQuerySourceInstance(
            new("source/incompatible"),
            new("domain/incompatible"),
            invalidProfile,
            InMemoryEntityRelationQuerySourceReader.DefaultLimits);
        var incompatible = new EntityRelationQuerySourceRegistration(
            fixture.SourceShape.Id,
            incompatibleInstance,
            new StubReader(new(
                incompatibleInstance.Id,
                incompatibleInstance.ExecutionDomain,
                incompatibleInstance.TargetProfile)));

        var badSelector = new EntityRelationQuerySourceCatalog([selectorFailure]).Place(fixture.Plan);
        var badProfile = new EntityRelationQuerySourceCatalog([incompatible]).Place(fixture.Plan);

        Assert.False(missing.IsSuccess);
        Assert.Contains(missing.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.PlacementMissing);
        Assert.False(badSelector.IsSuccess);
        Assert.Contains(badSelector.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.ConfigurationInvalid);
        Assert.False(badProfile.IsSuccess);
        Assert.Contains(badProfile.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.TargetProfileMismatch);
    }

    [Fact]
    public void Catalog_PreservesSelectorAndMissingSourceDiagnosticsFromOnePlacementAttempt()
    {
        var fixture = CreateTraversalQueryFixture();
        var validLoad = CreateRegistration(fixture.LoadShape, []);
        var invalidLoad = new EntityRelationQuerySourceRegistration(
            validLoad.Shape,
            validLoad.Source,
            validLoad.Reader,
            fieldSourceSelector: static _ => string.Empty);

        var authored = new EntityRelationQuerySourceCatalog([invalidLoad]).Place(fixture.Plan);

        Assert.False(authored.IsSuccess);
        Assert.Contains(authored.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.ConfigurationInvalid);
        Assert.Contains(authored.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPlacementAuthoringDiagnosticCodes.PlacementMissing);
    }

    [Fact]
    public void Catalog_RejectsDuplicateShapesSourcesAndReaderDescriptorMismatch()
    {
        var fixture = CreateQueryFixture();
        var first = CreateRegistration(fixture, [Stored("load-a", "Alpha")]);
        var second = CreateRegistration(
            fixture,
            [Stored("load-b", "Beta")],
            source: new("source/second"));
        var otherShapeSameSource = new EntityRelationQuerySourceRegistration(
            new(new("tests/other-graph"), fixture.SourceShape.Id.ShapeId),
            first.Source,
            first.Reader);
        var mismatchedReader = new StubReader(new(
            new("source/foreign"),
            first.Source.ExecutionDomain,
            first.Source.TargetProfile));

        Assert.Throws<ArgumentException>(() => new EntityRelationQuerySourceCatalog([first, second]));
        Assert.Throws<ArgumentException>(() => new EntityRelationQuerySourceCatalog([first, otherShapeSameSource]));
        Assert.Throws<ArgumentException>(() => new EntityRelationQuerySourceRegistration(
            first.Shape,
            first.Source,
            mismatchedReader));
    }

    [Fact]
    public async Task CatalogEvaluator_ExecutesCanonicalQueryThroughInMemoryEntitySource()
    {
        var fixture = CreateQueryFixture();
        var registration = CreateRegistration(
            fixture,
            [Stored("load-b", "Beta"), Stored("load-a", "Alpha")]);
        var catalog = new EntityRelationQuerySourceCatalog([registration]);
        var evaluator = catalog.CreateEvaluator(Policy());

        var outcome = await evaluator.EvaluateAsync(fixture.Evaluation);

        Assert.True(
            outcome.IsSuccessful,
            string.Join(Environment.NewLine, outcome.Compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var result = Assert.IsType<RelationQueryExecutionResult>(outcome.Result);
        var rows = Assert.Single(result.QueryResults).Rows;
        Assert.Equal(["load-a", "load-b"], rows.Select(static row => row.Value.GetProperty("id").String));
        Assert.Equal(["Alpha", "Beta"], rows.Select(static row => row.Value.GetProperty("name").String));
        Assert.Empty(result.RequirementGapAnalysis.Gaps);
    }

    [Fact]
    public async Task CatalogEvaluator_ExecutesForwardTraversalQueryAcrossEntitySources()
    {
        var fixture = CreateTraversalQueryFixture();
        var loadSource = CreateRegistration(
            fixture.LoadShape,
            [
                Snapshot(
                    fixture.LoadShape.Id.ShapeId,
                    "load-b",
                    ("id", ObservationValue.FromString("load-b")),
                    ("customerId", ObservationValue.FromString("customer-b"))),
                Snapshot(
                    fixture.LoadShape.Id.ShapeId,
                    "load-a",
                    ("id", ObservationValue.FromString("load-a")),
                    ("customerId", ObservationValue.FromString("customer-a")))
            ]);
        var customerSource = CreateRegistration(
            fixture.CustomerShape,
            [
                Snapshot(
                    fixture.CustomerShape.Id.ShapeId,
                    "customer-b",
                    ("id", ObservationValue.FromString("customer-b")),
                    ("name", ObservationValue.FromString("Beta Customer"))),
                Snapshot(
                    fixture.CustomerShape.Id.ShapeId,
                    "customer-a",
                    ("id", ObservationValue.FromString("customer-a")),
                    ("name", ObservationValue.FromString("Alpha Customer")))
            ]);
        var evaluator = new EntityRelationQuerySourceCatalog([loadSource, customerSource])
            .CreateEvaluator(Policy());

        var outcome = await evaluator.EvaluateAsync(fixture.Evaluation);

        Assert.True(
            outcome.IsSuccessful,
            $"Status: {outcome.Status}{Environment.NewLine}" + string.Join(
                Environment.NewLine,
                outcome.Diagnostics.Select(static diagnostic => diagnostic.Message)
                    .Concat(outcome.Compilation.Diagnostics.Select(static diagnostic => diagnostic.Message))
                    .Concat(outcome.Realization?.Diagnostics.Select(static diagnostic => diagnostic.Message) ?? [])
                    .Concat(outcome.PhysicalPlanning?.Diagnostics.Select(static diagnostic => diagnostic.Message) ?? [])
                    .Concat(outcome.PhysicalExecution?.Diagnostics.Select(static diagnostic => diagnostic.Message) ?? [])
                    .Concat(outcome.PhysicalExecution?.SourceReads.Select(static read =>
                        $"Read: {read.Kind}/{read.State}/{read.ReturnedRows}/{read.EvidenceReference}") ?? [])
                    .Concat(outcome.Result?.RequirementGapAnalysis.Gaps.Select(static gap =>
                        $"Gap: {gap.Cause}/{gap.Input.GetType().Name}/{gap.Input.Id.Value}/{gap.EvidenceReference}") ?? [])));
        var result = Assert.IsType<RelationQueryExecutionResult>(outcome.Result);
        var queryRows = Assert.Single(result.QueryResults).Rows;
        Assert.Equal(["load-a", "load-b"], queryRows.Select(static row => row.Value.GetProperty("id").String));
        Assert.Equal(
            ["Alpha Customer", "Beta Customer"],
            queryRows.Select(static row => row.Value.GetProperty("customerName").String));
        Assert.Empty(result.RequirementGapAnalysis.Gaps);
    }

    [Fact]
    public void DependencyInjection_ResolvesCatalogAndExistingCanonicalEvaluatorGateway()
    {
        var fixture = CreateQueryFixture();
        var registration = CreateRegistration(fixture, [Stored("load-a", "Alpha")]);
        ServiceCollection services = new();
        services.RegisterEntityRelationQuerySource(registration);
        services.RegisterEntityRelationQueryEvaluator(Policy());

        using var provider = services.BuildServiceProvider();
        var catalog = provider.GetEntityRelationQuerySourceCatalog();
        var evaluator = provider.GetRequiredService<IRelationQueryEvaluator>();

        Assert.Same(registration, Assert.Single(catalog.Sources));
        Assert.IsType<RelationQueryEvaluator>(evaluator);
    }

    [Fact]
    public void DependencyInjection_RejectsCompetingCanonicalEvaluatorGateway()
    {
        ServiceCollection services = new();
        services.RegisterEntityRelationQueryEvaluator(Policy());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.RegisterEntityRelationQueryEvaluator(Policy()));

        Assert.Contains("one canonical evaluator gateway", exception.Message, StringComparison.Ordinal);
    }

    static QueryFixture CreateQueryFixture()
    {
        var author = RelationQuery.Expression();
        var shape = author.Clr.Shape<StoredLoad>();
        var loads = author.Source(shape);
        var projected = author.Project(loads, (StoredLoad load) => new StoredLoadRow
        {
            Id = load.Id,
            Name = load.Name
        });
        var rows = author.Rows(projected.Node, projected.Binding, id: new("rows"));
        var query = author.BuildQuery(
            new("tests/storage/entity-query"),
            new("StorageEntityQuery"),
            rows);
        var evaluation = author.Evaluate(query, new("tests/storage/entity-query/evaluation")).Build();
        return new(shape, evaluation, Compile(evaluation));
    }

    static TraversalQueryFixture CreateTraversalQueryFixture()
    {
        var author = RelationQuery.Expression();
        var loadShape = author.Clr.Shape<JoinedLoad>();
        var customerShape = author.Clr.Shape<JoinedCustomer>();
        var loads = author.Source(loadShape);
        var customers = author.Traverse<JoinedLoad, JoinedCustomer>(loads, load => load.CustomerId);
        var documents = author.Project(
            customers,
            (JoinedLoad load, JoinedCustomer customer) => new JoinedLoadSearchRow
            {
                Id = load.Id,
                CustomerId = load.CustomerId,
                CustomerName = customer.Name
            });
        var rows = author.Rows(documents.Node, documents.Binding, id: new("rows"));
        var query = author.BuildQuery(
            new("tests/storage/entity-traversal/query"),
            new("StorageEntityTraversalQuery"),
            rows);
        var evaluation = author.Evaluate(query, new("tests/storage/entity-traversal/evaluation")).Build();
        return new(loadShape, customerShape, evaluation, Compile(evaluation));
    }

    static EntityRelationQuerySourceRegistration CreateRegistration(
        QueryFixture fixture,
        ImmutableArray<StoredLoad> values,
        RelationQuerySourceInstanceId? source = null)
    {
        var canonicalShape = fixture.SourceShape.Document.Graph.GetShape(fixture.SourceShape.Id);
        var entityShape = new Shape(
            canonicalShape.Id,
            canonicalShape.Fields,
            canonicalShape.Constraints,
            canonicalShape.Annotations,
            role: ShapeRoles.Entity);
        var definition = new EntityDefinition(new(entityShape.Id.Value), entityShape);
        var snapshots = values.Select(value => new EntitySnapshot(
            new(
                entityShape.Id,
                value.Id,
                new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                {
                    ["id"] = ObservationValue.FromString(value.Id),
                    ["name"] = ObservationValue.FromString(value.Name),
                    ["tenantId"] = ObservationValue.FromString(value.TenantId)
                }),
            value.TenantId,
            new($"seed/{value.Id}"))).ToArray();
        var repository = new InMemoryEntityOutboxRepository(
            definition,
            partitionKeyFieldName: "tenantId",
            seedSnapshots: snapshots);
        return EntityRelationQuerySourceRegistration.InMemory(
            fixture.SourceShape.Id,
            repository,
            source: source);
    }

    static EntityRelationQuerySourceRegistration CreateRegistration<T>(
        RelationQueryClrShape<T> sourceShape,
        ImmutableArray<EntitySnapshot> snapshots)
        where T : notnull
    {
        var canonicalShape = sourceShape.Document.Graph.GetShape(sourceShape.Id);
        var entityShape = new Shape(
            canonicalShape.Id,
            canonicalShape.Fields,
            canonicalShape.Constraints,
            canonicalShape.Annotations,
            role: ShapeRoles.Entity);
        var repository = new InMemoryEntityOutboxRepository(
            new EntityDefinition(new(entityShape.Id.Value), entityShape),
            static _ => "partition",
            snapshots);
        return EntityRelationQuerySourceRegistration.InMemory(sourceShape.Id, repository);
    }

    static EntitySnapshot Snapshot(
        ShapeId shape,
        string id,
        params (string Name, ObservationValue Value)[] fields) => new(
        new(
            shape,
            id,
            fields.ToDictionary(static field => field.Name, static field => field.Value, StringComparer.Ordinal)),
        "partition",
        new($"seed/{id}"));

    static StoredLoad Stored(string id, string name) => new()
    {
        Id = id,
        Name = name,
        TenantId = "tenant-a"
    };

    static CompiledRelationQueryPlan Compile(RelationQueryEvaluation evaluation)
    {
        var result = RelationQueryStaticCompiler.Compile(evaluation.Compilation);
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static RelationQueryPhysicalPlanningPolicy Policy() => new(
        new("tests/storage/entity-source-policy/v1"),
        conventionSetVersion: "tests/storage/entity-source-conventions/v1",
        maximumBatchSize: 100,
        maximumBufferedRows: 1_000,
        maximumLocalRows: 1_000,
        maximumFanOut: 100,
        maximumReferenceKeysPerObservation: 100,
        maximumConcurrency: 4);

    sealed record QueryFixture(
        RelationQueryClrShape<StoredLoad> SourceShape,
        RelationQueryEvaluation Evaluation,
        CompiledRelationQueryPlan Plan);

    sealed record TraversalQueryFixture(
        RelationQueryClrShape<JoinedLoad> LoadShape,
        RelationQueryClrShape<JoinedCustomer> CustomerShape,
        RelationQueryEvaluation Evaluation,
        CompiledRelationQueryPlan Plan);

    sealed record StoredLoad
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("tenantId")]
        public required string TenantId { get; init; }
    }

    sealed record StoredLoadRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed record JoinedLoad
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerId")]
        public required string CustomerId { get; init; }
    }

    sealed record JoinedCustomer
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }
    }

    sealed record JoinedLoadSearchRow
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("customerId")]
        public required string CustomerId { get; init; }

        [JsonPropertyName("customerName")]
        public required string CustomerName { get; init; }
    }

    sealed class StubReader(RelationQuerySourceReaderDescriptor descriptor) : IRelationQuerySourceReader
    {
        public RelationQuerySourceReaderDescriptor Descriptor { get; } = descriptor;

        public ValueTask<RelationQuerySourceReadResult> ReadAsync(
            RelationQuerySourceReadRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Catalog validation must not execute a stub source reader.");
    }
}
