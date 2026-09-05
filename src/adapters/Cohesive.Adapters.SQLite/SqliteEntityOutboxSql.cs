using Cohesive.Adapters.Sql;

namespace Cohesive.Adapters.SQLite;

// One immutable construction authority for the auxiliary schema and all of its DML.
internal sealed class SqliteEntityOutboxSql
{
    internal const int Direct = 1;
    internal const int Process = 2;
    internal const int MaximumReadCommits = 1000;
    internal const string Id = "id", Kind = "kind", Content = "content", Hash = "hash", Sequence = "sequence", Receipt = "receipt";
    static readonly SqliteSqlDialect Dialect = SqliteSqlDialect.Instance;

    internal SqliteEntityOutboxSql(string entityTable)
    {
        ReceiptsTable = entityTable + "__receipts";
        EmissionsTable = entityTable + "__emissions";
        CreationsTable = entityTable + "__creations";
        InitialMigration = new(version: 1,
        [
            $"CREATE TABLE {Quote(ReceiptsTable)} ({Quote(Sequence)} INTEGER PRIMARY KEY AUTOINCREMENT, {Quote(Id)} TEXT NOT NULL UNIQUE, {Quote(Kind)} INTEGER NOT NULL CHECK ({Quote(Kind)} IN ({Direct}, {Process})), {Quote(Content)} BLOB NOT NULL, {Quote(Hash)} TEXT NOT NULL) STRICT",
            IndexTable(EmissionsTable),
            IndexTable(CreationsTable),
            $"CREATE INDEX {Quote(ReceiptsTable + "__outbox_cursor")} ON {Quote(ReceiptsTable)} ({Quote(Kind)}, {Quote(Sequence)})"
        ]);
        ReadReceipt = Read(ReceiptsTable, Id, Kind, Content, Hash);
        ReadEmission = Read(EmissionsTable, Id, Receipt);
        ReadCreation = Read(CreationsTable, Id, Receipt);
        InsertReceipt = Insert(ReceiptsTable, Id, Kind, Content, Hash);
        InsertEmission = Insert(EmissionsTable, Id, Receipt);
        InsertCreation = Insert(CreationsTable, Id, Receipt);
        var query = new SqlSelectBuilder(new SqlQualifiedTable(ReceiptsTable), "r");
        foreach (var column in new[] { Sequence, Id, Kind, Content, Hash })
            query.Select(SqlExpression.UnqualifiedColumn(column), column);
        query.Where(Match(Kind));
        query.Where(SqlExpression.Binary(SqlBinaryOperator.GreaterThan,
            SqlExpression.UnqualifiedColumn(Sequence), SqlExpression.RuntimeParameter(Sequence)));
        ReadOutbox = query.OrderBy(SqlExpression.UnqualifiedColumn(Sequence)).Limit(MaximumReadCommits).BuildTemplate(Dialect);
    }

    internal string ReceiptsTable { get; }
    internal string EmissionsTable { get; }
    internal string CreationsTable { get; }
    internal SqliteMigration InitialMigration { get; }
    internal SqlCommandTemplate ReadReceipt { get; }
    internal SqlCommandTemplate ReadEmission { get; }
    internal SqlCommandTemplate ReadCreation { get; }
    internal SqlCommandTemplate InsertReceipt { get; }
    internal SqlCommandTemplate InsertEmission { get; }
    internal SqlCommandTemplate InsertCreation { get; }
    internal SqlCommandTemplate ReadOutbox { get; }

    string IndexTable(string table) =>
        $"CREATE TABLE {Quote(table)} ({Quote(Id)} TEXT PRIMARY KEY NOT NULL, {Quote(Receipt)} TEXT NOT NULL REFERENCES {Quote(ReceiptsTable)} ({Quote(Id)})) STRICT";

    static SqlCommandTemplate Read(string table, string key, params string[] columns)
    {
        var query = new SqlSelectBuilder(new SqlQualifiedTable(table), "r");
        foreach (var column in columns) query.Select(SqlExpression.UnqualifiedColumn(column), column);
        return query.Where(Match(key)).BuildTemplate(Dialect);
    }

    static SqlCommandTemplate Insert(string table, params string[] columns)
    {
        var insert = new SqlInsertBuilder(new(table));
        foreach (var column in columns) insert.Value(column, SqlExpression.RuntimeParameter(column));
        return insert.BuildTemplate(Dialect);
    }

    static SqlExpression Match(string column) => SqlExpression.Binary(SqlBinaryOperator.Equal,
        SqlExpression.UnqualifiedColumn(column), SqlExpression.RuntimeParameter(column));
    static string Quote(string identifier) => new SqlIdentifier(identifier).ToSql(Dialect);
}
