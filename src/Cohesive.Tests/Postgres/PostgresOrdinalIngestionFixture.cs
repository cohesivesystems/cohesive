using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Sql;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Npgsql;

namespace Cohesive.Tests.Postgres;

// Shared native-provider fixture. The caller supplies an isolated disposable database.
sealed class PostgresOrdinalIngestionFixture : IDisposable
{
    readonly string table = "ordinal_" + Guid.NewGuid().ToString("N");
    public NpgsqlConnection Connection { get; }
    public PostgresEntityRepository Repository { get; }
    public ObservationMaterializer<Row> Materializer { get; }
    public PostgresOrdinalIngestionFixture(string connectionString, int rowCount)
    {
        Shape shape = new(new("OrdinalRow"),
        [
            new(new("Id"), new ScalarTypeRef(ScalarTypeKind.String)),
            new(new("Tenant"), new ScalarTypeRef(ScalarTypeKind.String)),
            new(new("Amount"), new ScalarTypeRef(ScalarTypeKind.Decimal)),
            new(new("Payload"), new ScalarTypeRef(ScalarTypeKind.Bytes), nullability: FieldNullability.Nullable)
        ], role: ShapeRoles.Entity);
        var definition = new EntityDefinition(new("OrdinalRow"), shape);
        var mapping = new PostgresEntityRepositoryMapping(new SqlQualifiedTable("public", table),
        [
            new("Id", "id", PostgresRelationQueryScalarType.Text),
            new("Tenant", "tenant", PostgresRelationQueryScalarType.Text),
            new("Amount", "amount", PostgresRelationQueryScalarType.Numeric),
            new("Payload", "payload", PostgresRelationQueryScalarType.Bytea)
        ], identityField: "Id", partitionField: "Tenant");
        DataSource = NpgsqlDataSource.Create(connectionString);
        Repository = new(definition, new(new("ordinal-benchmark"), DataSource, "ordinal-benchmark"), mapping);
        var layout = ObservationLayout.Create(definition.StateShape, mapping.Fields.Select(field => field.FieldName));
        Materializer = ObservationMaterializer.For<Row>(definition.StateShape).Compile(layout);
        Connection = DataSource.OpenConnection();
        using var create = Connection.CreateCommand();
        create.CommandText = $"CREATE TABLE {table}(id text PRIMARY KEY, tenant text NOT NULL, amount numeric NOT NULL, payload bytea NULL, observation_version bigint NOT NULL)";
        create.ExecuteNonQuery();
        using var populate = Connection.CreateCommand();
        populate.CommandText = $"INSERT INTO {table} SELECT 'row-' || n, 'tenant', n::numeric / 4, CASE WHEN n % 3 = 0 THEN NULL ELSE decode(repeat('ab',256),'hex') END, n FROM generate_series(1, $1) n";
        populate.Parameters.AddWithValue(rowCount);
        populate.ExecuteNonQuery();
    }
    readonly NpgsqlDataSource DataSource;
    public NpgsqlCommand CreateCommand()
    {
        var command = Connection.CreateCommand();
        command.CommandText = $"SELECT id, tenant, amount, payload, observation_version, xmin FROM {table} ORDER BY id";
        return command;
    }
    public void Dispose()
    {
        using var drop = Connection.CreateCommand();
        drop.CommandText = $"DROP TABLE IF EXISTS {table}";
        drop.ExecuteNonQuery();
        Connection.Dispose();
        DataSource.Dispose();
    }
    public sealed record Row(string Id, string Tenant, decimal Amount, byte[]? Payload);
}
