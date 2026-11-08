using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresContributingRowsTests
{
    sealed class NativeTheoryAttribute : TheoryAttribute
    {
        public NativeTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")))
                Skip = "Requires a disposable PostgreSQL database.";
        }
    }

    [NativeTheory]
    [InlineData(1)]
    public void ReaderRejectsDifferentShapeRevisionWithTheSameQualifiedIds(int count)
    {
        var connection = Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!;
        using var original = new PostgresContributingRowsFixture(connection, count);
        using var revised = new PostgresContributingRowsFixture(connection, count, nameNullability: FieldNullability.NonNullable);
        Assert.Equal(original.Shape.QualifiedId, revised.Shape.QualifiedId);
        Assert.Throws<ArgumentException>(() => new PostgresQueryRowsReader(original.Artifact, original.Runtime,
            revised.Plan, maximumRows: 32, maximumBytes: 32_000_000));
    }

    [NativeTheory]
    [InlineData(1)]
    public async Task NullHiddenIdentityFailsWithoutPublishingPartialRows(int count)
    {
        using var fixture = new PostgresContributingRowsFixture(Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!, count);
        var table = fixture.Artifact.StorageBinding.Tables.Single(table => table.TableName == "right_rows");
        using var corrupt = fixture.Runtime.DataSource.CreateCommand($"ALTER TABLE \"{table.SchemaName}\".right_rows DROP CONSTRAINT right_rows_pkey; ALTER TABLE \"{table.SchemaName}\".right_rows ALTER COLUMN id DROP NOT NULL; INSERT INTO \"{table.SchemaName}\".right_rows VALUES (NULL, 1, 'invalid')");
        await corrupt.ExecuteNonQueryAsync();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Reader.ReadRowsAsync(new Dictionary<QueryParameterId, ObservationValue>()));
        Assert.Contains("Contributor identity", failure.Message);
    }

    [NativeTheory]
    [InlineData(JoinKind.Inner, 6, false, FieldNullability.Nullable)]
    [InlineData(JoinKind.Left, 6, false, FieldNullability.Nullable)]
    [InlineData(JoinKind.Left, 6, false, FieldNullability.NonNullable)]
    [InlineData(JoinKind.Left, 1, false, FieldNullability.Nullable)]
    [InlineData(JoinKind.Left, 32, true, FieldNullability.Nullable)]
    public async Task NativeOrdinalRowsMatchReferenceContributorsAndMissingNullSemantics(JoinKind kind, int count, bool sameShape, FieldNullability nameNullability)
    {
        using var fixture = new PostgresContributingRowsFixture(Environment.GetEnvironmentVariable("COHESIVE_POSTGRES_TEST_CONNECTION_STRING")!, count, kind, sameShape, nameNullability);
        var actual = await fixture.Reader.ReadRowsAsync(new Dictionary<QueryParameterId, ObservationValue>());
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, fixture.Reference.Status);
        var expected = Assert.Single(fixture.Reference.QueryResults).Rows;
        string Key(RelationQueryOutputRow row) => row.Value.Fields!["Id"].GetInt64() + ":" +
            string.Join("|", row.InputOccurrences.Select(item => $"{item.Binding.Value}/{item.ObservationIdentity}").Order(StringComparer.Ordinal));
        Assert.Equal(expected.Length, actual.Length);
        var reference = expected.OrderBy(Key).ToArray(); var native = actual.OrderBy(Key).ToArray();
        for (var index = 0; index < actual.Length; index++)
        {
            Assert.Equal(Key(reference[index]), Key(native[index]));
            Assert.Equal(reference[index].Value, native[index].Value);
            Assert.Equal(reference[index].Identity, native[index].Identity);
            Assert.Equal(reference[index].InputOccurrences.Select(item => (item.Binding, item.Shape, item.ObservationIdentity)).OrderBy(item => item.Binding.Value),
                native[index].InputOccurrences.Select(item => (item.Binding, item.Shape, item.ObservationIdentity)).OrderBy(item => item.Binding.Value));
            Assert.IsAssignableFrom<IOrdinalObservationFieldReader>(native[index].Value.Fields);
        }
        Assert.Equal(2, fixture.Artifact.Contributors.Length);
        var json = PostgresRelationQueryArtifactJsonSerializer.Serialize(fixture.Artifact, indented: false);
        var roundtrip = PostgresRelationQueryArtifactJsonSerializer.DeserializeTrusted(json);
        Assert.Equal(fixture.Artifact.Fingerprint, roundtrip.Fingerprint);
        Assert.Equal(json, PostgresRelationQueryArtifactJsonSerializer.Serialize(roundtrip, indented: false));
        var canonical = await fixture.Reader.ReadRowsCoreAsync(new Dictionary<QueryParameterId, ObservationValue>(), ordinalStorage: false);
        Assert.Equal(actual.OrderBy(Key).Select(Key), canonical.OrderBy(Key).Select(Key));
        var bounded = new PostgresQueryRowsReader(fixture.Artifact, fixture.Runtime, fixture.Plan, maximumRows: 1, maximumBytes: 32_000_000);
        if (actual.Length > 1) await Assert.ThrowsAsync<InvalidOperationException>(() => bounded.ReadRowsAsync(new Dictionary<QueryParameterId, ObservationValue>()));
        var bytes = new PostgresQueryRowsReader(fixture.Artifact, fixture.Runtime, fixture.Plan, maximumRows: 32, maximumBytes: 9);
        await Assert.ThrowsAsync<PostgresNpgsqlResultByteLimitExceededException>(() => bytes.ReadRowsAsync(new Dictionary<QueryParameterId, ObservationValue>()));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Reader.ReadRowsAsync(new Dictionary<QueryParameterId, ObservationValue>(), cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Reader.ReadAsync(new Dictionary<QueryParameterId, ObservationValue>()));
    }
}
