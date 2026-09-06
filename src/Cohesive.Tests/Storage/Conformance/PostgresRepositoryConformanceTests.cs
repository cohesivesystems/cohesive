using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Sql;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Npgsql;

namespace Cohesive.Tests.Storage.Conformance;

public sealed class PostgresRepositoryConformanceTests
{
    const string ConnectionVariable = "COHESIVE_POSTGRES_TEST_CONNECTION_STRING";

    [PostgresTheory]
    [MemberData(nameof(EntityRepositoryConformance.BasicCases), MemberType = typeof(EntityRepositoryConformance))]
    public async Task Conforms(RepositoryProbe probe)
    {
        await using var dataSource = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var schema = "adoption_" + Guid.NewGuid().ToString("N");
        var mapping = new PostgresEntityRepositoryMapping(new SqlQualifiedTable(schema, "controls"),
        [
            new(nameof(RunControl.Id), "Id", PostgresRelationQueryScalarType.Text),
            new(nameof(RunControl.Tenant), "Tenant", PostgresRelationQueryScalarType.Text),
            new(nameof(RunControl.Status), "Status", PostgresRelationQueryScalarType.Text),
            new(nameof(RunControl.Attempt), "Attempt", PostgresRelationQueryScalarType.Int64),
            new(nameof(RunControl.Enabled), "Enabled", PostgresRelationQueryScalarType.Boolean),
            new(nameof(RunControl.Limit), "Limit", PostgresRelationQueryScalarType.Numeric),
            new(nameof(RunControl.ScheduledAt), "ScheduledAt", PostgresRelationQueryScalarType.TimestampWithTimeZone),
            new(nameof(RunControl.InputDigest), "InputDigest", PostgresRelationQueryScalarType.Bytea)
        ], identityField: nameof(RunControl.Id), partitionField: nameof(RunControl.Tenant));
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection)) await create.ExecuteNonQueryAsync();
        try
        {
            await using (var create = new NpgsqlCommand($"""
                CREATE TABLE {schema}.controls (
                    "Id" text NOT NULL, "Tenant" text NOT NULL, "Status" text NOT NULL,
                    "Attempt" bigint NOT NULL, "Enabled" boolean NOT NULL, "Limit" numeric NOT NULL,
                    "ScheduledAt" timestamptz NOT NULL, "InputDigest" bytea NOT NULL,
                    observation_version bigint NOT NULL, PRIMARY KEY ("Tenant", "Id"))
                """, connection)) await create.ExecuteNonQueryAsync();
            var repository = new PostgresEntityRepository(RunControlFixture.Entity,
                new(new("adoption/postgres"), dataSource, "cohesive.conformance"), mapping);
            await EntityRepositoryConformance.Verify(repository, probe);
        }
        finally
        {
            // The random schema belongs exclusively to this test invocation.
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    sealed class PostgresTheoryAttribute : TheoryAttribute
    {
        public PostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to run PostgreSQL repository conformance.";
        }
    }
}
