using System.Collections.Immutable;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Npgsql;

namespace Cohesive.Tests.Postgres;

// Authoritative structural source lowers to the same IR consumed by native and reference interpreters.
sealed class PostgresContributingRowsFixture : IDisposable
{
    readonly string schema = "contributors_" + Guid.NewGuid().ToString("N");
    public CompiledRelationQueryPlan Plan { get; }
    public PostgresRelationQueryCompiledArtifact Artifact { get; }
    public PostgresNpgsqlRuntimeBinding Runtime { get; }
    public GraphShapeId Shape { get; }
    public PostgresQueryRowsReader Reader { get; }
    public ObservationMaterializer<Row> Materializer { get; }
    public RelationQueryExecutionResult Reference { get; }
    readonly NpgsqlDataSource dataSource;
    public PostgresContributingRowsFixture(string connection, int count, JoinKind kind = JoinKind.Left, bool sameShape = false, FieldNullability nameNullability = FieldNullability.Nullable)
    {
        ScalarTypeRef integer = new(ScalarTypeKind.Int64);
        Shape leftShape = new(new("Left"), [new(new("Id"), integer), new(new("Key"), integer),
            new(new("Name"), new ScalarTypeRef(ScalarTypeKind.String),
                presence: nameNullability == FieldNullability.NonNullable ? FieldPresence.Optional : FieldPresence.Required, nullability: nameNullability)]);
        Shape rightShape = new(new("Right"), [new(new("Id"), integer), new(new("Key"), integer),
            new(new("Name"), new ScalarTypeRef(ScalarTypeKind.String),
                presence: nameNullability == FieldNullability.NonNullable ? FieldPresence.Optional : FieldPresence.Required, nullability: nameNullability)]);
        Shape resultShape = new(new("Result"), [new(new("Id"), integer),
            new(new("Name"), new ScalarTypeRef(ScalarTypeKind.String), presence: FieldPresence.Optional, nullability: FieldNullability.Nullable)]);
        ShapeGraph graph = new(new("postgres-contributor-fixture/v1"), sameShape ? [leftShape, resultShape] : [leftShape, rightShape, resultShape]);
        Shape = new(graph, resultShape.Id);
        var author = RelationQuery.Structural();
        var left = author.Source(new(graph.Id, leftShape.Id), new("left"), new("left"));
        var right = author.Source(new(graph.Id, sameShape ? leftShape.Id : rightShape.Id), new("right"), new("right"));
        var joined = author.Join(left.Node, right.Node, kind, Expr.Eq(left.Binding.Field("Key"), right.Binding.Field("Key")), new("joined"));
        var projected = author.Project(joined, Shape.QualifiedId,
            [new(FieldPath.FromField("Id"), left.Binding.Field("Id")), new(FieldPath.FromField("Name"), right.Binding.Field("Name"))], new("projected"), new("result"));
        var source = author.BuildQuery(new("contributors"), new("Contributors"), [author.Rows(projected.Node, new("rows"))]);
        var compiled = RelationQueryStaticCompiler.Compile(new(source.CreateDocument(), [ShapeGraphDocument.FromGraph(graph)]));
        if (!compiled.IsSuccessful) throw new InvalidOperationException(string.Join("\n", compiled.Diagnostics));
        Plan = compiled.Plan!;
        Shape = new(Plan.Provenance.ShapeDocuments.Single(document => document.Graph.Id == graph.Id).Graph, resultShape.Id);
        var placementBuilder = RelationQueryPlacement.For(Plan);
        var physicalSource = placementBuilder.Source("postgres/contributors", PostgresRelationQuerySourceTargetProfile.Default, new("fixture/db"));
        var handles = Plan.InputContract.Sources.ToDictionary(item => item.Binding.Value,
            item => placementBuilder.Place(item, physicalSource).Identity(FieldPath.FromField("Id"), "Id").FieldsBySemanticPath());
        var placement = placementBuilder.Build().RequireValue();
        var storageBuilder = PostgresRelationQueryBinding.For(placement, explicitAuthority: "fixture/ddl/v1").Database(new("fixture/db"));
        foreach (var pair in handles)
            storageBuilder.Table(placement.GetInput(pair.Value), pair.Key + "_rows", table =>
            {
                table.Schema(schema).ColumnsExplicitly().Column(FieldPath.FromField("Key"), "key")
                    .Identity(FieldPath.FromField("Id"), "id");
                if (pair.Key == "left") table.Column(FieldPath.FromField("Id"), "id");
                if (pair.Key == "right") table.Column(FieldPath.FromField("Name"), "name");
            });
        var storage = storageBuilder.Build().RequireValue();
        var feasibility = RelationQueryRealizationCompiler.Compile(Plan, PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy, RelationQueryResultObservability.ExactContributors);
        var native = new PostgresRelationQueryCompiler().Compile(new RelationQueryBoundRealizationRequest(Plan, feasibility, placement.Placement), storage);
        if (!native.IsSuccessful) throw new InvalidOperationException(string.Join("\n", native.Diagnostics));
        Artifact = native.Artifacts.Single();
        dataSource = NpgsqlDataSource.Create(connection);
        Runtime = new(new("fixture/db"), dataSource, "fixture/ddl/v1");
        using var create = dataSource.CreateCommand($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.left_rows(id bigint PRIMARY KEY, key bigint NOT NULL); CREATE TABLE {schema}.right_rows(id bigint PRIMARY KEY, key bigint NOT NULL, name text NULL)");
        create.ExecuteNonQuery();
        using var populate = dataSource.CreateCommand($"INSERT INTO {schema}.left_rows SELECT n,n FROM generate_series(1,$1) n");
        populate.Parameters.AddWithValue(count); populate.ExecuteNonQuery();
        using var populateRight = dataSource.CreateCommand($"INSERT INTO {schema}.right_rows SELECT n*10+k,n,CASE WHEN n%3=1 THEN NULL ELSE 'name-' || n END FROM generate_series(1,$1) n CROSS JOIN generate_series(0,1) k WHERE n%3<>0 AND (k=0 OR n%5=0)");
        populateRight.Parameters.AddWithValue(count); populateRight.ExecuteNonQuery();
        Reader = new(Artifact, Runtime, Plan, maximumRows: count * 2, maximumBytes: 32_000_000);
        var layout = ObservationLayout.Create(Shape, Artifact.ResultFields.Select(field => field.Field.Path.Segments[0].Segment!));
        Materializer = ObservationMaterializer.For<Row>(Shape).Compile(layout);
        Reference = ReferenceExecution(left.Binding.Id, count, nameNullability);
    }

    RelationQueryExecutionResult ReferenceExecution(ValueBindingId left, int count, FieldNullability nameNullability)
    {
        var sourceEvidence = ImmutableArray.CreateBuilder<RelationQuerySourceEvidence>();
        var fieldEvidence = ImmutableArray.CreateBuilder<RelationQueryFieldEvidence>();
        foreach (var input in Plan.InputContract.Sources)
        {
            var occurrences = ImmutableArray.CreateBuilder<RelationQueryObservationOccurrence>();
            for (var n = 1; n <= count; n++)
            for (var k = 0; k < (input.Binding == left ? 1 : n % 3 == 0 ? 0 : n % 5 == 0 ? 2 : 1); k++)
            {
                var identity = input.Binding == left ? n : n * 10 + k;
                var occurrence = new RelationQueryObservationOccurrence(new($"fixture/{input.Binding}/{identity}"), input.Binding,
                    input.Shape, identity.ToString(System.Globalization.CultureInfo.InvariantCulture));
                occurrences.Add(occurrence);
                foreach (var field in Plan.RequirementGraph.Inputs.OfType<RelationQueryFieldInput>().Where(field => field.Binding == input.Binding))
                {
                    var name = field.Field.Path.Segments[0].Segment;
                    var value = name == "Id" ? ObservationValue.FromInt64(identity) : name == "Key" ? ObservationValue.FromInt64(n)
                        : n % 3 == 1 ? ObservationValue.Null : ObservationValue.FromString("name-" + n);
                    fieldEvidence.Add(value.Kind == ObservationValueKind.Null
                        ? new(field.Id, occurrence.Id, nameNullability == FieldNullability.Nullable ? RelationQueryFieldEvidenceState.Null : RelationQueryFieldEvidenceState.Missing)
                        : new(field.Id, occurrence.Id, RelationQueryFieldEvidenceState.Value, value));
                }
            }
            sourceEvidence.Add(new(input.Input.Id, RelationQuerySourceEvidenceState.Provided, occurrences.ToImmutable()));
        }
        var capabilities = Plan.RequirementGraph.Inputs.OfType<RelationQueryCapabilityInput>().Select(input =>
            new RelationQueryCapabilityEvidence(input.Id, RelationQueryCapabilityEvidenceState.Available, "fixture/native"));
        var evidence = new RelationQueryRuntimeEvidence(new("fixture"), Plan, sources: sourceEvidence.ToImmutable(),
            fields: fieldEvidence.ToImmutable(), capabilities: [.. capabilities]);
        return RelationQueryInMemoryInterpreter.Default.Execute(new(Plan, evidence));
    }
    public void Dispose()
    {
        using var cleanup = dataSource.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE"); cleanup.ExecuteNonQuery(); dataSource.Dispose();
    }
    public sealed record Row(long Id, string? Name);
}
