using Cohesive.Adapters.Sql;

namespace Cohesive.Adapters.SQLite;

static class SqliteStorageCommitSql
{
    const string Table = "__cohesive_storage_commits_v1";
    internal const string ItemKind = "item";
    internal const string ReceiptKind = "receipt";
    internal const string Target = "target";
    internal const string Partition = "partition";
    internal const string Id = "id";
    internal const string Kind = "kind";
    internal const string Payload = "payload";
    internal const string Token = "token";
    internal const string Expected = "expected";
    static readonly string[] Keys = [Target, Partition, Id, Kind];
    internal static readonly SqliteSchema Schema = new("cohesive.storage.commits/v1", [new(1, [$"""
        CREATE TABLE {SqliteDatabase.QuoteIdentifier(Table)} (
            {SqliteDatabase.QuoteIdentifier(Target)} TEXT NOT NULL,
            {SqliteDatabase.QuoteIdentifier(Partition)} TEXT NOT NULL,
            {SqliteDatabase.QuoteIdentifier(Id)} TEXT NOT NULL,
            {SqliteDatabase.QuoteIdentifier(Kind)} TEXT NOT NULL,
            {SqliteDatabase.QuoteIdentifier(Payload)} TEXT NOT NULL,
            {SqliteDatabase.QuoteIdentifier(Token)} TEXT NOT NULL,
            PRIMARY KEY ({string.Join(", ", Keys.Select(SqliteDatabase.QuoteIdentifier))})
        ) STRICT;
        """])]);
    internal static readonly SqliteCommandTemplate Read = BuildRead();
    internal static readonly SqliteCommandTemplate Create = BuildCreate();
    internal static readonly SqliteCommandTemplate Replace = BuildReplace();

    static SqliteCommandTemplate BuildRead()
    {
        var builder = new SqlSelectBuilder(new SqlQualifiedTable(Table), "item");
        builder.Select(SqlExpression.UnqualifiedColumn(Payload), Payload);
        builder.Select(SqlExpression.UnqualifiedColumn(Token), Token);
        foreach (var key in Keys) builder.Where(Match(key, key));
        return new(builder.BuildTemplate(SqliteSqlDialect.Instance));
    }
    static SqliteCommandTemplate BuildCreate()
    {
        var builder = new SqlInsertBuilder(new(Table));
        foreach (var key in Keys) builder.Value(key, SqlExpression.RuntimeParameter(key));
        builder.Value(Payload, SqlExpression.RuntimeParameter(Payload));
        builder.Value(Token, SqlExpression.RuntimeParameter(Token));
        builder.OnConflictDoNothing(Keys);
        return new(builder.BuildTemplate(SqliteSqlDialect.Instance));
    }
    static SqliteCommandTemplate BuildReplace()
    {
        var builder = new SqlUpdateBuilder(new(Table));
        builder.Set(Payload, SqlExpression.RuntimeParameter(Payload));
        builder.Set(Token, SqlExpression.RuntimeParameter(Token));
        foreach (var key in Keys) builder.Where(Match(key, key));
        builder.Where(Match(Token, Expected));
        return new(builder.BuildTemplate(SqliteSqlDialect.Instance));
    }
    static SqlExpression Match(string column, string parameter) => SqlExpression.Binary(SqlBinaryOperator.Equal,
        SqlExpression.UnqualifiedColumn(column), SqlExpression.RuntimeParameter(parameter));
}
