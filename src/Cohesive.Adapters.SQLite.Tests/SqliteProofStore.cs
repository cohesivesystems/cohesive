using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Sql;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite.Tests;

// Deliberately bounded fixture persistence, not a definition registry or production journal API.
internal sealed class SqliteProofStore(SqliteDatabase database)
{
    internal static readonly JsonSerializerOptions Json = EntityStorageJson.CreateOptions();
    const string Table = "adoption_proof";
    internal void Initialize() => new SqliteSchema("adoption/proof", [new(1,
        [$"CREATE TABLE {Table} (id TEXT PRIMARY KEY, content BLOB NOT NULL, hash TEXT NOT NULL) STRICT"])]).Apply(database);

    internal void Put<T>(string id, T value) where T : class => PutBytes(id, StrictDocumentJson.GetCanonicalBytes(value, Json));
    internal void PutBytes(string id, byte[] bytes)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteProofCommands.Command(database, connection, null,
            new SqlInsertBuilder(new(Table)).Value("id", SqlExpression.Constant(id)).Value("content", SqlExpression.Constant(bytes))
                .Value("hash", SqlExpression.Constant(Convert.ToHexStringLower(SHA256.HashData(bytes))))
                .OnConflictDoUpdate(["id"], ["content", "hash"]).BuildTemplate(SqliteSqlDialect.Instance).Bind(SqliteSqlDialect.Instance));
        command.ExecuteNonQuery();
    }

    internal T Get<T>(string id) where T : class => JsonSerializer.Deserialize<T>(GetBytes(id), Json)!;
    internal byte[] GetBytes(string id)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteProofCommands.Command(database, connection, null,
            new SqlSelectBuilder(new SqlQualifiedTable(Table), "p").Select(SqlExpression.Column("p", "content"), "content")
                .Select(SqlExpression.Column("p", "hash"), "hash").Where(SqliteProofCommands.Equal("id", id)).Build(SqliteSqlDialect.Instance));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException($"Missing pinned proof record '{id}'.");
        var length = reader.GetBytes(0, 0, null, 0, 0);
        if (length > 16 * 1024 * 1024) throw new InvalidOperationException("Proof record exceeds its byte limit.");
        var bytes = new byte[checked((int)length)];
        reader.GetBytes(0, 0, bytes, 0, bytes.Length);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != reader.GetString(1)) throw new InvalidOperationException("Corrupt proof record.");
        return bytes;
    }

    internal void Delete(string id)
    {
        using var connection = database.OpenConnection();
        using var command = SqliteProofCommands.Command(database, connection, null,
            new SqlDeleteBuilder(new(Table)).Where(SqliteProofCommands.Equal("id", id)).BuildTemplate(SqliteSqlDialect.Instance).Bind(SqliteSqlDialect.Instance));
        command.ExecuteNonQuery();
    }

    internal ExecutionDefinitionDocumentCatalog LoadCatalog()
    {
        using var connection = database.OpenConnection();
        using var command = SqliteProofCommands.Command(database, connection, null,
            new SqlSelectBuilder(new SqlQualifiedTable(Table), "p").Select(SqlExpression.Column("p", "id"), "id").Build(SqliteSqlDialect.Instance));
        using var reader = command.ExecuteReader();
        List<ExecutionDefinitionDocument> documents = [];
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!id.StartsWith("definition/", StringComparison.Ordinal)) continue;
            documents.Add(ExecutionDefinitionJsonSerializer.Deserialize(Encoding.UTF8.GetString(GetBytes(id))));
        }
        Cohesive.ExecutionKernel.TestFixtures.Storage.RunControlFixture.Require(ExecutionDefinitionDocumentCatalog.TryCreate(documents, out var catalog));
        return catalog!;
    }

    internal static string DocumentKey(ExecutionDefinitionDocument document) => "definition/" + document.Metadata.DefinitionId.Value + "/" + document.Metadata.RevisionId.Value;
}

internal static class SqliteProofCommands
{
    internal static SqlExpression Equal(string column, object value) => SqlExpression.Binary(SqlBinaryOperator.Equal,
        SqlExpression.UnqualifiedColumn(column), SqlExpression.Constant(value));

    internal static SqliteCommand Command(SqliteDatabase database, SqliteConnection connection, SqliteTransaction? transaction, SqlStatement statement) =>
        database.CreateCommand(connection, transaction, statement.Text,
            statement.Parameters.Select(parameter => new SqliteParameter(parameter.Placeholder, parameter.Value)).ToArray());
}
