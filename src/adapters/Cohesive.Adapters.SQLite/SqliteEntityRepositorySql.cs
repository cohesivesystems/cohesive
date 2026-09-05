using System.Collections.Immutable;
using Cohesive.Adapters.Sql;
using static Cohesive.Adapters.SQLite.SqliteEntityRepositoryMapping;

namespace Cohesive.Adapters.SQLite;

internal sealed record SqliteEntityRepositorySql(
    SqlCommandTemplate ReadByIdentity,
    SqlCommandTemplate ReadByIdentityAndPartition,
    SqlCommandTemplate Upsert,
    SqlCommandTemplate Replace,
    ImmutableDictionary<string, int> FieldIndexByBinding)
{
    internal const string IdentityBinding = "id";
    internal const string PartitionBinding = "partition";
    internal const string VersionBinding = "version";
    internal const string TokenBinding = "token";
    internal const string GraphBinding = "graph";
    internal const string ShapeBinding = "shape";
    internal const string ExpectedConcurrencyBinding = "expected";
    const string SourceAlias = "entity";

    internal static SqliteEntityRepositorySql Create(SqliteEntityRepositoryMapping mapping)
    {
        var table = new SqlQualifiedTable(mapping.TableName);
        var insert = new SqlInsertBuilder(table);
        var replace = new SqlUpdateBuilder(table);
        var updatedColumns = new List<string>(mapping.Bindings.Length + 2);
        var fieldIndices = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < mapping.Bindings.Length; index++)
        {
            var binding = mapping.Bindings[index];
            var column = mapping.FieldColumns[binding.Field.Name.Value];
            insert.Value(column, SqlExpression.RuntimeParameter(binding.Parameter));
            replace.Set(column, SqlExpression.RuntimeParameter(binding.Parameter));
            updatedColumns.Add(column);
            fieldIndices.Add(binding.Parameter, index);
        }
        foreach (var (column, binding) in new[] { (VersionColumn, VersionBinding), (TokenColumn, TokenBinding), (GraphColumn, GraphBinding), (ShapeColumn, ShapeBinding) })
            insert.Value(column, SqlExpression.RuntimeParameter(binding));
        replace.Set(VersionColumn, SqlExpression.RuntimeParameter(VersionBinding));
        replace.Set(TokenColumn, SqlExpression.RuntimeParameter(TokenBinding));
        updatedColumns.Add(VersionColumn);
        updatedColumns.Add(TokenColumn);
        var sameShape = SqlExpression.Binary(SqlBinaryOperator.And, Match(GraphColumn, GraphBinding), Match(ShapeColumn, ShapeBinding));
        string[] keys = mapping.IdentityField == mapping.PartitionField
            ? [mapping.FieldColumns[mapping.IdentityField]]
            : [mapping.FieldColumns[mapping.PartitionField], mapping.FieldColumns[mapping.IdentityField]];
        insert.OnConflictDoUpdate(keys, updatedColumns, predicate: sameShape);
        insert.Returning(SqlExpression.UnqualifiedColumn(TokenColumn), TokenColumn);
        replace.Where(Match(mapping.FieldColumns[mapping.IdentityField], IdentityBinding));
        replace.Where(Match(mapping.FieldColumns[mapping.PartitionField], PartitionBinding));
        replace.Where(sameShape);
        replace.Where(Match(TokenColumn, ExpectedConcurrencyBinding));
        replace.Returning(SqlExpression.UnqualifiedColumn(TokenColumn), TokenColumn);
        return new(Read(mapping, table, partition: false), Read(mapping, table, partition: true),
            insert.BuildTemplate(SqliteSqlDialect.Instance), replace.BuildTemplate(SqliteSqlDialect.Instance), fieldIndices.ToImmutable());
    }

    static SqlCommandTemplate Read(SqliteEntityRepositoryMapping mapping, SqlQualifiedTable table, bool partition)
    {
        var query = new SqlSelectBuilder(table, SourceAlias);
        foreach (var binding in mapping.Bindings)
        {
            var column = mapping.FieldColumns[binding.Field.Name.Value];
            query.Select(SqlExpression.Column(SourceAlias, column), column);
        }
        foreach (var column in new[] { VersionColumn, TokenColumn, GraphColumn, ShapeColumn })
            query.Select(SqlExpression.Column(SourceAlias, column), column);
        query.Where(Match(mapping.FieldColumns[mapping.IdentityField], IdentityBinding));
        if (partition) query.Where(Match(mapping.FieldColumns[mapping.PartitionField], PartitionBinding));
        return query.Limit(2).BuildTemplate(SqliteSqlDialect.Instance);
    }

    static SqlExpression Match(string column, string binding) => SqlExpression.Binary(SqlBinaryOperator.Equal,
        SqlExpression.UnqualifiedColumn(column), SqlExpression.RuntimeParameter(binding));
}
