using Cohesive.Adapters.Postgres;
using Cohesive.Processes.Distribution;
using Npgsql;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresProcessDistributionStoreTests
{
    [Fact]
    public async Task Capabilities_DeclareDurableCompetingConsumerGuaranteesAndCompositionLimit()
    {
        await using var dataSource = NpgsqlDataSource.Create(
            "Host=localhost;Database=unused;Username=unused;Password=unused");
        var store = new PostgresProcessDistributionStore(
            dataSource,
            new("authority/test"));

        Assert.True(store.Capabilities.IsDurable);
        Assert.True(store.Capabilities.SupportsAtomicClaim);
        Assert.True(store.Capabilities.SupportsCompareAndSwap);
        Assert.True(store.Capabilities.SupportsWorkerLeases);
        Assert.True(store.Capabilities.SupportsClaimRenewal);
        Assert.True(store.Capabilities.SupportsMonotonicFencing);
        Assert.True(store.Capabilities.SupportsRunnableDiscovery);
        Assert.True(store.Capabilities.SupportsCapacityReservations);
        Assert.True(store.Capabilities.SupportsPoisonWork);
        Assert.False(store.Capabilities.SupportsAtomicProcessCommit);
        Assert.Equal(
            PostgresProcessDistributionStoreOptions.DefaultMaximumLedgerBytes,
            store.Capabilities.MaximumAuthorityStateBytes);
        Assert.True(ProcessDistributionCapabilityValidator.ValidateProduction(
            store.Capabilities,
            requireAtomicProcessCommit: false).IsValid);
        var composed = ProcessDistributionCapabilityValidator.ValidateProduction(store.Capabilities);
        var diagnostic = Assert.Single(composed.Diagnostics);
        Assert.Equal(ProcessDistributionDiagnosticCodes.AtomicProcessCommitUnavailable, diagnostic.Code);
    }

    [Theory]
    [InlineData("bad-name", "ledger")]
    [InlineData("schema", "bad.table")]
    [InlineData("1schema", "ledger")]
    public void Options_RejectUnsafeSqlIdentifiers(string schema, string table)
    {
        Assert.Throws<ArgumentException>(() => new PostgresProcessDistributionStoreOptions(
            "authority/test",
            schema,
            table));
    }

    [Fact]
    public void Options_ExposeExplicitAggregateDocumentLimit()
    {
        var options = new PostgresProcessDistributionStoreOptions(
            "authority/test",
            maximumLedgerBytes: 1024);

        Assert.Equal(1024, options.MaximumLedgerBytes);
        Assert.Equal("\"cohesive\".\"process_distribution_ledgers\"", options.QualifiedTable);
    }
}
