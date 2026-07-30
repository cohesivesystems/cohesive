using Cohesive.Adapters.Postgres;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresNpgsqlRuntimeBindingTests
{
    [Fact]
    public async Task BindingRetainsExactInstanceWhilePublishingSanitizedEvidence()
    {
        await using var first = NpgsqlDataSource.Create(
            "Host=localhost;Database=operations;Username=cohesive;Password=first-secret;Persist Security Info=true");
        await using var second = NpgsqlDataSource.Create(
            "Host=localhost;Database=operations;Username=cohesive;Password=second-secret;Persist Security Info=true");
        var database = new PostgresRelationQueryDatabaseId("operations-primary");

        var binding = new PostgresNpgsqlRuntimeBinding(
            database,
            first,
            "cohesive.tests/deployment/postgres-primary");
        var equivalentSettings = new PostgresNpgsqlRuntimeBinding(
            database,
            second,
            "cohesive.tests/deployment/postgres-primary");

        Assert.Equal(database, binding.Database);
        Assert.Equal("cohesive.tests/deployment/postgres-primary", binding.Authority);
        Assert.Equal("sha256", binding.DataSourceFingerprint.Algorithm);
        Assert.Equal(64, binding.DataSourceFingerprint.Value.Length);
        Assert.Equal(binding.DataSourceFingerprint, equivalentSettings.DataSourceFingerprint);
        Assert.True(binding.Matches(first));
        Assert.False(binding.Matches(second));
    }

    [Fact]
    public async Task BindingRejectsMultiHostDataSourcesAndCredentialAuthorities()
    {
        await using var multiHost = new NpgsqlDataSourceBuilder(
                "Host=primary,secondary;Database=operations;Username=cohesive;Password=not-used")
            .BuildMultiHost();
        var database = new PostgresRelationQueryDatabaseId("operations-primary");

        Assert.Throws<ArgumentException>(() => new PostgresNpgsqlRuntimeBinding(
            database,
            multiHost,
            "cohesive.tests/deployment/postgres-primary"));

        await using var singleHost = NpgsqlDataSource.Create(
            "Host=localhost;Database=operations;Username=cohesive;Password=not-used");
        Assert.Throws<ArgumentException>(() => new PostgresNpgsqlRuntimeBinding(
            default,
            singleHost,
            "cohesive.tests/deployment/postgres-primary"));
        Assert.Throws<ArgumentException>(() => new PostgresNpgsqlRuntimeBinding(
            database,
            singleHost,
            "Password=should-not-be-here"));
        Assert.Throws<ArgumentException>(() => new PostgresNpgsqlRuntimeBinding(
            database,
            singleHost,
            "Host=localhost;Username=cohesive;Pwd=should-not-be-here"));
    }
}
