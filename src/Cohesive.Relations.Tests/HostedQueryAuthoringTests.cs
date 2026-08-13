using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class HostedQueryAuthoringTests
{
    [Fact]
    public void Create_AuthorsTypedCanonicalDocumentAndExactRelationDependency()
    {
        var projection = Projection();
        var query = Create(
            new("schema-mapping", "tenant-and-payload-exact", "retain-entity-version"),
            dependencies: [projection.AsHostedQueryDependency("projection")]);

        Assert.True(query.IsValid, Format(query.Validation));
        Assert.Equal(HostedQueryDefinitionDocuments.Kind, query.Document.Kind);
        Assert.Equal(new ExecutionDefinitionId("query/tests/event-source"), query.Reference.DefinitionId);
        Assert.Equal(new ExecutionRevisionId("1"), query.Reference.RevisionId);
        Assert.Equal(query.Document.Metadata.Fingerprint, query.Reference.Fingerprint);
        Assert.Equal(
            new DefaultClrTypeRefMapper().Map(typeof(QueryInput), null),
            query.InputContract.Type);
        Assert.Equal(
            new DefaultClrTypeRefMapper().Map(typeof(QueryResult), null),
            query.ResultContract.Type);
        Assert.Equal(new("tests.event-source", "1"), query.Implementation);

        var dependency = Assert.Single(query.Dependencies);
        Assert.Equal("projection", dependency.Role);
        Assert.Equal(projection.Reference, dependency.Definition);
        Assert.Equal(PortableValueState.Concrete, query.Configuration.State);
        Assert.Equal(
            ObservationValue.FromString("schema-mapping"),
            query.Configuration.Value!.Value.GetProperty(nameof(QueryConfiguration.SourceFamily)));
    }

    [Fact]
    public void Document_RoundTripsThroughStrictSharedEnvelopeAndTypedFacade()
    {
        var query = Create(
            new("human-feedback", "partition-exact", "retain-entity-version"),
            dependencies: [Dependency("dependency/tests/projection", "1", 'a')]);
        var json = ExecutionDefinitionJsonSerializer.Serialize(query.Document);

        var validation = HostedQueryDefinitionDocuments.TryDeserialize(
            json,
            out var document,
            out var definition);

        Assert.True(validation.IsValid, Format(validation));
        Assert.Equal(query.Document, document);
        Assert.Equal(query.Definition, definition);
    }

    [Fact]
    public void Fingerprint_CoversImplementationDependencyConfigurationAndInvocationContracts()
    {
        var baselineDependency = Dependency("dependency/tests/projection", "1", 'a');
        var baseline = Create(
            new("schema-mapping", "partition-exact", "retain-entity-version"),
            dependencies: [baselineDependency]);
        var changedImplementation = Create(
            new("schema-mapping", "partition-exact", "retain-entity-version"),
            implementationVersion: "2",
            dependencies: [baselineDependency]);
        var changedDependency = Create(
            new("schema-mapping", "partition-exact", "retain-entity-version"),
            dependencies: [Dependency("dependency/tests/projection", "1", 'b')]);
        var changedConfiguration = Create(
            new("schema-mapping", "partition-exact", "use-admission-version"),
            dependencies: [baselineDependency]);
        var changedInput = HostedQuery<OtherQueryInput, QueryResult>.Create(
            new("query/tests/event-source"),
            new("1"),
            new("tests.event-source", "1"),
            new QueryConfiguration("schema-mapping", "partition-exact", "retain-entity-version"),
            Provenance(),
            [baselineDependency]);
        var changedResult = HostedQuery<QueryInput, OtherQueryResult>.Create(
            new("query/tests/event-source"),
            new("1"),
            new("tests.event-source", "1"),
            new QueryConfiguration("schema-mapping", "partition-exact", "retain-entity-version"),
            Provenance(),
            [baselineDependency]);

        var fingerprint = baseline.Reference.Fingerprint;
        Assert.NotEqual(fingerprint, changedImplementation.Reference.Fingerprint);
        Assert.NotEqual(fingerprint, changedDependency.Reference.Fingerprint);
        Assert.NotEqual(fingerprint, changedConfiguration.Reference.Fingerprint);
        Assert.NotEqual(fingerprint, changedInput.Reference.Fingerprint);
        Assert.NotEqual(fingerprint, changedResult.Reference.Fingerprint);
    }

    [Fact]
    public void Create_RetainsPortableConfigurationDiagnosticsWithoutHashingRuntimeObjects()
    {
        var query = HostedQuery<QueryInput, QueryResult>.Create(
            new("query/tests/invalid-configuration"),
            new("1"),
            new("tests.event-source", "1"),
            new Dictionary<string, string> { ["policy"] = "exact" },
            Provenance());

        Assert.False(query.IsValid);
        Assert.Contains(
            query.Validation.Diagnostics,
            static diagnostic => diagnostic.Code == HostedQueryDefinitionDiagnosticCodes.ValueInvalid
                && diagnostic.Location?.StartsWith(
                    "/definition/configuration/contract/type",
                    StringComparison.Ordinal) == true);
        Assert.Equal(query.Validation.Diagnostics, query.Document.Metadata.Diagnostics);
    }

    [Fact]
    public void Definition_NormalizesDependencyOrderAndRejectsDuplicateEvidence()
    {
        var alpha = Dependency("dependency/tests/alpha", "1", 'a', role: "alpha");
        var beta = Dependency("dependency/tests/beta", "1", 'b', role: "beta");
        var query = Create(
            new("synthetic", "partition-exact", "retain-entity-version"),
            dependencies: [beta, alpha]);

        Assert.Equal(["alpha", "beta"], query.Dependencies.Select(static dependency => dependency.Role));
        Assert.Throws<ArgumentException>(() => Create(
            new("synthetic", "partition-exact", "retain-entity-version"),
            dependencies: [alpha, new("alpha", beta.Definition)]));
        Assert.Throws<ArgumentException>(() => Create(
            new("synthetic", "partition-exact", "retain-entity-version"),
            dependencies: [alpha, new("other", alpha.Definition)]));
    }

    [Fact]
    public void Validator_RejectsNonConcreteConfigurationAndNonSingularBoundaries()
    {
        var stringContract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var definition = new HostedQueryDefinition(
            new(
                new ScalarTypeRef(ScalarTypeKind.String),
                cardinality: FieldCardinality.Many),
            new(
                new ScalarTypeRef(ScalarTypeKind.String),
                cardinality: FieldCardinality.Many),
            new("tests.event-source", "1"),
            PortableValue.Unknown(stringContract));

        var validation = HostedQueryDefinitionValidator.Validate(definition);

        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == HostedQueryDefinitionDiagnosticCodes.CardinalityInvalid
                && diagnostic.Location == "/input/cardinality");
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == HostedQueryDefinitionDiagnosticCodes.CardinalityInvalid
                && diagnostic.Location == "/result/cardinality");
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == HostedQueryDefinitionDiagnosticCodes.ConfigurationStateInvalid
                && diagnostic.Location == "/configuration/state");
    }

    static HostedQuery<QueryInput, QueryResult> Create(
        QueryConfiguration configuration,
        string implementationVersion = "1",
        IEnumerable<HostedQueryDependency>? dependencies = null) =>
        HostedQuery<QueryInput, QueryResult>.Create(
            new("query/tests/event-source"),
            new("1"),
            new("tests.event-source", implementationVersion),
            configuration,
            Provenance(),
            dependencies,
            displayName: "Acquire one event source");

    static Relation<ProjectionInput, QueryResult> Projection()
    {
        var author = RelationQuery.Expression();
        var input = author.Source<ProjectionInput>();
        var result = author.Project(input, value => new QueryResult
        {
            Id = value.Id,
            Value = value.Payload
        });
        var authored = result.BuildRelation(
            value => value.Id,
            id: new("relation/tests/source-projection"),
            name: new("Source projection"));
        return author.CreateRelation(authored, input, result, new("1"));
    }

    static HostedQueryDependency Dependency(
        string id,
        string revision,
        char fingerprint,
        string role = "projection") =>
        new(
            role,
            new(
                new(id),
                new(revision),
                new("SHA-256", "tests/v1", new string(fingerprint, 64))));

    static ExecutionProvenance Provenance() => new(
        new("hosted-query-tests", "1"),
        new("tests/relations/hosted-query"),
        DocumentOrigin.Generated);

    static string Format(DocumentValidationResult validation) =>
        string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Severity} {diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}"));

    public sealed record QueryConfiguration(
        string SourceFamily,
        string TenantPolicy,
        string VersionPolicy);

    public sealed class QueryInput
    {
        public required string Id { get; init; }
    }

    public sealed class OtherQueryInput
    {
        public required string Id { get; init; }

        public required string Tenant { get; init; }
    }

    public sealed class ProjectionInput
    {
        public required string Id { get; init; }

        public required string Payload { get; init; }
    }

    public sealed class QueryResult
    {
        public required string Id { get; init; }

        public required string Value { get; init; }
    }

    public sealed class OtherQueryResult
    {
        public required string Id { get; init; }

        public required string Value { get; init; }

        public required string Source { get; init; }
    }
}
