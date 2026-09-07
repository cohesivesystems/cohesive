using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite.TestFixtures;

// Shared synthetic fixture compiled into the adapter tests and benchmarks, never the adapter package.
sealed class SqliteTypedRowFixture : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), $"cohesive-typed-rows-{Guid.NewGuid():N}.db");
    public SqliteDatabase Database { get; }
    public SqliteConnection Connection { get; }
    public GraphShapeId Shape { get; }
    public SqliteRelationQueryCompiledArtifact Artifact { get; }
    public SqliteRelationQueryRowMapping<Row> Mapping { get; }

    public SqliteTypedRowFixture(int payloadLength = 32)
    {
        Shape definition = new(new("Row"),
        [
            new(new(nameof(Row.Id)), new ScalarTypeRef(ScalarTypeKind.Int64)),
            new(new(nameof(Row.Label)), new ScalarTypeRef(ScalarTypeKind.String)),
            new(new(nameof(Row.Payload)), new ScalarTypeRef(ScalarTypeKind.Bytes),
                presence: FieldPresence.Optional, nullability: FieldNullability.Nullable)
        ]);
        ShapeGraph graph = new(new("typed-row-fixture/v1"), [definition]);
        Shape = new(graph, definition.Id);
        var author = RelationQuery.Structural();
        var source = author.Source(Shape.QualifiedId, nodeId: new("rows"), bindingId: new("row"));
        var ordered = author.Order(source.Node, [new(source.Binding.Field(nameof(Row.Id)))], nodeId: new("ordered"));
        var document = author.BuildQuery(new("typed-rows/v1"), new("TypedRows"),
            [author.Rows(ordered, id: new("rows"))]).CreateDocument();
        var compiled = RelationQueryStaticCompiler.Compile(new(document, [ShapeGraphDocument.FromGraph(graph)]));
        if (!compiled.IsSuccessful) throw new InvalidOperationException(string.Join("\n", compiled.Validation.Diagnostics));
        var plan = compiled.Plan!;
        var input = plan.InputContract.Sources.Single();
        var instance = new RelationQuerySourceInstanceId("sqlite/typed-rows");
        var binding = new RelationQuerySourcePlacementBinding(new("row"), input.Input.Id, input.Node, input.Binding,
            input.Shape, instance, RelationQuerySourcePlacementBindingKind.SourceSet,
            RelationQuerySourceAcquisitionKind.BoundedEnumeration, RelationQuerySourcePlacementOrigin.Explicit,
            new(input.Shape, nameof(Row.Id), FieldPath.FromField(nameof(Row.Id))),
            [.. input.Fields.Select(field => new RelationQuerySourceFieldBinding(
                field.Input.Id, field.Input.Field.Path, field.Input.Field.Path.ToString()))]);
        var placement = new RelationQuerySourcePlacement(RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan), SqliteRelationQueryTargetProfile.ConventionSet,
            [new(instance, new("sqlite/typed-rows"), SqliteRelationQueryTargetProfile.Default, new(10, 10, 10, 1))], [binding]);
        var payload = binding.Fields.Single(field => field.SourceSelector == nameof(Row.Payload));
        var storage = new SqliteRelationQueryStorageBinding(placement,
            [new(binding.Id, "Rows", "typed-row-fixture/schema-v1", [new(payload.Input, "PayloadPresent")])]);
        var feasibility = RelationQueryRealizationCompiler.Compile(plan, SqliteRelationQueryTargetProfile.Default,
            SqliteRelationQueryTargetProfile.Policy, RelationQueryResultObservability.ExactContributors);
        var native = new SqliteRelationQueryCompiler().Compile(new(plan, feasibility, placement), storage);
        if (!native.IsSuccessful) throw new InvalidOperationException(string.Join("\n", native.Diagnostics));
        Artifact = native.Artifacts.Single();
        Mapping = Artifact.CreateRowMapping<Row>(Shape);
        Database = new(new(path, pooling: SqliteConnectionPooling.Disabled));
        Connection = Database.OpenConnection();
        using (var schema = Database.CreateCommand(Connection, null,
                   "CREATE TABLE Rows(Id INTEGER PRIMARY KEY, Label TEXT NOT NULL, Payload BLOB NULL, PayloadPresent INTEGER NOT NULL) STRICT;"))
            schema.ExecuteNonQuery();
        using var insert = Database.CreateCommand(Connection, null,
            "INSERT INTO Rows VALUES (1, 'first', zeroblob($length), 1), (2, 'null', NULL, 1), (3, 'missing', NULL, 0)");
        insert.Parameters.AddWithValue("$length", payloadLength);
        insert.ExecuteNonQuery();
    }

    public SqliteCommand CreateCommand() => Database.CreateCommand(Connection, null, Artifact.Command, []);

    public void Dispose()
    {
        Connection.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
    }

    public sealed record Row(long Id, string Label, byte[]? Payload);
}
