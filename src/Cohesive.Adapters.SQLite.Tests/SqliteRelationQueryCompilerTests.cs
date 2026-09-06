using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Model;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;
using Cohesive.Relations.TestFixtures;
using Microsoft.Data.Sqlite;
using static Cohesive.Relations.TestFixtures.RepresentativeSelectionFixture;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteRelationQueryCompilerTests
{
    [Fact]
    public void GeneratedSqlKeepsSemanticNamesAndMatchesThePublishedExample()
    {
        var plan = RepresentativeSelectionFixture.Compile(Document(filterAfter: true));
        var (request, binding) = Bind(plan);
        var artifact = Assert.Single(new SqliteRelationQueryCompiler().Compile(request, binding).Artifacts);
        var path = Path.Combine(AppContext.BaseDirectory, "representative-selection.sql");
        if (Environment.GetEnvironmentVariable("UPDATE_SQL_EXAMPLES") == "1")
            File.WriteAllText(path, artifact.Statement.Text + "\n");
        Assert.Equal(File.ReadAllText(path), artifact.Statement.Text + "\n");
        Assert.DoesNotContain("\"c0\"", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("representative_rank", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("candidate_Key_present", artifact.Statement.Text, StringComparison.Ordinal);
        Assert.Equal([3L], AssertEquivalent(plan,
            [new(1, S("a"), 5), new(2, S("a"), 8, Eligible: false), new(3, S("b"), 4)])
            .Select(static row => row.Value.Fields!["Id"].Int64));
    }

    [Theory]
    [InlineData(QuerySortDirection.Ascending, QueryNullPlacement.First)]
    [InlineData(QuerySortDirection.Descending, QueryNullPlacement.First)]
    [InlineData(QuerySortDirection.Ascending, QueryNullPlacement.Last)]
    [InlineData(QuerySortDirection.Descending, QueryNullPlacement.Last)]
    public void RepresentativeResultsMatchReferenceIncludingNullsMissingTiesAndProvenance(QuerySortDirection direction, QueryNullPlacement nulls)
    {
        var plan = RepresentativeSelectionFixture.Compile(Document(direction: direction, nullPlacement: nulls));
        Candidate[] rows = [new(1, S("a"), null), new(2, S("a"), 7), new(3, S("a"), 7), new(4, S("a"), 3),
            new(5, ObservationValue.Null, 6), new(6, ObservationValue.Undefined, 9), new(7, S("b"), 0),
            new(8, S("A"), 1), new(9, S("a\0b"), 2), new(0, S("zero"), 1)];
        AssertEquivalent(plan, rows);
        AssertEquivalent(plan, [.. rows.Reverse()]);
    }

    [Fact]
    public void PostSelectionFilterDoesNotFallBackToOlderEligibleCandidate()
    {
        var plan = RepresentativeSelectionFixture.Compile(Document(filterAfter: true));
        Candidate[] rows = [new(1, S("a"), 5), new(2, S("a"), 8, Eligible: false), new(3, S("b"), 4)];
        Assert.Equal([3L], AssertEquivalent(plan, rows).Select(static row => row.Value.Fields!["Id"].Int64));
    }

    [Fact]
    public void EmptyAndGlobalPartitionMatchReference()
    {
        var plan = RepresentativeSelectionFixture.Compile(Document(global: true));
        Assert.Empty(AssertEquivalent(plan, []));
        Assert.Single(AssertEquivalent(plan, [new(1, S("a"), 4), new(2, S("b"), 6)]));
    }

    [Fact]
    public void NonUniqueOrderingFailsBeforePostSelectionFiltering()
    {
        var plan = RepresentativeSelectionFixture.Compile(Document(tieBreaker: false, filterAfter: true));
        var (request, binding) = Bind(plan);
        var result = new SqliteRelationQueryCompiler().Compile(request, binding);
        Assert.False(result.IsSuccessful);
        Assert.Empty(result.Artifacts);
        Assert.Contains(result.BoundRealization.Evidence.Assessments,
            assessment => assessment.AdapterDecisionCode?.Value == "SQLITE_REL_UNIQUE_ORDER");
    }

    [Fact]
    public void MissingPresenceEvidenceFailsClosed()
    {
        var plan = RepresentativeSelectionFixture.Compile(Document());
        var (request, binding) = Bind(plan);
        var changed = new SqliteRelationQueryStorageBinding(binding.Placement, [binding.Tables[0] with { Presence = [] }]);
        var result = new SqliteRelationQueryCompiler().Compile(request, changed);
        Assert.False(result.IsSuccessful);
        Assert.Contains(result.BoundRealization.Evidence.Assessments,
            assessment => assessment.AdapterDecisionCode?.Value == "SQLITE_REL_PRESENCE");
    }

    [Fact]
    public void CompilationIsDeterministicAndPinnedToExactPlacement()
    {
        var plan = RepresentativeSelectionFixture.Compile(Document());
        var (request, binding) = Bind(plan);
        var compiler = new SqliteRelationQueryCompiler();
        var first = compiler.Compile(request, binding);
        Assert.True(first.IsSuccessful, Format(first.Diagnostics));
        var artifact = Assert.Single(first.Artifacts);
        Assert.Equal(artifact.Fingerprint, Assert.Single(compiler.Compile(request, binding).Artifacts).Fingerprint);
        Assert.Equal(first.BoundRealization.Fingerprint, compiler.Realize(request, binding).Fingerprint);
        Assert.Equal(binding.Fingerprint, artifact.Provenance.AdapterBinding.Fingerprint);
        var (otherRequest, _) = Bind(RepresentativeSelectionFixture.Compile(Document(global: true)));
        Assert.False(compiler.Compile(otherRequest, binding).IsSuccessful);
    }

    [Fact]
    public void StorageRoundTripVerifiesVersionAndFingerprintAndExportsInspectableProof()
    {
        var (request, binding) = Bind(RepresentativeSelectionFixture.Compile(Document()));
        var json = JsonSerializer.Serialize(binding);
        var reopened = JsonSerializer.Deserialize<SqliteRelationQueryStorageBinding>(json)!;
        Assert.Equal(binding.Fingerprint, reopened.Fingerprint);
        var artifact = Assert.Single(new SqliteRelationQueryCompiler().Compile(request, reopened).Artifacts);
        var inspection = artifact.ToJson();
        using var document = JsonDocument.Parse(inspection);
        Assert.Equal(artifact.SchemaVersion, document.RootElement.GetProperty("SchemaVersion").GetString());
        Assert.Equal(artifact.Fingerprint, document.RootElement.GetProperty("Fingerprint").GetString());
        Assert.False(document.RootElement.TryGetProperty("Command", out _));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<SqliteRelationQueryStorageBinding>(
            json.Replace("tests/candidate-schema-v1", "tests/changed-schema-v1", StringComparison.Ordinal)));
        Assert.Throws<ArgumentException>(() => new SqliteRelationQueryStorageBinding(binding.Placement, binding.Tables, schemaVersion: "future/v2"));
    }

    [Fact]
    public void NarrowOutputRetainsSelectionDemandAndWinningProvenance()
    {
        var compiled = RelationQueryStaticCompiler.Compile(new(Document(), Shapes,
            demand: RelationQueryCompilationDemand.ForQueryResults([QueryResultDemand.SelectedFields(new("rows"),
                [new(RepresentativeSelectionFixture.Shape, Id)])])));
        Assert.True(compiled.IsSuccessful);
        var actual = AssertEquivalent(compiled.Plan!, [new(1, S("a"), 3), new(2, S("a"), 8)]);
        Assert.Equal(2L, Assert.Single(actual).Value.Fields!["Id"].Int64);
        Assert.Single(actual[0].Value.Fields!);
        Assert.Equal("2", Assert.Single(actual[0].Occurrences).ObservationIdentity);
    }

    [Fact]
    public void NullProjectionLiteralUsesTheCanonicalTargetContract()
    {
        var original = Assert.IsType<QueryDefinition>(Document().Definition);
        QueryNodeId project = new("project");
        var plan = RepresentativeSelectionFixture.Compile(RelationQueryDocument.FromDefinition(original with
        {
            Body = new([.. original.Body.Nodes, new ProjectQueryNode(project, new("result-order"), new("output"), RepresentativeSelectionFixture.Shape,
                [new(new("id"), Id, Expr.Field(RepresentativeSelectionFixture.Binding, Id)),
                 new(new("key"), Key, new ConstantExpr(ObservationValue.Null)),
                 new(new("preference"), Preference, Expr.Field(RepresentativeSelectionFixture.Binding, Preference)),
                 new(new("eligible"), Eligible, Expr.Field(RepresentativeSelectionFixture.Binding, Eligible))])]),
            Results = [new RowsQueryResultDefinition(new("rows"), project)]
        }));
        AssertEquivalent(plan, [new(0, S("a"), 5)]);
    }

    [Theory]
    [InlineData(JoinKind.Inner)]
    [InlineData(JoinKind.Left)]
    public void JoinedRepresentativeProjectionMatchesPresenceAndWinningContributors(JoinKind kind)
    {
        var plan = JoinedPlan(kind, includeRightIdentity: true);
        ValueBindingId right = new("other");
        Dictionary<ValueBindingId, Candidate[]> rows = new()
        {
            [RepresentativeSelectionFixture.Binding] = [new(1, S("a"), 3), new(2, S("missing-match"), 4), new(3, ObservationValue.Null, 5)],
            [right] = [new(11, S("a"), 8), new(12, S("a"), 9), new(13, ObservationValue.Null, 6)]
        };
        var actual = AssertEquivalent(plan, rows);
        Assert.DoesNotContain(actual.SelectMany(static row => row.Occurrences), occurrence => occurrence.ObservationIdentity == "12");
        if (kind == JoinKind.Left)
        {
            var unmatched = actual.Single(row => row.Value.Fields!["Id"].Int64 == 2);
            Assert.False(unmatched.Value.Fields!.ContainsKey("Key"));
            Assert.Single(unmatched.Occurrences);
            Assert.Equal(ObservationValueKind.Null, actual.Single(row => row.Value.Fields!["Id"].Int64 == 3).Value.Fields!["Key"].Kind);
        }
    }

    [Fact]
    public void JoinMultiplicationInvalidatesSingleSourceIdentityTieBreaker()
    {
        var (request, binding) = Bind(JoinedPlan(JoinKind.Left, includeRightIdentity: false));
        var result = new SqliteRelationQueryCompiler().Compile(request, binding);
        Assert.False(result.IsSuccessful);
        Assert.Contains(result.BoundRealization.Evidence.Assessments,
            assessment => assessment.AdapterDecisionCode?.Value == "SQLITE_REL_UNIQUE_ORDER");
    }

    [Fact]
    public void ReusableParametersPreserveIndexedFilteringBeforeSelection()
    {
        var original = Assert.IsType<QueryDefinition>(Document().Definition);
        var filtered = new QueryNodeId("source-filter");
        var nodes = original.Body.Nodes.Select(node => node is SelectRepresentativeQueryNode representative
            ? representative with { Input = filtered } : node).ToList();
        nodes.Add(new FilterQueryNode(filtered, Source, Expr.And(
            Expr.Eq(Expr.Field(RepresentativeSelectionFixture.Binding, Key), new ParameterExpr("key")),
            Expr.Le(Expr.Field(RepresentativeSelectionFixture.Binding, Id), new ParameterExpr("cutoff")))));
        var plan = RepresentativeSelectionFixture.Compile(RelationQueryDocument.FromDefinition(original with
        {
            Body = new([.. nodes], [new(new("key"), new ScalarTypeRef(ScalarTypeKind.String)),
                new(new("cutoff"), new ScalarTypeRef(ScalarTypeKind.Int64))])
        }));
        var (request, binding) = Bind(plan);
        var compilation = new SqliteRelationQueryCompiler().Compile(request, binding);
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var artifact = Assert.Single(compilation.Artifacts);
        Candidate[] rows = [new(1, S("a"), 3), new(2, S("a"), 8), new(3, S("b"), 4)];
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        Load(fixture.Database, connection, "candidate", rows);
        using (var index = fixture.Database.CreateCommand(connection, null,
                   "CREATE INDEX candidate_order ON candidate(KeyPresent, Key COLLATE BINARY, Preference DESC, Id);"))
            index.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var scope = new SqliteCommandScope(fixture.Database, connection, transaction);
        var evidence = SourceEvidence(plan, new Dictionary<ValueBindingId, Candidate[]> { [RepresentativeSelectionFixture.Binding] = rows });
        foreach (var (key, cutoff, expected) in new[] { ("a", 1L, 1L), ("a", 2L, 2L), ("b", 3L, 3L) })
        {
            Dictionary<QueryParameterId, ObservationValue> values = new() { [new("key")] = S(key), [new("cutoff")] = ObservationValue.FromInt64(cutoff) };
            var reference = RelationQueryInMemoryInterpreter.Default.Execute(new(plan, new(new("parameters/run"), plan,
                sources: evidence.Sources, fields: evidence.Fields, capabilities: evidence.Capabilities,
                parameters: [.. plan.InputContract.Parameters.Select(p => new RelationQueryParameterEvidence(p.Input.Id,
                    RelationQueryParameterEvidenceState.Provided, values[p.Definition.Id]))])));
            Assert.Equal(RelationQueryExecutionStatus.Succeeded, reference.Status);
            using var reader = scope.ExecuteReader(artifact.Command, default, artifact.BindParameters(values));
            Assert.True(reader.Read());
            var actual = artifact.ReadCurrentRow(reader);
            Assert.Equal(expected, actual.Value.Fields!["Id"].Int64);
            Assert.True(ObservationValueSemantics.Equals(Assert.Single(Assert.Single(reference.QueryResults).Rows).Value, actual.Value));
            Assert.False(reader.Read());
        }
        using var explain = connection.CreateCommand();
        explain.Transaction = transaction;
        explain.CommandText = "EXPLAIN QUERY PLAN " + artifact.Statement.Text;
        var bound = artifact.Statement.Bind(SqliteSqlDialect.Instance, new Dictionary<string, object?> { ["key"] = "a", ["cutoff"] = 3L });
        foreach (var parameter in bound.Parameters) explain.Parameters.AddWithValue(parameter.Placeholder, parameter.Value ?? DBNull.Value);
        using var planReader = explain.ExecuteReader();
        List<string> details = [];
        while (planReader.Read()) details.Add(planReader.GetString(3));
        Assert.Contains(details, detail => detail.StartsWith("SEARCH ", StringComparison.Ordinal)
            && detail.Contains(" USING INDEX candidate_order", StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => artifact.BindParameters(new Dictionary<QueryParameterId, ObservationValue>()));
    }

    [Fact]
    public void TextOrderingWithoutAnExactOrdinalDomainIsRejected()
    {
        var original = Assert.IsType<QueryDefinition>(Document().Definition);
        foreach (var path in new[] { Key })
        {
            var plan = RepresentativeSelectionFixture.Compile(RelationQueryDocument.FromDefinition(original with
            {
                Body = new([.. original.Body.Nodes.Select(node => node is SelectRepresentativeQueryNode representative
                    ? representative with { Orderings = [new(Expr.Field(RepresentativeSelectionFixture.Binding, path)),
                        new(Expr.Field(RepresentativeSelectionFixture.Binding, Id))] } : node)])
            }));
            var (request, binding) = Bind(plan);
            var compilation = new SqliteRelationQueryCompiler().Compile(request, binding);
            Assert.False(compilation.IsSuccessful);
            Assert.Contains(compilation.BoundRealization.Evidence.Assessments,
                assessment => assessment.AdapterDecisionCode?.Value == "SQLITE_REL_ORDER_ENCODING");
        }
    }

    [Fact]
    public void NormalizationCollidingBindingsKeepSeparateValuesAndContributors()
    {
        ValueBindingId other = new("candidate!");
        var plan = JoinedPlan(JoinKind.Left, includeRightIdentity: true, other);
        var actual = AssertEquivalent(plan, new Dictionary<ValueBindingId, Candidate[]>
        {
            [RepresentativeSelectionFixture.Binding] = [new(1, S("a"), 3), new(2, S("missing"), 4)],
            [other] = [new(10, S("a"), 8), new(11, S("a"), 9)]
        });
        Assert.Equal(["candidate", "candidate!"], actual[0].Occurrences.Select(static occurrence => occurrence.Binding.Value));
        Assert.Equal("10", actual[0].Occurrences.Single(occurrence => occurrence.Binding == other).ObservationIdentity);
        Assert.Single(actual[1].Occurrences);
    }

    static CompiledRelationQueryPlan JoinedPlan(JoinKind kind, bool includeRightIdentity, ValueBindingId? other = null)
    {
        ValueBindingId left = RepresentativeSelectionFixture.Binding;
        ValueBindingId right = other ?? new("other");
        ValueBindingId output = new("projected");
        QueryNodeId join = new("join"), order = new("order"), project = new("project");
        List<QueryOrdering> ordering = [new(Expr.Field(left, Preference), QuerySortDirection.Descending), new(Expr.Field(left, Id))];
        if (includeRightIdentity) ordering.Add(new(Expr.Field(right, Id)));
        QueryDefinition query = new(new("joined-candidates"), new("JoinedCandidates"), new([
            new SourceQueryNode(Source, left, RepresentativeSelectionFixture.Shape), new SourceQueryNode(new("other-source"), right, RepresentativeSelectionFixture.Shape),
            new JoinQueryNode(join, Source, new("other-source"), kind, Expr.Eq(Expr.Field(left, Key), Expr.Field(right, Key))),
            new SelectRepresentativeQueryNode(Selection, join, [Expr.Field(left, Id)], [.. ordering]),
            new OrderQueryNode(order, Selection, [new(Expr.Field(left, Id)), new(Expr.Field(right, Id))]),
            new ProjectQueryNode(project, order, output, RepresentativeSelectionFixture.Shape,
                [new(new("id"), Id, Expr.Field(left, Id)), new(new("key"), Key, Expr.Field(right, Key)),
                 new(new("preference"), Preference, Expr.Field(left, Preference)), new(new("eligible"), Eligible, Expr.Field(left, Eligible))])
        ]), [new RowsQueryResultDefinition(new("rows"), project)]);
        return RepresentativeSelectionFixture.Compile(RelationQueryDocument.FromDefinition(query));
    }

    static ImmutableArray<SqliteRelationQueryRow> AssertEquivalent(CompiledRelationQueryPlan plan, Candidate[] rows) =>
        AssertEquivalent(plan, new Dictionary<ValueBindingId, Candidate[]> { [RepresentativeSelectionFixture.Binding] = rows });

    static ImmutableArray<SqliteRelationQueryRow> AssertEquivalent(CompiledRelationQueryPlan plan, IReadOnlyDictionary<ValueBindingId, Candidate[]> sources)
    {
        var (request, binding) = Bind(plan);
        var compilation = new SqliteRelationQueryCompiler().Compile(request, binding);
        Assert.True(compilation.IsSuccessful, Format(compilation.Diagnostics));
        var artifact = Assert.Single(compilation.Artifacts);
        var reference = RelationQueryInMemoryInterpreter.Default.Execute(new(plan, SourceEvidence(plan, sources)));
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, reference.Status);
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        foreach (var (sourceBinding, rows) in sources)
        {
            var table = binding.Tables.Single(table => table.Placement.Value == sourceBinding.Value).Table;
            Load(fixture.Database, connection, table, rows);
        }
        using var transaction = connection.BeginTransaction(deferred: true);
        using var scope = new SqliteCommandScope(fixture.Database, connection, transaction);
        using var reader = scope.ExecuteReader(artifact.Command, default);
        var actual = ImmutableArray.CreateBuilder<SqliteRelationQueryRow>();
        while (reader.Read()) actual.Add(artifact.ReadCurrentRow(reader));
        var expected = Assert.Single(reference.QueryResults).Rows;
        Assert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(ObservationValueSemantics.Equals(expected[index].Value, actual[index].Value),
                $"Row {index}: expected {expected[index].Value}; actual {actual[index].Value}");
            Assert.Equal(expected[index].InputOccurrences.Select(static occurrence => (occurrence.Binding.Value, occurrence.ObservationIdentity)).Order(),
                actual[index].Occurrences.Select(static occurrence => (occurrence.Binding.Value, occurrence.ObservationIdentity)).Order());
        }
        return actual.ToImmutable();
    }

    static void Load(SqliteDatabase database, SqliteConnection connection, string table, Candidate[] rows)
    {
        using (var schema = database.CreateCommand(connection, null,
                   $"CREATE TABLE \"{table}\" (Id INTEGER PRIMARY KEY, Key TEXT COLLATE NOCASE, KeyPresent INTEGER NOT NULL CHECK(KeyPresent IN (0,1)), Preference INTEGER, Eligible INTEGER NOT NULL CHECK(Eligible IN(0,1)), CHECK(KeyPresent=1 OR Key IS NULL)) STRICT;"))
            schema.ExecuteNonQuery();
        foreach (var row in rows)
        {
            using var insert = database.CreateCommand(connection, null,
                $"INSERT INTO \"{table}\" VALUES ($id, $key, $present, $preference, $eligible);",
                new("$id", row.Id), new("$key", (object?)(row.Key.Kind == ObservationValueKind.String ? row.Key.String : null) ?? DBNull.Value),
                new("$present", row.Key.Kind == ObservationValueKind.Undefined ? 0L : 1L),
                new("$preference", (object?)row.Preference ?? DBNull.Value), new("$eligible", row.Eligible ? 1L : 0L));
            insert.ExecuteNonQuery();
        }
    }

    static RelationQueryRuntimeEvidence SourceEvidence(CompiledRelationQueryPlan plan, IReadOnlyDictionary<ValueBindingId, Candidate[]> rows)
    {
        List<RelationQuerySourceEvidence> sources = [];
        List<RelationQueryFieldEvidence> fields = [];
        foreach (var source in plan.InputContract.Sources)
        {
            var occurrences = rows[source.Binding].Select(row => new RelationQueryObservationOccurrence(
                new(source.Binding.Value + "/" + row.Id), source.Binding, source.Shape,
                row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToImmutableArray();
            sources.Add(new(source.Input.Id, RelationQuerySourceEvidenceState.Provided, occurrences));
            foreach (var field in source.Fields)
            {
                for (var index = 0; index < rows[source.Binding].Length; index++)
                {
                    var row = rows[source.Binding][index];
                    var value = field.Input.Field.Path == Id ? ObservationValue.FromInt64(row.Id)
                        : field.Input.Field.Path == Key ? row.Key
                        : field.Input.Field.Path == Preference ? row.Preference is { } preference ? ObservationValue.FromInt64(preference) : ObservationValue.Null
                        : ObservationValue.FromBool(row.Eligible);
                    fields.Add(new(field.Input.Id, occurrences[index].Id,
                        value.Kind == ObservationValueKind.Undefined ? RelationQueryFieldEvidenceState.Missing
                        : value.Kind == ObservationValueKind.Null ? RelationQueryFieldEvidenceState.Null : RelationQueryFieldEvidenceState.Value,
                        value.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null ? null : value));
                }
            }
        }
        return new(new("sqlite/test-run"), plan, sources: [.. sources], fields: [.. fields],
            capabilities: [.. plan.RequirementGraph.Inputs.OfType<RelationQueryCapabilityInput>().Select(input =>
                new RelationQueryCapabilityEvidence(input.Id, RelationQueryCapabilityEvidenceState.Available))]);
    }

    internal static (RelationQueryBoundRealizationRequest Request, SqliteRelationQueryStorageBinding Binding) Bind(CompiledRelationQueryPlan plan)
    {
        var feasibility = RelationQueryRealizationCompiler.Compile(plan, SqliteRelationQueryTargetProfile.Default,
            SqliteRelationQueryTargetProfile.Policy, RelationQueryResultObservability.ExactContributors);
        Assert.True(feasibility.IsRealizable, Format(feasibility.Diagnostics));
        var sourceId = new RelationQuerySourceInstanceId("sqlite/test");
        var placements = plan.InputContract.Sources.Select(source => new RelationQuerySourcePlacementBinding(
            new(source.Binding.Value), source.Input.Id, source.Node, source.Binding, source.Shape, sourceId,
            RelationQuerySourcePlacementBindingKind.SourceSet, RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            RelationQuerySourcePlacementOrigin.Explicit, new(source.Shape, "Id", Id),
            [.. source.Fields.Select(field => new RelationQuerySourceFieldBinding(field.Input.Id, field.Input.Field.Path, field.Input.Field.Path.ToString()))])).ToImmutableArray();
        var placement = new RelationQuerySourcePlacement(RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan), SqliteRelationQueryTargetProfile.ConventionSet,
            [new(sourceId, new("sqlite/test-database"), SqliteRelationQueryTargetProfile.Default, new(10000, 10000, 10000, 1))], placements);
        var tables = placements.Select(p => new SqliteRelationQueryTableBinding(p.Id, p.Binding.Value, "tests/candidate-schema-v1",
            [.. p.Fields.Where(field => field.SemanticPath == Key).Select(field => new SqliteRelationQueryFieldPresence(field.Input, "KeyPresent"))])).ToImmutableArray();
        return (new(plan, feasibility, placement), new(placement, tables));
    }

    static ObservationValue S(string value) => ObservationValue.FromString(value);
    static string Format<T>(IEnumerable<T> values) => string.Join("\n", values);
}
