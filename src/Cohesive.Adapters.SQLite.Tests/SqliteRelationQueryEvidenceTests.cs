using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteRelationQueryEvidenceTests
{
    static readonly QualifiedShapeId Shape = new(new("keys/v1"), new("Row"));
    static readonly QualifiedShapeId ResultShape = new(Shape.GraphId, new("Result"));
    static readonly ValueBindingId Left = new("item");
    static readonly ValueBindingId Right = new("detail");
    static readonly FieldPath Id = FieldPath.FromField("Id");
    static readonly FieldPath Part = FieldPath.FromField("Part");
    static readonly FieldPath Link = FieldPath.FromField("Link");
    static readonly FieldPath Score = FieldPath.FromField("Score");
    static readonly ScalarTypeRef Text = new(ScalarTypeKind.String);
    static readonly ScalarTypeRef Integer = new(ScalarTypeKind.Int64);

    [Fact]
    public void CompositeAndTextKeysPreserveWinnersAndGuardedJoinValues()
    {
        var (plan, request, storage) = Build();
        var compilation = new SqliteRelationQueryCompiler().Compile(request, storage);
        Assert.True(compilation.IsSuccessful, string.Join("\n", compilation.Diagnostics));
        var artifact = Assert.Single(compilation.Artifacts);
        Dictionary<ValueBindingId, ObservationValue[]> rows = new()
        {
            [Left] = [Row("A", 1, "x", 1), Row("B", 1, null, 2), // New unmatched winner suppresses old matched row.
                Row("A", 2, "x", 3), Row("B", 2, "y", 3), // ASCII identity resolves a preference tie.
                Row("A", 3, "x", 1), Row("B", 3, "z", 2), // Null detail score suppresses older eligible row.
                Row("A", 4, "x", 1), Row("B", 4, "late", 2),
                Row("a/\"\\", 5, "x", null)],
            [Right] = [Row("x", 0, null, 3), Row("y", 0, null, 7), Row("z", 0, null, null), Row("late", 0, null, 11)]
        };
        var expected = RelationQueryInMemoryInterpreter.Default.Execute(new(plan, Evidence(plan, rows)));
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, expected.Status);
        using var database = new DatabaseFixture();
        using var connection = database.Database.OpenConnection();
        Load(connection, rows);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var scope = new SqliteCommandScope(database.Database, connection, transaction);
        using var reader = scope.ExecuteReader(artifact.Command, default);
        List<SqliteRelationQueryRow> actual = [];
        while (reader.Read()) actual.Add(artifact.ReadCurrentRow(reader));
        var reference = Assert.Single(expected.QueryResults).Rows;
        Assert.Equal([2L, 5L], actual.Select(static row => row.Value.Fields!["Part"].Int64));
        Assert.Equal(reference.Length, actual.Count);
        for (var index = 0; index < reference.Length; index++)
        {
            Assert.True(ObservationValueSemantics.Equals(reference[index].Value, actual[index].Value));
            Assert.Equal(reference[index].InputOccurrences.Select(static row => (row.Binding, row.ObservationIdentity)).OrderBy(static row => row.Binding.Value),
                actual[index].Occurrences.Select(static row => (row.Binding, row.ObservationIdentity)).OrderBy(static row => row.Binding.Value));
        }
        var roundTrip = JsonSerializer.Deserialize<SqliteRelationQueryStorageBinding>(JsonSerializer.Serialize(storage))!;
        Assert.Equal(storage.Fingerprint, roundTrip.Fingerprint);
        Assert.Equal(artifact.Fingerprint, Assert.Single(new SqliteRelationQueryCompiler().Compile(request, roundTrip).Artifacts).Fingerprint);
    }

    [Theory]
    [InlineData(false, true, "SQLITE_REL_ORDER_ENCODING")]
    public void MissingOrderingOrGuardEvidenceFailsClosed(bool ascii, bool guard, string code)
    {
        var (_, request, storage) = Build(ascii: ascii, guard: guard);
        var result = new SqliteRelationQueryCompiler().Compile(request, storage);
        Assert.False(result.IsSuccessful);
        Assert.Contains(result.BoundRealization.Evidence.Assessments, assessment => assessment.AdapterDecisionCode?.Value == code);
    }

    [Fact]
    public void CanonicalValidationRejectsUnguardedMissingComparison() =>
        Assert.Throws<ArgumentException>(() => Build(guard: false));

    [Fact]
    public void JoinMultiplicationCannotClaimTheLeftKeyIsUnique()
    {
        var (_, request, storage) = Build(uniqueJoin: false);
        var result = new SqliteRelationQueryCompiler().Compile(request, storage);
        Assert.False(result.IsSuccessful);
        Assert.Contains(result.BoundRealization.Evidence.Assessments, assessment => assessment.AdapterDecisionCode?.Value == "SQLITE_REL_UNIQUE_ORDER");
    }

    [Fact]
    public void IdentityEncodingSeparatesTypesAndComponentBoundaries()
    {
        static string Encode(params ObservationValue[] values) => SqliteRelationQueryOccurrenceColumn.EncodeIdentity(values);
        Assert.NotEqual(Encode(S("a/b"), S("c")), Encode(S("a"), S("b/c")));
        Assert.NotEqual(Encode(S("1"), S("2")), Encode(N(1), N(2)));
        Assert.Equal("0", Encode(N(0)));
        Assert.Equal("a/b", Encode(S("a/b")));
        Assert.Throws<ArgumentException>(() => Encode(ObservationValue.Null));
        Assert.Throws<ArgumentException>(() => Encode());
    }

    static (CompiledRelationQueryPlan Plan, RelationQueryBoundRealizationRequest Request, SqliteRelationQueryStorageBinding Storage)
        Build(bool ascii = true, bool guard = true, bool uniqueJoin = true)
    {
        Expr F(ValueBindingId binding, FieldPath path) => Expr.Field(binding, path);
        var join = new QueryNodeId("joined");
        var winner = new QueryNodeId("winner");
        var eligible = new QueryNodeId("eligible");
        var projected = new QueryNodeId("result");
        var ordered = new QueryNodeId("ordered");
        var compare = Expr.Le(F(Right, Score), Expr.Const(10L));
        var predicate = guard ? Expr.And(Expr.Eq(F(Right, Id), F(Left, Link)),
            Expr.And(Expr.Ne(F(Right, Score), Expr.Null()), compare)) : compare;
        LogicalQueryNode[] nodes =
        [
            new SourceQueryNode(new("items"), Left, Shape), new SourceQueryNode(new("details"), Right, Shape),
            new JoinQueryNode(join, new("items"), new("details"), JoinKind.Left,
                uniqueJoin ? Expr.Eq(F(Left, Link), F(Right, Id)) : Expr.Eq(F(Left, Part), F(Right, Part))),
            new SelectRepresentativeQueryNode(winner, join, [F(Left, Part)],
                [new(F(Left, Score), QuerySortDirection.Descending), new(F(Left, Id), QuerySortDirection.Descending)]),
            new FilterQueryNode(eligible, winner, predicate),
            new ProjectQueryNode(projected, eligible, new("output"), ResultShape,
                [new(new("id"), Id, F(Left, Id)), new(new("part"), Part, F(Left, Part)), new(new("score"), Score, F(Right, Score))]),
            new OrderQueryNode(ordered, projected, [new(Expr.Field(new("output"), Part))])
        ];
        var document = RelationQueryDocument.FromDefinition(new QueryDefinition(new("keys"), new("Keys"),
            new([.. nodes]), [new RowsQueryResultDefinition(new("rows"), ordered)]));
        var shapes = ShapeGraphDocument.FromGraph(new(Shape.GraphId,
        [
            new(Shape.ShapeId, [new(new("Id"), Text), new(new("Part"), Integer),
                new(new("Link"), Text, nullability: FieldNullability.Nullable), new(new("Score"), Integer, nullability: FieldNullability.Nullable)]),
            new(ResultShape.ShapeId, [new(new("Id"), Text), new(new("Part"), Integer),
                new(new("Score"), Integer, presence: FieldPresence.Optional, nullability: FieldNullability.Nullable)])
        ]));
        var compiled = RelationQueryStaticCompiler.Compile(new(document, [shapes]));
        Assert.True(compiled.IsSuccessful, string.Join("\n", compiled.Validation.Diagnostics));
        var plan = compiled.Plan!;
        var feasibility = RelationQueryRealizationCompiler.Compile(plan, SqliteRelationQueryTargetProfile.Default,
            SqliteRelationQueryTargetProfile.Policy, RelationQueryResultObservability.ExactContributors);
        var instance = new RelationQuerySourceInstanceId("database");
        var placements = plan.InputContract.Sources.Select(source => new RelationQuerySourcePlacementBinding(
            new(source.Binding.Value), source.Input.Id, source.Node, source.Binding, source.Shape, instance,
            RelationQuerySourcePlacementBindingKind.SourceSet, RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            RelationQuerySourcePlacementOrigin.Explicit,
            new(source.Shape, source.Binding == Left ? "primary-key" : "Id", source.Binding == Left ? null : Id),
            [.. source.Fields.Select(field => new RelationQuerySourceFieldBinding(field.Input.Id, field.Input.Field.Path, field.Input.Field.Path.ToString()))])).ToImmutableArray();
        var placement = new RelationQuerySourcePlacement(RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan), SqliteRelationQueryTargetProfile.ConventionSet,
            [new(instance, new("database"), SqliteRelationQueryTargetProfile.Default, new(100, 100, 100, 1))], placements);
        var tables = placements.Select(p => new SqliteRelationQueryTableBinding(p.Id, p.Binding.Value, "tests/key-schema-v1",
            IdentityFields: p.Binding == Left ? [p.Fields.Single(f => f.SemanticPath == Id).Input, p.Fields.Single(f => f.SemanticPath == Part).Input] : [],
            AsciiOrderingFields: ascii ? [.. p.Fields.Where(f => f.SemanticPath == Id).Select(f => f.Input)] : [])).ToImmutableArray();
        return (plan, new(plan, feasibility, placement), new(placement, tables));
    }

    static RelationQueryRuntimeEvidence Evidence(CompiledRelationQueryPlan plan, Dictionary<ValueBindingId, ObservationValue[]> rows)
    {
        List<RelationQuerySourceEvidence> sources = [];
        List<RelationQueryFieldEvidence> fields = [];
        foreach (var source in plan.InputContract.Sources)
        {
            var values = rows[source.Binding];
            var occurrences = values.Select((row, index) => new RelationQueryObservationOccurrence(new(source.Binding.Value + "/" + index),
                source.Binding, source.Shape, SqliteRelationQueryOccurrenceColumn.EncodeIdentity(source.Binding == Left
                    ? [row.Fields!["Id"], row.Fields["Part"]] : [row.Fields!["Id"]]))).ToImmutableArray();
            sources.Add(new(source.Input.Id, RelationQuerySourceEvidenceState.Provided, occurrences));
            foreach (var field in source.Fields)
                for (var index = 0; index < values.Length; index++)
                {
                    var value = values[index].Fields![field.Input.Field.Path.ToString()];
                    fields.Add(new(field.Input.Id, occurrences[index].Id,
                        value.Kind == ObservationValueKind.Null ? RelationQueryFieldEvidenceState.Null : RelationQueryFieldEvidenceState.Value,
                        value.Kind == ObservationValueKind.Null ? null : value));
                }
        }
        return new(new("keys/run"), plan, sources: [.. sources], fields: [.. fields], capabilities:
            [.. plan.RequirementGraph.Inputs.OfType<RelationQueryCapabilityInput>().Select(input =>
                new RelationQueryCapabilityEvidence(input.Id, RelationQueryCapabilityEvidenceState.Available))]);
    }

    static void Load(SqliteConnection connection, Dictionary<ValueBindingId, ObservationValue[]> rows)
    {
        foreach (var (binding, values) in rows)
        {
            using var schema = connection.CreateCommand();
            schema.CommandText = $"CREATE TABLE {binding.Value} (Id TEXT NOT NULL, Part INTEGER NOT NULL, Link TEXT, Score INTEGER, PRIMARY KEY ({(binding == Left ? "Id, Part" : "Id")})) STRICT;";
            schema.ExecuteNonQuery();
            foreach (var row in values)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = $"INSERT INTO {binding.Value} VALUES ($id,$part,$link,$score);";
                foreach (var (name, value) in row.Fields!)
                    insert.Parameters.AddWithValue("$" + name.ToLowerInvariant(), value.Kind switch
                    {
                        ObservationValueKind.Int64 => value.Int64,
                        ObservationValueKind.String => value.String!, _ => (object)DBNull.Value
                    });
                insert.ExecuteNonQuery();
            }
        }
    }

    static ObservationValue Row(string id, long part, string? link, long? score) => ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        { ["Id"] = S(id), ["Part"] = N(part), ["Link"] = link is null ? ObservationValue.Null : S(link), ["Score"] = score is null ? ObservationValue.Null : N(score.Value) });
    static ObservationValue S(string value) => ObservationValue.FromString(value);
    static ObservationValue N(long value) => ObservationValue.FromInt64(value);
}
